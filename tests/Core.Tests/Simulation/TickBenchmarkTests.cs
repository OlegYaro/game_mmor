using System.Diagnostics;
using Core.Entities;
using Core.Sim;

namespace Core.Tests.Sim;

/// <summary>
/// Smoke check for NFR-02 (tick within 8 ms). The threshold is generous on purpose: this only catches gross
/// regressions on a loaded CI machine, it is not a measurement. Excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
public sealed class TickBenchmarkTests
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
}
