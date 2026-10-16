using System.Diagnostics;
using Core.Entities;
using Core.Heroes;
using Core.Pathfinding;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Residents;

/// <summary>
/// Smoke checks for NFR-01/NFR-02 with residents (tick within 8 ms on average): the most residents the data allows
/// walking at once across a 1024² map (partial paths replanned on the way), and the cost of the morning arrival tick.
/// Thresholds catch gross regressions only. Excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
public sealed class ResidentBenchmarkTests(ITestOutputHelper output)
{
    private const int Ticks = 600;
    private const double MaxAverageMilliseconds = 8.0;

    [Fact]
    [Trait("Req", "NFR-01")]
    [Trait("Req", "NFR-02")]
    [Trait("Req", "FR-NPC-02")]
    [Trait("Category", "Benchmark")]
    public void TheResidentLimitWalkingAcrossA1024MapStaysInsideTheTickBudget()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, 1024));
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        Build(sim, "campfire");
        int residents = sim.Content.Economy.MaxResidents;
        for (int i = 0; i < residents; i++)
        {
            sim.Enqueue(new SpawnResidentCommand());
        }

        sim.Step();
        GameWorld world = sim.World!;
        Assert.Equal(residents, world.Residents.Count);

        // Each resident walks to its own far tile: a 10 × 10 grid of goals 60 tiles apart around the start.
        PassabilityGrid grid = world.Pathfinder.Grid;
        int from = Arrivals.MeetingTiles(sim)[0];
        long planning = Stopwatch.GetTimestamp();
        for (int i = 0; i < residents; i++)
        {
            int goal = ReachableNear(world, from, grid.XOf(from) + (((i % 10) - 5) * 60), grid.YOf(from) + (((i / 10) - 5) * 60));
            Assert.True(AgentMovement.TryPlanFrom(world, from, goal, 0, sprint: false, out Mover mover));
            world.Movers.Get(world.Residents.Entities[i]) = mover;
        }

        double planned = Stopwatch.GetElapsedTime(planning).TotalMilliseconds;
        double total = 0;
        double worst = 0;
        int walking = 0;
        for (int i = 0; i < Ticks; i++)
        {
            long start = Stopwatch.GetTimestamp();
            sim.Step();
            sim.ComputeHashes();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            worst = Math.Max(worst, elapsed);
            total += elapsed;
            walking += world.Movers.Values.ToArray().Count(m => m.IsMoving);
        }

        double average = total / Ticks;
        output.WriteLine($"{residents} residents: first plans {planned:F1} ms ({planned / residents:F2} ms each), tick average {average:F3} ms, "
            + $"worst {worst:F2} ms, walker-ticks {walking}");
        Assert.True(walking > residents * Ticks / 2, $"Most residents should walk most of the time ({walking} walker-ticks).");
        Assert.InRange(average, 0.0, MaxAverageMilliseconds);
    }

    [Fact]
    [Trait("Req", "NFR-02")]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Category", "Benchmark")]
    public void TheMorningArrivalTickOnA1024MapIsCheap()
    {
        Simulation sim = Simulation.CreateNewGame(Certain, new NewGameOptions(1, 1024));
        Settle(sim, houses: 1);
        StepTo(sim, ArrivalTick(sim, 0) - 11);
        double quiet = 0;
        for (int i = 0; i < 10; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            sim.Step();
            quiet += Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
        }

        // The connectivity map is built on its first query; in a played game the hero's orders have built it long before.
        long build = Stopwatch.GetTimestamp();
        sim.World!.Pathfinder.Connectivity.Rebuild();
        double connectivity = Stopwatch.GetElapsedTime(build).TotalMilliseconds;
        long start = Stopwatch.GetTimestamp();
        sim.Step();
        double arrival = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        Assert.Equal(1, sim.World!.Residents.Count);
        Mover mover = sim.World.Movers.Get(sim.World.Residents.Entities[0]);
        output.WriteLine($"arrival tick {arrival:F2} ms (spawn search and first path of {mover.Path.Count} tiles); connectivity build {connectivity:F2} ms; "
            + $"ticks before it {quiet / 10:F3} ms");
        Assert.InRange(arrival, 0.0, 100.0);
    }

    // The passable tile reachable from `from` nearest to (x, y) by rows in a growing square (clamped to the map).
    private static int ReachableNear(GameWorld world, int from, int x, int y)
    {
        PassabilityGrid grid = world.Pathfinder.Grid;
        for (int ring = 0; ring < grid.Size; ring++)
        {
            for (int ty = y - ring; ty <= y + ring; ty++)
            {
                for (int tx = x - ring; tx <= x + ring; tx++)
                {
                    if (AgentMovement.Distance(tx, ty, x, y) == ring && grid.IsPassable(tx, ty) && world.Pathfinder.IsReachable(from, grid.Index(tx, ty)))
                    {
                        return grid.Index(tx, ty);
                    }
                }
            }
        }

        throw new InvalidOperationException($"Nothing reachable near {x},{y}.");
    }
}
