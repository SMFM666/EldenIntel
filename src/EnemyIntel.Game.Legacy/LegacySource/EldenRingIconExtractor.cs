using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Pfim;

namespace EnemyIntelReader;

/// <summary>
/// Reads Elden Ring *.layout atlas metadata, locates MENU_ItemIcon_##### entries,
/// crops the matching DDS atlas, and saves the icon as a transparent PNG.
///
/// NuGet dependency:
///     Pfim
/// </summary>
public sealed class EldenRingIconExtractor : IDisposable
{
    private readonly string _layoutDirectory;
    private readonly string _textureDirectory;
    private readonly Dictionary<int, AtlasEntry> _entries = new();
    private readonly Dictionary<string, Bitmap> _atlasCache =
        new(StringComparer.OrdinalIgnoreCase);

    public EldenRingIconExtractor(string layoutDirectory, string textureDirectory)
    {
        _layoutDirectory = Path.GetFullPath(layoutDirectory);
        _textureDirectory = Path.GetFullPath(textureDirectory);

        if (!Directory.Exists(_layoutDirectory))
            throw new DirectoryNotFoundException(
                $"Layout directory not found: {_layoutDirectory}");

        if (!Directory.Exists(_textureDirectory))
            throw new DirectoryNotFoundException(
                $"Texture directory not found: {_textureDirectory}");

        BuildIndex();
    }

    public int IndexedIconCount => _entries.Count;

    public bool ContainsIcon(int iconId) => _entries.ContainsKey(iconId);

    /// <summary>
    /// Extracts an icon and returns a new Bitmap owned by the caller.
    /// </summary>
    public Bitmap ExtractIcon(int iconId)
    {
        if (!_entries.TryGetValue(iconId, out AtlasEntry? entry))
        {
            throw new KeyNotFoundException(
                $"Icon ID {iconId} was not found in any .layout file under " +
                $"'{_layoutDirectory}'.");
        }

        Bitmap atlas = GetAtlas(entry.AtlasImagePath);

        Rectangle crop = new(entry.X, entry.Y, entry.Width, entry.Height);
        Rectangle atlasBounds = new(0, 0, atlas.Width, atlas.Height);

        if (!atlasBounds.Contains(crop))
        {
            throw new InvalidDataException(
                $"The crop rectangle {crop} for icon {iconId} falls outside " +
                $"atlas '{entry.AtlasImagePath}' ({atlas.Width}x{atlas.Height}).");
        }

        return atlas.Clone(crop, PixelFormat.Format32bppArgb);
    }

    /// <summary>
    /// Extracts an icon and saves it as PNG. Existing files are replaced.
    /// </summary>
    public string ExtractIconToPng(int iconId, string outputPath)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        string? parent = Path.GetDirectoryName(fullOutputPath);

        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        using Bitmap icon = ExtractIcon(iconId);

        if (File.Exists(fullOutputPath))
            File.Delete(fullOutputPath);

