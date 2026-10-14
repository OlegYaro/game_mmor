using Core.Determinism;
using Core.Entities;
using Core.Replay;
using Core.Sim;
using Core.Tests.Content;
using Core.Tests.Sim;

namespace Core.Tests.Replay;

/// <summary>
/// Golden files in <c>tests/Core.Tests/Golden/</c>, read straight from the repository (NFR-04, ADR 0005).
/// </summary>
/// <remarks>
/// <para>Goldens are never rewritten by a normal test run. To regenerate them on purpose (after an intended change
/// to the simulation, the hash layout, or data/), run:</para>
/// <code>UPDATE_GOLDEN=1 tools/test.sh          (Windows: $env:UPDATE_GOLDEN=1; tools/test.ps1)</code>
/// <para>then review the diff and commit it. Past-version save samples (<c>save_v*.sav</c>) are only created when
/// missing and never overwritten, even with UPDATE_GOLDEN=1: they must load forever (ADR 0007).</para>
/// </remarks>
internal static class GoldenFiles
{
    public const string UpdateVariable = "UPDATE_GOLDEN";

    private static readonly Lazy<ReplayGolden> _basic = new(() => LoadOrUpdate(
        "replay_basic", BasicReplayScript, sampleInterval: 10));

    private static readonly Lazy<ReplayGolden> _scenario = new(() => LoadOrUpdate(
        "scenario_m0", ScenarioReplayScript, sampleInterval: 250));

    /// <summary>True when the run may (re)write goldens.</summary>
    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string Directory => Path.Combine(TestPaths.Root, "tests", "Core.Tests", "Golden");

    /// <summary>Mixed spawn/destroy commands over a few hundred ticks; hashes every 10 ticks.</summary>
    public static ReplayGolden Basic => _basic.Value;

    /// <summary>M0 acceptance scenario: 10 000 ticks with scheduled commands; hashes every 250 ticks.</summary>
    public static ReplayGolden Scenario => _scenario.Value;

    public static string PathOf(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>Reads a golden file, failing with instructions when it does not exist.</summary>
    public static byte[] ReadBytes(string fileName)
    {
        string path = PathOf(fileName);
        Assert.True(File.Exists(path), $"Golden file {path} is missing. Generate it deliberately with {UpdateVariable}=1 tools/test.sh.");
        return File.ReadAllBytes(path);
    }

    public static string ReadText(string fileName) => System.Text.Encoding.UTF8.GetString(ReadBytes(fileName));

    // Only called when Updating. LF line ends regardless of platform.
    public static void WriteText(string fileName, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathOf(fileName), text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>Runs a replay to its end from a new simulation on the real data/ and returns every tick's hashes.</summary>
    public static HashTrace RunDense(ReplayFile replay)
    {
        ReplayPlayer player = ReplayPlayer.Start(replay, SimTestData.Real);
        player.RunToEnd();
        return player.Hashes;
    }

    private static ReplayGolden LoadOrUpdate(string name, Func<ReplayFile> script, int sampleInterval)
    {
        string replayFile = name + ".json";
        string hashFile = name + ".hashes.txt";
        if (Updating)
        {
            ReplayFile generated = script();
            HashTrace hashes = RunDense(generated).Sample(sampleInterval);
            WriteText(replayFile, generated.ToJson());
            WriteText(hashFile, hashes.ToText());
        }

        return new ReplayGolden(ReplayFile.Parse(ReadText(replayFile)), HashTrace.Parse(ReadText(hashFile)));
    }

    private static ReplayFile BasicReplayScript() =>
        RecordRandomCommands(seed: 0xB1A5ED, scriptSeed: 1, endTick: 300, spawnPermille: 200, destroyPermille: 120);

    private static ReplayFile ScenarioReplayScript() =>
        RecordRandomCommands(seed: 0x5CE7A410, scriptSeed: 2, endTick: 10_000, spawnPermille: 8, destroyPermille: 5);

    /// <summary>
    /// Records a replay by playing a seeded random script: per tick it may spawn markers (sometimes two in one tick),
    /// destroy a live entity, or destroy a stale id (which the simulation must ignore).
    /// </summary>
    private static ReplayFile RecordRandomCommands(ulong seed, ulong scriptSeed, long endTick, int spawnPermille, int destroyPermille)
    {
        var sim = new Simulation(seed, SimTestData.Real);
        var recorder = new ReplayRecorder(sim);
        var script = new Xoshiro256StarStar(scriptSeed);
        var destroyed = new List<EntityId>();
        while (sim.Tick < endTick)
        {
            int roll = script.NextPermille();
            if (roll < spawnPermille)
            {
                int count = script.ChancePercent(20) ? 2 : 1;
                for (int i = 0; i < count; i++)
                {
                    recorder.Enqueue(new SpawnMarkerCommand(new Position(script.NextInt(-4096, 4096), script.NextInt(-4096, 4096))));
                }
            }
            else if (roll < spawnPermille + destroyPermille)
            {
                EntityId[] alive = sim.Entities.Registry.AliveEntities().ToArray();
                if (destroyed.Count > 0 && script.ChancePercent(15))
                {
                    recorder.Enqueue(new DestroyEntityCommand(destroyed[script.NextInt(destroyed.Count)]));
                }
                else if (alive.Length > 0)
                {
                    EntityId victim = alive[script.NextInt(alive.Length)];
                    destroyed.Add(victim);
                    recorder.Enqueue(new DestroyEntityCommand(victim));
                }
            }

            sim.Step();
        }

        return recorder.ToReplay();
    }
}

/// <summary>A replay and its expected (sampled) hashes.</summary>
internal sealed record ReplayGolden(ReplayFile Replay, HashTrace Expected);
