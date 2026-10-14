using Core.Determinism;
using Core.Entities;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Save;

/// <summary>Simulations with every kind of saved state: holes in the registry, generations above 1, advanced RNG, pending commands.</summary>
internal static class SaveTestData
{
    /// <summary>Seed of <see cref="BuildV1Sample"/>; part of the frozen save_v1.sav expectations.</summary>
    public const ulong V1SampleSeed = 0x5A5E_0001;

    /// <summary>
    /// Script of the frozen <c>Golden/save_v1.sav</c>. Changing it does not change the committed file (it is never
    /// overwritten); it only documents how the file was made. Expected state at tick 9:
    /// slot 0 free (generation 2), #1v2 at (7,7), #2v1 at (1000,1000); pending for tick 10: spawn (11,-22) seq 6 and
    /// destroy #2v1 seq 7; next sequence 8; AI stream advanced once, combat stream twice.
    /// </summary>
    public static Simulation BuildV1Sample()
    {
        var sim = new Simulation(V1SampleSeed, SimTestData.Real);
        sim.Enqueue(new SpawnMarkerCommand(new Position(256, 512)));
        sim.Enqueue(new SpawnMarkerCommand(new Position(-300, 40)));
        sim.Enqueue(new SpawnMarkerCommand(new Position(1000, 1000)));
        sim.Step();
        sim.Enqueue(new DestroyEntityCommand(new EntityId(1, 1)));
        sim.Step();
        sim.Enqueue(new SpawnMarkerCommand(new Position(7, 7)));
        sim.Step();
        sim.Enqueue(new DestroyEntityCommand(new EntityId(0, 1)));
        sim.Step();
        sim.Rng[RngStreamId.Ai].NextUInt64();
        sim.Rng[RngStreamId.Combat].NextUInt64();
        sim.Rng[RngStreamId.Combat].NextUInt64();
        sim.Step(5);
        sim.Enqueue(new SpawnMarkerCommand(new Position(11, -22)));
        sim.Enqueue(new DestroyEntityCommand(new EntityId(2, 1)));
        return sim;
    }

    /// <summary>Seed of <see cref="BuildV2Sample"/>; part of the frozen save_v2.sav expectations.</summary>
    public const ulong V2SampleSeed = 0x5A5E_0002;

    /// <summary>Map size of <see cref="BuildV2Sample"/>.</summary>
    public const int V2SampleMapSize = 64;

    /// <summary>
    /// Script of the frozen <c>Golden/save_v2.sav</c> (first format with a world): a hard 64² game, a marker spawned
    /// at tile (5, 6) in tick 1, the start marker moved by 3 tiles east before tick 3 (so explored is wider than
    /// visible), 4 ticks run, then a pending <c>reveal_all</c> and a pending spawn at tile (1, 1).
    /// </summary>
    public static Simulation BuildV2Sample()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(V2SampleSeed, V2SampleMapSize, Difficulty.Hard));
        sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(5, 6)));
        sim.Step(2);
        EntityId start = sim.World!.Revealers.Entities[0];
        sim.Positions.Get(start) = new Position(sim.Positions.Get(start).X + (3 * Position.UnitsPerTile), sim.Positions.Get(start).Y);
        sim.Step(2);
        sim.Enqueue(new RevealAllCommand());
        sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(1, 1)));
        return sim;
    }

    /// <summary>A new 64² game run for <paramref name="ticks"/> ticks with a few markers, ending with a pending reveal.</summary>
    public static Simulation BuildWorld(ulong seed = 0xF06, int ticks = 30, Difficulty difficulty = Difficulty.Normal)
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(seed, 64, difficulty));
        for (int i = 0; i < ticks; i++)
        {
            if (i % 7 == 0)
            {
                sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(i % 64, (i * 3) % 64)));
            }

            sim.Step();
        }

        sim.Enqueue(new RevealAllCommand());
        return sim;
    }

    /// <summary>A longer busy run (seeded random script) ending with a pending spawn and a pending destroy of a live entity.</summary>
    public static Simulation BuildBusy(ulong seed = 0xBEEF, int ticks = 120)
    {
        var sim = new Simulation(seed, SimTestData.Real);
        var script = new Xoshiro256StarStar(seed ^ 0x1234);
        for (int i = 0; i < ticks; i++)
        {
            if (script.ChancePercent(30))
            {
                sim.Enqueue(new SpawnMarkerCommand(new Position(script.NextInt(-1000, 1000), script.NextInt(-1000, 1000))));
            }

            EntityId[] alive = sim.Entities.Registry.AliveEntities().ToArray();
            if (alive.Length > 0 && script.ChancePercent(15))
            {
                sim.Enqueue(new DestroyEntityCommand(alive[script.NextInt(alive.Length)]));
            }

            sim.Step();
        }

        sim.Rng[RngStreamId.Storyteller].NextUInt64();
        sim.Enqueue(new SpawnMarkerCommand(new Position(1, 2)));
        sim.Enqueue(new DestroyEntityCommand(sim.Entities.Registry.AliveEntities().First()));
        return sim;
    }
}
