using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using var singleInstance = new Mutex(initiallyOwned: true,
    @"Local\EldenIntel.Interact.Host.SingleInstance", out var isFirstInstance);
if (!isFirstInstance) return;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(HasUsableLocalhostCertificate()
    ? ["https://localhost:8080", "http://localhost:8081"]
    : ["http://localhost:8081"]);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".ext-twitch.tv", StringComparison.OrdinalIgnoreCase)))
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
var bossCatalog = BossCatalog.Load();
var relay = new EffectRelay(bossCatalog);
var twitchAuth = TwitchExtensionAuth.Load();

app.UseCors();
app.UseWebSockets();

app.UseDefaultFiles(new DefaultFilesOptions { DefaultFileNames = { "viewer.html" } });
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

app.MapGet("/api/state", (HttpContext context) =>
{
    var local = context.IsDirectLoopback();
    if (!local && !twitchAuth.TryValidate(context.Request.Headers.Authorization.ToString(), out _, out var authError))
        return Results.Json(new { message = authError }, statusCode: StatusCodes.Status401Unauthorized);
    return Results.Ok(relay.Snapshot());
});
app.MapGet("/api/bosses", (HttpContext context) =>
{
    var local = context.IsDirectLoopback();
    if (!local && !twitchAuth.TryValidate(context.Request.Headers.Authorization.ToString(), out _, out var authError))
        return Results.Json(new { message = authError }, statusCode: StatusCodes.Status401Unauthorized);
    return Results.Ok(bossCatalog.Names);
});
app.MapPost("/api/effects", (HttpContext context, EffectRequest request) =>
{
    // Cloudflare terminates the public connection and forwards to Kestrel over
    // loopback. Its connecting-IP header must therefore force the public JWT
    // path even though the immediate socket is local.
    var local = context.IsDirectLoopback();
    TwitchViewer? viewer = null;
    if (!local && !twitchAuth.TryValidate(context.Request.Headers.Authorization.ToString(), out viewer, out var authError))
        return Results.Json(new { message = authError }, statusCode: StatusCodes.Status401Unauthorized);
    var authorizedRequest = request with { Viewer = viewer?.OpaqueUserId ?? request.Viewer };
    return relay.Enqueue(authorizedRequest, out var message)
        ? Results.Accepted(value: relay.Snapshot())
        : Results.BadRequest(new { message });
});
app.MapPost("/api/clone-vote/start", (HttpContext context, CloneVoteRequest request) =>
{
    var local = context.IsDirectLoopback();
    TwitchViewer? viewer = null;
    if (!local && !twitchAuth.TryValidate(context.Request.Headers.Authorization.ToString(), out viewer, out var authError))
        return Results.Json(new { message = authError }, statusCode: StatusCodes.Status401Unauthorized);
    return relay.StartCloneVote(viewer?.OpaqueUserId ?? request.Viewer, out var message)
        ? Results.Accepted(value: relay.Snapshot())
        : Results.BadRequest(new { message });
});
app.MapPost("/api/clone-vote/vote", (HttpContext context, CloneVoteBallot request) =>
{
    var local = context.IsDirectLoopback();
    TwitchViewer? viewer = null;
    if (!local && !twitchAuth.TryValidate(context.Request.Headers.Authorization.ToString(), out viewer, out var authError))
        return Results.Json(new { message = authError }, statusCode: StatusCodes.Status401Unauthorized);
    return relay.CastCloneVote(viewer?.OpaqueUserId ?? request.Viewer, request.Choice, out var message)
        ? Results.Ok(relay.Snapshot())
        : Results.BadRequest(new { message });
});
app.MapPost("/api/control", (HttpContext context, ControlRequest request) =>
{
    var local = context.IsDirectLoopback();
    TwitchViewer? viewer = null;
    if (!local && !twitchAuth.TryValidate(context.Request.Headers.Authorization.ToString(), out viewer, out var authError))
        return Results.Json(new { message = authError }, statusCode: StatusCodes.Status401Unauthorized);
    if (!local && viewer?.Role is not ("broadcaster" or "moderator"))
        return Results.Json(new { message = "Broadcaster or moderator authorization is required." }, statusCode: StatusCodes.Status403Forbidden);
    return relay.Control(request.Action, out var message)
        ? Results.Ok(relay.Snapshot())
        : Results.BadRequest(new { message });
});
app.MapPost("/api/consumer/claim", (HttpContext context) =>
    relay.Authorize(context) ? Results.Ok(relay.Claim()) : Results.Unauthorized());
