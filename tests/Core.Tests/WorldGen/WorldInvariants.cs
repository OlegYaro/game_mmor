using Core.Content;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>Checks of the placement steps (ADR 0006 steps 5–8) that must hold on every generated world.</summary>
internal static class WorldInvariants
{
    /// <summary>
    /// The fixed FR-WORLD-09 distance, independent of the data (which must not go below it): 120 tiles of the former
    /// 64×32 grid, 240 tiles of the 32×16 grid (P-07).
    /// </summary>
    public const int EvilMinStartDistance = 240;

    public static void AssertAll(WorldMap world, WorldGenParamsDef p)
    {
        ResourcesOnlyOnOpenLandWithStock(world, p.Resources);
        ResourcesRespectTheirBiomes(world, p.Resources);
        PointsOfInterestAreValid(world, p.PointsOfInterest);
        StartIsValid(world, p.Start);
        SettlementSitesAreValid(world, p);
    }

    public static bool IsLand(WorldMap world, int i) => (world.Terrain.Features[i] & TileFeatures.Water) == 0;

    public static bool IsOpen(WorldMap world, int i) => (world.Terrain.Features[i] & (TileFeatures.Water | TileFeatures.River)) == 0;

    /// <summary>B2: land without a river whose 8 neighbours all have the same height.</summary>
    public static bool IsBuildable(WorldMap world, int x, int y)
    {
        TerrainMap t = world.Terrain;
        if (x < 1 || y < 1 || x > t.Size - 2 || y > t.Size - 2 || !IsOpen(world, t.Index(x, y)))
        {
            return false;
        }

        int h = t.GetHeight(x, y);
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (t.GetHeight(x + dx, y + dy) != h)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static long Distance2(int x0, int y0, int x1, int y1) => ((long)(x1 - x0) * (x1 - x0)) + ((long)(y1 - y0) * (y1 - y0));

    /// <summary>Tiles within <paramref name="radius"/> of (<paramref name="cx"/>, <paramref name="cy"/>) matching <paramref name="predicate"/> (by index).</summary>
    public static int CountInDisc(WorldMap world, int cx, int cy, int radius, Func<int, bool> predicate)
    {
        int n = 0;
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(world.Size - 1, cy + radius); y++)
        {
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(world.Size - 1, cx + radius); x++)
            {
                if (Distance2(cx, cy, x, y) <= (long)radius * radius && predicate((y * world.Size) + x))
                {
                    n++;
                }
            }
        }

