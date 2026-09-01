using System.IO;
using System.Text.Json;

namespace EnemyIntel.App;

internal static class CaptureSettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(AppContext.BaseDirectory, "settings");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "capture.json");

    public static string LoadDirectory()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(SettingsPath));
                if (!string.IsNullOrWhiteSpace(settings?.Directory))
                    return Path.GetFullPath(settings.Directory);
            }
        }
        catch { }

        return Path.Combine(AppContext.BaseDirectory, "captures");
    }

    public static void SaveDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
            new CaptureSettings(fullPath),
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record CaptureSettings(string Directory);
}
