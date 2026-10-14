using Core.Content;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// The world keeps its look across map sizes on the 32×16 grid (P-07): water and biome shares, river source density,
/// point-of-interest density (one per 6400 land tiles = one per 40x40 tiles of the former 64x32 grid), the B1 start
/// zone without relaxation and the evil distance of 240. Checked on 512² (many seeds), 1024² (seeds 1–3) and 2048²
/// (seeds 1–2, Benchmark-tagged: about 7 s per map in Debug).
/// </summary>
/// <remarks>
/// Bounds come from seeds 1–20 at 512² and 1–8 at 1024² after the rescale (water 300‰ by construction; meadow
/// 185–278‰, forest 264–420‰, mountain 149–196‰, wasteland 120–294‰ of land; at most 0.02‰ isolated biome tiles), with
/// room for tuning.
/// </remarks>
public sealed class ScaleInvariantTests
{
    private static WorldGenParamsDef P => WorldGenTestData.Params;

    [Theory]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Req", "FR-WORLD-03")]
    [Trait("Req", "FR-WORLD-07")]
    [MemberData(nameof(WorldGenTestData.InvariantSeeds), MemberType = typeof(WorldGenTestData))]
    public void SharesAndDensitiesHoldAt512(int seed) => AssertScale(WorldGenTestData.InvariantWorld(seed));

    [Theory]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Req", "FR-WORLD-03")]
    [Trait("Req", "FR-WORLD-07")]
    [Trait("Req", "FR-WORLD-08")]
    [Trait("Req", "FR-WORLD-09")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LargeMapsKeepTheLookAndAllInvariants(int seed) => AssertEverything(WorldGenTestData.LargeWorld(seed));

    [Theory]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Req", "FR-WORLD-03")]
    [Trait("Req", "FR-WORLD-07")]
    [Trait("Req", "FR-WORLD-08")]
    [Trait("Req", "FR-WORLD-09")]
    [Trait("Category", "Benchmark")]
    [InlineData(1)]
    [InlineData(2)]
    public void HugeMapsKeepTheLookAndAllInvariants(int seed) => AssertEverything(WorldGenTestData.HugeWorld(seed));

    [Fact]
    [Trait("Req", "FR-WORLD-08")]
    [Trait("Req", "FR-WORLD-09")]
    public void RealDataUsesTheScaledLengths()
    {
        // P-07: lengths in tiles double, areas quadruple against the values set on the 64x32 grid.
        Assert.Equal(80, P.Start.ZoneRadius);
        Assert.Equal(17, P.Settlements.SiteSize);
        Assert.True(P.Settlements.Factions.Single(f => f.Faction == "orc").MinStartDistance >= P.Start.ZoneRadius + P.Settlements.SiteSize);
        Assert.Equal(6400, P.PointsOfInterest.TilesPerPoi);
        FogBalanceDef fog = SimTestData.Real.Fog;
        Assert.Equal((24, 12, 16, 8, 32, 32), (fog.HeroDay, fog.HeroNight, fog.VillagerDay, fog.VillagerNight, fog.TowerDay, fog.TowerNight));
    }

    private static void AssertEverything(WorldMap world)
    {
        TerrainInvariants.AssertAll(world.Terrain);
        WorldInvariants.AssertAll(world, P);
        AssertScale(world);
    }

    private static void AssertScale(WorldMap world)
    {
        TerrainMap map = world.Terrain;
        int[] biomes = map.CountBiomes();
        int land = TerrainInvariants.CountLand(map);
        string where = $"seed {world.Seed} at {world.Size}²";

        Assert.InRange(biomes[(int)Biome.Water] * 1000L / map.TileCount, 280, 320);
        AssertShare(Biome.Meadow, 150, 350);
        AssertShare(Biome.Forest, 200, 500);
        AssertShare(Biome.Mountain, 100, 260);
        AssertShare(Biome.Wasteland, 80, 400);
        int isolated = TerrainInvariants.CountIsolatedBiomeTiles(map);
        Assert.True(isolated * 1000L <= land, $"{where}: {isolated} isolated biome tiles of {land} land tiles.");

        // Rivers: the source density per tile is the data's (a source a later river joins has an upstream tile, hence
        // the lower bound).
        long sourceTarget = Math.Max(1L, (long)P.Rivers.SourcesPerMillionTiles * map.TileCount / 1_000_000);
        Assert.InRange(CountRiverSources(map), sourceTarget * 3 / 4, sourceTarget);

        // Points of interest: the full target of one per TilesPerPoi land tiles.
        long poiTarget = (land + (P.PointsOfInterest.TilesPerPoi / 2)) / P.PointsOfInterest.TilesPerPoi;
        Assert.Equal(poiTarget, world.PointsOfInterest.Count);

        // B1 start without relaxation and every settlement site placed (no warnings at all).
        Assert.Equal(StartRelaxation.None, world.StartRelaxation);
        Assert.Empty(world.Warnings);

        void AssertShare(Biome biome, int min, int max)
        {
            long share = biomes[(int)biome] * 1000L / land;
            Assert.True(share >= min && share <= max, $"{where}: {biome} covers {share}‰ of land, expected {min}–{max}‰.");
        }
    }

    // River tiles without an upstream river tile.
    private static int CountRiverSources(TerrainMap map)
    {
        bool[] hasUpstream = new bool[map.TileCount];
        for (int i = 0; i < map.TileCount; i++)
        {
            if ((map.Features[i] & TileFeatures.River) != 0)
            {
                (int dx, int dy) = TerrainMap.Directions[map.RiverDirections[i]];
                hasUpstream[i + (dy * map.Size) + dx] = true;
            }
        }

        int sources = 0;
        for (int i = 0; i < map.TileCount; i++)
        {
            sources += (map.Features[i] & TileFeatures.River) != 0 && !hasUpstream[i] ? 1 : 0;
        }

        return sources;
    }
}
