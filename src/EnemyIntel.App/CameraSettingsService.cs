using System.IO;
using System.Text.Json;

namespace EnemyIntel.App;

internal static class CameraSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EldenIntel",
        "camera-settings.json");

    public static double LoadAutoOrbitSpeedMultiplier()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return 1.0;
            var settings = JsonSerializer.Deserialize<CameraSettings>(File.ReadAllText(SettingsPath));
            return Math.Clamp(settings?.AutoOrbitSpeedMultiplier ?? 1.0, 0.1, 4.0);
        }
        catch
        {
            return 1.0;
        }
    }

    public static void SaveAutoOrbitSpeedMultiplier(double multiplier)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(
                    new CameraSettings(Math.Clamp(multiplier, 0.1, 4.0)),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A settings write must never interrupt live camera control.
        }
    }

    private sealed record CameraSettings(double AutoOrbitSpeedMultiplier);
}
