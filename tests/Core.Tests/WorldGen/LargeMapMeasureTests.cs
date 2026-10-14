using System.Diagnostics;
using System.Globalization;
using Core.Determinism;
using Core.Entities;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// Informational measurements at the player sizes 1024² and 2048² (P-07): save size and save/load time with the fog
/// fresh, partly and fully revealed, fog tick cost with a moving revealer, memory held by a game, biome shares. The
/// printed numbers are the output (run in Release for real figures:
/// <c>CONFIGURATION=Release tools/test.sh --filter Category=Benchmark</c>); the assertions are loose sanity bounds.
/// Excluded with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
[Collection(WorldGenBenchmarkGroup.Name)]
public sealed class LargeMapMeasureTests(ITestOutputHelper output)
{
    private const int FogTicks = 300;

    // NFR-02: the whole tick budget is 8 ms; the fog share at the largest size must stay well inside it.
    private const double MaxFogTickMs = 8.0;

    // The map is regenerated on load, never stored, so a save stays small even with the fog explored in a long band.
    private const int MaxPartialSaveBytes = 64 * 1024;

    [Theory]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Category", "Benchmark")]
    [InlineData(1024)]
    [InlineData(2048)]
    public void SaveSizeAndTime(int size)
    {
        // One game through three fog states (each load regenerates the map, about 7.5 s at 2048² in Debug).
        Simulation walked = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, size));
        Report(size, "fresh", walked);

        // Partly revealed: the hero wanders for 2 x size ticks, one tile per tick, turning to a random one of the eight
        // directions every 16 ticks (a seeded walk, so the explored area is irregular and Brotli cannot fold it away).
        EntityId marker = walked.World!.Revealers.Entities[0];
        var rng = new Xoshiro256StarStar(7);
        Position at = walked.Positions.Get(marker);
        (int x, int y) = (at.TileX, at.TileY);
        (int dx, int dy) = (1, 0);
        for (int i = 0; i < 2 * size; i++)
        {
            if (i % 16 == 0)
            {
                (dx, dy) = TerrainMap.Directions[rng.NextInt(TerrainMap.Directions.Count)];
            }

            x = Math.Clamp(x + dx, 32, size - 33);
            y = Math.Clamp(y + dy, 32, size - 33);
            walked.Positions.Get(marker) = Simulation.TileCenter(x, y);
            walked.Step();
        }

        Core.Fog.FogCounts counts = walked.World.Fog.CountStates();
        int partial = Report(size, string.Create(CultureInfo.InvariantCulture,
            $"wandered (explored {100.0 * (counts.Explored + counts.Visible) / (size * size):F1}%)"), walked);
        Assert.InRange(partial, 1, MaxPartialSaveBytes);

        walked.Enqueue(new RevealAllCommand());
        walked.Step(2);
        Assert.Equal(size * size, walked.World.Fog.CountStates().Explored + walked.World.Fog.CountStates().Visible);
        Report(size, "revealed", walked);
    }

    [Theory]
    [Trait("Req", "NFR-02")]
    [Trait("Category", "Benchmark")]
    [InlineData(1024)]
    [InlineData(2048)]
    public void MovingRevealerTick(int size)
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, size));
        EntityId marker = sim.World!.Revealers.Entities[0];
        Position start = sim.Positions.Get(marker);
        sim.Step(10);
        var stopwatch = new Stopwatch();
        for (int i = 0; i < FogTicks; i++)
        {
            // A square of 64 tiles per side around the start, one tile per tick.
            int step = i % 256;
            int dx = step < 64 ? step : step < 128 ? 64 : step < 192 ? 192 - step : 0;
            int dy = step < 64 ? 0 : step < 128 ? step - 64 : step < 192 ? 64 : 256 - step;
            sim.Positions.Get(marker) = new Position(start.X + ((dx - 32) * Position.UnitsPerTile), start.Y + ((dy - 32) * Position.UnitsPerTile));
            stopwatch.Start();
            sim.Step();
            stopwatch.Stop();
        }

        double perTick = stopwatch.Elapsed.TotalMilliseconds / FogTicks;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{size} moving revealer: {perTick:F3} ms per tick"));
        Assert.InRange(perTick, 0.0, MaxFogTickMs);
    }

    [Theory]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Category", "Benchmark")]
    [InlineData(1024)]
    [InlineData(2048)]
    public void MemoryHeldByAGame(int size)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, size));
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long fogBytes = 2L * sizeof(ulong) * sim.World!.Fog.ExploredBits.Length;

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{size} game: managed heap +{(after - before) / (1024.0 * 1024.0):F1} MiB (fog bits {fogBytes / 1024.0:F0} KiB), process working set {Environment.WorkingSet / (1024.0 * 1024.0):F0} MiB"));
        GC.KeepAlive(sim);

        // A tile costs 13 bytes in the map layers (height, biome, features, river, elevation, moisture, temperature,
        // resource kind and stock) plus 2 bits of fog; allow headroom for the rest of the game.
        Assert.InRange(after - before, 1, 32L * size * size);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Trait("Category", "Benchmark")]
    public void BiomeSharesAt1024(int seed)
    {
        TerrainMap terrain = WorldGenTestData.LargeWorld(seed).Terrain;
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

    private int Report(int size, string label, Simulation sim)
    {
        var stopwatch = Stopwatch.StartNew();
        byte[] bytes = SaveGame.ToBytes(sim);
        double saveMs = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        SaveLoadResult loaded = SaveGame.Read(bytes, SimTestData.Real);
        double loadMs = stopwatch.Elapsed.TotalMilliseconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{size} {label}: save {bytes.Length} bytes, write {saveMs:F0} ms, read (regenerates map) {loadMs:F0} ms"));
        Assert.NotNull(loaded.Simulation);
        Assert.Equal(sim.ComputeHashes().Combined, loaded.Simulation.ComputeHashes().Combined);
        return bytes.Length;
    }
}
