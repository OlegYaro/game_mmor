using System.Diagnostics;
using Core.Entities;
using Core.Jobs;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Jobs;

/// <summary>
/// NFR-01/NFR-02 with work (tick within 8 ms on average): the most residents the data allows, working on a 1024² map
/// (looking for jobs, walking, chopping, quarrying, foraging, farming, hauling to storages). Thresholds catch gross
/// regressions only. Excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
public sealed class JobBenchmarkTests(ITestOutputHelper output)
{
    private const int WarmupTicks = 100;
    private const int Ticks = 3000;
    private const double MaxAverageMilliseconds = 8.0;

    private static readonly Lazy<Core.Content.ContentCatalog> _roomy = new(() => Residents.ResidentTestWorld.WithEconomy(json =>
        json.Replace("\"storageStacksPerItem\": 4", "\"storageStacksPerItem\": 20", StringComparison.Ordinal)));

    [Fact]
    [Trait("Req", "NFR-01")]
    [Trait("Req", "NFR-02")]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Category", "Benchmark")]
    public void TheResidentLimitAtWorkOnA1024MapStaysInsideTheTickBudget()
    {
        // The storages' per-item cap is lifted: the benchmark measures the cost of the work, not when the storages fill up.
        Simulation sim = Simulation.CreateNewGame(_roomy.Value, new NewGameOptions(1, 1024));
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        Build(sim, "campfire");
        for (int i = 0; i < 4; i++)
        {
            Build(sim, "storage");
        }

        for (int i = 0; i < 4; i++)
        {
            Build(sim, "field");
        }

        int residents = sim.Content.Economy.MaxResidents;
        for (int i = 0; i < residents; i++)
        {
            sim.Enqueue(new SpawnResidentCommand());
        }

        GameWorld world = sim.World!;
        double first = 0;
        double total = 0;
        double worst = 0;
        long worstTick = 0;
        int working = 0;
        var slow = new List<(long Tick, double Ms)>();
        for (int i = 0; i < WarmupTicks + Ticks; i++)
        {
            long start = Stopwatch.GetTimestamp();
            sim.Step();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (i == 0)
            {
                first = elapsed;
            }

            if (i < WarmupTicks)
            {
                continue;
            }

            total += elapsed;
            if (elapsed > 3)
            {
                slow.Add((sim.Tick, elapsed));
            }

            if (elapsed > worst)
            {
                (worst, worstTick) = (elapsed, sim.Tick);
            }

            working += world.Jobs.Values.ToArray().Count(j => j.Kind != JobKind.None);
        }

        double average = total / Ticks;
        int stored = world.Buildings.Entities.ToArray().Where(world.Inventories.Has).Sum(b => world.Inventories.Get(b).Backpack.Sum(s => s.Count));
        output.WriteLine($"{residents} residents at work: spawn tick {first:F1} ms, tick average {average:F3} ms, worst {worst:F2} ms (tick {worstTick}), "
            + $"resident-ticks with a job {working} of {residents * Ticks}, items stored {stored}, era {Core.Buildings.Construction.CurrentEra(sim).Id}");
        output.WriteLine($"ticks over 3 ms: {slow.Count}: " + string.Join(", ", slow.Select(s => $"{s.Tick}:{s.Ms:F1}")));
        Assert.Equal(residents, world.Residents.Count);
        Assert.True(working > residents * Ticks / 2, $"Most residents should hold a job most of the time ({working}).");
        Assert.InRange(average, 0.0, MaxAverageMilliseconds);
    }
}
