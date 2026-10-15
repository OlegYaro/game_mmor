using Core.Content;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// Ore and coal veins (P-21, research #10) and the start zone after the density cut, on the real data. The general vein
/// invariants (spacing, size, even split, stock range of the data) run on every world in <see cref="WorldInvariants"/>.
/// </summary>
public sealed class VeinTests
{
    // Owner decision: a vein as a whole holds 5 to 10 times the average stock of a normal node of its resource.
    private const int MinTimesNode = 5;
    private const int MaxTimesNode = 10;

    // Seeds of the start distribution run (research #10: check 40+ seeds after raising the thresholds).
    private const int StartSeedCount = 40;

    private static WorldGenParamsDef P => WorldGenTestData.Params;

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-WORLD-08")]
    public void RealDataFollowsTheOwnerDecisions()
    {
        VeinParams v = P.Resources.Veins;

        // Veins only in the mountains at height 2+, ore and coal, 5–10 times an average node in whole.
        Assert.Equal([("mountain", 2)], v.Biomes.Select(b => (b.Biome, b.MinHeight)));
        Assert.Equal(["ore", "coal"], v.Kinds.Select(k => k.Kind));
        Assert.All(v.Kinds, k => Assert.Equal((MinTimesNode * 1000, MaxTimesNode * 1000), (k.StockMultiplierMinPermille, k.StockMultiplierMaxPermille)));
        Assert.Equal(["mountain"], P.Resources.Coal.Biomes.Select(b => b.Biome));

        // Start thresholds raised with the density cut (research #10, section 6).
        Assert.Equal((400, 60), (P.Start.MinTreeTiles, P.Start.MinStoneTiles));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-04")]
    [MemberData(nameof(WorldGenTestData.InvariantSeeds), MemberType = typeof(WorldGenTestData))]
    public void EveryVeinHoldsFiveToTenAverageNodesInTheMountains(int seed)
    {
        WorldMap world = WorldGenTestData.InvariantWorld(seed);
        int half = P.Settlements.SiteSize / 2;
        foreach (Vein vein in world.Veins)
        {
            ClusterResourceParams node = vein.Kind == ResourceKind.OreVein ? P.Resources.Ore : P.Resources.Coal;
            int average = (node.StockMin + node.StockMax) / 2;
            Assert.InRange(vein.Stock, MinTimesNode * average, MaxTimesNode * average);
            Assert.DoesNotContain(world.SettlementSites, s => SiteHoldsVeinTile(world, s, half, vein));
        }

        for (int i = 0; i < world.TileCount; i++)
        {
            if (WorldNames.IsVein(world.ResourceKinds[i]))
            {
                Assert.Equal(Biome.Mountain, world.Terrain.Biomes[i]);
                Assert.True(world.Terrain.Heights[i] >= 2, $"Vein tile {i} below height 2.");
                Assert.InRange(world.ResourceAmounts[i], 1, ushort.MaxValue);
            }
        }
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void VeinsOfBothKindsAppear()
    {
        // About 4 veins per 1000² tiles with the research defaults (cell 96, chance 350‰ in the mountains): 0–3 on a
        // 512² map (23 on seeds 1–20), 4–6 on 1024², 15–16 on 2048².
        List<Vein> veins = [.. Enumerable.Range(1, WorldGenTestData.InvariantSeedCount).SelectMany(s => WorldGenTestData.InvariantWorld(s).Veins)];
        Assert.True(veins.Count >= WorldGenTestData.InvariantSeedCount / 2, $"Only {veins.Count} veins on {WorldGenTestData.InvariantSeedCount} maps.");
        Assert.Contains(veins, v => v.Kind == ResourceKind.OreVein);
        Assert.Contains(veins, v => v.Kind == ResourceKind.CoalVein);
        Assert.All(WorldGenTestData.GoldenSeeds, seed => Assert.NotEmpty(WorldGenTestData.LargeWorld((int)seed).Veins));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void SameSeedGivesTheSameVeins()
    {
        WorldMap cached = WorldGenTestData.GoldenWorld(2);
        WorldMap again = WorldGenTestData.GenerateWorld(cached.Seed, cached.Size);

        Assert.NotEmpty(again.Veins);
        Assert.Equal(cached.Veins, again.Veins);
        Assert.True(cached.ResourceKinds.SequenceEqual(again.ResourceKinds));
        Assert.True(cached.ResourceAmounts.SequenceEqual(again.ResourceAmounts));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-WORLD-07")]
    [Trait("Req", "FR-WORLD-09")]
    public void VeinsOnFlatLandKeepTheirStockUnderPointsAndSites()
    {
        // Veins everywhere on land: points of interest and settlement footprints, which clear resources, must avoid them.
        WorldGenParamsDef p = P with
        {
            Resources = P.Resources with
            {
                Veins = P.Resources.Veins with
                {
                    CellSize = 32,
                    MinSpacing = 24,
                    Biomes = [.. BiomeNames.Generated.Where(b => b != Biome.Water).Select(b => new VeinBiomeDef(BiomeNames.ToName(b), 1000, 0))],
                },
            },
        };
        WorldMap world = WorldGenTestData.GenerateWorld(4, WorldGenTestData.InvariantSize, p);

        Assert.True(world.Veins.Count >= 100, $"Only {world.Veins.Count} veins.");
        Assert.Contains(world.Veins, v => world.Terrain.GetBiome(v.X, v.Y) == Biome.Meadow);
        Assert.NotEmpty(world.SettlementSites);
        Assert.NotEmpty(world.PointsOfInterest);
        WorldInvariants.AssertAll(world, p);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-08")]
    public void StartsOnFortySeedsAreRarelyRelaxedAndKeepTheZoneStocked()
    {
        StartParams p = P.Start;
        List<WorldMap> worlds = [.. Enumerable.Range(1, StartSeedCount).Select(s => s <= WorldGenTestData.InvariantSeedCount
            ? WorldGenTestData.InvariantWorld(s)
            : WorldGenTestData.GenerateWorld((ulong)s, WorldGenTestData.InvariantSize))];
        List<int> trees = [];
        List<int> stone = [];
        foreach (WorldMap world in worlds)
        {
            StartPoint s = world.Start;
            int t = WorldInvariants.CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => world.ResourceKinds[i] == ResourceKind.Tree);
            int st = WorldInvariants.CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => world.ResourceKinds[i] == ResourceKind.Stone);
            if (world.StartRelaxation != StartRelaxation.Fallback)
            {
                Assert.True(t >= p.MinTreeTiles, $"Seed {world.Seed}: {t} trees in the start zone.");
                Assert.True(st >= p.MinStoneTiles, $"Seed {world.Seed}: {st} stone tiles in the start zone.");
            }

            trees.Add(t);
            stone.Add(st);
        }

        // Research #10: at most 5 % relaxed starts; a typical zone holds at least twice the thresholds (measured on
        // seeds 1–40: no relaxed start, median 1007 trees and 207 stone tiles over 80 starts at 512² and 1024²).
        int relaxed = worlds.Count(w => w.StartRelaxation != StartRelaxation.None);
        Assert.True(relaxed * 100 <= worlds.Count * 5, $"{relaxed} of {worlds.Count} starts relaxed.");
        trees.Sort();
        stone.Sort();
        Assert.True(trees[trees.Count / 2] >= 2 * p.MinTreeTiles, $"Median {trees[trees.Count / 2]} trees in the start zone.");
        Assert.True(stone[stone.Count / 2] >= 2 * p.MinStoneTiles, $"Median {stone[stone.Count / 2]} stone tiles in the start zone.");
    }

    // True when a tile of the footprint is a tile of the vein (its kind, within its radius).
    private static bool SiteHoldsVeinTile(WorldMap world, SettlementSite site, int half, Vein vein)
    {
        for (int y = site.Y - half; y <= site.Y + half; y++)
        {
            for (int x = site.X - half; x <= site.X + half; x++)
            {
                if (WorldInvariants.Distance2(vein.X, vein.Y, x, y) <= (long)vein.Radius * vein.Radius && world.GetResource(x, y) == vein.Kind)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
