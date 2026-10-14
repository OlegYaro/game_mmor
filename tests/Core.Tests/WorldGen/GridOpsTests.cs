using Core.WorldGen.Terrain;

namespace Core.Tests.WorldGen;

public sealed class GridOpsTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(-3.0, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(1.0, 65535)]
    [InlineData(7.5, 65535)]
    [InlineData(double.PositiveInfinity, 65535)]
    [InlineData(0.5, 32768)]
    [InlineData(1.0 / 65535.0, 1)]
    public void UnitToFixed16ClampsAndRounds(double value, int expected) =>
        Assert.Equal(expected, GridOps.UnitToFixed16(value));

    [Fact]
    public void LevelForPermilleFindsTheQuantile()
    {
        int[] histogram = [10, 0, 30, 60]; // 100 values: ten 0s, thirty 2s, sixty 3s

        Assert.Equal(0, GridOps.LevelForPermille(histogram, 100, 0));
        Assert.Equal(1, GridOps.LevelForPermille(histogram, 100, 100));
        Assert.Equal(1, GridOps.LevelForPermille(histogram, 100, 109)); // target 10.9 truncates to 10
        Assert.Equal(3, GridOps.LevelForPermille(histogram, 100, 110));
        Assert.Equal(3, GridOps.LevelForPermille(histogram, 100, 400));
        Assert.Equal(4, GridOps.LevelForPermille(histogram, 100, 999));
    }

    [Fact]
    public void RankPermilleRanksMaskedValuesOnly()
    {
        int[] values = [-50, 7, 7, 1000, 3, 99];
        bool[] mask = [true, true, true, true, true, false];
        ushort[] ranks = new ushort[values.Length];

        GridOps.RankPermille(values, mask, ranks);

        // Five masked values; ranks = share strictly below, in permille.
        Assert.Equal([0, 400, 400, 800, 200, 0], ranks.Select(r => (int)r));

        GridOps.RankPermille(values, new bool[values.Length], ranks);
        Assert.All(ranks, r => Assert.Equal(0, r));
    }

    [Fact]
    public void LabelRegionsUsesFourConnectivity()
    {
        // 4x4:      X X . .
        //           . . X .
        //           . . X .
        //           X . . .
        bool[] set =
        [
            true, true, false, false,
            false, false, true, false,
            false, false, true, false,
            true, false, false, false,
        ];
        List<int> sizes = [];
        List<bool> edge = [];

        int[] labels = GridOps.LabelRegions(set, 4, sizes, edge);

        Assert.Equal([2, 2, 1], sizes);
        Assert.Equal([true, false, true], edge);
        Assert.Equal([0, 0, -1, -1, -1, -1, 1, -1, -1, -1, 1, -1, 2, -1, -1, -1], labels);
    }

    [Fact]
    public void MajorityFilterRemovesIsolatedValuesAndRespectsTheMask()
    {
        const int Size = 5;
        byte[] values = new byte[Size * Size];
        values[12] = 1; // a single 1 in the middle of 0s
        values[0] = 2;  // a single 2 in a corner (only 4 voters there)
        bool[] mask = Enumerable.Repeat(true, Size * Size).ToArray();
        mask[24] = false;
        values[24] = 3; // masked out: never changes and never votes

        GridOps.MajorityFilter(values, mask, Size, passes: 1, threshold: 3, categories: 4);

        Assert.Equal(0, values[12]);
        Assert.Equal(0, values[0]);
        Assert.Equal(3, values[24]);
    }

    [Fact]
    public void MajorityFilterKeepsTiesAndRespectsThreshold()
    {
        const int Size = 4;

        // Checkerboard: every tile sees 4 or 5 of its own value in a 3x3 block, never a clear majority of 9.
        byte[] checker = new byte[Size * Size];
        for (int i = 0; i < checker.Length; i++)
        {
            checker[i] = (byte)(((i % Size) + (i / Size)) % 2);
        }

        byte[] copy = (byte[])checker.Clone();
        bool[] all = Enumerable.Repeat(true, Size * Size).ToArray();
        GridOps.MajorityFilter(checker, all, Size, passes: 2, threshold: 9, categories: 2);
        Assert.Equal(copy, checker);

        // Zero passes change nothing.
        byte[] single = new byte[Size * Size];
        single[5] = 1;
        GridOps.MajorityFilter(single, all, Size, passes: 0, threshold: 5, categories: 2);
        Assert.Equal(1, single[5]);
    }
}
