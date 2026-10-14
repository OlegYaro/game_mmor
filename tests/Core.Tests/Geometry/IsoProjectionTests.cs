using Core.Entities;
using Core.Geometry;

namespace Core.Tests.Geometry;

/// <summary>Isometric projection shared by Core and the renderer (ADR 0010).</summary>
public sealed class IsoProjectionTests
{
    [Theory]
    [Trait("Req", "FR-WORLD-13")]
    [InlineData(0, 0, 16, 8)]
    [InlineData(1, 0, 32, 16)]
    [InlineData(0, 1, 0, 16)]
    [InlineData(5, 3, 48, 72)]
    [InlineData(511, 511, 16, 8184)]
    [InlineData(-2, 1, -32, 0)]
    public void TileCenterMatchesTheAdrFormula(int x, int y, int px, int py)
    {
        Assert.Equal((px, py), IsoProjection.TileCenter(x, y));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void HeightLiftsTheTopFaceByTheHeightStepPerStep()
    {
        (int px, int py) = IsoProjection.TileCenter(7, 9);
        for (int h = 0; h <= 3; h++)
        {
            Assert.Equal((px, py - (IsoProjection.HeightStep * h)), IsoProjection.TileCenter(7, 9, h));
        }
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void EveryPixelOfADiamondMapsBackToItsTile()
    {
        Assert.Equal((32, 16, 8), (IsoProjection.TileWidth, IsoProjection.TileHeight, IsoProjection.HeightStep));
        for (int ty = -3; ty <= 3; ty++)
        {
            for (int tx = -3; tx <= 3; tx++)
            {
                (int cx, int cy) = IsoProjection.TileCenter(tx, ty);
                for (int dy = -IsoProjection.HalfHeight; dy < IsoProjection.HalfHeight; dy++)
                {
                    for (int dx = -IsoProjection.HalfWidth; dx < IsoProjection.HalfWidth; dx++)
                    {
                        // Pixel centre offset from the tile centre; inside the diamond when |dx|/halfWidth + |dy|/halfHeight < 1.
                        double ox = dx + 0.5;
                        double oy = dy + 0.5;
                        if ((Math.Abs(ox) / IsoProjection.HalfWidth) + (Math.Abs(oy) / IsoProjection.HalfHeight) < 1)
                        {
                            Assert.Equal((tx, ty), IsoProjection.TileAtPixel(cx + dx, cy + dy));
                        }
                    }
                }
            }
        }
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void PixelsTileThePlaneWithoutGapsOrOverlaps()
    {
        // Each tile owns exactly TileWidth*TileHeight/2 = 256 pixels.
        var counts = new Dictionary<(int, int), int>();
        for (int py = -200; py < 200; py++)
        {
            for (int px = -200; px < 200; px++)
            {
                (int, int) tile = IsoProjection.TileAtPixel(px, py);
                counts[tile] = counts.GetValueOrDefault(tile) + 1;
            }
        }

        (int x, int y) inner = IsoProjection.TileAtPixel(0, 0);
        Assert.Equal(IsoProjection.TileWidth * IsoProjection.TileHeight / 2, counts[inner]);
        Assert.Equal(IsoProjection.TileWidth * IsoProjection.TileHeight / 2, counts[(inner.x + 1, inner.y)]);
        Assert.Equal(IsoProjection.TileWidth * IsoProjection.TileHeight / 2, counts[(inner.x, inner.y - 2)]);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void SubPixelEdgesGoToTheLargerTile()
    {
        // (32, 8) is the right corner of tile (0,0), on the edge with (1,0): exact edge points go to the larger tile.
        Assert.Equal((1, 0), IsoProjection.TileAtSubPixel(32, 8, 1));
        Assert.Equal((0, 0), IsoProjection.TileAtSubPixel(16, 8, 1));
        Assert.Equal((0, 0), IsoProjection.TileAtSubPixel(16 * 256, 8 * 256, 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => IsoProjection.TileAtSubPixel(0, 0, 0));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void PickTilePrefersTheRaisedBlockInFront()
    {
        // Tile (5,5) is a height-3 block; its top face sits 24 px above its ground diamond, over the ground of (4,4).
        int Height(int x, int y) => (x, y) == (5, 5) ? 3 : 0;
        (int px, int py) = IsoProjection.TileCenter(5, 5, 3);
        Assert.Equal((4, 4), IsoProjection.TileAtPixel(px, py - 4 + 8)); // ground pick below it is another tile

        Assert.True(IsoProjection.PickTile(px, py, 64, Height, out (int X, int Y) top));
        Assert.Equal((5, 5), top);

        // The side face (between the top face and the ground) also belongs to the block.
        Assert.True(IsoProjection.PickTile(px, py + 12, 64, Height, out (int X, int Y) side));
        Assert.Equal((5, 5), side);

        // Flat ground elsewhere is picked as ground.
        (int gx, int gy) = IsoProjection.TileCenter(10, 2);
        Assert.True(IsoProjection.PickTile(gx, gy, 64, Height, out (int X, int Y) ground));
        Assert.Equal((10, 2), ground);

        // Outside the map nothing is picked.
        Assert.False(IsoProjection.PickTile(-5000, -5000, 64, Height, out _));
        Assert.Throws<ArgumentNullException>(() => IsoProjection.PickTile(0, 0, 64, null!, out _));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void PositionOfATileCentreProjectsToTheTileCentre()
    {
        Position centre = Core.Sim.Simulation.TileCenter(12, 34);
        (long px, long py) = IsoProjection.PixelOfPosition(centre);
        Assert.Equal(IsoProjection.TileCenter(12, 34), ((int)(px / Position.UnitsPerTile), (int)(py / Position.UnitsPerTile)));
        Assert.Equal(0, px % Position.UnitsPerTile);
        Assert.Equal(0, py % Position.UnitsPerTile);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-13")]
    public void MapBoundsEncloseEveryTile()
    {
        const int Size = 64;
        (int minX, int minY, int maxX, int maxY) = IsoProjection.MapBounds(Size);
        Assert.Equal(IsoProjection.TileWidth * Size, maxX - minX);
        foreach ((int x, int y) in new[] { (0, 0), (Size - 1, 0), (0, Size - 1), (Size - 1, Size - 1) })
        {
            (int px, int py) = IsoProjection.TileCenter(x, y, 3);
            Assert.InRange(px - IsoProjection.HalfWidth, minX, maxX);
            Assert.InRange(px + IsoProjection.HalfWidth, minX, maxX);
            Assert.InRange(py - IsoProjection.HalfHeight, minY, maxY);
            (int gx, int gy) = IsoProjection.TileCenter(x, y);
            Assert.InRange(gy + IsoProjection.HalfHeight, minY, maxY);
            Assert.InRange(gx, minX, maxX);
        }
    }
}
