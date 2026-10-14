using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>Invariants of the terrain stage on the golden maps (512², seeds 1–3) and on other seeds and sizes.</summary>
public sealed class TerrainInvariantTests
{
    // Checkerboard residue after smoothing: land tiles that share their biome with no orthogonal land neighbour.
    // Measured on the golden seeds: 0–3 of ~183 000 land tiles at 512² (2–10 on the former 64x32 grid's 512² maps, whose
    // geography the 1024² maps now cover with 3–9 of ~734 000); the threshold leaves room for tuning.
    private const int MaxIsolatedPerMille = 1;

    [Theory]
    [Trait("Req", "FR-WORLD-01")]
    [Trait("Req", "FR-WORLD-03")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void GoldenMapsSatisfyStructuralInvariants(int index) => TerrainInvariants.AssertAll(WorldGenTestData.Golden(index));

    [Theory]
    [Trait("Req", "FR-WORLD-03")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void EveryBiomeIsPresentWithAMeaningfulShare(int index)
    {
        TerrainMap map = WorldGenTestData.Golden(index);
        int[] counts = map.CountBiomes();
        int land = TerrainInvariants.CountLand(map);

        Assert.Equal(0, counts[(int)Biome.None]);
        Assert.InRange(counts[(int)Biome.Water] * 1000L / map.TileCount, 200, 450);
        foreach (Biome biome in BiomeNames.Land)
        {
            Assert.True(counts[(int)biome] * 1000L / land >= 50, $"{biome} covers only {counts[(int)biome]} of {land} land tiles.");
        }
    }

    [Theory]
    [Trait("Req", "FR-WORLD-03")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void AllHeightStepsArePresentOnLand(int index)
    {
        TerrainMap map = WorldGenTestData.Golden(index);
        int[] steps = new int[TerrainMap.MaxHeight + 1];
        for (int i = 0; i < map.TileCount; i++)
        {
            if ((map.Features[i] & TileFeatures.Water) == 0)
            {
                steps[map.Heights[i]]++;
            }
        }

        Assert.All(steps, count => Assert.True(count > map.TileCount / 50));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-03")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void TemperatureFallsWithHeight(int index)
    {
        TerrainMap map = WorldGenTestData.Golden(index);
        long[] sum = new long[TerrainMap.MaxHeight + 1];
        long[] count = new long[TerrainMap.MaxHeight + 1];
        for (int i = 0; i < map.TileCount; i++)
        {
            if ((map.Features[i] & TileFeatures.Water) == 0)
            {
                sum[map.Heights[i]] += map.Temperatures[i];
                count[map.Heights[i]]++;
            }
        }

        long[] mean = sum.Zip(count, (s, c) => s / c).ToArray();
        for (int step = 1; step <= TerrainMap.MaxHeight; step++)
        {
            Assert.True(mean[step] < mean[step - 1], $"Mean temperature of step {step} ({mean[step]}) is not below step {step - 1} ({mean[step - 1]}).");
        }

        Assert.True(mean[0] - mean[TerrainMap.MaxHeight] >= 200, $"Height barely affects temperature: {string.Join(", ", mean)}.");
    }

    // Checked at 1024², the geography of the former 512² golden maps (P-07): a 512² map on the 32x16 grid covers only
    // the former 256² area, where lakes are rare (none on about half of the seeds). River tiles: 300 on the former grid,
    // twice that now because a river of the same length crosses twice as many tiles.
    [Theory]
    [Trait("Req", "FR-WORLD-03")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LargeMapsHaveRiversAndLakes(int seed)
    {
        TerrainMap map = WorldGenTestData.LargeWorld(seed).Terrain;
        int rivers = 0;
        int lakes = 0;
        foreach (TileFeatures features in map.Features)
        {
            rivers += (features & TileFeatures.River) != 0 ? 1 : 0;
            lakes += (features & TileFeatures.Lake) != 0 ? 1 : 0;
        }

        Assert.True(rivers >= 600, $"Only {rivers} river tiles.");
        Assert.True(lakes > 0);
    }

    [Theory]
    [Trait("Req", "FR-WORLD-03")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void NoCheckerboardResidueAfterSmoothing(int index)
    {
        TerrainMap map = WorldGenTestData.Golden(index);
        int land = TerrainInvariants.CountLand(map);

        int isolated = TerrainInvariants.CountIsolatedBiomeTiles(map);

        Assert.True(isolated * 1000L <= land * MaxIsolatedPerMille, $"{isolated} isolated biome tiles of {land} land tiles.");
    }

    [Fact]
    [Trait("Req", "FR-WORLD-03")]
    public void SmoothingRemovesIsolatedTiles()
    {
        var unsmoothed = WorldGenTestData.Params with
        {
            Height = WorldGenTestData.Params.Height with { SmoothingPasses = 0 },
            Biomes = WorldGenTestData.Params.Biomes with { SmoothingPasses = 0 },
        };
        // 1024²: on the 32x16 grid (P-07) the noise is smooth at tile scale, so a 256² or 512² map has 0–2 isolated tiles
        // even unsmoothed; seed 1 at 1024² has 8 unsmoothed and 3 smoothed.
        TerrainMap raw = WorldGenerator.GenerateTerrain(new WorldGenRequest(1, WorldGenTestData.LargeSize, unsmoothed));
        TerrainMap smooth = WorldGenTestData.LargeWorld(1).Terrain;

        int before = TerrainInvariants.CountIsolatedBiomeTiles(raw);
        int after = TerrainInvariants.CountIsolatedBiomeTiles(smooth);

        Assert.True(after < before, $"Smoothing did not help: {before} -> {after}.");
        TerrainInvariants.AssertAll(raw);
    }

    [Theory]
    [Trait("Req", "FR-WORLD-01")]
    [InlineData(11UL, 64)]
    [InlineData(12UL, 96)]
    [InlineData(ulong.MaxValue, 128)]
    [InlineData(0UL, 160)]
    public void OtherSeedsAndSizesSatisfyInvariants(ulong seed, int size) =>
        TerrainInvariants.AssertAll(WorldGenTestData.Generate(seed, size));
}
