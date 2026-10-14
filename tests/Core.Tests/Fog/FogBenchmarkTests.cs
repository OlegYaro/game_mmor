using System.Diagnostics;
using Core.Entities;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Fog;

/// <summary>
/// NFR-02 share of the fog (budget 8 ms per tick in total): on a 512² map with one revealer that moves every tick
/// (worst case: the fog is recomputed every tick), a tick stays under 2 ms on average. Generous threshold for CI;
/// excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
public sealed class FogBenchmarkTests
{
    private const int Ticks = 1000;
    private const double MaxAverageMilliseconds = 2.0;

    [Fact]
    [Trait("Req", "NFR-02")]
    [Trait("Req", "FR-WORLD-06")]
    [Trait("Category", "Benchmark")]
    public void TickWithAMovingRevealerOnA512MapStaysUnderTwoMilliseconds()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, 512));
        EntityId marker = sim.World!.Revealers.Entities[0];
        Position start = sim.Positions.Get(marker);
        sim.Step(10); // warm up
        long versionBefore = sim.World.Fog.Version;

        var stopwatch = new Stopwatch();
        for (int i = 0; i < Ticks; i++)
        {
            // Walk one tile per tick along a 64-tile square loop, so every tick changes the circle.
            int step = i % 256;
            int dx = step < 64 ? step : step < 128 ? 64 : step < 192 ? 192 - step : 0;
            int dy = step < 64 ? 0 : step < 128 ? step - 64 : step < 192 ? 64 : 256 - step;
            sim.Positions.Get(marker) = new Position(start.X + ((dx - 32) * Position.UnitsPerTile), start.Y + ((dy - 32) * Position.UnitsPerTile));
            stopwatch.Start();
            sim.Step();
            stopwatch.Stop();
        }

        Assert.Equal(versionBefore + Ticks, sim.World.Fog.Version);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds / Ticks, 0.0, MaxAverageMilliseconds);
    }
}
