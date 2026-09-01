namespace EnemyIntel.Core;

public interface IGameSession : IAsyncDisposable
{
    event EventHandler<GameSessionStatus>? StatusChanged;
    event EventHandler<TargetSnapshot?>? TargetChanged;
    GameSessionStatus Status { get; }
    void Start();
    void Stop();
    void SetTargetOverride(ulong? address);
    void SetPortraitOverride(int paramId, string path);
}

public interface IEnemyIntelRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string?> FindEnemyNameAsync(int paramId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EquipmentSlot>> FindEquipmentAsync(int paramId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EnemyDrop>> FindDropsAsync(int paramId, CancellationToken cancellationToken = default);
    Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

public sealed record DatabaseStats(int EnemyCount, int ItemCount, int DropCount, DateTimeOffset BuiltAt);
