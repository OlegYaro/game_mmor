using Core.Content;
using Core.Determinism;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

public sealed class WorldGeneratorTests
{
    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void SameSeedTwiceGivesIdenticalTerrain()
    {
        TerrainMap first = WorldGenTestData.Generate(1, WorldGenTestData.GoldenSize);
        TerrainMap second = WorldGenTestData.Generate(1, WorldGenTestData.GoldenSize);

        Assert.Equal(first.ComputeHash(), second.ComputeHash());
        Assert.Equal(WorldGenTestData.Golden(0).ComputeHash(), first.ComputeHash());
        Assert.True(first.Heights.SequenceEqual(second.Heights));
        Assert.True(first.Elevations.SequenceEqual(second.Elevations));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void DifferentSeedsGiveDifferentTerrain()
    {
        ulong[] hashes = WorldGenTestData.AllGolden.Select(m => m.ComputeHash()).ToArray();

        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        TerrainMap a = WorldGenTestData.Golden(0);
        TerrainMap b = WorldGenTestData.Golden(1);
        int differentBiomes = 0;
        for (int i = 0; i < a.TileCount; i++)
        {
            differentBiomes += a.Biomes[i] != b.Biomes[i] ? 1 : 0;
        }

        // Not a one-tile difference: a different world.
        Assert.True(differentBiomes > a.TileCount / 4, $"Only {differentBiomes} tiles differ.");
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void HashCoversEveryLayerAndTheHeader()
    {
        TerrainMap map = WorldGenTestData.Generate(9, 64);
        var writer = new StateWriter();
        map.WriteState(writer);

        // Tag + version + seed + size + 4 byte layers + 3 ushort layers, each with an Int32 length prefix.
        int tiles = map.TileCount;
        Assert.Equal(4 + 7 + 4 + 8 + 4 + (7 * 4) + (4 * tiles) + (3 * 2 * tiles), writer.Length);
        Assert.Equal(StateHasher.HashBytes(writer.WrittenSpan), map.ComputeHash());
        Assert.NotEqual(map.ComputeHash(), WorldGenTestData.Generate(10, 64).ComputeHash());
        Assert.Throws<ArgumentNullException>(() => map.WriteState(null!));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-02")]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void SizeParameterIsRespected(int size)
    {
        TerrainMap map = WorldGenTestData.Generate(5, size);

        Assert.Equal(size, map.Size);
        Assert.Equal(size * size, map.TileCount);
        Assert.Equal(size / 32, map.ChunksPerSide);
        Assert.Equal(size * size, map.Heights.Length);
        Assert.Equal(size * size, map.Biomes.Length);
        Assert.Equal(size * size, map.Features.Length);
        Assert.Equal(size * size, map.RiverDirections.Length);
        Assert.Equal(size * size, map.Elevations.Length);
        Assert.Equal(size * size, map.Moistures.Length);
        Assert.Equal(size * size, map.Temperatures.Length);
        Assert.Equal(5UL, map.Seed);
        Assert.Equal(WorldGenerator.Version, map.Version);
        TerrainInvariants.AssertAll(map);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-02")]
    public void StandardSizesIncludeMvpAndV1Sizes()
    {
        Assert.Equal([512, 1024, 2048], WorldGenRequest.StandardSizes);
        Assert.All(WorldGenRequest.StandardSizes, s => new WorldGenRequest(1, s, WorldGenTestData.Params).Validate());
    }

    [Theory]
    [Trait("Req", "FR-WORLD-02")]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(4096)]
    public void InvalidSizesAreRejected(int size) =>
        Assert.Throws<ArgumentException>(() => WorldGenerator.GenerateTerrain(new WorldGenRequest(1, size, WorldGenTestData.Params)));

    [Theory]
    [Trait("Req", "FR-WORLD-01")]
    [InlineData(0)]
    [InlineData(1)] // v1 had no veins (P-21); only the current version is reproduced
    [InlineData(3)]
    public void OtherGeneratorVersionsAreNotSupported(int version)
    {
        Assert.False(WorldGenerator.IsSupportedVersion(version));
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => WorldGenerator.GenerateTerrain(new WorldGenRequest(1, 64, WorldGenTestData.Params, version)));
        Assert.Contains($"v{version}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => WorldGenerator.GenerateTerrain(null!));
        Assert.Throws<ArgumentNullException>(() => new WorldGenRequest(1, 64, null!).Validate());
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void SubSeedsDependOnSeedStepAndChunk()
    {
        ulong baseSeed = WorldGenSeeds.Derive(1, WorldGenStep.Height);

        Assert.Equal(baseSeed, WorldGenSeeds.Derive(1, WorldGenStep.Height, 0, 0));
        ulong[] variants =
        [
            baseSeed,
            WorldGenSeeds.Derive(2, WorldGenStep.Height),
            WorldGenSeeds.Derive(1, WorldGenStep.Climate),
            WorldGenSeeds.Derive(1, WorldGenStep.Height, 1, 0),
            WorldGenSeeds.Derive(1, WorldGenStep.Height, 0, 1),
            WorldGenSeeds.Derive(1, WorldGenStep.Height, -1, 0),
        ];
        Assert.Equal(variants.Length, variants.Distinct().Count());
        Assert.Equal(WorldGenSeeds.CreateRng(7, WorldGenStep.Resources, 3, 4).NextUInt64(),
            new Xoshiro256StarStar(WorldGenSeeds.Derive(7, WorldGenStep.Resources, 3, 4)).NextUInt64());
    }

    [Fact]
    public void StepNumbersAreStable()
    {
        // Sub-seeds depend on these values; renumbering would silently change every world.
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9],
            Enum.GetValues<WorldGenStep>().Select(s => (int)s));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-03")]
    public void NoRiverSourcesWhenDisabled()
    {
        var request = new WorldGenRequest(3, 128, WorldGenTestData.Params with
        {
            Rivers = WorldGenTestData.Params.Rivers with { SourcesPerMillionTiles = 0 },
        });

        TerrainMap map = WorldGenerator.GenerateTerrain(request);

        Assert.DoesNotContain(map.Features.ToArray(), f => (f & TileFeatures.River) != 0);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-03")]
    public void BiomeRuleWithUnknownBiomeFailsLoudly()
    {
        var request = new WorldGenRequest(3, 64, WorldGenTestData.Params with
        {
            Biomes = WorldGenTestData.Params.Biomes with { Rules = [new BiomeRuleDef("water")] },
        });

        Assert.Throws<ArgumentException>(() => WorldGenerator.GenerateTerrain(request));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-03")]
    public void BiomeTableWithoutCatchAllFailsLoudly()
    {
        var request = new WorldGenRequest(3, 64, WorldGenTestData.Params with
        {
            Biomes = WorldGenTestData.Params.Biomes with { Rules = [new BiomeRuleDef("meadow", MaxHeight: 0)] },
        });

        Assert.Throws<ArgumentException>(() => WorldGenerator.GenerateTerrain(request));
    }
}
