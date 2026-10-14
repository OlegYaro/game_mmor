using Core.WorldGen;

namespace Core.Tests.WorldGen;

public sealed class ChunkTests
{
    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void ChunkOfTileAndCounts()
    {
        Assert.Equal(32, ChunkCoord.Size);
        Assert.Equal(new ChunkCoord(0, 0), ChunkCoord.OfTile(0, 31));
        Assert.Equal(new ChunkCoord(1, 15), ChunkCoord.OfTile(32, 511));
        Assert.Equal(16, ChunkCoord.CountPerSide(512));
        Assert.Equal(32, ChunkCoord.CountPerSide(1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkCoord.OfTile(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkCoord.OfTile(0, -1));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void WindowMapsLocalTilesToWholeMapIndices()
    {
        var window = new ChunkWindow(new ChunkCoord(2, 3), 512);

        Assert.Equal(64, window.OriginX);
        Assert.Equal(96, window.OriginY);
        Assert.Equal(512, window.MapSize);
        Assert.Equal((96 * 512) + 64, window.MapIndex(0, 0));
        Assert.Equal(((96 + 31) * 512) + 64 + 31, window.MapIndex(31, 31));
        Assert.Equal((97 * 512) + 64, window.RowStart(1));
        Assert.True(window.Contains(64, 96));
        Assert.True(window.Contains(95, 127));
        Assert.False(window.Contains(96, 127));
        Assert.False(window.Contains(63, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.MapIndex(32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.MapIndex(0, -1));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-13")]
    [InlineData(-1, 0)]
    [InlineData(0, 16)]
    [InlineData(16, 0)]
    public void WindowsOutsideTheMapAreRejected(int cx, int cy) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkWindow(new ChunkCoord(cx, cy), 512));

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void ChunkWindowsTileTheMapExactlyOnce()
    {
        TerrainMap map = WorldGenTestData.Golden(0);
        int[] seen = new int[map.TileCount];
        for (int cy = 0; cy < map.ChunksPerSide; cy++)
        {
            for (int cx = 0; cx < map.ChunksPerSide; cx++)
            {
                ChunkWindow window = map.GetChunk(cx, cy);
                for (int ly = 0; ly < ChunkCoord.Size; ly++)
                {
                    for (int lx = 0; lx < ChunkCoord.Size; lx++)
                    {
                        seen[window.MapIndex(lx, ly)]++;
                    }
                }
            }
        }

        Assert.All(seen, count => Assert.Equal(1, count));
    }
}
