using Core.Determinism;
using Core.Pathfinding;
using Core.Tests.WorldGen;
using Core.WorldGen;

namespace Core.Tests.Pathfinding;

public sealed class PathfinderTests
{
    private const int Limit = 1_000_000;

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void StraightAndDiagonalStepsCostTenAndFourteen()
    {
        TestMap map = PathTestMaps.Open(10);
        var path = new List<int>();

        PathResult straight = map.Pathfinder.FindPath(map.Index(0, 0), map.Index(9, 0), path, Limit);
        Assert.Equal(PathStatus.Found, straight.Status);
        Assert.Equal(90, straight.Cost);
        Assert.Equal(10, straight.Length);
        Assert.Equal(map.Index(9, 0), straight.EndTile);
        Assert.True(straight.HasPath);
        map.AssertValidPath(path, map.Index(0, 0), 90);

        PathResult diagonal = map.Pathfinder.FindPath(map.Index(0, 0), map.Index(9, 9), path, Limit);
        Assert.Equal(PathStatus.Found, diagonal.Status);
        Assert.Equal(126, diagonal.Cost);
        Assert.Equal(10, diagonal.Length);

        PathResult same = map.Pathfinder.FindPath(map.Index(4, 4), map.Index(4, 4), path, Limit);
        Assert.Equal(new PathResult(PathStatus.Found, 0, 1, map.Index(4, 4), 0), same);
        Assert.Equal([map.Index(4, 4)], path);
    }

    [Theory]
    [Trait("Req", "FR-HERO-01")]
    [InlineData(false)]
    [InlineData(true)]
    public void PathsAreOptimalAgainstReferenceDijkstraOnRandomMaps(bool variableCost)
    {
        var path = new List<int>();
        int found = 0;
        int unreachable = 0;
        int blocked = 0;
        for (ulong seed = 1; seed <= 120; seed++)
        {
            var rng = new Xoshiro256StarStar(seed * 7919);
            int size = rng.NextInt(6, 41);
            TestMap map = PathTestMaps.Random(seed, size, blockedPermille: rng.NextInt(0, 300), variableCost: variableCost);
            for (int pair = 0; pair < 12; pair++)
            {
                int start = rng.NextInt(size * size);
                int goal = rng.NextInt(size * size);
                int[] dist = map.Dijkstra(start);
                PathResult result = map.Pathfinder.FindPath(start, goal, path, Limit);
                if (map.RefHeight(start % size, start / size) < 0 || map.RefHeight(goal % size, goal / size) < 0)
                {
                    Assert.Equal(PathStatus.Blocked, result.Status);
                    Assert.Empty(path);
                    blocked++;
                }
                else if (dist[goal] == int.MaxValue)
                {
                    Assert.Equal(PathResult.Fail(PathStatus.Unreachable), result);
                    Assert.Empty(path);
                    unreachable++;
                }
                else
                {
                    Assert.Equal(PathStatus.Found, result.Status);
                    Assert.Equal(dist[goal], result.Cost);
                    Assert.Equal(goal, path[^1]);
                    Assert.Equal(path.Count, result.Length);
                    map.AssertValidPath(path, start, result.Cost);
                    found++;
                }
            }
        }

        // The maps must exercise every outcome.
        Assert.True(found > 300, $"found {found}");
        Assert.True(unreachable > 50, $"unreachable {unreachable}");
        Assert.True(blocked > 50, $"blocked {blocked}");
    }

