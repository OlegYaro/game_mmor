using System.Text.RegularExpressions;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

public sealed class BiomeNamesTests
{
    [Fact]
    [Trait("Req", "FR-WORLD-03")]
    public void NumericValuesAreStable()
    {
        // Part of the terrain hash, saves and art tables: append only.
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<Biome>().Select(b => (int)b));
        Assert.Equal([0, 1, 2, 4, 8], Enum.GetValues<TileFeatures>().Select(f => (int)f));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-03")]
    public void NamesRoundTrip()
    {
        Assert.Equal(["none", "water", "meadow", "forest", "mountain", "wasteland"], Enum.GetValues<Biome>().Select(BiomeNames.ToName));
        foreach (Biome biome in Enum.GetValues<Biome>())
        {
            Assert.True(BiomeNames.TryParse(BiomeNames.ToName(biome), out Biome parsed));
            Assert.Equal(biome, parsed);
        }

        Assert.False(BiomeNames.TryParse("Meadow", out Biome none));
        Assert.Equal(Biome.None, none);
        Assert.False(BiomeNames.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => BiomeNames.ToName((Biome)6));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-03")]
    public void LandPatternMatchesExactlyTheLandBiomes()
    {
        Assert.Equal([Biome.Meadow, Biome.Forest, Biome.Mountain, Biome.Wasteland], BiomeNames.Land);
        Assert.Equal(Enum.GetValues<Biome>().Where(b => b != Biome.None), BiomeNames.Generated);
        foreach (Biome biome in Enum.GetValues<Biome>())
        {
            Assert.Equal(BiomeNames.Land.Contains(biome), Regex.IsMatch(BiomeNames.ToName(biome), BiomeNames.LandPattern));
        }
    }
}
