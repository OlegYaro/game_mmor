using System.Diagnostics;
using Core.Entities;
using Core.Sim;

namespace Core.Tests.Sim;

/// <summary>
/// Smoke check for NFR-02 (tick within 8 ms). The threshold is generous on purpose: this only catches gross
/// regressions on a loaded CI machine, it is not a measurement. Excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
public sealed class TickBenchmarkTests(ITestOutputHelper output)
{
    private const int Entities = 500;
    private const int Ticks = 1000;
    private const double MaxAverageMilliseconds = 8.0;

    [Fact]
    [Trait("Req", "NFR-02")]
    [Trait("Category", "Benchmark")]
    public void AverageTickStaysUnderEightMilliseconds()
    {
        var sim = new Simulation(1, SimTestData.Real);
        for (int i = 0; i < Entities; i++)
        {
            sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(i % 64, i / 64)));
        }

        sim.Step();
        Assert.Equal(Entities, sim.Positions.Count);

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < Ticks; i++)
        {
            sim.Step();
            sim.ComputeHashes();
        }

        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds / Ticks, 0.0, MaxAverageMilliseconds);
    }

    // M2: a 1024² game where the hero keeps walking (sprinting) across the revealed map, so long paths run out of the
    // node limit and are planned again on the way, then gathers; 500 markers as in the M0 check; hash every tick.
    [Fact]
    [Trait("Req", "NFR-02")]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Category", "Benchmark")]
    public void GameTickWithAWalkingAndGatheringHeroStaysUnderEightMilliseconds()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, 1024));
        sim.Enqueue(new RevealAllCommand());
        for (int i = 0; i < Entities; i++)
        {
            sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(i % 64, i / 64)));
        }

        sim.Step();
        GameWorld world = sim.World!;
        (int X, int Y)[] targets = [.. FarTargets(world)];
        Assert.NotEmpty(targets);

        double total = 0;
        double worst = 0;
        int order = 0;
        int walking = 0;
        for (int i = 0; i < Ticks * 3; i++)
        {
            Core.Heroes.Mover mover = world.Movers.Get(world.HeroEntity);
            walking += mover.IsMoving ? 1 : 0;
            if (!mover.IsMoving && world.Heroes.Get(world.HeroEntity).Activity != Core.Heroes.HeroActivity.Gathering)
            {
                (int x, int y) = targets[order++ % targets.Length];
                sim.Enqueue(order % 3 == 0 ? new GatherCommand(x, y) : new MoveHeroCommand(x, y, Sprint: order % 2 == 0));
            }

            long start = Stopwatch.GetTimestamp();
            sim.Step();
            sim.ComputeHashes();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            worst = Math.Max(worst, elapsed);
            total += elapsed;
        }

        double average = total / (Ticks * 3);
        output.WriteLine($"orders {order} (last {world.Heroes.Get(world.HeroEntity).LastOrder}), average {average:F3} ms, worst {worst:F2} ms, "
            + $"walking ticks {walking}, changed tiles {world.Tiles.ChangedResourceTiles}, targets {targets.Length}");
        Assert.True(walking > Ticks, $"The hero should walk most of the time (walked {walking} ticks).");
        Assert.InRange(average, 0.0, MaxAverageMilliseconds);
    }

    // Trees spread over the map, each with a neighbour reachable from the hero: for each of four points far from the
    // start, the first such tree scanning rows from the point.
    private static IEnumerable<(int X, int Y)> FarTargets(GameWorld world)
    {
        int size = world.Size;
        Core.Pathfinding.Pathfinder pathfinder = world.Pathfinder;
        Position hero = world.Map.Start is var s ? Simulation.TileCenter(s.X, s.Y) : default;
        int from = pathfinder.Grid.Index(hero.TileX, hero.TileY);
        foreach ((int px, int py) in new[] { (size / 8, size / 8), (size * 7 / 8, size / 8), (size * 7 / 8, size * 7 / 8), (size / 8, size * 7 / 8) })
        {
            (int X, int Y)? found = null;
            for (int y = py; y < py + 64 && found is null; y++)
            {
                for (int x = px; x < px + 64 && found is null; x++)
                {
                    if (world.Map.GetResource(x, y) == Core.WorldGen.ResourceKind.Tree && pathfinder.IsReachable(from, pathfinder.Grid.Index(x + 1, y)))
                    {
                        found = (x, y);
                    }
                }
            }

            if (found is { } tile)
            {
                yield return tile;
            }
        }
    }
}