    [Theory]
    [Trait("Req", "FR-HERO-01")]
    [InlineData(false)]
    [InlineData(true)]
    public void ApproachPathsReachTheCheapestNeighbourOnRandomMaps(bool variableCost)
    {
        var path = new List<int>();
        int found = 0;
        for (ulong seed = 1; seed <= 80; seed++)
        {
            var rng = new Xoshiro256StarStar(seed * 104729);
            int size = rng.NextInt(6, 33);
            TestMap map = PathTestMaps.Random(seed + 1000, size, variableCost: variableCost);
            for (int pair = 0; pair < 10; pair++)
            {
                int start = rng.NextInt(size * size);
                int target = rng.NextInt(size * size);
                int[] dist = map.Dijkstra(start);
                int best = int.MaxValue;
                int tx = target % size;
                int ty = target / size;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx | dy) != 0 && map.RefHeight(tx + dx, ty + dy) >= 0)
                        {
                            best = Math.Min(best, dist[map.Index(tx + dx, ty + dy)]);
                        }
                    }
                }

                PathResult result = map.Pathfinder.FindPathToAdjacent(start, target, path, Limit);
                if (best == int.MaxValue)
                {
                    Assert.Equal(PathResult.Fail(PathStatus.Blocked), result);
                    continue;
                }

                Assert.Equal(PathStatus.Found, result.Status);
                Assert.Equal(best, result.Cost);
                int end = path[^1];
                Assert.Equal(end, result.EndTile);
                Assert.Equal(1, Math.Max(Math.Abs((end % size) - tx), Math.Abs((end / size) - ty)));
                map.AssertValidPath(path, start, result.Cost);
                found++;
            }
        }

        Assert.True(found > 200, $"found {found}");
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void PathGoesAroundABlockedCornerInsteadOfCuttingIt()
    {
        TestMap map = PathTestMaps.Parse(
            ".#",
            "..");
        var path = new List<int>();

        PathResult result = map.Pathfinder.FindPath(map.Index(0, 0), map.Index(1, 1), path, Limit);

        Assert.Equal(PathStatus.Found, result.Status);
        Assert.Equal(20, result.Cost);
        Assert.Equal([map.Index(0, 0), map.Index(0, 1), map.Index(1, 1)], path);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void PathClimbsOneStepButGoesAroundACliff()
    {
        TestMap map = PathTestMaps.Parse(
            "00300",
            "01210",
            "00300",
            "00000",
            "00000");
        var path = new List<int>();

        // Over the ridge: 0 -> 1 -> 2 -> 1 -> 0, one step at a time.
        PathResult ridge = map.Pathfinder.FindPath(map.Index(0, 1), map.Index(4, 1), path, Limit);
        Assert.Equal(PathStatus.Found, ridge.Status);
        Assert.Equal(40, ridge.Cost);
        Assert.Equal([5, 6, 7, 8, 9], path);

        // The peak of height 3 is a cliff from the 0 tiles: the way along row 0 goes down and round.
        map.Blocker.Set(2, 1, true);
        map.Pathfinder.NotifyTileChanged(2, 1);
        PathResult around = map.Pathfinder.FindPath(map.Index(0, 0), map.Index(4, 0), path, Limit);
        Assert.Equal(PathStatus.Found, around.Status);
        Assert.DoesNotContain(map.Index(2, 0), path);
        Assert.DoesNotContain(map.Index(2, 2), path);
        Assert.Equal(map.Dijkstra(map.Index(0, 0))[map.Index(4, 0)], around.Cost);
        map.AssertValidPath(path, map.Index(0, 0), around.Cost);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void WaterIsAvoidedAndARiverWithoutAFordCutsTheMap()
    {
        TestMap lake = PathTestMaps.Parse(
            ".....",
            ".~~~.",
            ".~~~.",
            ".~~~.",
            ".....");
        var path = new List<int>();
        PathResult around = lake.Pathfinder.FindPath(lake.Index(0, 2), lake.Index(4, 2), path, Limit);
        Assert.Equal(PathStatus.Found, around.Status);
        Assert.Equal(lake.Dijkstra(lake.Index(0, 2))[lake.Index(4, 2)], around.Cost);
        lake.AssertValidPath(path, lake.Index(0, 2), around.Cost);

        TestMap river = PathTestMaps.Parse(
            "..r..",
            "..r..",
            "..r..",
            "..r..",
            "..r..");
        PathResult cut = river.Pathfinder.FindPath(river.Index(0, 0), river.Index(4, 4), path, Limit);
        Assert.Equal(new PathResult(PathStatus.Unreachable, 0, 0, -1, 0), cut);
        Assert.Empty(path);
        Assert.False(river.Pathfinder.IsReachable(0, 0, 4, 4));
        Assert.True(river.Pathfinder.IsReachable(0, 0, 1, 4));
        Assert.Equal(PathStatus.Blocked, river.Pathfinder.FindPath(river.Index(0, 0), river.Index(2, 2), path, Limit).Status);
        Assert.Equal(PathStatus.Blocked, river.Pathfinder.FindPath(river.Index(2, 2), river.Index(0, 0), path, Limit).Status);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void UnreachableGoalOnAGeneratedMapIsRejectedWithoutSearching()
    {
        // Golden seed 3 has a pocket of land (about 1500 tiles) cut off from the mainland by water and cliffs.
        TerrainMap terrain = WorldGenTestData.Golden(2);
        var pathfinder = new Pathfinder(new PassabilityGrid(terrain));
        ConnectivityMap connectivity = pathfinder.Connectivity;

        // Two land tiles in different components, found by scanning.
        int first = -1;
        int other = -1;
        for (int i = 0; i < terrain.TileCount && other < 0; i++)
        {
            int component = connectivity.GetComponent(i);
            if (component == ConnectivityMap.NoComponent)
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }
            else if (component != connectivity.GetComponent(first))
            {
                other = i;
            }
        }

        Assert.True(other >= 0, "The map has more than one component.");
        var path = new List<int>();
        PathResult result = pathfinder.FindPath(first, other, path, Limit);
        Assert.Equal(PathStatus.Unreachable, result.Status);
        Assert.Equal(0, result.ExpandedNodes);
        Assert.False(pathfinder.IsReachable(first, other));
        Assert.Equal(0, pathfinder.SearchMemoryBytes); // A* never ran
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void SearchWithoutTheConnectivityCheckExhaustsTheComponentAndFails()
    {
        // What A* alone does when the goal is cut off (e.g. a caller forgot NotifyTileChanged): it floods the start's
        // component, then reports Unreachable.
        TestMap map = PathTestMaps.Parse(
            "..r..",
            "..r..",
            "..r..",
            "..r..",
            "..r..");
        var search = new AStarSearch(map.Grid);
        var path = new List<int> { 99 };

        PathResult result = search.Search(map.Index(0, 0), map.Index(4, 4), PathGoalKind.Tile, Limit, path);

        Assert.Equal(new PathResult(PathStatus.Unreachable, 0, 0, -1, 10), result);
        Assert.Empty(path);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void NodeLimitGivesAPartialPathToTheClosestExploredTile()
    {
        TestMap map = PathTestMaps.Open(30);
        var path = new List<int>();

        PathResult result = map.Pathfinder.FindPath(map.Index(0, 0), map.Index(29, 29), path, 5);

        Assert.Equal(PathStatus.Partial, result.Status);
        Assert.Equal(5, result.ExpandedNodes);
        Assert.Equal(map.Index(4, 4), result.EndTile);
        Assert.Equal(56, result.Cost);
        Assert.Equal([0, 31, 62, 93, 124], path);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void PartialPathBehindAWallStillMakesProgress()
    {
        TestMap map = PathTestMaps.Parse(
            "........",
            "........",
            "........",
            "........",
            "........",
            "######..",
            "........",
            "........");
        var path = new List<int>();
        int start = map.Index(1, 3);
        int goal = map.Index(1, 7);

        PathResult partial = map.Pathfinder.FindPath(start, goal, path, 6);
        Assert.Equal(PathStatus.Partial, partial.Status);
        Assert.Equal(6, partial.ExpandedNodes);
        map.AssertValidPath(path, start, partial.Cost);
        Assert.True(map.Grid.Heuristic(map.Grid.XOf(partial.EndTile), map.Grid.YOf(partial.EndTile), 1, 7) < map.Grid.Heuristic(1, 3, 1, 7));

        // Within one node no progress is possible: the partial path is just the start.
        PathResult stuck = map.Pathfinder.FindPath(start, goal, path, 1);
        Assert.Equal(new PathResult(PathStatus.Partial, 0, 1, start, 1), stuck);
        Assert.Equal([start], path);

        PathResult full = map.Pathfinder.FindPath(start, goal, path, Limit);
        Assert.Equal(PathStatus.Found, full.Status);
        Assert.Equal(map.Dijkstra(start)[goal], full.Cost);
        map.AssertValidPath(path, start, full.Cost);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-04")]
    public void TiesBreakTheSameWayEveryTime()
    {
        // Several cheapest paths exist; the (f, h, index) order picks the diagonal first.
        TestMap map = PathTestMaps.Open(5);
        var path = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            PathResult result = map.Pathfinder.FindPath(map.Index(0, 0), map.Index(3, 1), path, Limit);
            Assert.Equal(34, result.Cost);
            Assert.Equal([0, 6, 7, 8], path);
            Assert.Equal(3, result.ExpandedNodes);
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-04")]
    public void SameSeedGivesTheSamePathsOnAGeneratedMap()
    {
        TerrainMap terrain = WorldGenTestData.Golden(1);
        var a = new Pathfinder(new PassabilityGrid(terrain));
        var b = new Pathfinder(new PassabilityGrid(WorldGenTestData.Generate(WorldGenTestData.GoldenSeeds[1], WorldGenTestData.GoldenSize)));
        var rng = new Xoshiro256StarStar(42);
        var pairs = new (int Start, int Goal)[60];
        for (int i = 0; i < pairs.Length; i++)
        {
            pairs[i] = (RandomLand(terrain, rng), RandomLand(terrain, rng));
        }

        var first = new List<int[]>();
        var pathA = new List<int>();
        foreach ((int start, int goal) in pairs)
        {
            PathResult result = a.FindPath(start, goal, pathA, 50_000);
            first.Add([(int)result.Status, result.Cost, result.ExpandedNodes, .. pathA]);
        }

        // Another instance, in reverse order (reused node arrays must not leak between searches).
        var pathB = new List<int>();
        for (int i = pairs.Length - 1; i >= 0; i--)
        {
            PathResult result = b.FindPath(pairs[i].Start, pairs[i].Goal, pathB, 50_000);
            int[] again = [(int)result.Status, result.Cost, result.ExpandedNodes, .. pathB];
            Assert.Equal(first[i], again);
        }

        Assert.Contains(first, r => r[0] == (int)PathStatus.Found && r.Length > 50);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void PathsOnAGeneratedMapMatchDijkstra()
    {
        TerrainMap terrain = WorldGenTestData.Golden(2);
        var map = new TestMap(terrain, new TestBlocker(terrain.Size));
        var rng = new Xoshiro256StarStar(5);
        var path = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            int start = RandomLand(terrain, rng);
            int[] dist = map.Dijkstra(start);
            for (int j = 0; j < 10; j++)
            {
                int goal = RandomLand(terrain, rng);
                PathResult result = map.Pathfinder.FindPath(start, goal, path, PathfinderConfig.MaxNodeLimit);
                if (dist[goal] == int.MaxValue)
                {
                    Assert.Equal(PathStatus.Unreachable, result.Status);
                    continue;
                }

                Assert.Equal(PathStatus.Found, result.Status);
                Assert.Equal(dist[goal], result.Cost);
                map.AssertValidPath(path, start, result.Cost);
            }
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void NodeArraysSurviveTheGenerationCounterWrapping()
    {
        TestMap map = PathTestMaps.Parse(
            "....",
            ".#..",
            "..#.",
            "....");
        var path = new List<int>();
        PathResult expected = map.Pathfinder.FindPath(0, 15, path, Limit);
        int[] expectedPath = [.. path];
        for (int i = 0; i < ushort.MaxValue + 10; i++)
        {
            PathResult result = map.Pathfinder.FindPath(0, 15, path, Limit);
            if (result != expected)
            {
                Assert.Fail($"Search {i} differs: {result}");
            }
        }

        Assert.Equal(expectedPath, path);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void SearchesDoNotAllocateAfterWarmUp()
    {
        TerrainMap terrain = WorldGenTestData.Golden(0);
        var pathfinder = new Pathfinder(new PassabilityGrid(terrain));
        var rng = new Xoshiro256StarStar(9);
        int[] tiles = new int[64];
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = RandomLand(terrain, rng);
        }

        var path = new List<int>(4096);
        for (int round = 0; round < 2; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i + 1 < tiles.Length; i++)
            {
                pathfinder.FindPath(tiles[i], tiles[i + 1], path, 20_000);
                pathfinder.FindPathToAdjacent(tiles[i + 1], tiles[i], path, 20_000);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round == 1)
            {
                Assert.Equal(0, allocated);
            }
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void BlockingAndUnblockingAPassChangesTheAnswer()
    {
        TestMap map = PathTestMaps.Open(10);
        for (int y = 0; y < 9; y++)
        {
            map.Blocker.Set(5, y, true);
            map.Pathfinder.NotifyTileChanged(5, y);
        }

        var path = new List<int>();
        int start = map.Index(0, 0);
        int goal = map.Index(9, 0);
        PathResult open = map.Pathfinder.FindPath(start, goal, path, Limit);
        Assert.Equal(PathStatus.Found, open.Status);
        Assert.Contains(map.Index(5, 9), path);

        map.Blocker.Set(5, 9, true);
        map.Pathfinder.NotifyTileChanged(5, 9);
        Assert.Equal(PathStatus.Unreachable, map.Pathfinder.FindPath(start, goal, path, Limit).Status);

        map.Blocker.Set(5, 9, false);
        map.Pathfinder.NotifyTileChanged(5, 9);
        Assert.Equal(open, map.Pathfinder.FindPath(start, goal, path, Limit));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "FR-NPC-07")]
    public void ApproachTileIsTheNearestStandingPlaceNextToATree()
    {
        // A tree (blocked) with water on its west side: the walker comes from the west and must go round the water.
        TestMap map = PathTestMaps.Parse(
            "........",
            "....~...",
            "....~#..",
            "....~...",
            "........",
            "........",
            "........",
            "........");
        var path = new List<int>();
        int tree = map.Index(5, 2);

        PathResult result = map.Pathfinder.FindPathToAdjacent(map.Index(0, 2), tree, path, Limit);
        Assert.Equal(PathStatus.Found, result.Status);
        int[] dist = map.Dijkstra(map.Index(0, 2));
        int[] neighbours = [map.Index(5, 1), map.Index(6, 1), map.Index(6, 2), map.Index(5, 3), map.Index(6, 3)];
        Assert.Equal(neighbours.Min(n => dist[n]), result.Cost);
        Assert.Contains(result.EndTile, neighbours);
        map.AssertValidPath(path, map.Index(0, 2), result.Cost);
        Assert.True(map.Pathfinder.TryFindApproachTile(map.Index(0, 2), tree, Limit, out int tile));
        Assert.Equal(result.EndTile, tile);

        // Already next to it: stay.
        PathResult here = map.Pathfinder.FindPathToAdjacent(map.Index(6, 3), tree, path, Limit);
        Assert.Equal(new PathResult(PathStatus.Found, 0, 1, map.Index(6, 3), 0), here);

        // Standing on a passable target: step off it to a neighbour.
        PathResult off = map.Pathfinder.FindPathToAdjacent(map.Index(1, 6), map.Index(1, 6), path, Limit);
        Assert.Equal(PathStatus.Found, off.Status);
        Assert.Equal(10, off.Cost);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ApproachFailsWhenNoNeighbourCanBeReached()
    {
        TestMap map = PathTestMaps.Parse(
            "......",
            ".~~~..",
            ".~#~..",
            ".~~~..",
            "......",
            "#.....");
        var path = new List<int>();

        Assert.Equal(PathResult.Fail(PathStatus.Blocked), map.Pathfinder.FindPathToAdjacent(map.Index(0, 0), map.Index(2, 2), path, Limit));
        Assert.Equal(PathResult.Fail(PathStatus.Blocked), map.Pathfinder.FindPathToAdjacent(map.Index(0, 5), map.Index(4, 4), path, Limit));
        Assert.False(map.Pathfinder.TryFindApproachTile(map.Index(0, 0), map.Index(2, 2), Limit, out int tile));
        Assert.Equal(-1, tile);

        // Corner target: off-map neighbours are skipped.
        Assert.Equal(PathStatus.Found, map.Pathfinder.FindPathToAdjacent(map.Index(4, 4), map.Index(5, 0), path, Limit).Status);

        // The limit runs out first: a partial approach.
        PathResult partial = map.Pathfinder.FindPathToAdjacent(map.Index(0, 0), map.Index(5, 5), path, 2);
        Assert.Equal(PathStatus.Partial, partial.Status);
        Assert.False(map.Pathfinder.TryFindApproachTile(map.Index(0, 0), map.Index(5, 5), 2, out _));
        Assert.Equal(PathStatus.Found, map.Pathfinder.Find(map.Index(0, 0), map.Index(5, 5), PathGoalKind.Adjacent, path, Limit).Status);
        Assert.Equal(PathStatus.Found, map.Pathfinder.Find(map.Index(0, 0), map.Index(5, 5), PathGoalKind.Tile, path, Limit).Status);
        Assert.Equal(map.Index(5, 5), path[^1]);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ArgumentsAreChecked()
    {
        TestMap map = PathTestMaps.Open(4);
        var path = new List<int>();
        Pathfinder pf = map.Pathfinder;

        Assert.Throws<ArgumentNullException>(() => new Pathfinder(null!));
        Assert.Throws<ArgumentNullException>(() => pf.FindPath(0, 1, null!, Limit));
        Assert.Throws<ArgumentNullException>(() => pf.FindPathToAdjacent(0, 1, null!, Limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.FindPath(-1, 1, path, Limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.FindPath(0, 16, path, Limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.FindPathToAdjacent(16, 0, path, Limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.FindPathToAdjacent(0, 16, path, Limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.FindPath(0, 1, path, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.FindPath(0, 1, path, PathfinderConfig.MaxNodeLimit + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.IsReachable(0, 0, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pf.IsReachable(0, 99));
        Assert.Throws<ArgumentNullException>(() => new ConnectivityMap(null!));
        Assert.Throws<ArgumentNullException>(() => new PassabilityGrid(null!));
    }

    internal static int RandomLand(TerrainMap terrain, Xoshiro256StarStar rng)
    {
        while (true)
        {
            int x = rng.NextInt(terrain.Size);
            int y = rng.NextInt(terrain.Size);
            if (!terrain.IsWater(x, y) && !terrain.IsRiver(x, y))
            {
                return terrain.Index(x, y);
            }
        }
    }
}
