using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace EnemyIntel.App;

internal sealed class UiFixManager
{
    private const string Resolution = "32_9";
    private readonly string _assetRoot = Path.Combine(AppContext.BaseDirectory, "assets", "UIFixes", Resolution, "menu");

    private static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["dialogue"] = ["01_060_caption.gfx"],
        ["map"] = ["02_120_worldmap.gfx", "02_122_worldmap_warplist.gfx"],
        ["loading"] = ["01_900_black.gfx", "01_910_fade.gfx", "02_903_nowloading2.gfx", "02_904_nowloading3.gfx"],
        ["frontEnd"] = ["04_100_chrmake_bg.gfx", "04_200_chrmake_basechrselect.gfx", "05_000_title.gfx", "05_010_profileselect.gfx", "05_031_newgame_brightnesssetting.gfx", "05_032_newgame_controlsetting.gfx", "05_040_termofservice.gfx", "05_900_logo_fromsoft.gfx", "05_901_logo_bne.gfx"],
        ["hud"] = ["01_000_fe.gfx", "01_001_fe_soul.gfx", "01_002_fe_saveicon.gfx", "01_031_bloodmessage_top.gfx", "01_070_commandlist.gfx", "01_071_commandlist2.gfx", "01_100_clock.gfx", "01_930_keyguide.gfx", "02_000_ingametop.gfx", "02_110_detailkeyguide.gfx", "02_130_tutorial_modal.gfx", "02_131_tutorial_toast.gfx"],
        ["menus"] = ["01_010_messagebox.gfx", "01_012_itemquantityselect.gfx", "01_014_itemquantityselect_recipe.gfx", "02_010_equiptop.gfx", "02_011_equip.gfx", "02_012_soulforging.gfx", "02_020_inventory.gfx", "02_046_brightnesssetting.gfx", "02_070_status.gfx", "02_150_network.gfx", "02_151_signranknetwork.gfx", "02_152_networkkeywordsetting.gfx", "02_160_keyconfiguration.gfx", "03_000_shoptop.gfx", "03_001_shop.gfx", "03_002_spexchangeshop.gfx", "03_010_levelup.gfx", "03_020_spell.gfx", "03_050_itembox.gfx", Path.Combine("win", "02_040_optionsetting.gfx"), Path.Combine("win", "02_042_pc_graphicsetting.gfx")]
    };

    public UiFixState GetState(string key)
    {
        var files = ResolveGroup(key).ToArray();
        if (!Directory.Exists(_assetRoot) || files.Any(file => !File.Exists(Path.Combine(_assetRoot, file))))
            return new(false, false, "32:9 fix assets are unavailable in this build.");
        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
            return new(false, false, "No active Elden Ring ME3 profile was found.");
        var matching = files.Count(file => FilesMatch(Path.Combine(_assetRoot, file), Path.Combine(profileRoot, file)));
        var enabled = matching == files.Length;
        var running = Process.GetProcessesByName("eldenring").Length > 0;
        var suffix = running ? " Restart Elden Ring to apply changes." : " Applied on the next Elden Ring launch.";
        var label = enabled ? "Enabled." : matching > 0 ? $"Partially enabled ({matching}/{files.Length})." : "Disabled.";
        return new(true, enabled, label + suffix);
    }

    public string SetEnabled(string key, bool enabled)
    {
        var files = ResolveGroup(key).ToArray();
        var profileRoot = ResolveProfileRoot()
            ?? throw new DirectoryNotFoundException("No active Elden Ring ME3 profile was found.");
        Directory.CreateDirectory(profileRoot);
        foreach (var relative in files)
        {
            var source = Path.Combine(_assetRoot, relative);
            var target = Path.Combine(profileRoot, relative);
            var backup = target + ".eldenintel-original";
            if (!File.Exists(source)) throw new FileNotFoundException("A bundled UI fix asset is missing.", source);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (enabled)
            {
                if (File.Exists(target) && !FilesMatch(source, target) && !File.Exists(backup))
                    File.Copy(target, backup, false);
                File.Copy(source, target, true);
            }
            else if (File.Exists(backup))
            {
                File.Copy(backup, target, true);
                File.Delete(backup);
            }
            else if (FilesMatch(source, target))
            {
                File.Delete(target);
            }
        }

        var action = enabled ? "ENABLED" : "DISABLED";
        return $"{DisplayName(key)} {action} · RESTART ELDEN RING TO APPLY";
    }

    private static string? ResolveProfileRoot()
    {
        var configRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "garyttierney", "me3", "config");
        var settingsPath = Path.Combine(configRoot, "manager_settings.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = document.RootElement;
            if (!root.GetProperty("active_profiles").TryGetProperty("Elden Ring", out var activeProfile))
                return null;
            var activeId = activeProfile.GetString();
            if (string.IsNullOrWhiteSpace(activeId) ||
                !root.GetProperty("profiles").TryGetProperty("Elden Ring", out var profiles))
                return null;

            foreach (var profile in profiles.EnumerateArray())
            {
                if (!profile.TryGetProperty("id", out var id) || id.GetString() != activeId ||
                    !profile.TryGetProperty("mods_path", out var modsPath)) continue;
                var path = modsPath.GetString();
                return string.IsNullOrWhiteSpace(path) ? null : Path.Combine(path, "mod", "menu");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }

        return null;
    }

    private static IEnumerable<string> ResolveGroup(string key) =>
        key.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Groups.Values.SelectMany(files => files).Distinct(StringComparer.OrdinalIgnoreCase)
            : Groups.TryGetValue(key, out var files) ? files : throw new ArgumentOutOfRangeException(nameof(key));

    private static string DisplayName(string key) => key switch
    {
        "all" => "FULL 32:9 UI FIX",
        "dialogue" => "DIALOGUE + CAPTIONS",
        "hud" => "HUD + PROMPTS",
        "menus" => "MENUS + POPUPS",
        "map" => "WORLD MAP",
        "loading" => "LOADING + FADES",
        "frontEnd" => "TITLE + CHARACTER",
        _ => "UI FIX"
    };

    private static bool FilesMatch(string left, string right)
    {
        if (!File.Exists(left) || !File.Exists(right)) return false;
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
    }
}

internal readonly record struct UiFixState(bool Available, bool Enabled, string Message);
internal sealed record UiFixOption(string Key, string Display);
internal sealed record GestureOption(int Id, string Display);
