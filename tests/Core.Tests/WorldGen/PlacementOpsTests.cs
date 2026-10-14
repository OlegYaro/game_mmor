using Core.Determinism;
using Core.WorldGen.Placement;

namespace Core.Tests.WorldGen;

/// <summary>Integer helpers of the placement steps, checked against brute force.</summary>
public sealed class PlacementOpsTests
{
    private const int Size = 40;

    private static bool[] RandomMask(ulong seed, int permille)
    {
        var rng = new Xoshiro256StarStar(seed);
        return Enumerable.Range(0, Size * Size).Select(_ => rng.ChancePermille(permille)).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    public void DiscHalfWidthsMatchTheEuclideanDisc(int radius)
    {
        int[] widths = PlacementOps.DiscHalfWidths(radius);

        Assert.Equal((2 * radius) + 1, widths.Length);
        for (int dy = -radius; dy <= radius; dy++)
        {
            int w = widths[dy + radius];
            Assert.True((w * w) + (dy * dy) <= radius * radius);
            Assert.True(((w + 1) * (w + 1)) + (dy * dy) > radius * radius);
        }
    }

    [Theory]
    [InlineData(20, 20, 7)]
    [InlineData(0, 0, 9)]
    [InlineData(39, 2, 15)]
    [InlineData(5, 38, 60)]
    public void DiscCounterMatchesBruteForce(int cx, int cy, int radius)
    {
        bool[] mask = RandomMask(3, 400);
        var counter = new DiscCounter(mask, Size);
        int[] widths = PlacementOps.DiscHalfWidths(radius);
        int count = 0;
        int area = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (PlacementOps.DistanceSquared(cx, cy, x, y) <= (long)radius * radius)
                {
                    area++;
                    count += mask[(y * Size) + x] ? 1 : 0;
                }
            }
        }

        Assert.Equal(count, counter.Count(cx, cy, widths));
        Assert.Equal(area, counter.Area(cx, cy, widths));
    }

    [Theory]
    [InlineData(0, 0, 39, 39)]
    [InlineData(3, 4, 10, 20)]
    [InlineData(-5, -5, 2, 2)]
    [InlineData(30, 35, 60, 60)]
    [InlineData(10, 10, 9, 20)]
    [InlineData(45, 0, 50, 5)]
    public void AreaSumMatchesBruteForce(int x0, int y0, int x1, int y1)
    {
        bool[] mask = RandomMask(4, 500);
        int expected = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(Size - 1, y1); y++)
        {
            for (int x = Math.Max(0, x0); x <= Math.Min(Size - 1, x1); x++)
            {
                expected += mask[(y * Size) + x] ? 1 : 0;
            }
        }

        Assert.Equal(expected, new AreaSum(mask, Size).Sum(x0, y0, x1, y1));
    }

    [Fact]
    public void SpacingGridAnswersLikeALinearScan()
    {
        var rng = new Xoshiro256StarStar(5);
        var grid = new SpacingGrid(200, 16);
        List<(int X, int Y)> points = [];
        for (int k = 0; k < 40; k++)
        {
            (int x, int y) = (rng.NextInt(200), rng.NextInt(200));
            grid.Add(x, y);
            points.Add((x, y));
        }

        foreach (int distance in new[] { 1, 10, 16, 40 })
        {
            for (int k = 0; k < 300; k++)
            {
                (int x, int y) = (rng.NextInt(200), rng.NextInt(200));
                bool expected = points.All(p => PlacementOps.DistanceSquared(x, y, p.X, p.Y) >= (long)distance * distance);
                Assert.Equal(expected, grid.IsFar(x, y, distance));
            }
        }
    }

    [Fact]
    public void HashHelpersAreStableAndInRange()
    {
        Assert.Equal(PlacementOps.Hash(1, 2), PlacementOps.Hash(1, 2));
        Assert.NotEqual(PlacementOps.Hash(1, 2), PlacementOps.Hash(1, 3));
        Assert.NotEqual(PlacementOps.Hash(1, 2), PlacementOps.Hash(2, 2));
        for (long v = 0; v < 2000; v++)
        {
            ulong h = PlacementOps.Hash(7, v);
            Assert.InRange(PlacementOps.InRange(h, -3, 9), -3, 9);
            Assert.InRange(PlacementOps.Permille(h), 0, 999);
        }

        Assert.Equal(25, PlacementOps.DistanceSquared(1, 1, 4, 5));
    }

    [Fact]
    public void ShuffleIsASeededPermutation()
    {
        List<int> a = [.. Enumerable.Range(0, 50)];
        List<int> b = [.. Enumerable.Range(0, 50)];
        PlacementOps.Shuffle(a, new Xoshiro256StarStar(9));
        PlacementOps.Shuffle(b, new Xoshiro256StarStar(9));

        Assert.Equal(a, b);
        Assert.NotEqual(Enumerable.Range(0, 50), a);
        Assert.Equal(Enumerable.Range(0, 50), a.Order());
    }

    [Fact]
    public void BuildableMatchesB2()
    {
        Core.WorldGen.TerrainMap map = WorldGenTestData.Golden(0);
        bool[] buildable = PlacementOps.Buildable(map);
        var world = WorldGenTestData.GoldenWorld(0);
        for (int y = 0; y < map.Size; y += 3)
        {
            for (int x = 0; x < map.Size; x += 3)
            {
                Assert.Equal(WorldInvariants.IsBuildable(world, x, y), buildable[map.Index(x, y)]);
            }
        }
    }
}
