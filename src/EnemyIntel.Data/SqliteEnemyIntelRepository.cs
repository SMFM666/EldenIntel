using System.Globalization;
using System.Text.Json;
using EnemyIntel.Core;
using Microsoft.Data.Sqlite;

namespace EnemyIntel.Data;

public sealed class SqliteEnemyIntelRepository(string databasePath, string sourceDatabaseDirectory) : IEnemyIntelRepository
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly string _sourceDirectory = Path.GetFullPath(sourceDatabaseDirectory);
    private readonly Lazy<IReadOnlyDictionary<(long ItemId, string Category), CatalogItemDescription>> _itemDescriptions =
        new(() => LoadItemDescriptions(Path.GetFullPath(sourceDatabaseDirectory)));
    private readonly Lazy<IReadOnlyDictionary<(long ItemId, string Category), string>> _categoryItemNames =
        new(() => LoadCategoryItemNames(Path.GetFullPath(sourceDatabaseDirectory)));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecuteAsync(connection, Schema, cancellationToken);

        var existing = await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM metadata WHERE key='source_signature'", cancellationToken);
        var signature = BuildSourceSignature();
        var current = existing == 0 ? null : await ScalarAsync<string>(connection, "SELECT value FROM metadata WHERE key='source_signature'", cancellationToken);
        if (string.Equals(current, signature, StringComparison.Ordinal)) return;

        await ImportAsync(connection, signature, cancellationToken);
    }

    public async Task<string?> FindEnemyNameAsync(int paramId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM enemies WHERE param_id=$id";
        command.Parameters.AddWithValue("$id", paramId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<IReadOnlyList<EnemyDrop>> FindDropsAsync(int paramId, CancellationToken cancellationToken = default)
    {
        var result = new List<EnemyDrop>();
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id,item_name,category,quantity,chance_percent FROM drops WHERE param_id=$id ORDER BY slot,item_slot";
        command.Parameters.AddWithValue("$id", paramId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = reader.GetInt64(0);
            var category = reader.GetString(2);
            var importedName = reader.GetString(1);
            var name = _categoryItemNames.Value.TryGetValue((itemId, category), out var categoryName)
                ? categoryName
                : importedName;
            result.Add(new EnemyDrop(itemId, name, category, reader.GetInt32(3), reader.GetDouble(4), FindItemImage(itemId, category), FindItemDescription(itemId, category)));
        }
        return result;
    }

    public async Task<IReadOnlyList<EquipmentSlot>> FindEquipmentAsync(int paramId, CancellationToken cancellationToken = default)
    {
        var result = new List<EquipmentSlot>();
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT slot,item_id,item_name,category FROM equipment WHERE param_id=$id ORDER BY sort_order";
        command.Parameters.AddWithValue("$id", paramId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetString(0);
            var itemId = reader.GetInt64(1);
            var name = reader.GetString(2);
            var category = reader.GetString(3);
            result.Add(new EquipmentSlot(slot, itemId, name, category, FindItemImage(itemId, category), FindItemDescription(itemId, category)));
        }
        return result;
    }

    public async Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var enemies = await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM enemies", cancellationToken);
        var items = await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM items", cancellationToken);
        var drops = await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM drops", cancellationToken);
        var built = await ScalarAsync<string>(connection, "SELECT value FROM metadata WHERE key='built_at'", cancellationToken);
        return new DatabaseStats((int)enemies, (int)items, (int)drops, DateTimeOffset.Parse(built, CultureInfo.InvariantCulture));
    }

    private async Task ImportAsync(SqliteConnection connection, string signature, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await ExecuteAsync(connection, "DELETE FROM drops; DELETE FROM equipment; DELETE FROM enemies; DELETE FROM items; DELETE FROM metadata;", token, transaction);
        await ImportTsvAsync(connection, Path.Combine(_sourceDirectory, "enemies.tsv"), "INSERT OR REPLACE INTO enemies(param_id,name,hp) VALUES($a,$b,$c)", 3, transaction, token);
        await ImportTsvAsync(connection, Path.Combine(_sourceDirectory, "items.tsv"), "INSERT OR REPLACE INTO items(item_id,category,name,icon_id,image_path) VALUES($a,$b,$c,$d,$e)", 5, transaction, token);
        await ImportDropsAsync(connection, Path.Combine(_sourceDirectory, "enemy_rewards.json"), transaction, token);
        await ImportEquipmentAsync(connection, Path.Combine(_sourceDirectory, "enemy_visual_loadouts.json"), transaction, token);
        await ImportEquipmentAsync(connection, Path.Combine(_sourceDirectory, "enemy_loadouts.json"), transaction, token);
        await SetMetadataAsync(connection, "source_signature", signature, transaction, token);
        await SetMetadataAsync(connection, "built_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), transaction, token);
        await transaction.CommitAsync(token);
    }

    private static async Task ImportTsvAsync(SqliteConnection connection, string path, string sql, int columns, SqliteTransaction transaction, CancellationToken token)
    {
        using var reader = new StreamReader(path);
        _ = await reader.ReadLineAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var i = 0; i < columns; i++) command.Parameters.Add(new SqliteParameter($"${(char)('a' + i)}", string.Empty));
        while (await reader.ReadLineAsync(token) is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length < columns || !long.TryParse(fields[0], out var id)) continue;
            command.Parameters[0].Value = id;
            if (columns == 3)
            {
                command.Parameters[1].Value = fields[1];
                command.Parameters[2].Value = int.TryParse(fields[4], out var hp) ? hp : 0;
            }
            else
            {
                command.Parameters[1].Value = fields[1];
                command.Parameters[2].Value = fields[2];
                command.Parameters[3].Value = int.TryParse(fields[3], out var icon) ? icon : 0;
                command.Parameters[4].Value = fields[4];
            }
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task ImportDropsAsync(SqliteConnection connection, string path, SqliteTransaction transaction, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO drops(param_id,slot,item_slot,item_id,item_name,category,quantity,chance_percent) VALUES($p,$s,$is,$i,$n,$c,$q,$ch)";
        foreach (var name in new[] { "$p", "$s", "$is", "$i", "$n", "$c", "$q", "$ch" }) command.Parameters.Add(new SqliteParameter(name, string.Empty));
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var paramId = entry.GetProperty("paramId").GetInt32();
            foreach (var drop in entry.GetProperty("drops").EnumerateArray())
            {
                command.Parameters[0].Value = paramId;
                command.Parameters[1].Value = drop.GetProperty("slot").GetInt32();
                command.Parameters[2].Value = drop.GetProperty("itemSlot").GetInt32();
                command.Parameters[3].Value = drop.GetProperty("itemId").GetInt64();
                command.Parameters[4].Value = drop.GetProperty("itemName").GetString() ?? "Unknown item";
                command.Parameters[5].Value = drop.GetProperty("category").GetString() ?? "Unknown";
                command.Parameters[6].Value = drop.GetProperty("quantity").GetInt32();
                command.Parameters[7].Value = drop.GetProperty("chancePercent").GetDouble();
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }

    private static async Task ImportEquipmentAsync(SqliteConnection connection, string path, SqliteTransaction transaction, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO equipment(param_id,slot,item_id,item_name,category,sort_order) VALUES($p,$s,$i,$n,$c,$o)";
        foreach (var name in new[] { "$p", "$s", "$i", "$n", "$c", "$o" }) command.Parameters.Add(new SqliteParameter(name, string.Empty));
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var paramId = entry.GetProperty("paramId").GetInt32();
            var order = 0;
            foreach (var slot in entry.GetProperty("slots").EnumerateArray())
            {
                var slotName = slot.GetProperty("slot").GetString() ?? "Unknown";
                var itemId = slot.GetProperty("itemId").GetInt64();
                var itemName = slot.TryGetProperty("note", out var note) ? note.GetString() : null;
                command.Parameters[0].Value = paramId;
                command.Parameters[1].Value = slotName;
                command.Parameters[2].Value = itemId;
                command.Parameters[3].Value = string.IsNullOrWhiteSpace(itemName) ? $"Item {itemId}" : itemName;
                command.Parameters[4].Value = CategoryForSlot(slotName);
                command.Parameters[5].Value = order++;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }

    private static string CategoryForSlot(string slot) =>
        slot.Contains("Helmet", StringComparison.OrdinalIgnoreCase) || slot.Contains("Armor", StringComparison.OrdinalIgnoreCase) || slot.Contains("Gauntlet", StringComparison.OrdinalIgnoreCase) || slot.Contains("Legging", StringComparison.OrdinalIgnoreCase) ? "Armor" :
        slot.Contains("Talisman", StringComparison.OrdinalIgnoreCase) || slot.Contains("Accessory", StringComparison.OrdinalIgnoreCase) ? "Accessories" :
        slot.Contains("Item", StringComparison.OrdinalIgnoreCase) || slot.Contains("Pouch", StringComparison.OrdinalIgnoreCase) ? "Goods" : "Weapons";

    private string? FindItemImage(long itemId, string category)
    {
        foreach (var candidateId in ItemMediaCandidates(itemId, category))
        {
            var portableItemDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Items",
                category,
                candidateId.ToString(CultureInfo.InvariantCulture));
            foreach (var file in new[] { "image.png", "icon.png" })
            {
                var portablePath = Path.Combine(portableItemDirectory, file);
                if (File.Exists(portablePath)) return portablePath;
            }

            var root = Directory.GetParent(_sourceDirectory)!.FullName;
            foreach (var file in new[] { "image.png", "icon.png" })
            {
                var path = Path.Combine(root, "Items", category, candidateId.ToString(CultureInfo.InvariantCulture), file);
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    private string FindItemDescription(long itemId, string category)
    {
        if (_itemDescriptions.Value.TryGetValue((itemId, category), out var catalogItem))
        {
            var catalogDescription = BuildItemDescription(catalogItem.Description, catalogItem.Effect);
            if (!IsPlaceholderDescription(catalogDescription)) return catalogDescription;
        }

        var root = Directory.GetParent(_sourceDirectory)!.FullName;
        var path = Path.Combine(root, "Items", category, itemId.ToString(CultureInfo.InvariantCulture), "description.txt");
        if (!File.Exists(path)) return "Description unavailable.";
        var description = File.ReadAllText(path).Trim();
        return IsPlaceholderDescription(description) ? "Description unavailable." : description;
    }

    private static IReadOnlyDictionary<(long ItemId, string Category), CatalogItemDescription> LoadItemDescriptions(
        string databaseDirectory)
    {
        var descriptions = new Dictionary<(long ItemId, string Category), CatalogItemDescription>();
        var path = Path.Combine(databaseDirectory, "item_descriptions.json");

        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)) continue;
                    AddCatalogDescription(descriptions, itemId, property.Value);
                }
            }

            var overridesPath = Path.Combine(databaseDirectory, "item_description_overrides.json");
            if (File.Exists(overridesPath))
            {
                using var overrides = JsonDocument.Parse(File.ReadAllText(overridesPath));
                foreach (var item in overrides.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("itemId", out var id) || !id.TryGetInt64(out var itemId)) continue;
                    AddCatalogDescription(descriptions, itemId, item);
                }
            }
        }
        catch (JsonException)
        {
            // Preserve any valid entries loaded before a malformed optional override.
        }

        return descriptions;
    }

    private static void AddCatalogDescription(
        IDictionary<(long ItemId, string Category), CatalogItemDescription> descriptions,
        long itemId,
        JsonElement item)
    {
        var category = item.TryGetProperty("category", out var categoryProperty)
            ? categoryProperty.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(category)) return;

        descriptions[(itemId, category)] = new CatalogItemDescription(
            item.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty,
            item.TryGetProperty("effect", out var effect) ? effect.GetString() ?? string.Empty : string.Empty);
    }

    private static IEnumerable<long> ItemMediaCandidates(long itemId, string category)
    {
        yield return itemId;
        if (!category.Equals("Weapons", StringComparison.OrdinalIgnoreCase)) yield break;

        var familyBase = itemId - itemId % 10_000;
        for (var offset = 0L; offset <= 1_200; offset += 100)
        {
            var candidate = familyBase + offset;
            if (candidate != itemId) yield return candidate;
        }
    }

    private static IReadOnlyDictionary<(long ItemId, string Category), string> LoadCategoryItemNames(
        string databaseDirectory)
    {
        var result = new Dictionary<(long ItemId, string Category), string>();
        foreach (var (fileName, category) in new[]
                 {
                     ("EquipParamGoods.txt", "Goods"),
                     ("EquipParamWeapon.txt", "Weapons"),
                     ("EquipParamProtector.txt", "Armor"),
                     ("EquipParamAccessory.txt", "Accessories")
                 })
        {
            var path = Path.Combine(databaseDirectory, fileName);
            if (!File.Exists(path)) continue;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var separator = line.IndexOf(";;", StringComparison.Ordinal);
                if (separator <= 0 ||
                    !long.TryParse(line.AsSpan(0, separator), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var itemId)) continue;

                var name = line[(separator + 2)..].Trim();
                if (!string.IsNullOrWhiteSpace(name)) result[(itemId, category)] = name;
            }
        }

        return result;
    }

    private static string BuildItemDescription(string description, string effect)
    {
        description = description.Trim();
        effect = effect.Trim();
        if (string.IsNullOrWhiteSpace(effect)) return description;
        if (string.IsNullOrWhiteSpace(description)) return effect;
        return description.Contains(effect, StringComparison.OrdinalIgnoreCase)
            ? description
            : $"{effect}\n\n{description}";
    }

    private static bool IsPlaceholderDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var normalized = value.Trim();
        return normalized.Equals("Description unavailable.", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Description pending extraction.", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Pending extraction.", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CatalogItemDescription(string Description, string Effect);

    private string BuildSourceSignature() => string.Join('|', new[] { "enemies.tsv", "items.tsv", "enemy_rewards.json", "enemy_visual_loadouts.json", "enemy_loadouts.json", "item_descriptions.json", "item_description_overrides.json", "EquipParamGoods.txt", "EquipParamWeapon.txt", "EquipParamProtector.txt", "EquipParamAccessory.txt" }.Select(name => new FileInfo(Path.Combine(_sourceDirectory, name))).Select(info => $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"));
    private SqliteConnection Open() => new($"Data Source={_databasePath}");
    private static async Task ExecuteAsync(SqliteConnection c, string sql, CancellationToken t, SqliteTransaction? x = null) { await using var cmd = c.CreateCommand(); cmd.Transaction = x; cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(t); }
    private static async Task<T> ScalarAsync<T>(SqliteConnection c, string sql, CancellationToken t) { await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return (T)(await cmd.ExecuteScalarAsync(t))!; }
    private static async Task SetMetadataAsync(SqliteConnection c, string key, string value, SqliteTransaction x, CancellationToken t) { await using var cmd = c.CreateCommand(); cmd.Transaction=x; cmd.CommandText="INSERT INTO metadata(key,value) VALUES($k,$v)"; cmd.Parameters.AddWithValue("$k",key); cmd.Parameters.AddWithValue("$v",value); await cmd.ExecuteNonQueryAsync(t); }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS enemies(param_id INTEGER PRIMARY KEY,name TEXT NOT NULL,hp INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS items(item_id INTEGER PRIMARY KEY,category TEXT NOT NULL,name TEXT NOT NULL,icon_id INTEGER NOT NULL,image_path TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS drops(id INTEGER PRIMARY KEY,param_id INTEGER NOT NULL,slot INTEGER NOT NULL,item_slot INTEGER NOT NULL,item_id INTEGER NOT NULL,item_name TEXT NOT NULL,category TEXT NOT NULL,quantity INTEGER NOT NULL,chance_percent REAL NOT NULL);
        CREATE TABLE IF NOT EXISTS equipment(param_id INTEGER NOT NULL,slot TEXT NOT NULL,item_id INTEGER NOT NULL,item_name TEXT NOT NULL,category TEXT NOT NULL,sort_order INTEGER NOT NULL,PRIMARY KEY(param_id,slot));
        CREATE INDEX IF NOT EXISTS ix_drops_param_id ON drops(param_id);
        """;
}
