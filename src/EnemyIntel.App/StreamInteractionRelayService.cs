using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace EnemyIntel.App;

internal sealed class StreamInteractionRelayService : IDisposable
{
    private readonly HttpClient _client = new() { BaseAddress = new Uri("http://localhost:8081/"), Timeout = TimeSpan.FromSeconds(3) };
    private readonly Func<string, string?, Task<(bool Succeeded, string Message)>> _execute;
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private string _relayKey = string.Empty;
    private Task? _worker;
    private Process? _relayProcess;

    public StreamInteractionRelayService(Func<string, string?, Task<(bool Succeeded, string Message)>> execute) => _execute = execute;

    public void Start()
    {
        _worker = Task.Run(() => BootstrapAsync(_stop.Token));
    }

    public async Task<string> TriggerHurtSethTestAsync()
    {
        try
        {
            using var response = await _client.PostAsJsonAsync("api/control", new { action = "trigger-hurt-seth" });
            return response.IsSuccessStatusCode
                ? "HURT SETH TEST · LIVE FOR 20s"
                : $"HURT SETH TEST REJECTED · {(int)response.StatusCode}";
        }
        catch (Exception exception)
        {
            return $"HURT SETH TEST FAILED · {exception.Message}";
        }
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        await EnsureRelayHostAsync(cancellationToken);
        var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldenIntel", "V1", "relay.key");
        for (var attempt = 0; attempt < 30 && !File.Exists(keyPath); attempt++)
            await Task.Delay(100, cancellationToken);
        if (!File.Exists(keyPath)) return;
        _relayKey = File.ReadAllText(keyPath).Trim();
        _client.DefaultRequestHeaders.Add("X-EldenIntel-Relay-Key", _relayKey);
        await RunAsync(cancellationToken);
    }

    private async Task EnsureRelayHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync("127.0.0.1", 8081, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
            return;
        }
        catch { }

        var hostPath = Path.Combine(AppContext.BaseDirectory, "TwitchRelay", "EldenIntel.Interact.Host.exe");
        if (!File.Exists(hostPath)) return;
        _relayProcess = Process.Start(new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RunSocketAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch
            {
                // Preserve the proven HTTP path while the persistent socket is
                // reconnecting, so a transient upgrade failure cannot strand a queue.
                await PollOnceAsync(cancellationToken);
                await Task.Delay(750, cancellationToken);
            }
        }
    }

    private async Task RunSocketAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-EldenIntel-Relay-Key", _relayKey);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(new Uri("ws://localhost:8081/ws/consumer"), cancellationToken);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var item = await ReceiveAsync<RelayEffect>(socket, cancellationToken);
            if (item is null) return;
            var result = await _execute(item.EffectId, item.BossName);
            await SendAsync(socket, new { succeeded = result.Succeeded, message = result.Message }, cancellationToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.PostAsync("api/consumer/claim", null, cancellationToken);
            if (!response.IsSuccessStatusCode) return;
            var item = await response.Content.ReadFromJsonAsync<RelayEffect>(cancellationToken: cancellationToken);
            if (item is null) return;
            var result = await _execute(item.EffectId, item.BossName);
            await _client.PostAsJsonAsync($"api/consumer/{item.Id}/complete",
                new { succeeded = result.Succeeded, message = result.Message }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
    }

    private async Task<T?> ReceiveAsync<T>(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return default;
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<T>(stream.ToArray(), _json);
    }

    private Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            if (_relayProcess is { HasExited: false }) _relayProcess.Kill(entireProcessTree: true);
            _relayProcess?.Dispose();
        }
        catch { }
        _client.Dispose();
        _stop.Dispose();
    }

    private sealed record RelayEffect(Guid Id, string EffectId, string? BossName);
}
