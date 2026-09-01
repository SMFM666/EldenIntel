namespace EnemyIntel.Core;

public enum GameConnectionState
{
    Waiting,
    Connecting,
    Connected,
    Faulted
}

public sealed record EquipmentSlot(string Slot, long ItemId, string Name, string Category, string? ImagePath, string Description);

public sealed record EnemyDrop(long ItemId, string Name, string Category, int Quantity, double ChancePercent, string? ImagePath, string Description);

public sealed record TargetSnapshot(
    ulong Address,
    uint LocalId,
    int ParamId,
    string Name,
    int CurrentHp,
    int MaxHp,
    float CurrentPoise,
    float MaxPoise,
    int ClearCount,
    string? PortraitPath,
    IReadOnlyList<EquipmentSlot> Equipment,
    IReadOnlyList<EnemyDrop> Drops,
    DateTimeOffset CapturedAt);

public sealed record GameSessionStatus(GameConnectionState State, string Title, string Detail, DateTimeOffset ChangedAt);