app.MapPost("/api/consumer/{id:guid}/complete", (HttpContext context, Guid id, EffectCompletion completion) =>
    relay.Authorize(context) ? Results.Ok(relay.Complete(id, completion)) : Results.Unauthorized());
app.Map("/ws/viewer", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var local = context.IsDirectLoopback();
    if (!local)
    {
        var authMessage = await WebSocketJson.ReceiveAsync<ViewerSocketAuth>(socket, context.RequestAborted);
        if (authMessage is null || !twitchAuth.TryValidate(authMessage.Token, out _, out _))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid Twitch authorization", context.RequestAborted);
            return;
        }
    }
    await relay.RunViewerAsync(socket, context.RequestAborted);
});
app.Map("/ws/consumer", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    if (!relay.Authorize(context)) { context.Response.StatusCode = 401; return; }
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    await relay.RunConsumerAsync(socket, context.RequestAborted);
});

app.Run();

static bool HasUsableLocalhostCertificate()
{
    try
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Any(certificate => certificate.HasPrivateKey &&
            certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
            certificate.Subject.Contains("CN=localhost", StringComparison.OrdinalIgnoreCase));
    }
    catch
    {
        return false;
    }
}

sealed record EffectRequest(string EffectId, string? Viewer, string? BossName = null);
sealed record CloneVoteRequest(string? Viewer);
sealed record CloneVoteBallot(string Choice, string? Viewer);
sealed record EffectCompletion(bool Succeeded, string? Message);
sealed record CompletedEffect(Guid Id, string EffectId, string Name, bool Succeeded, string Message, DateTimeOffset CompletedAt);
sealed record ControlRequest(string Action);
sealed record ViewerSocketAuth(string Token);
sealed record EffectItem(Guid Id, string EffectId, string Name, string Viewer, string? BossName, int Duration, DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt = null, string? Result = null);

