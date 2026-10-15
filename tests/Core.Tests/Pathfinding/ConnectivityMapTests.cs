using Core.Determinism;
using Core.Pathfinding;
using Core.Tests.WorldGen;
using Core.WorldGen;

namespace Core.Tests.Pathfinding;

public sealed class ConnectivityMapTests
{
    [Theory]
    [Trait("Req", "FR-HERO-01")]
    [InlineData(1UL, 16)]
    [InlineData(2UL, 17)]
    [InlineData(3UL, 37)]
    [InlineData(4UL, 64)]
    [InlineData(5UL, 5)]
    [InlineData(6UL, 48)]
    public void ComponentsMatchAReferenceFloodFill(ulong seed, int size)
    {
        for (int blocked = 0; blocked <= 400; blocked += 100)
        {
            TestMap map = PathTestMaps.Random(seed * 31 + (ulong)blocked, size, blockedPermille: blocked);
            AssertSamePartition(map.ReferenceComponents(), map.Pathfinder.Connectivity, size * size);
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ComponentsOfAGeneratedMapMatchAReferenceFloodFill()
    {
        TerrainMap terrain = WorldGenTestData.Golden(0);
        var map = new TestMap(terrain, new TestBlocker(terrain.Size));

        AssertSamePartition(map.ReferenceComponents(), map.Pathfinder.Connectivity, terrain.TileCount);
        Assert.True(map.Pathfinder.Connectivity.RegionCount >= (terrain.Size / ConnectivityMap.CellSize) * (terrain.Size / ConnectivityMap.CellSize) / 2);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void DiagonalContactAloneDoesNotConnect()
    {
        TestMap map = PathTestMaps.Parse(
            ".#",
            "#.");

        Assert.False(map.Pathfinder.IsReachable(0, 3));
        Assert.Equal(ConnectivityMap.NoComponent, map.Pathfinder.Connectivity.GetComponent(1, 0));
        Assert.Equal(PathStatus.Unreachable, map.Pathfinder.FindPath(0, 3, [], 100).Status);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void BuildIsDeferredToTheFirstQuery()
    {
        TestMap map = PathTestMaps.Open(64);
        ConnectivityMap connectivity = map.Pathfinder.Connectivity;

        Assert.Equal(4, connectivity.CellsPerSide);
        Assert.True(connectivity.IsDirty);
        Assert.Equal(0, connectivity.CellsRebuilt);

        Assert.True(connectivity.AreConnected(0, (64 * 64) - 1));
        Assert.Equal(16, connectivity.CellsRebuilt);
        Assert.Equal(1, connectivity.ComponentPasses);
        Assert.Equal(16, connectivity.RegionCount);
        Assert.False(connectivity.IsDirty);
        Assert.True(connectivity.MemoryBytes > 64 * 64);

        connectivity.Rebuild(); // nothing dirty: no work
        Assert.Equal(16, connectivity.CellsRebuilt);
        Assert.Equal(1, connectivity.ComponentPasses);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void InvalidateRebuildsOnlyTheCellsOfChangedTiles()
    {
        TestMap map = PathTestMaps.Open(64);
        ConnectivityMap connectivity = map.Pathfinder.Connectivity;
        connectivity.Rebuild();
        long cells = connectivity.CellsRebuilt;

        // Three tiles of one cell, then one tile of another cell, in one tick: two floods, one component pass.
        map.Blocker.Set(20, 20, true);
        connectivity.Invalidate(20, 20);
        connectivity.Invalidate(21, 20);
        connectivity.Invalidate(31, 31);
        connectivity.Invalidate(40, 3);
        Assert.True(connectivity.IsDirty);
        Assert.Equal(cells, connectivity.CellsRebuilt); // lazy until a query

        Assert.Equal(ConnectivityMap.NoComponent, connectivity.GetComponent(20, 20));
        Assert.Equal(cells + 2, connectivity.CellsRebuilt);
        Assert.Equal(2, connectivity.ComponentPasses);
        Assert.True(connectivity.AreConnected(map.Index(19, 20), map.Index(21, 20)));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void WallAcrossCellBordersSplitsAndAGapJoinsAgain()
    {
        // A wall along x = 16 (the first column of the second cell column) from top to bottom, then a gap.
        TestMap map = PathTestMaps.Open(48);
        ConnectivityMap connectivity = map.Pathfinder.Connectivity;
        Assert.True(connectivity.AreConnected(map.Index(0, 0), map.Index(47, 47)));
        for (int y = 0; y < 48; y++)
        {
            map.Blocker.Set(16, y, true);
            map.Pathfinder.NotifyTileChanged(16, y);
        }

        Assert.False(connectivity.AreConnected(map.Index(0, 0), map.Index(47, 47)));
        Assert.True(connectivity.AreConnected(map.Index(15, 0), map.Index(0, 47)));
        Assert.True(connectivity.AreConnected(map.Index(17, 0), map.Index(47, 47)));
        Assert.Equal(PathStatus.Unreachable, map.Pathfinder.FindPath(map.Index(0, 0), map.Index(47, 47), [], 100_000).Status);

        map.Blocker.Set(16, 33, false);
        map.Pathfinder.NotifyTileChanged(16, 33);
        Assert.True(connectivity.AreConnected(map.Index(0, 0), map.Index(47, 47)));
        Assert.Equal(PathStatus.Found, map.Pathfinder.FindPath(map.Index(0, 0), map.Index(47, 47), [], 100_000).Status);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void RandomEditsKeepMatchingTheReference()
    {
        TestMap map = PathTestMaps.Random(77, 50, blockedPermille: 250);
        ConnectivityMap connectivity = map.Pathfinder.Connectivity;
        var rng = new Xoshiro256StarStar(78);
        for (int round = 0; round < 40; round++)
        {
            int edits = rng.NextInt(1, 30);
            for (int e = 0; e < edits; e++)
            {
                int x = rng.NextInt(50);
                int y = rng.NextInt(50);
                map.Blocker.Set(x, y, !map.Blocker.IsBlocked(x, y));
                connectivity.Invalidate(x, y);
            }

            AssertSamePartition(map.ReferenceComponents(), connectivity, 50 * 50);
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void TheMostFragmentedCellStillFitsItsRegionIds()
    {
        // A checkerboard of blocked tiles: 128 single-tile regions per 16×16 cell, the maximum.
        TestMap map = PathTestMaps.Open(32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                map.Blocker.Set(x, y, ((x + y) & 1) == 1);
            }
        }

        AssertSamePartition(map.ReferenceComponents(), map.Pathfinder.Connectivity, 32 * 32);
        Assert.Equal(4 * 128, map.Pathfinder.Connectivity.RegionCount);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ComponentsDoNotDependOnTheOrderOfInvalidations()
    {
        TestMap a = PathTestMaps.Random(9, 40);
        TestMap b = PathTestMaps.Random(9, 40);
        a.Pathfinder.Connectivity.Rebuild();
        b.Pathfinder.Connectivity.Rebuild();
        (int X, int Y)[] edits = [(3, 3), (20, 5), (39, 39), (16, 16), (15, 15), (0, 39)];
        foreach ((int x, int y) in edits)
        {
            a.Blocker.Set(x, y, true);
            b.Blocker.Set(x, y, true);
            a.Pathfinder.NotifyTileChanged(x, y);
        }

        foreach ((int x, int y) in Enumerable.Reverse(edits))
        {
            b.Pathfinder.NotifyTileChanged(x, y);
        }

        for (int i = 0; i < 40 * 40; i++)
        {
            Assert.Equal(a.Pathfinder.Connectivity.GetComponent(i), b.Pathfinder.Connectivity.GetComponent(i));
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ArgumentsAreChecked()
    {
        ConnectivityMap connectivity = PathTestMaps.Open(4).Pathfinder.Connectivity;

        Assert.Throws<ArgumentOutOfRangeException>(() => connectivity.Invalidate(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => connectivity.GetComponent(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => connectivity.GetComponent(16));
        Assert.Throws<ArgumentOutOfRangeException>(() => connectivity.AreConnected(0, -1));
    }

    // Same tiles grouped together: a bijection between reference and map component ids.
    private static void AssertSamePartition(int[] reference, ConnectivityMap connectivity, int tiles)
    {
        var toMap = new Dictionary<int, int>();
        var toReference = new Dictionary<int, int>();
        for (int i = 0; i < tiles; i++)
        {
            int actual = connectivity.GetComponent(i);
            if (reference[i] < 0)
            {
                Assert.Equal(ConnectivityMap.NoComponent, actual);
                continue;
            }

            Assert.NotEqual(ConnectivityMap.NoComponent, actual);
            if (toMap.TryGetValue(reference[i], out int mapped))
            {
                Assert.Equal(mapped, actual);
            }
            else
            {
                toMap[reference[i]] = actual;
            }

            if (toReference.TryGetValue(actual, out int back))
            {
                Assert.Equal(back, reference[i]);
            }
            else
            {
                toReference[actual] = reference[i];
            }
        }
    }
}