        return n;
    }

    public static void ResourcesOnlyOnOpenLandWithStock(WorldMap world, ResourceParams p)
    {
        for (int i = 0; i < world.TileCount; i++)
        {
            ResourceKind kind = world.ResourceKinds[i];
            int amount = world.ResourceAmounts[i];
            if (kind == ResourceKind.None)
            {
                Assert.True(amount == 0, $"Tile {i}: stock {amount} without a resource.");
                continue;
            }

            Assert.True(IsOpen(world, i), $"Tile {i}: {kind} on water or a river.");
            (int min, int max) = kind switch
            {
                ResourceKind.Tree => (p.Trees.StockMin, p.Trees.StockMax),
                ResourceKind.Stone => (p.Stone.StockMin, p.Stone.StockMax),
                ResourceKind.Ore => (p.Ore.StockMin, p.Ore.StockMax),
                ResourceKind.BerryBush => (p.Berries.StockMin, p.Berries.StockMax),
                _ => throw new InvalidOperationException($"Tile {i}: unknown resource kind {kind}."),
            };
            Assert.InRange(amount, min, max);
        }
    }

    public static void ResourcesRespectTheirBiomes(WorldMap world, ResourceParams p)
    {
        HashSet<string> treeBiomes = p.Trees.Biomes.Select(b => b.Biome).ToHashSet(StringComparer.Ordinal);
        HashSet<string> stoneBiomes = p.Stone.Biomes.Select(b => b.Biome).ToHashSet(StringComparer.Ordinal);
        HashSet<string> oreBiomes = p.Ore.Biomes.Select(b => b.Biome).ToHashSet(StringComparer.Ordinal);
        HashSet<string> berryBiomes = p.Berries.Biomes.Select(b => b.Biome).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < world.TileCount; i++)
        {
            string biome = BiomeNames.ToName(world.Terrain.Biomes[i]);
            int height = world.Terrain.Heights[i];
            switch (world.ResourceKinds[i])
            {
                case ResourceKind.Tree:
                    Assert.Contains(biome, treeBiomes);
                    break;
                case ResourceKind.Stone:
                    Assert.Contains(biome, stoneBiomes);
                    Assert.True(height >= p.Stone.MinHeight);
                    break;
                case ResourceKind.Ore:
                    Assert.Contains(biome, oreBiomes);
                    Assert.True(height >= p.Ore.MinHeight, $"Ore on tile {i} at height {height}.");
                    break;
                case ResourceKind.BerryBush:
                    Assert.Contains(biome, berryBiomes);
                    break;
            }
        }
    }

    public static void PointsOfInterestAreValid(WorldMap world, PoiParams p)
    {
        IReadOnlyList<PointOfInterest> pois = world.PointsOfInterest;
        AssertSortedByRowThenColumn(pois.Select(q => (q.X, q.Y)).ToList());
        long spacing2 = (long)p.MinSpacing * p.MinSpacing;
        foreach (PointOfInterest poi in pois)
        {
            Assert.NotEqual(PoiKind.None, poi.Kind);
            Assert.True(IsBuildable(world, poi.X, poi.Y), $"{poi} is not on flat walkable land.");
            Assert.InRange(poi.X, p.EdgeMargin, world.Size - 1 - p.EdgeMargin);
            Assert.InRange(poi.Y, p.EdgeMargin, world.Size - 1 - p.EdgeMargin);
            Assert.Equal(ResourceKind.None, world.GetResource(poi.X, poi.Y));
        }

        for (int a = 0; a < pois.Count; a++)
        {
            for (int b = a + 1; b < pois.Count; b++)
            {
                Assert.True(Distance2(pois[a].X, pois[a].Y, pois[b].X, pois[b].Y) >= spacing2, $"{pois[a]} and {pois[b]} are too close.");
            }
        }
    }

    public static void StartIsValid(WorldMap world, StartParams p)
    {
        StartPoint s = world.Start;
        bool startWarning = world.Warnings.Any(w => w.StartsWith("Start:", StringComparison.Ordinal));
        Assert.Equal(world.StartRelaxation != StartRelaxation.None, startWarning);
        Assert.True(world.Terrain.Contains(s.X, s.Y));
        Assert.True(IsOpen(world, world.Terrain.Index(s.X, s.Y)), "The start is on water or a river.");
        if (world.StartRelaxation == StartRelaxation.Fallback)
        {
            return;
        }

        // FR-WORLD-08 / B1: forest, stone (and water unless relaxed with a warning) in the zone; flat building area.
        Assert.True(IsBuildable(world, s.X, s.Y));
        Assert.Equal(p.Biome, BiomeNames.ToName(world.Terrain.GetBiome(s.X, s.Y)));
        Assert.InRange(s.X, p.EdgeMargin, world.Size - 1 - p.EdgeMargin);
        Assert.InRange(s.Y, p.EdgeMargin, world.Size - 1 - p.EdgeMargin);
        Assert.True(CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => world.ResourceKinds[i] == ResourceKind.Tree) >= p.MinTreeTiles);
        Assert.True(CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => world.ResourceKinds[i] == ResourceKind.Stone) >= p.MinStoneTiles);
        if (world.StartRelaxation == StartRelaxation.None)
        {
            Assert.True(CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => !IsOpen(world, i)) >= p.MinWaterTiles);
        }

        int flat = CountInDisc(world, s.X, s.Y, p.FlatRadius, i => IsBuildable(world, i % world.Size, i / world.Size));
        int area = CountInDisc(world, s.X, s.Y, p.FlatRadius, _ => true);
        Assert.True(flat * 1000L >= (long)p.MinFlatPermille * area, $"Only {flat} of {area} tiles around the start are buildable.");
        Assert.All(world.PointsOfInterest, q => Assert.True(Distance2(s.X, s.Y, q.X, q.Y) > (long)p.PoiClearance * p.PoiClearance, $"{q} is inside the start clearance."));
    }

    public static void SettlementSitesAreValid(WorldMap world, WorldGenParamsDef p)
    {
        SettlementParams sp = p.Settlements;
        IReadOnlyList<SettlementSite> sites = world.SettlementSites;
        AssertSortedByRowThenColumn(sites.Select(q => (q.X, q.Y)).ToList());
        StartPoint s = world.Start;
        int half = sp.SiteSize / 2;
        long zone2 = (long)p.Start.ZoneRadius * p.Start.ZoneRadius;
        foreach (SettlementSite site in sites)
        {
            FactionSitesDef def = sp.Factions.Single(f => f.Faction == WorldNames.ToName(site.Faction));
            long d2 = Distance2(s.X, s.Y, site.X, site.Y);
            Assert.True(d2 >= (long)def.MinStartDistance * def.MinStartDistance, $"{site} is too close to the start {s}.");
            if (WorldNames.IsEvil(site.Faction))
            {
                // FR-WORLD-09: guaranteed by construction, whatever the start relaxation.
                Assert.True(d2 >= (long)EvilMinStartDistance * EvilMinStartDistance, $"Evil {site} is closer than {EvilMinStartDistance} to the start.");
            }

            for (int y = site.Y - half; y <= site.Y + half; y++)
            {
                for (int x = site.X - half; x <= site.X + half; x++)
                {
                    Assert.True(IsBuildable(world, x, y), $"Footprint tile ({x}, {y}) of {site} is not buildable.");
                    Assert.Equal(ResourceKind.None, world.GetResource(x, y));
                    Assert.True(Distance2(s.X, s.Y, x, y) > zone2, $"Footprint tile ({x}, {y}) of {site} overlaps the start zone.");
                }
            }

            Assert.DoesNotContain(world.PointsOfInterest, q =>
                Math.Abs(q.X - site.X) <= half + sp.PoiClearance && Math.Abs(q.Y - site.Y) <= half + sp.PoiClearance);
        }

        for (int a = 0; a < sites.Count; a++)
        {
            for (int b = a + 1; b < sites.Count; b++)
            {
                Assert.True(Distance2(sites[a].X, sites[a].Y, sites[b].X, sites[b].Y) >= (long)sp.MinSpacing * sp.MinSpacing);
            }
        }

        // Every shortfall is reported.
        foreach (Faction faction in WorldNames.Factions)
        {
            FactionSitesDef? def = sp.Factions.SingleOrDefault(f => f.Faction == WorldNames.ToName(faction));
            int target = def is null || def.PerMillionTiles == 0 ? 0 : (int)Math.Max(1L, (long)def.PerMillionTiles * world.TileCount / 1_000_000);
            int got = sites.Count(q => q.Faction == faction);
            Assert.True(got <= target);
            Assert.Equal(got < target, world.Warnings.Any(w => w.Contains($" {WorldNames.ToName(faction)} sites", StringComparison.Ordinal)));
        }
    }

    private static void AssertSortedByRowThenColumn(List<(int X, int Y)> points)
    {
        for (int k = 1; k < points.Count; k++)
        {
            (int x0, int y0) = points[k - 1];
            (int x1, int y1) = points[k];
            Assert.True(y0 < y1 || (y0 == y1 && x0 < x1), $"List not strictly sorted by (y, x) at {k}.");
        }
    }
}