sealed class EffectRelay
{
    private const int Capacity = 8;
    private readonly object _gate = new();
    private readonly Queue<EffectItem> _queue = new();
    private EffectItem? _active;
    private readonly Dictionary<string, (string Name, int Duration, TimeSpan Cooldown)> _allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["summon-boss"] = ("SUMMON A BOSS", 4, TimeSpan.FromSeconds(45)),
        ["time-shift"] = ("SHIFT TIME", 5, TimeSpan.FromSeconds(12)),
        ["heal-player"] = ("HEAL PLAYER", 2, TimeSpan.FromSeconds(60)),
        ["life-steal"] = ("LIFE STEAL", 3, TimeSpan.FromSeconds(60)),
        ["slow-world"] = ("SLOW WORLD", 8, TimeSpan.FromSeconds(120)),
        ["random"] = ("RANDOM EFFECT", 8, TimeSpan.FromSeconds(20))
        ,["spawn-clone-help"] = ("ALLY CLONE · HELP", 4, TimeSpan.FromMinutes(3))
        ,["spawn-clone-hurt"] = ("ENEMY CLONE · HURT", 4, TimeSpan.FromMinutes(3))
        ,["hurt-seth"] = ("HURT SETH · 0.5%", 0, TimeSpan.Zero)
    };
    private readonly Dictionary<string, DateTimeOffset> _lastQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastViewerQueued = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queuedSignal = new(0);
    private readonly ConcurrentDictionary<Guid, WebSocket> _viewers = new();
    private readonly SemaphoreSlim _broadcastGate = new(1, 1);
    private readonly string _key;
    private readonly BossCatalog _bossCatalog;
    private bool _armed = true;
    private int _consumerConnections;
    private long _hurtDamage;
    private long _hurtHits;
    private int _hurtMaxHp;
    private Guid _smashEventId;
    private DateTimeOffset _smashEndsAt;
    private Guid _cloneVoteId;
    private DateTimeOffset _cloneVoteEndsAt;
    private readonly Dictionary<string, bool> _cloneVotes = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCloneVoteEndedAt;
    private CompletedEffect? _lastCompleted;

    public EffectRelay(BossCatalog bossCatalog)
    {
        _bossCatalog = bossCatalog;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldenIntel", "V1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "relay.key");
        _key = File.Exists(path) ? File.ReadAllText(path).Trim() : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!File.Exists(path)) File.WriteAllText(path, _key);
    }

    public bool Enqueue(EffectRequest request, out string message)
    {
        lock (_gate)
        {
            if (!_armed)
            {
                message = "Viewer interactions are currently paused by the broadcaster.";
                return false;
            }
            if (_consumerConnections <= 0)
            {
                message = "EldenIntel is offline. No game effect was queued.";
                return false;
            }
            var effectId = request.EffectId ?? string.Empty;
            var isHurtTick = effectId.Equals("hurt-seth", StringComparison.OrdinalIgnoreCase);
            if (!_allowed.TryGetValue(effectId, out var definition))
            {
                message = "That effect is not enabled in the live relay yet.";
                return false;
            }
            string? bossName = null;
            if (effectId.Equals("summon-boss", StringComparison.OrdinalIgnoreCase))
            {
                bossName = _bossCatalog.Resolve(request.BossName);
                if (bossName is null)
                {
                    message = "Choose a boss from the verified search results.";
                    return false;
                }
            }
            if (_queue.Count >= (isHurtTick ? 256 : Capacity))
            {
                message = "The effect queue is full.";
                return false;
            }
            if (!isHurtTick && (_active?.EffectId.Equals(effectId, StringComparison.OrdinalIgnoreCase) == true ||
                _queue.Any(item => item.EffectId.Equals(effectId, StringComparison.OrdinalIgnoreCase)))
            ){
                message = "That effect is already active or queued.";
                return false;
            }
            var now = DateTimeOffset.UtcNow;
            var viewerKey = string.IsNullOrWhiteSpace(request.Viewer) ? "TWITCH VIEWER" : request.Viewer.Trim();
            if (!isHurtTick && _lastViewerQueued.TryGetValue(viewerKey, out var lastViewerQueued) && now - lastViewerQueued < TimeSpan.FromSeconds(2))
            {
                message = "Please wait a moment before requesting another effect.";
                return false;
            }
            if (_lastQueued.TryGetValue(effectId, out var lastQueued) && now - lastQueued < definition.Cooldown)
            {
                message = $"That effect is cooling down for {Math.Ceiling((definition.Cooldown - (now - lastQueued)).TotalSeconds)} more seconds.";
                return false;
            }
            _queue.Enqueue(new EffectItem(Guid.NewGuid(), effectId, definition.Name,
                string.IsNullOrWhiteSpace(request.Viewer) ? "TWITCH VIEWER" : request.Viewer.Trim()[..Math.Min(32, request.Viewer.Trim().Length)],
                bossName, definition.Duration, now));
            _lastQueued[effectId] = now;
            if (!isHurtTick) _lastViewerQueued[viewerKey] = now;
            message = "Queued";
            _queuedSignal.Release();
            _ = BroadcastAsync();
            return true;
        }
    }

    public bool StartCloneVote(string? viewer, out string message)
    {
        Guid voteId;
        lock (_gate)
        {
            if (!_armed) { message = "Viewer interactions are currently paused by the broadcaster."; return false; }
            if (_consumerConnections <= 0) { message = "EldenIntel is offline. The clone vote was not started."; return false; }
            if (_cloneVoteEndsAt > DateTimeOffset.UtcNow) { message = "A clone vote is already active."; return false; }
            if (DateTimeOffset.UtcNow - _lastCloneVoteEndedAt < TimeSpan.FromMinutes(3))
            {
                message = "Spawn Clone is cooling down.";
                return false;
            }
            _cloneVoteId = voteId = Guid.NewGuid();
            _cloneVoteEndsAt = DateTimeOffset.UtcNow.AddSeconds(20);
            _cloneVotes.Clear();
            message = "Clone vote started.";
        }
        _ = BroadcastAsync();
        _ = CompleteCloneVoteAfterDelayAsync(voteId);
        return true;
    }

    public bool CastCloneVote(string? viewer, string? choice, out string message)
    {
        lock (_gate)
        {
            if (_cloneVoteEndsAt <= DateTimeOffset.UtcNow) { message = "There is no active clone vote."; return false; }
            var voter = string.IsNullOrWhiteSpace(viewer) ? "LOCAL VIEWER" : viewer.Trim();
            if (_cloneVotes.ContainsKey(voter)) { message = "You already voted."; return false; }
            var help = choice?.Trim().Equals("help", StringComparison.OrdinalIgnoreCase) == true;
            var hurt = choice?.Trim().Equals("hurt", StringComparison.OrdinalIgnoreCase) == true;
            if (!help && !hurt) { message = "Vote HELP or HURT."; return false; }
            _cloneVotes[voter] = help;
            message = help ? "Voted HELP." : "Voted HURT.";
        }
        _ = BroadcastAsync();
        return true;
    }

    private async Task CompleteCloneVoteAfterDelayAsync(Guid voteId)
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        string effectId;
        lock (_gate)
        {
            if (_cloneVoteId != voteId || _cloneVoteEndsAt == default) return;
            var helpVotes = _cloneVotes.Values.Count(help => help);
            var hurtVotes = _cloneVotes.Count - helpVotes;
            var helpWins = helpVotes == hurtVotes ? Random.Shared.Next(2) == 0 : helpVotes > hurtVotes;
            effectId = helpWins ? "spawn-clone-help" : "spawn-clone-hurt";
            _cloneVoteEndsAt = default;
            _lastCloneVoteEndedAt = DateTimeOffset.UtcNow;
            _cloneVotes.Clear();
        }
        Enqueue(new EffectRequest(effectId, "TWITCH VOTE"), out _);
        await BroadcastAsync();
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var visibleQueue = _queue
                .Where(item => !item.EffectId.Equals("hurt-seth", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var visibleActive = _active is not null &&
                                !_active.EffectId.Equals("hurt-seth", StringComparison.OrdinalIgnoreCase)
                ? _active
                : null;
            var cooldowns = _lastQueued
                .Where(entry => _allowed.TryGetValue(entry.Key, out var definition) && entry.Value + definition.Cooldown > now)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value + _allowed[entry.Key].Cooldown,
                    StringComparer.OrdinalIgnoreCase);
            var smashEvent = _smashEndsAt > now
                ? new { id = _smashEventId, endsAt = _smashEndsAt, target = 40 }
                : null;
            var cloneVote = _cloneVoteEndsAt > now
                ? new
                {
                    id = _cloneVoteId,
                    endsAt = _cloneVoteEndsAt,
                    helpVotes = _cloneVotes.Values.Count(help => help),
                    hurtVotes = _cloneVotes.Values.Count(help => !help)
                }
                : null;
            return new
            {
                capacity = Capacity,
                armed = _armed,
                consumerOnline = _consumerConnections > 0,
                active = visibleActive,
                queue = visibleQueue,
                cooldowns,
                hurtDamage = _hurtDamage,
                hurtHits = _hurtHits,
                hurtMaxHp = _hurtMaxHp,
                smashEvent,
                cloneVote,
                lastCompleted = _lastCompleted
            };
        }
    }

    public bool Control(string? action, out string message)
    {
        lock (_gate)
        {
            switch (action?.Trim().ToLowerInvariant())
            {
                case "arm":
                    _armed = true;
                    message = "Viewer interactions armed.";
                    break;
                case "disarm":
                    _armed = false;
                    message = "Viewer interactions paused.";
                    break;
                case "clear":
                    _queue.Clear();
                    while (_queuedSignal.Wait(0)) { }
                    message = "Queued effects cleared.";
                    break;
                case "trigger-hurt-seth":
                    _smashEventId = Guid.NewGuid();
                    _smashEndsAt = DateTimeOffset.UtcNow.AddSeconds(20);
                    message = "HURT SETH test event triggered for 20 seconds.";
                    break;
                default:
                    message = "Unknown control action.";
                    return false;
            }
        }
        _ = BroadcastAsync();
        return true;
    }

    public EffectItem? Claim()
    {
        lock (_gate)
        {
            if (_active is not null || _queue.Count == 0) return null;
            _active = _queue.Dequeue() with { StartedAt = DateTimeOffset.UtcNow };
            _ = BroadcastAsync();
            return _active;
        }
    }

    public object Complete(Guid id, EffectCompletion completion)
    {
        lock (_gate)
        {
            if (_active?.Id != id) return Snapshot();
            _lastCompleted = new CompletedEffect(
                _active.Id,
                _active.EffectId,
                _active.Name,
                completion.Succeeded,
                completion.Message ?? string.Empty,
                DateTimeOffset.UtcNow);
            if (_active.EffectId.Equals("hurt-seth", StringComparison.OrdinalIgnoreCase) && completion.Succeeded)
            {
                var numbers = System.Text.RegularExpressions.Regex.Matches(completion.Message ?? string.Empty, @"\d+")
                    .Select(match => int.Parse(match.Value)).ToArray();
                if (numbers.Length >= 3)
                {
                    _hurtDamage += Math.Max(0, numbers[0] - numbers[1]);
                    _hurtHits++;
                    _hurtMaxHp = numbers[2];
                }
            }
            _active = null;
            _ = BroadcastAsync();
            return Snapshot();
        }
    }

    public bool Authorize(HttpContext context)
    {
        var supplied = context.Request.Headers["X-EldenIntel-Relay-Key"].ToString();
        return supplied.Length == _key.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(_key));
    }

    public async Task RunViewerAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _viewers[id] = socket;
        await WebSocketJson.SendAsync(socket, Snapshot(), cancellationToken);
        var buffer = new byte[64];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { _viewers.TryRemove(id, out _); socket.Dispose(); }
    }

    public async Task RunConsumerAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        EffectItem? claimed = null;
        Interlocked.Increment(ref _consumerConnections);
        _ = BroadcastAsync();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                claimed = Claim();
                if (claimed is null)
                {
                    await _queuedSignal.WaitAsync(cancellationToken);
                    continue;
                }
                await WebSocketJson.SendAsync(socket, claimed, cancellationToken);
                var completion = await WebSocketJson.ReceiveAsync<EffectCompletion>(socket, cancellationToken);
                if (completion is null) break;
                Complete(claimed.Id, completion);
                claimed = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            Interlocked.Decrement(ref _consumerConnections);
            if (claimed is not null)
            {
                lock (_gate)
                {
                    if (_active?.Id == claimed.Id)
                    {
                        _active = null;
                        _queue.Enqueue(claimed with { StartedAt = null });
                        _queuedSignal.Release();
                    }
                }
                _ = BroadcastAsync();
            }
            socket.Dispose();
            _ = BroadcastAsync();
        }
    }

    private async Task BroadcastAsync()
    {
        await _broadcastGate.WaitAsync();
        try
        {
            var snapshot = Snapshot();
            foreach (var viewer in _viewers.ToArray())
            {
                try
                {
                    if (viewer.Value.State == WebSocketState.Open)
                        await WebSocketJson.SendAsync(viewer.Value, snapshot, CancellationToken.None);
                    else _viewers.TryRemove(viewer.Key, out _);
                }
                catch { _viewers.TryRemove(viewer.Key, out _); }
            }
        }
        finally { _broadcastGate.Release(); }
    }
}

