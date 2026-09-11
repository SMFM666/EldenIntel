using System.IO;
using System.Text.Json;
using System.Windows;

namespace EnemyIntel.App;

internal static class WindowSettingsService
{
    private const int CurrentLayoutVersion = 2;
    private const double DefaultWidth = 960;
    private const double DefaultHeight = 960;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EldenIntel", "V1", "settings.json");

    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var settings = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(SettingsPath));
            if (settings is null || settings.Width < window.MinWidth || settings.Height < window.MinHeight) return;
            if (!IntersectsVirtualDesktop(settings.Left, settings.Top, settings.Width, settings.Height)) return;
            bool migrateLegacyPortraitLayout = settings.LayoutVersion < CurrentLayoutVersion &&
                                               settings.Height / settings.Width >= 1.2;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = settings.Left;
            window.Top = settings.Top;
            window.Width = migrateLegacyPortraitLayout ? DefaultWidth : settings.Width;
            window.Height = migrateLegacyPortraitLayout ? DefaultHeight : settings.Height;
            window.WindowState = settings.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        catch { }
    }

    public static void Save(Window window)
    {
        try
        {
            var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            var settings = new WindowSettings(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized,
                "F2", "Ctrl+1", "Ctrl+P", "Ctrl+F6", CurrentLayoutVersion);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static bool IntersectsVirtualDesktop(double left, double top, double width, double height) =>
        new Rect(left, top, width, height).IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));

    private sealed record WindowSettings(double Left, double Top, double Width, double Height, bool Maximized,
        string HudHotkey, string CaptureHotkey, string PauseHotkey, string FreecamHotkey, int LayoutVersion = 0);
}
