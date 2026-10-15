using System.Diagnostics;
using System.Globalization;
using Core.Determinism;
using Core.Entities;
using Core.Pathfinding;
using Core.Tests.WorldGen;
using Core.WorldGen;

namespace Core.Tests.Pathfinding;

/// <summary>
/// ADR 0003 benchmarks: 100 walkers on 512² under the per-tick node budget, paths across 2048², building and updating
/// the connectivity map at 2048². The printed numbers are the output (run in Release:
/// <c>CONFIGURATION=Release tools/test.sh --filter Category=Benchmark</c>); the assertions are loose bounds for CI.
/// Excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
[Collection(WorldGenBenchmarkGroup.Name)]
public sealed class PathfindingBenchmarkTests(ITestOutputHelper output)
{
    private const int Agents = 100;
    private const int WarmUpTicks = 100;
    private const int MeasuredTicks = 300;

    // NFR-02: the whole tick is 8 ms; all path searches of a tick together get half of it.
    private const double MaxAverageTickMs = 4.0;

    [Theory]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-02")]
    [Trait("Category", "Benchmark")]
    [InlineData(64)]
    [InlineData(0)]
    public void HundredWalkersOn512StayInsideTheTickBudget(int goalRadius)
    {
        TerrainMap terrain = WorldGenTestData.GoldenWorld(0).Terrain;
        StartPoint startPoint = WorldGenTestData.GoldenWorld(0).Start;
        var pathfinder = new Pathfinder(new PassabilityGrid(terrain));
        var config = new PathfinderConfig();
        var scheduler = new PathRequestScheduler(pathfinder, config);
        int mainland = pathfinder.Connectivity.GetComponent(startPoint.X, startPoint.Y);
        var rng = new Xoshiro256StarStar(2026);
        var sink = new WalkerSink(Agents);
        for (int i = 0; i < Agents; i++)
        {
            sink.Position[i] = RandomTileIn(pathfinder, mainland, rng, -1, 0);
        }

        // Each walker asks for a new goal every 10 ticks, staggered: 10 requests per tick on average.
        var stopwatch = new Stopwatch();
        double maxTickMs = 0;
        int maxPending = 0;
        int processed = 0;
        long nodes = 0;
        for (int tick = 0; tick < WarmUpTicks + MeasuredTicks; tick++)
        {
            for (int i = tick % 10; i < Agents; i += 10)
            {
                int goal = RandomTileIn(pathfinder, mainland, rng, goalRadius == 0 ? -1 : sink.Position[i], goalRadius);
                scheduler.Enqueue(new PathRequest(new EntityId(i, 1), sink.Position[i], goal), tick);
            }

            long before = stopwatch.ElapsedTicks;
            stopwatch.Start();
            PathSchedulerTickStats stats = scheduler.ProcessTick(tick, sink);
            stopwatch.Stop();
            if (tick == WarmUpTicks - 1)
            {
                stopwatch.Reset();
                sink.Reset();
                continue;
            }

            if (tick >= WarmUpTicks)
            {
                maxTickMs = Math.Max(maxTickMs, (stopwatch.ElapsedTicks - before) * 1000.0 / Stopwatch.Frequency);
                maxPending = Math.Max(maxPending, stats.Pending);
                processed += stats.Processed;
                nodes += stats.NodesUsed;
            }
        }

        double averageTickMs = stopwatch.Elapsed.TotalMilliseconds / MeasuredTicks;
        string goals = goalRadius == 0 ? "anywhere on the mainland" : $"within {goalRadius} tiles";
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"512², {Agents} walkers, goals {goals}, budget {config.NodeBudgetPerTick} nodes/tick, limit {config.MaxNodesPerRequest}/request: " +
            $"tick avg {averageTickMs:F3} ms, max {maxTickMs:F3} ms; {processed} searches ({sink.Found} found, {sink.Partial} partial, {sink.Failed} failed), " +
            $"{(double)nodes / Math.Max(1, processed):F0} nodes and {stopwatch.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, processed):F1} µs per search, " +
            $"{stopwatch.Elapsed.TotalMilliseconds * 1_000_000.0 / Math.Max(1, nodes):F0} ns per node, max pending {maxPending}"));
        Assert.True(processed > MeasuredTicks, "The scheduler must keep answering.");
        Assert.InRange(averageTickMs, 0.0, MaxAverageTickMs);
    }

    [Theory]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-02")]
    [Trait("Category", "Benchmark")]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleSearchCostOn512(bool withBlocker)
    {
        TerrainMap terrain = WorldGenTestData.GoldenWorld(0).Terrain;

        // An empty blocker layer: measures the cost of consulting it (the building layer of M3 will be one).
        var pathfinder = new Pathfinder(new PassabilityGrid(terrain, withBlocker ? new TestBlocker(terrain.Size) : null));
        StartPoint startPoint = WorldGenTestData.GoldenWorld(0).Start;
        int mainland = pathfinder.Connectivity.GetComponent(startPoint.X, startPoint.Y);
        var rng = new Xoshiro256StarStar(7);
        var path = new List<int>();
        const int Searches = 400;
        var stopwatch = new Stopwatch();
        long nodes = 0;
        int partial = 0;
        double maxMs = 0;
        for (int i = 0; i < Searches; i++)
        {
            int start = RandomTileIn(pathfinder, mainland, rng, -1, 0);
            int goal = RandomTileIn(pathfinder, mainland, rng, -1, 0);
            long before = stopwatch.ElapsedTicks;
            stopwatch.Start();
            PathResult result = pathfinder.FindPath(start, goal, path, PathfinderConfig.MaxNodeLimit);
            stopwatch.Stop();
            maxMs = Math.Max(maxMs, (stopwatch.ElapsedTicks - before) * 1000.0 / Stopwatch.Frequency);
            nodes += result.ExpandedNodes;
            partial += result.Status == PathStatus.Partial ? 1 : 0;
            Assert.Equal(PathStatus.Found, result.Status);
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"512², {Searches} unlimited searches between random mainland tiles{(withBlocker ? " (empty blocker layer)" : "")}: avg {stopwatch.Elapsed.TotalMilliseconds / Searches:F3} ms, max {maxMs:F2} ms, " +
            $"avg {nodes / Searches} nodes, {stopwatch.Elapsed.TotalMilliseconds * 1_000_000.0 / Math.Max(1, nodes):F0} ns per node; A* arrays {pathfinder.SearchMemoryBytes / 1024.0:F0} KiB, " +
            $"connectivity {pathfinder.Connectivity.MemoryBytes / 1024.0:F0} KiB ({pathfinder.Connectivity.RegionCount} regions)"));
        Assert.Equal(0, partial);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Category", "Benchmark")]
    public void PathsAcrossThe2048Map()
    {
        WorldMap world = WorldGenTestData.HugeWorld(1);
        TerrainMap terrain = world.Terrain;
        var pathfinder = new Pathfinder(new PassabilityGrid(terrain));

        var stopwatch = Stopwatch.StartNew();
        pathfinder.Connectivity.Rebuild();
        double buildMs = stopwatch.Elapsed.TotalMilliseconds;
        int mainland = pathfinder.Connectivity.GetComponent(world.Start.X, world.Start.Y);
        var rng = new Xoshiro256StarStar(11);
        var path = new List<int>();
        int size = terrain.Size;
        for (int i = 0; i < 4; i++)
        {
            // Opposite quarters of the map: at least about 1000 tiles apart on each axis.
            int start = RandomTileInBox(pathfinder, mainland, rng, 0, size / 4);
            int goal = RandomTileInBox(pathfinder, mainland, rng, 3 * size / 4, size);
            stopwatch.Restart();
            PathResult result = pathfinder.FindPath(start, goal, path, PathfinderConfig.MaxNodeLimit);
            double ms = stopwatch.Elapsed.TotalMilliseconds;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"2048²: ({start % size},{start / size}) -> ({goal % size},{goal / size}): {result.Status}, {result.Length} tiles, cost {result.Cost}, {result.ExpandedNodes} nodes, {ms:F1} ms"));
            Assert.NotEqual(PathStatus.Unreachable, result.Status);
            Assert.InRange(ms, 0.0, 5000.0);
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"2048²: connectivity build {buildMs:F0} ms, {pathfinder.Connectivity.MemoryBytes / (1024.0 * 1024.0):F2} MiB, {pathfinder.Connectivity.RegionCount} regions; " +
            $"A* arrays {pathfinder.SearchMemoryBytes / (1024.0 * 1024.0):F1} MiB"));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-02")]
    [Trait("Category", "Benchmark")]
    public void ConnectivityBuildAndUpdateAt2048()
    {
        TerrainMap terrain = WorldGenTestData.HugeWorld(1).Terrain;
        int size = terrain.Size;
        var blocker = new TestBlocker(size);
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var connectivity = new ConnectivityMap(new PassabilityGrid(terrain, blocker));
        var stopwatch = Stopwatch.StartNew();
        connectivity.Rebuild();
        double buildMs = stopwatch.Elapsed.TotalMilliseconds;
        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);

        // Single-tile edits, each followed by a query (the worst case: one rebuild per edit).
        var rng = new Xoshiro256StarStar(3);
        const int Edits = 300;
        double maxMs = 0;
        stopwatch.Reset();
        for (int i = 0; i < Edits; i++)
        {
            int x = rng.NextInt(size);
            int y = rng.NextInt(size);
            blocker.Set(x, y, !blocker.IsBlocked(x, y));
            long before = stopwatch.ElapsedTicks;
            stopwatch.Start();
            connectivity.Invalidate(x, y);
            _ = connectivity.GetComponent(x, y);
            stopwatch.Stop();
            maxMs = Math.Max(maxMs, (stopwatch.ElapsedTicks - before) * 1000.0 / Stopwatch.Frequency);
        }

        double updateMs = stopwatch.Elapsed.TotalMilliseconds / Edits;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"2048² connectivity: full build {buildMs:F0} ms, {connectivity.RegionCount} regions, arrays {connectivity.MemoryBytes / (1024.0 * 1024.0):F2} MiB " +
            $"(managed heap +{(heapAfter - heapBefore) / (1024.0 * 1024.0):F1} MiB with the grid's 4 MiB static layer); one-tile update avg {updateMs:F3} ms, max {maxMs:F3} ms"));
        GC.KeepAlive(connectivity);
        Assert.InRange(buildMs, 0.0, 3000.0);
        Assert.InRange(updateMs, 0.0, 5.0);
        Assert.InRange(connectivity.MemoryBytes, 1, 8L * 1024 * 1024);
    }

    private static int RandomTileIn(Pathfinder pathfinder, int component, Xoshiro256StarStar rng, int around, int radius)
    {
        int size = pathfinder.Grid.Size;
        while (true)
        {
            int x;
            int y;
            if (around < 0)
            {
                x = rng.NextInt(size);
                y = rng.NextInt(size);
            }
            else
            {
                x = Math.Clamp((around % size) + rng.NextInt(-radius, radius + 1), 0, size - 1);
                y = Math.Clamp((around / size) + rng.NextInt(-radius, radius + 1), 0, size - 1);
            }

            if (pathfinder.Connectivity.GetComponent(x, y) == component)
            {
                return (y * size) + x;
            }
        }
    }

    private static int RandomTileInBox(Pathfinder pathfinder, int component, Xoshiro256StarStar rng, int from, int to)
    {
        while (true)
        {
            int x = rng.NextInt(from, to);
            int y = rng.NextInt(from, to);
            if (pathfinder.Connectivity.GetComponent(x, y) == component)
            {
                return pathfinder.Grid.Index(x, y);
            }
        }
    }

    /// <summary>Moves each walker to the end of its latest path and counts the outcomes.</summary>
    private sealed class WalkerSink(int agents) : IPathResultSink
    {
        public int[] Position { get; } = new int[agents];

        public int Found { get; private set; }

        public int Partial { get; private set; }

        public int Failed { get; private set; }

        public void Reset() => (Found, Partial, Failed) = (0, 0, 0);

        public void OnPathResult(in PathRequest request, in PathResult result, ReadOnlySpan<int> path)
        {
            switch (result.Status)
            {
                case PathStatus.Found:
                    Found++;
                    break;
                case PathStatus.Partial:
                    Partial++;
                    break;
                default:
                    Failed++;
                    break;
            }

            if (result.HasPath)
            {
                Position[request.Entity.Index] = path[^1];
            }
        }
    }
}
