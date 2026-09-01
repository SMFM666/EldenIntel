using System.Diagnostics;

namespace EnemyIntel.Game.Legacy;

public enum AntiCheatState
{
    Disabled,
    Active,
    Unverified
}

public sealed record AntiCheatStatus(AntiCheatState State, string Label, string Detail);

public static class AntiCheatVerifier
{
    private static readonly object Gate = new();
    private static DateTimeOffset _lastChecked;
    private static AntiCheatStatus _lastStatus =
        new(AntiCheatState.Unverified, "EAC UNVERIFIED", "Elden Ring is not running.");

    public static AntiCheatStatus Check()
    {
        lock (Gate)
        {
            if (DateTimeOffset.UtcNow - _lastChecked < TimeSpan.FromSeconds(1))
                return _lastStatus;

            _lastChecked = DateTimeOffset.UtcNow;
            try
            {
                _lastStatus = CheckCore();
            }
            catch (Exception exception)
            {
                _lastStatus = new(
                    AntiCheatState.Unverified,
                    "EAC UNVERIFIED",
                    $"Verification failed: {exception.Message}. Memory-changing tools are blocked.");
            }
            return _lastStatus;
        }
    }

    private static AntiCheatStatus CheckCore()
    {
        var game = Process.GetProcessesByName("eldenring").FirstOrDefault();
        if (game is null)
            return new(AntiCheatState.Unverified, "EAC UNVERIFIED", "Elden Ring is not running.");

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string processName;
                    try { processName = process.ProcessName; }
                    catch { continue; }
                    if (!ContainsEacMarker(processName)) continue;
                    return new(
                        AntiCheatState.Active,
                        "EAC ACTIVE",
                        $"Blocked: detected {processName}. Read-only features remain available.");
                }
            }

            try
            {
                foreach (ProcessModule module in game.Modules)
                {
                    var moduleName = module.ModuleName ?? string.Empty;
                    var modulePath = module.FileName ?? string.Empty;
                    if (!ContainsEacMarker(moduleName) && !ContainsEacMarker(modulePath)) continue;
                    return new(
                        AntiCheatState.Active,
                        "EAC ACTIVE",
                        $"Blocked: Elden Ring loaded {moduleName}. Read-only features remain available.");
                }
            }
            catch
            {
                return new(
                    AntiCheatState.Unverified,
                    "EAC UNVERIFIED",
                    "Could not inspect Elden Ring's loaded modules. Memory-changing tools are blocked.");
            }

            // Opening the same read/write process handle used by the tool is
            // the affirmative half of verification. Merely failing to find an
            // EAC-named process is not enough to authorize memory changes.
            try
            {
                using var probe = new global::EnemyIntelReader.MemoryReader("eldenring");
            }
            catch
            {
                return new(
                    AntiCheatState.Unverified,
                    "EAC UNVERIFIED",
                    "Elden Ring is protected or inaccessible. Memory-changing tools are blocked.");
            }

            return new(
                AntiCheatState.Disabled,
                "EAC DISABLED",
                "Verified: no EAC process/module detected and offline process access is available.");
        }
        finally
        {
            game.Dispose();
        }
    }

    private static bool ContainsEacMarker(string value) =>
        value.Contains("easyanticheat", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("start_protected_game", StringComparison.OrdinalIgnoreCase);
}
