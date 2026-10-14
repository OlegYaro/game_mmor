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
