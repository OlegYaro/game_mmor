using Core.Determinism;
using Core.Entities;
using Core.Replay;
using Core.Sim;
using Core.Tests.Content;
using Core.Tests.Sim;
using Core.WorldGen;

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

    private static readonly Lazy<ReplayGolden> _world = new(() => LoadOrUpdate(
        "scenario_world_m1", WorldReplayScript, sampleInterval: 250));

    private static readonly Lazy<ReplayGolden> _m2 = new(() => LoadOrUpdate(
        "scenario_m2", M2Scenario.Record, sampleInterval: 50));

    private static readonly Lazy<ReplayGolden> _residents = new(() => LoadOrUpdate(
        "scenario_m3a_residents", ResidentScenario.Record, sampleInterval: 500));

    /// <summary>True when the run may (re)write goldens.</summary>
    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string Directory => Path.Combine(TestPaths.Root, "tests", "Core.Tests", "Golden");

    /// <summary>Mixed spawn/destroy commands over a few hundred ticks; hashes every 10 ticks.</summary>
    public static ReplayGolden Basic => _basic.Value;

    /// <summary>M0 acceptance scenario: 10 000 ticks with scheduled commands; hashes every 250 ticks.</summary>
    public static ReplayGolden Scenario => _scenario.Value;

    /// <summary>
    /// M1 scenario: a new game on a 64² world (hard), through nightfall (fog radius halves) and dawn, with markers,
    /// destroys and a <c>reveal</c> after nightfall; hashes every 250 ticks. Since M2 the hero also walks (and
    /// sprints), gathers, gets items, is stopped and receives rejected orders (<see cref="WorldHeroTicks"/>).
    /// </summary>
    public static ReplayGolden World => _world.Value;

    /// <summary>M2 acceptance scenario (<see cref="M2Scenario"/>): gathering, campfire, house, storage, upgrade, move; hashes every 50 ticks.</summary>
    public static ReplayGolden M2 => _m2.Value;

    /// <summary>M3a residents scenario (<see cref="ResidentScenario"/>): a settlement, then newcomers over several mornings; hashes every 500 ticks.</summary>
    public static ReplayGolden Residents => _residents.Value;

    /// <summary>New-game parameters of <see cref="World"/>.</summary>
    public static NewGameOptions WorldOptions { get; } = new(0x3011D_F06, 64, Difficulty.Hard);

    /// <summary>Tick at which <see cref="World"/> reveals the map (after nightfall at tick 6000).</summary>
    public const long WorldRevealTick = 7000;

    /// <summary>
    /// Ticks (before the step) at which the <see cref="World"/> script orders the hero: gather the nearest tree, sprint
    /// away, receive stone, an order outside the map (rejected), gather a stone or bush, gather a tree farther away shortly
    /// before the save at tick 6500 (so the hero walks across the save and gathers after it), stop.
    /// </summary>
    public static IReadOnlyList<long> WorldHeroTicks { get; } = [20, 1500, 2000, 2600, 3000, 6490, 8200];

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
        RecordRandomCommands(new Simulation(0xB1A5ED, SimTestData.Real), scriptSeed: 1, endTick: 300, spawnPermille: 200, destroyPermille: 120);

    private static ReplayFile ScenarioReplayScript() =>
        RecordRandomCommands(new Simulation(0x5CE7A410, SimTestData.Real), scriptSeed: 2, endTick: 10_000, spawnPermille: 8, destroyPermille: 5);

    // The hero (the only revealer) is entity 0; it is never destroyed, so the fog keeps a source. Hero targets are
    // computed from the running simulation, so they are always explored and reachable.
    private static ReplayFile WorldReplayScript()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, WorldOptions);
        return RecordRandomCommands(sim, scriptSeed: 3, endTick: 9_500, spawnPermille: 6, destroyPermille: 4,
            keepAlive: new EntityId(0, 1), extra: (tick, recorder) =>
            {
                if (tick == WorldRevealTick - 1)
                {
                    recorder.Enqueue(new RevealAllCommand());
                }

                ISimCommand? hero = Array.IndexOf([.. WorldHeroTicks], tick) switch
                {
                    0 => Gather(Heroes.HeroTestWorld.NearestNode(sim, ResourceKind.Tree)),
                    1 => Walk(Heroes.HeroTestWorld.StraightTarget(sim, 9), sprint: true),
                    2 => new GiveItemCommand("stone", 200),
                    3 => new MoveHeroCommand(-1, 3),
                    4 => Gather(NodeOrTree(sim, ResourceKind.Stone, ResourceKind.BerryBush)),
                    5 => Gather(Heroes.HeroTestWorld.NearestNode(sim, ResourceKind.Tree, minDistance: 8)),
                    6 => new StopHeroCommand(),
                    _ => null,
                };
                if (hero is not null)
                {
                    recorder.Enqueue(hero);
                }
            });

        static GatherCommand Gather((int X, int Y) tile) => new(tile.X, tile.Y);

        static MoveHeroCommand Walk((int X, int Y) tile, bool sprint) => new(tile.X, tile.Y, sprint);

        static (int X, int Y) NodeOrTree(Simulation sim, params ResourceKind[] kinds)
        {
            foreach (ResourceKind kind in kinds)
            {
                try
                {
                    return Heroes.HeroTestWorld.NearestNode(sim, kind);
                }
                catch (InvalidOperationException)
                {
                    // None of this kind in sight; try the next.
                }
            }

            return Heroes.HeroTestWorld.NearestNode(sim, ResourceKind.Tree);
        }
    }

    /// <summary>
    /// Records a replay by playing a seeded random script: per tick it may spawn markers (sometimes two in one tick),
    /// destroy a live entity, or destroy a stale id (which the simulation must ignore).
    /// </summary>
    /// <remarks>
    /// <paramref name="keepAlive"/> is never chosen as a victim; <paramref name="extra"/> may enqueue more commands
    /// each tick (called with the current tick, after the random ones). Both default to no-ops, which keeps the M0
    /// scripts (and their goldens) unchanged.
    /// </remarks>
    private static ReplayFile RecordRandomCommands(Simulation sim, ulong scriptSeed, long endTick, int spawnPermille, int destroyPermille,
        EntityId? keepAlive = null, Action<long, ReplayRecorder>? extra = null)
    {
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
                EntityId[] alive = sim.Entities.Registry.AliveEntities().Where(e => e != keepAlive).ToArray();
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

            extra?.Invoke(sim.Tick, recorder);
            sim.Step();
        }

        return recorder.ToReplay();
    }
}

/// <summary>A replay and its expected (sampled) hashes.</summary>
internal sealed record ReplayGolden(ReplayFile Replay, HashTrace Expected);
