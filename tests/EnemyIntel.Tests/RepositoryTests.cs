using EnemyIntel.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EnemyIntel.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public async Task ImportsLegacyDataIntoQueryableDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"enemy-intel-{Guid.NewGuid():N}.db");
        try
        {
            var sourceDatabase = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "EnemyIntel.App", "assets", "Database"));
            var repository = new SqliteEnemyIntelRepository(path, sourceDatabase);
            await repository.InitializeAsync();
            var stats = await repository.GetStatsAsync();
            Assert.True(stats.EnemyCount > 1000);
            Assert.True(stats.ItemCount > 1000);
            Assert.False(string.IsNullOrWhiteSpace(await repository.FindEnemyNameAsync(32510010)));
            var equipment = await repository.FindEquipmentAsync(43111110);
            Assert.Collection(
                equipment,
                item => Assert.Equal(("Primary Right", 2040000L, "Lordsworn's Straight Sword"), (item.Slot, item.ItemId, item.Name)),
                item => Assert.Equal(("Primary Left", 24000000L, "Torch"), (item.Slot, item.ItemId, item.Name)),
                item => Assert.Equal(("Helmet", 1700000L, "Godrick Soldier Helm"), (item.Slot, item.ItemId, item.Name)),
                item => Assert.Equal(("Armor", 1700100L, "Tree-and-Beast Surcoat"), (item.Slot, item.ItemId, item.Name)),
                item => Assert.Equal(("Gauntlets", 1700200L, "Godrick Soldier Gauntlets"), (item.Slot, item.ItemId, item.Name)),
                item => Assert.Equal(("Leggings", 1700300L, "Godrick Soldier Greaves"), (item.Slot, item.ItemId, item.Name)));
            Assert.Empty(await repository.FindEquipmentAsync(54900000));

            var alteredBanishedKnightDrops = await repository.FindDropsAsync(30100000);
            Assert.Contains(
                alteredBanishedKnightDrops,
                drop => drop is { ItemId: 201000, Category: "Armor", Name: "Banished Knight Helm (Altered)" });
            Assert.DoesNotContain(
                alteredBanishedKnightDrops,
                drop => drop.Category == "Armor" && drop.Name == "Banished Knight Oleg");

            var swordSoldierDrops = await repository.FindDropsAsync(43111110);
            var shieldSoldierDrops = await repository.FindDropsAsync(43114010);
            Assert.Contains(swordSoldierDrops, drop => drop.Name == "Lordsworn's Straight Sword");
            Assert.DoesNotContain(shieldSoldierDrops, drop => drop.Name == "Lordsworn's Straight Sword");
            Assert.Contains(shieldSoldierDrops, drop => drop.Name == "Lordsworn's Shield");

            var daggerDrops = await repository.FindDropsAsync(43710000);
            var dagger = Assert.Single(daggerDrops, drop => drop is
                { ItemId: 1000000, Category: "Weapons", Name: "Dagger" });
            Assert.NotNull(dagger.ImagePath);
            Assert.EndsWith(
                Path.Combine("Weapons", "1000400", "icon.png"),
                dagger.ImagePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Quickstep", dagger.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Haima", dagger.Description, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
        }
    }
}