static class WebSocketJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static Task SendAsync(WebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public static async Task<T?> ReceiveAsync<T>(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return default;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 32_768) return default;
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<T>(stream.ToArray(), Options);
    }
}

sealed class BossCatalog
{
    private readonly Dictionary<string, string> _names;
    public IReadOnlyList<string> Names { get; }

    private BossCatalog(IEnumerable<string> names)
    {
        Names = names.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _names = Names.ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);
    }

    public static BossCatalog Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "boss_catalog.json");
        if (!File.Exists(path)) throw new InvalidOperationException("The verified boss catalog is missing.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var names = document.RootElement.EnumerateArray()
            .Select(entry => entry.TryGetProperty("Name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>();
        var catalog = new BossCatalog(names);
        if (catalog.Names.Count == 0) throw new InvalidOperationException("The verified boss catalog is empty.");
        return catalog;
    }

    public string? Resolve(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? null : _names.GetValueOrDefault(requested.Trim());
}

static class IpAddressExtensions
{
    public static bool IsLoopback(this System.Net.IPAddress address) => System.Net.IPAddress.IsLoopback(address);

    public static bool IsDirectLoopback(this HttpContext context) =>
        context.Connection.RemoteIpAddress?.IsLoopback() == true &&
        !context.Request.Headers.ContainsKey("CF-Connecting-IP") &&
        !context.Request.Headers.ContainsKey("X-Forwarded-For") &&
        !context.Request.Headers.ContainsKey("Forwarded") &&
        !context.Request.Headers.ContainsKey("Tailscale-User-Login");
}

sealed record TwitchViewer(string ChannelId, string OpaqueUserId, string Role);

sealed class TwitchExtensionAuth
{
    private readonly byte[] _secret;
    private TwitchExtensionAuth(byte[] secret) => _secret = secret;

    public static TwitchExtensionAuth Load()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EldenIntel", "V1", "twitch-extension-secret.dpapi");
        if (!File.Exists(path)) return new TwitchExtensionAuth([]);
        try
        {
            var encrypted = Convert.FromHexString(File.ReadAllText(path).Trim());
            var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var base64 = Encoding.Unicode.GetString(clear);
            return new TwitchExtensionAuth(Convert.FromBase64String(base64));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            return new TwitchExtensionAuth([]);
        }
    }

    public bool TryValidate(string authorization, out TwitchViewer? viewer, out string error)
    {
        viewer = null;
        error = "A valid Twitch Extension authorization token is required.";
        if (_secret.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(authorization)) return false;
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim() : authorization.Trim();
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            using var hmac = new HMACSHA256(_secret);
            var expected = hmac.ComputeHash(signed);
            var supplied = DecodeBase64Url(parts[2]);
            if (expected.Length != supplied.Length || !CryptographicOperations.FixedTimeEquals(expected, supplied))
                return false;
            using var payload = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var root = payload.RootElement;
            var expires = root.GetProperty("exp").GetInt64();
            if (DateTimeOffset.FromUnixTimeSeconds(expires) <= DateTimeOffset.UtcNow.AddSeconds(-5))
            {
                error = "The Twitch authorization token expired.";
                return false;
            }
            var channel = root.GetProperty("channel_id").GetString() ?? string.Empty;
            var opaque = root.GetProperty("opaque_user_id").GetString() ?? string.Empty;
            var role = root.GetProperty("role").GetString() ?? string.Empty;
            if (channel.Length == 0 || opaque.Length == 0 || role is not ("viewer" or "moderator" or "broadcaster"))
                return false;
            viewer = new TwitchViewer(channel, opaque, role);
            error = string.Empty;
            return true;
        }
        catch { return false; }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
