using System.Diagnostics;
using System.Globalization;
using Core.Entities;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// Informational 1024² measurements (save size and time, fog tick cost, biome shares). The printed numbers are the
/// output; the assertions are loose sanity bounds. Excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
[Collection(WorldGenBenchmarkGroup.Name)]
public sealed class LargeMapMeasureTests(ITestOutputHelper output)
{
    private const int Size = 1024;
    private const int FogTicks = 300;

    [Fact]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Category", "Benchmark")]
    public void SaveSizeAndTimeAt1024()
    {
        Simulation fresh = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, Size));
        Report("fresh", fresh);

        Simulation revealed = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, Size));
        revealed.Enqueue(new RevealAllCommand());
        revealed.Step(2);
        Assert.Equal(Size * Size, revealed.World!.Fog.CountStates().Explored + revealed.World.Fog.CountStates().Visible);
        Report("revealed", revealed);
    }

    [Fact]
    [Trait("Req", "NFR-02")]
    [Trait("Category", "Benchmark")]
    public void MovingRevealerTickAt1024()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, Size));
        EntityId marker = sim.World!.Revealers.Entities[0];
        Position start = sim.Positions.Get(marker);
        sim.Step(10);
        var stopwatch = new Stopwatch();
        for (int i = 0; i < FogTicks; i++)
        {
            int step = i % 256;
            int dx = step < 64 ? step : step < 128 ? 64 : step < 192 ? 192 - step : 0;
            int dy = step < 64 ? 0 : step < 128 ? step - 64 : step < 192 ? 64 : 256 - step;
            sim.Positions.Get(marker) = new Position(start.X + ((dx - 32) * Position.UnitsPerTile), start.Y + ((dy - 32) * Position.UnitsPerTile));
            stopwatch.Start();
            sim.Step();
            stopwatch.Stop();
        }

        output.WriteLine($"1024 moving revealer: {stopwatch.Elapsed.TotalMilliseconds / FogTicks:F3} ms per tick");
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds / FogTicks, 0.0, 8.0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Trait("Category", "Benchmark")]
    public void BiomeSharesAt1024(int seed)
    {
        TerrainMap terrain = WorldGenTestData.Generate((ulong)seed, Size);
        int[] counts = terrain.CountBiomes();
        double total = terrain.TileCount;
        int ocean = 0;
        int lake = 0;
        int river = 0;
        foreach (TileFeatures f in terrain.Features)
        {
            ocean += (f & TileFeatures.Ocean) != 0 ? 1 : 0;
            lake += (f & TileFeatures.Lake) != 0 ? 1 : 0;
            river += (f & TileFeatures.River) != 0 ? 1 : 0;
        }

        string shares = string.Join(", ", Enum.GetValues<Biome>().Where(b => b != Biome.None).Select(b => string.Create(CultureInfo.InvariantCulture, $"{b} {100.0 * counts[(int)b] / total:F1}%")));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed {seed}: {shares}; ocean {100.0 * ocean / total:F1}%, lake {100.0 * lake / total:F1}%, river {100.0 * river / total:F1}%"));
        Assert.Equal(terrain.TileCount, counts.Sum());
    }

    private void Report(string label, Simulation sim)
    {
        var stopwatch = Stopwatch.StartNew();
        byte[] bytes = SaveGame.ToBytes(sim);
        double saveMs = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        SaveLoadResult loaded = SaveGame.Read(bytes, SimTestData.Real);
        double loadMs = stopwatch.Elapsed.TotalMilliseconds;
        output.WriteLine($"1024 {label}: save {bytes.Length} bytes, write {saveMs:F0} ms, read (regenerates map) {loadMs:F0} ms");
        Assert.NotNull(loaded.Simulation);
        Assert.Equal(sim.ComputeHashes().Combined, loaded.Simulation.ComputeHashes().Combined);
    }
}
