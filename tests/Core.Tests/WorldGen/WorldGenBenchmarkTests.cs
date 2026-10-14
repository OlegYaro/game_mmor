using System.Diagnostics;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// Smoke checks for FR-WORLD-02 (1024² world in ≤ 30 s) over the whole pipeline (<see cref="WorldGenerator.Generate"/>,
/// ADR 0006 steps 1–9). Thresholds are generous so a loaded CI machine in Debug never fails them; the printed times
/// are the useful part. Excluded with <c>--filter "Category!=Benchmark"</c>. Runs alone, after the parallel tests, so
/// the timing is not distorted by other generators running at the same time.
/// </summary>
[Collection(WorldGenBenchmarkGroup.Name)]
public sealed class WorldGenBenchmarkTests(ITestOutputHelper output)
{
    private const double MaxSeconds512 = 10.0;

    // The FR-WORLD-02 budget for 1024² (v1); measured in M1 so the risk is visible early.
    private const double MaxSeconds1024 = 30.0;

    [Fact]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Category", "Benchmark")]
    public void World512IsFast() => Measure(512, MaxSeconds512);

    [Fact]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Category", "Benchmark")]
    public void World1024StaysWithinTheBudget()
    {
        WorldMap world = Measure(1024, MaxSeconds1024);

        Assert.Equal(1024, world.Size);
        TerrainInvariants.AssertAll(world.Terrain);
        WorldInvariants.AssertAll(world, WorldGenTestData.Params);
    }

    private WorldMap Measure(int size, double maxSeconds)
    {
        WorldGenTestData.GenerateWorld(77, 64); // JIT warm-up
        var stopwatch = Stopwatch.StartNew();
        TerrainMap terrain = WorldGenTestData.Generate(42, size);
        double terrainMs = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        WorldMap world = WorldGenTestData.GenerateWorld(42, size);
        double worldMs = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        ulong hash = world.ComputeHash();
        double hashMs = stopwatch.Elapsed.TotalMilliseconds;
        output.WriteLine($"{size}x{size}: terrain {terrainMs:F0} ms, whole pipeline {worldMs:F0} ms (placement steps ~{worldMs - terrainMs:F0} ms), map hash {hashMs:F0} ms");

        Assert.Equal(terrain.ComputeHash(), world.Terrain.ComputeHash());
        Assert.NotEqual(0UL, hash);
        Assert.InRange((worldMs + hashMs) / 1000.0, 0.0, maxSeconds);
        return world;
    }
}

/// <summary>Non-parallel collection of the world generation benchmarks.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorldGenBenchmarkGroup
{
    public const string Name = "World generation benchmarks";
}
