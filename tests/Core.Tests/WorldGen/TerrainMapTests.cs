using Core.WorldGen;

namespace Core.Tests.WorldGen;

public sealed class TerrainMapTests
{
    private static TerrainMap Map => WorldGenTestData.Golden(0);

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void AccessorsReadTheLayersAtYTimesSizePlusX()
    {
        TerrainMap map = Map;
        int x = 200;
        int y = 300;
        int i = (y * map.Size) + x;

        Assert.Equal(i, map.Index(x, y));
        Assert.Equal(map.Heights[i], map.GetHeight(x, y));
        Assert.Equal(map.Biomes[i], map.GetBiome(x, y));
        Assert.Equal(map.Features[i], map.GetFeatures(x, y));
        Assert.Equal(map.RiverDirections[i], map.GetRiverDirection(x, y));
        Assert.Equal(map.Elevations[i], map.GetElevation(x, y));
        Assert.Equal(map.Moistures[i], map.GetMoisture(x, y));
        Assert.Equal(map.Temperatures[i], map.GetTemperature(x, y));
        Assert.Equal((map.Features[i] & TileFeatures.Water) != 0, map.IsWater(x, y));
        Assert.Equal((map.Features[i] & TileFeatures.River) != 0, map.IsRiver(x, y));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-13")]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(512, 0)]
    [InlineData(0, 512)]
    public void OutOfMapTilesAreRejected(int x, int y)
    {
        Assert.False(Map.Contains(x, y));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map.Index(x, y));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map.GetBiome(x, y));
    }

    [Fact]
    public void BiomeCountsAddUpToTheMap()
    {
        int[] counts = Map.CountBiomes();

        Assert.Equal(BiomeNames.Generated.Count + 1, counts.Length);
        Assert.Equal(Map.TileCount, counts.Sum());
    }

    [Fact]
    public void DirectionsAreTheEightNeighboursOrthogonalFirst()
    {
        Assert.Equal(8, TerrainMap.Directions.Count);
        Assert.Equal(8, TerrainMap.Directions.Distinct().Count());
        Assert.All(TerrainMap.Directions.Take(4), d => Assert.Equal(1, Math.Abs(d.Dx) + Math.Abs(d.Dy)));
        Assert.All(TerrainMap.Directions.Skip(4), d => Assert.Equal(2, Math.Abs(d.Dx) + Math.Abs(d.Dy)));
    }
}