        icon.Save(fullOutputPath, System.Drawing.Imaging.ImageFormat.Png);
        return fullOutputPath;
    }

    /// <summary>
    /// Convenient helper for the preferred item asset structure:
    /// assets\Items\Category\ItemID\image.png
    /// </summary>
    public string ExtractToItemFolder(
        int iconId,
        string assetsRoot,
        string category,
        int itemId)
    {
        string outputPath = Path.Combine(
            assetsRoot,
            "Items",
            SanitizeFolderName(category),
            itemId.ToString(),
            "image.png");

        return ExtractIconToPng(iconId, outputPath);
    }

    private void BuildIndex()
    {
        string[] layoutFiles = Directory
            .EnumerateFiles(_layoutDirectory, "*.layout", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (layoutFiles.Length == 0)
        {
            throw new FileNotFoundException(
                $"No .layout files were found under '{_layoutDirectory}'.");
        }

        foreach (string layoutPath in layoutFiles)
        {
            XDocument document;

            try
            {
                document = XDocument.Load(layoutPath, LoadOptions.None);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Failed to parse layout file '{layoutPath}'.", ex);
            }

            XElement? root = document.Root;
            if (root is null ||
                !string.Equals(root.Name.LocalName, "TextureAtlas",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? atlasImagePath = (string?)root.Attribute("imagePath");
            if (string.IsNullOrWhiteSpace(atlasImagePath))
                continue;

            foreach (XElement element in root.Elements())
            {
                if (!string.Equals(element.Name.LocalName, "SubTexture",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? name = (string?)element.Attribute("name");
                if (!TryParseIconId(name, out int iconId))
                    continue;

                AtlasEntry entry = new(
                    iconId,
                    atlasImagePath,
                    ReadRequiredInt(element, "x"),
                    ReadRequiredInt(element, "y"),
                    ReadRequiredInt(element, "width"),
                    ReadRequiredInt(element, "height"),
                    layoutPath);

                // Most item icon IDs are unique. If an exact duplicate appears,
                // prefer the first indexed file and leave the mapping stable.
                _entries.TryAdd(iconId, entry);
            }
        }

        if (_entries.Count == 0)
        {
            throw new InvalidDataException(
                "Layout files were found, but no MENU_ItemIcon_##### entries " +
                "could be indexed.");
        }
    }

    private Bitmap GetAtlas(string atlasImagePath)
    {
        string ddsName = Path.ChangeExtension(
            Path.GetFileName(atlasImagePath), ".dds");

        if (_atlasCache.TryGetValue(ddsName, out Bitmap? cached))
            return cached;

        string? ddsPath = Directory
            .EnumerateFiles(_textureDirectory, ddsName, SearchOption.AllDirectories)
            .FirstOrDefault();

        if (ddsPath is null)
        {
            throw new FileNotFoundException(
                $"Could not find DDS atlas '{ddsName}' under " +
                $"'{_textureDirectory}'.");
        }

        Bitmap bitmap = LoadDdsAsBitmap(ddsPath);
        _atlasCache.Add(ddsName, bitmap);
        return bitmap;
    }

    private static Bitmap LoadDdsAsBitmap(string ddsPath)
    {
        using IImage image = Pfimage.FromFile(ddsPath);

        PixelFormat pixelFormat;
        int bytesPerPixel;

        switch (image.Format)
        {
            case Pfim.ImageFormat.Rgba32:
                pixelFormat = PixelFormat.Format32bppArgb;
                bytesPerPixel = 4;
                break;

            case Pfim.ImageFormat.Rgb24:
                pixelFormat = PixelFormat.Format24bppRgb;
                bytesPerPixel = 3;
                break;

            default:
                throw new NotSupportedException(
                    $"DDS '{ddsPath}' decoded as unsupported format " +
                    $"'{image.Format}'. Expected Rgba32 or Rgb24.");
        }

        Bitmap bitmap = new(image.Width, image.Height, pixelFormat);
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData bitmapData = bitmap.LockBits(
            rect, ImageLockMode.WriteOnly, pixelFormat);

        try
        {
            int sourceRowBytes = image.Width * bytesPerPixel;
            int sourceStride = image.Stride;
            int destinationStride = bitmapData.Stride;

            for (int y = 0; y < image.Height; y++)
            {
                IntPtr destinationRow =
                    bitmapData.Scan0 + (y * destinationStride);

                Marshal.Copy(
                    image.Data,
                    y * sourceStride,
                    destinationRow,
                    sourceRowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return bitmap;
    }

    private static bool TryParseIconId(string? name, out int iconId)
    {
        iconId = 0;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        const string prefix = "MENU_ItemIcon_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string number = Path.GetFileNameWithoutExtension(name)[prefix.Length..];
        return int.TryParse(number, out iconId);
    }

    private static int ReadRequiredInt(XElement element, string attributeName)
    {
        string? raw = (string?)element.Attribute(attributeName);

        if (!int.TryParse(raw, out int value))
        {
            throw new InvalidDataException(
                $"SubTexture '{(string?)element.Attribute("name")}' has an " +
                $"invalid or missing '{attributeName}' value.");
        }

        return value;
    }

    private static string SanitizeFolderName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    public void Dispose()
    {
        foreach (Bitmap bitmap in _atlasCache.Values)
            bitmap.Dispose();

        _atlasCache.Clear();
    }

    private sealed record AtlasEntry(
        int IconId,
        string AtlasImagePath,
        int X,
        int Y,
        int Width,
        int Height,
        string SourceLayout);
}
