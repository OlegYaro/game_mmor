using Core.Entities;
using Core.Pathfinding;

namespace Core.Tests.Pathfinding;

/// <summary>Records every answer of a scheduler tick.</summary>
internal sealed class RecordingSink : IPathResultSink
{
    public List<(PathRequest Request, PathResult Result, int[] Path)> Results { get; } = [];

    public Action? OnResult { get; set; }

    public void OnPathResult(in PathRequest request, in PathResult result, ReadOnlySpan<int> path)
    {
        Results.Add((request, result, path.ToArray()));
        OnResult?.Invoke();
    }
}

public sealed class PathRequestSchedulerTests
{
    private static readonly PathfinderConfig _roomy = new(MaxNodesPerRequest: 10_000, NodeBudgetPerTick: 1_000_000, ReplanIntervalTicks: 10);

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-04")]
    public void RequestsRunByPriorityThenEntityThenSequence()
    {
        TestMap map = PathTestMaps.Open(16);
        var scheduler = new PathRequestScheduler(map.Pathfinder, _roomy);
        var sink = new RecordingSink();

        scheduler.Enqueue(new PathRequest(new EntityId(5, 1), 0, 10), 0);
        scheduler.Enqueue(new PathRequest(new EntityId(2, 1), 0, 11), 0);
        scheduler.Enqueue(new PathRequest(new EntityId(9, 1), 0, 12, Priority: 3), 0);
        scheduler.Enqueue(new PathRequest(new EntityId(2, 2), 0, 13), 0);
        scheduler.Enqueue(new PathRequest(new EntityId(7, 1), 0, 14, Priority: -1), 0);
        scheduler.Enqueue(new PathRequest(new EntityId(1, 1), 0, 15, Priority: 3), 0);
        Assert.Equal(6, scheduler.PendingCount);

        PathSchedulerTickStats stats = scheduler.ProcessTick(0, sink);

        Assert.Equal([15, 12, 11, 13, 10, 14], sink.Results.Select(r => r.Request.Goal));
        Assert.Equal(6, stats.Processed);
        Assert.Equal(0, stats.Pending);
        Assert.Equal(sink.Results.Sum(r => Math.Max(1, r.Result.ExpandedNodes)), stats.NodesUsed);
        Assert.All(sink.Results, r => Assert.Equal(PathStatus.Found, r.Result.Status));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ResultsAreTheSameAsADirectSearch()
    {
        TestMap map = PathTestMaps.Random(3, 40, blockedPermille: 200);
        var scheduler = new PathRequestScheduler(map.Pathfinder, _roomy);
        var sink = new RecordingSink();
        var requests = new List<PathRequest>();
        for (int i = 0; i < 30; i++)
        {
            var request = new PathRequest(new EntityId(i, 1), (i * 53) % 1600, (i * 197 + 11) % 1600, i % 3 == 0 ? PathGoalKind.Adjacent : PathGoalKind.Tile);
            requests.Add(request);
            scheduler.Enqueue(request, 0);
        }

        scheduler.ProcessTick(0, sink);

        var path = new List<int>();
        Assert.Equal(30, sink.Results.Count);
        foreach ((PathRequest request, PathResult result, int[] got) in sink.Results)
        {
            PathResult direct = map.Pathfinder.Find(request.Start, request.Goal, request.Kind, path, _roomy.MaxNodesPerRequest);
            Assert.Equal(direct, result);
            Assert.Equal(path, got);
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-02")]
    public void NodeBudgetSpreadsRequestsOverTicks()
    {
        TestMap map = PathTestMaps.Open(32);
        var config = new PathfinderConfig(MaxNodesPerRequest: 1000, NodeBudgetPerTick: 50, ReplanIntervalTicks: 10);
        var scheduler = new PathRequestScheduler(map.Pathfinder, config);
        var sink = new RecordingSink();

        // Each request walks 31 diagonal steps: 31 expanded nodes, so a budget of 50 runs two per tick (the second one overruns).
        for (int i = 0; i < 5; i++)
        {
            scheduler.Enqueue(new PathRequest(new EntityId(i, 1), 0, (32 * 32) - 1), 0);
        }

        PathSchedulerTickStats first = scheduler.ProcessTick(0, sink);
        Assert.Equal(new PathSchedulerTickStats(2, 62, 3), first);
        PathSchedulerTickStats second = scheduler.ProcessTick(1, sink);
        Assert.Equal(new PathSchedulerTickStats(2, 62, 1), second);
        PathSchedulerTickStats third = scheduler.ProcessTick(2, sink);
        Assert.Equal(new PathSchedulerTickStats(1, 31, 0), third);
        Assert.Equal([0, 1, 2, 3, 4], sink.Results.Select(r => r.Request.Entity.Index));
        Assert.Equal(new PathSchedulerTickStats(0, 0, 0), scheduler.ProcessTick(3, sink));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void FailedSearchesCountOneNode()
    {
        TestMap map = PathTestMaps.Parse(
            ".r.",
            ".r.",
            ".r.");
        var scheduler = new PathRequestScheduler(map.Pathfinder, new PathfinderConfig(100, 2, 0));
        var sink = new RecordingSink();
        for (int i = 0; i < 3; i++)
        {
            scheduler.Enqueue(new PathRequest(new EntityId(i, 1), 0, 2), 0);
        }

        Assert.Equal(new PathSchedulerTickStats(2, 2, 1), scheduler.ProcessTick(0, sink));
        Assert.All(sink.Results, r => Assert.Equal(PathStatus.Unreachable, r.Result.Status));
        Assert.All(sink.Results, r => Assert.Empty(r.Path));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void AWalkerReplansAtMostOncePerIntervalUnlessForced()
    {
        TestMap map = PathTestMaps.Open(8);
        var scheduler = new PathRequestScheduler(map.Pathfinder, _roomy);
        var sink = new RecordingSink();
        var hero = new EntityId(1, 1);
        var other = new EntityId(2, 1);

        scheduler.Enqueue(new PathRequest(hero, 0, 9), 0);
        scheduler.ProcessTick(0, sink);
        Assert.Single(sink.Results);

        // Ordinary replan at tick 3 waits until tick 10; another walker is not held up.
        scheduler.Enqueue(new PathRequest(hero, 0, 20), 3);
        scheduler.Enqueue(new PathRequest(other, 0, 21), 3);
        PathSchedulerTickStats stats = scheduler.ProcessTick(3, sink);
        Assert.Equal(1, stats.Processed);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(21, sink.Results[^1].Request.Goal);
        for (long tick = 4; tick < 10; tick++)
        {
            Assert.Equal(0, scheduler.ProcessTick(tick, sink).Processed);
        }

        Assert.Equal(1, scheduler.ProcessTick(10, sink).Processed);
        Assert.Equal(20, sink.Results[^1].Request.Goal);

        // The path became invalid: a forced request runs at once.
        scheduler.Enqueue(new PathRequest(hero, 0, 30, Forced: true), 11);
        Assert.Equal(1, scheduler.ProcessTick(11, sink).Processed);
        Assert.Equal(30, sink.Results[^1].Request.Goal);

        // A late enqueue whose interval is already over is ready at once.
        scheduler.Enqueue(new PathRequest(hero, 0, 31), 40);
        Assert.Equal(1, scheduler.ProcessTick(40, sink).Processed);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ANewRequestReplacesThePendingOneOfTheSameWalker()
    {
        TestMap map = PathTestMaps.Open(8);
        var scheduler = new PathRequestScheduler(map.Pathfinder, _roomy);
        var sink = new RecordingSink();
        var hero = new EntityId(1, 1);

        long first = scheduler.Enqueue(new PathRequest(hero, 0, 9), 0);
        long second = scheduler.Enqueue(new PathRequest(hero, 0, 18), 0);
        Assert.True(second > first);
        Assert.Equal(1, scheduler.PendingCount);

        scheduler.ProcessTick(0, sink);
        Assert.Equal(18, Assert.Single(sink.Results).Request.Goal);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void CancelAndForgetDropRequestsAndHistory()
    {
        TestMap map = PathTestMaps.Open(8);
        var scheduler = new PathRequestScheduler(map.Pathfinder, _roomy);
        var sink = new RecordingSink();
        var hero = new EntityId(1, 1);

        scheduler.Enqueue(new PathRequest(hero, 0, 9), 0);
        Assert.True(scheduler.Cancel(hero));
        Assert.False(scheduler.Cancel(hero));
        Assert.Equal(0, scheduler.ProcessTick(0, sink).Processed);

        scheduler.Enqueue(new PathRequest(hero, 0, 9), 0);
        scheduler.ProcessTick(0, sink);
        scheduler.Enqueue(new PathRequest(hero, 0, 10), 1);
        scheduler.Forget(hero);
        Assert.Equal(0, scheduler.PendingCount);

        // Forgotten: the replan interval no longer applies.
        scheduler.Enqueue(new PathRequest(hero, 0, 11), 2);
        Assert.Equal(1, scheduler.ProcessTick(2, sink).Processed);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void TheSinkCannotChangeTheSchedulerDuringATick()
    {
        TestMap map = PathTestMaps.Open(8);
        var scheduler = new PathRequestScheduler(map.Pathfinder, _roomy);
        var sink = new RecordingSink();
        var hero = new EntityId(1, 1);
        scheduler.Enqueue(new PathRequest(hero, 0, 9), 0);
        sink.OnResult = () => scheduler.Enqueue(new PathRequest(hero, 0, 10), 0);

        Assert.Throws<InvalidOperationException>(() => scheduler.ProcessTick(0, sink));

        sink.OnResult = () => scheduler.Cancel(hero);
        scheduler.Enqueue(new PathRequest(hero, 0, 9, Forced: true), 1);
        Assert.Throws<InvalidOperationException>(() => scheduler.ProcessTick(1, sink));

        sink.OnResult = () => scheduler.ProcessTick(2, new RecordingSink());
        scheduler.Enqueue(new PathRequest(hero, 0, 9, Forced: true), 2);
        Assert.Throws<InvalidOperationException>(() => scheduler.ProcessTick(2, sink));

        // After a failed tick the scheduler is usable again.
        sink.OnResult = null;
        scheduler.Enqueue(new PathRequest(hero, 0, 9, Forced: true), 3);
        Assert.Equal(1, scheduler.ProcessTick(3, sink).Processed);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void TheSameRequestsGiveTheSameLog()
    {
        static List<string> Run()
        {
            TestMap map = PathTestMaps.Random(11, 48, blockedPermille: 200);
            var scheduler = new PathRequestScheduler(map.Pathfinder, new PathfinderConfig(400, 900, 5));
            var sink = new RecordingSink();
            var log = new List<string>();
            for (long tick = 0; tick < 60; tick++)
            {
                for (int e = 0; e < 12; e++)
                {
                    if ((tick + e) % 4 == 0)
                    {
                        int goal = (int)((tick * 131 + e * 977) % (48 * 48));
                        scheduler.Enqueue(new PathRequest(new EntityId(e, 1), (e * 191) % (48 * 48), goal, Priority: e % 2, Forced: e == 3), tick);
                    }
                }

                PathSchedulerTickStats stats = scheduler.ProcessTick(tick, sink);
                log.Add($"{tick}: {stats}");
            }

            log.AddRange(sink.Results.Select(r => $"{r.Request} {r.Result} {string.Join(',', r.Path)}"));
            return log;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ArgumentsAndConfigAreChecked()
    {
        TestMap map = PathTestMaps.Open(4);
        Assert.Throws<ArgumentNullException>(() => new PathRequestScheduler(null!, _roomy));
        Assert.Throws<ArgumentNullException>(() => new PathRequestScheduler(map.Pathfinder, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathRequestScheduler(map.Pathfinder, new PathfinderConfig(0, 1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathRequestScheduler(map.Pathfinder, new PathfinderConfig(PathfinderConfig.MaxNodeLimit + 1, 1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathRequestScheduler(map.Pathfinder, new PathfinderConfig(1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathRequestScheduler(map.Pathfinder, new PathfinderConfig(1, 1, -1)));
        new PathfinderConfig().Validate();

        var scheduler = new PathRequestScheduler(map.Pathfinder, _roomy);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Enqueue(new PathRequest(new EntityId(0, 1), 0, 16), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Enqueue(new PathRequest(new EntityId(0, 1), -1, 0), 0));
        Assert.Throws<ArgumentNullException>(() => scheduler.ProcessTick(0, null!));
    }
}
