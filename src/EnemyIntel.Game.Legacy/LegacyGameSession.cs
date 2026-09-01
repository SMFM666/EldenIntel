using EnemyIntel.Core;
using System.IO;

namespace EnemyIntel.Game.Legacy;

public sealed class LegacyGameSession : IGameSession
{
    private readonly global::EnemyIntelReader.LiveEnemyReaderService _reader;
    private readonly string _assetRoot;
    private readonly Dictionary<int, string?> _portraitCache = [];
    private readonly object _portraitCacheGate = new();
    private bool _disposed;

    public LegacyGameSession(string assetRoot)
    {
        _assetRoot = Path.GetFullPath(assetRoot);
        _reader = new global::EnemyIntelReader.LiveEnemyReaderService();
        _reader.StatusChanged += OnStatus;
        _reader.NoTarget += OnNoTarget;
        _reader.TargetFound += OnTarget;
    }

    public event EventHandler<GameSessionStatus>? StatusChanged;
    public event EventHandler<TargetSnapshot?>? TargetChanged;
    public GameSessionStatus Status { get; private set; } = new(GameConnectionState.Waiting, "WAITING FOR ELDEN RING", "Launch the game, then lock onto an enemy.", DateTimeOffset.Now);

    public void Start() => _reader.Start();
    public void Stop() => _reader.Stop();
    public void SetTargetOverride(ulong? address) => _reader.SetTargetOverride(address);
    public void SetPortraitOverride(int paramId, string path)
    {
        if (paramId <= 0 || string.IsNullOrWhiteSpace(path)) return;
        lock (_portraitCacheGate) _portraitCache[paramId] = Path.GetFullPath(path);
    }

    private void OnStatus(global::EnemyIntelReader.ReaderStatus status)
    {
        var state = status.Title.Contains("DETECTED", StringComparison.OrdinalIgnoreCase) ? GameConnectionState.Connecting
            : status.Title.Contains("PAUSED", StringComparison.OrdinalIgnoreCase) ? GameConnectionState.Faulted
            : status.Title.Contains("WAITING", StringComparison.OrdinalIgnoreCase) ? GameConnectionState.Waiting
            : GameConnectionState.Connected;
        Status = new GameSessionStatus(state, status.Title, status.Detail, DateTimeOffset.Now);
        StatusChanged?.Invoke(this, Status);
    }

    private void OnNoTarget() => TargetChanged?.Invoke(this, null);

    private void OnTarget(global::EnemyIntelReader.CharacterInfo target)
    {
        string? portrait;
        bool portraitWasCached;
        lock (_portraitCacheGate)
        {
            portraitWasCached = _portraitCache.TryGetValue(target.ParamId, out portrait);
        }
        if (!portraitWasCached)
        {
            portrait = FindPortrait(target.ParamId);
            lock (_portraitCacheGate) _portraitCache[target.ParamId] = portrait;
        }
        TargetChanged?.Invoke(this, new TargetSnapshot(target.Address, target.LocalId, target.ParamId, $"Enemy {target.ParamId}", target.CurrentHp, target.MaxHp, target.Stagger?.Current ?? 0, target.Stagger?.Maximum ?? 0, target.ClearCount, portrait, Array.Empty<EquipmentSlot>(), Array.Empty<EnemyDrop>(), DateTimeOffset.Now));
    }

    private string? FindPortrait(int paramId)
    {
        var portableDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "Characters");
        var portable = Path.Combine(portableDirectory, $"{paramId}.png");
        if (File.Exists(portable)) return portable;

        var legacyDirectory = Path.Combine(_assetRoot, "Characters");
        var legacy = Path.Combine(legacyDirectory, $"{paramId}.png");
        if (File.Exists(legacy)) return legacy;
        return Directory.Exists(legacyDirectory)
            ? Directory.EnumerateFiles(legacyDirectory, $"{paramId}.png", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _reader.StatusChanged -= OnStatus;
        _reader.NoTarget -= OnNoTarget;
        _reader.TargetFound -= OnTarget;
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }
}
