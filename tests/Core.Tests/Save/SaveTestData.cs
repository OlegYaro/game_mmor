using Core.Determinism;
using Core.Entities;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;

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

    /// <summary>Seed of <see cref="BuildV3Sample"/>; part of the frozen save_v3.sav expectations.</summary>
    public const ulong V3SampleSeed = 0x5A5E_0003;

    /// <summary>
    /// Script of the frozen <c>Golden/save_v3.sav</c> (first format with the hero, buildings and tile changes): a peaceful
    /// 64² game; 40 wood and 7 berries given in tick 1 while the hero starts gathering the nearest explored tree; a
    /// campfire placed in tick 2 and built; a house placed right after it (still under construction in the save); the
    /// hero gathers until its first yield, is then sent sprinting 6 tiles away and the save is taken one tick into that
    /// walk (mid-step), with a pending gather order on the same tree.
    /// </summary>
    public static Simulation BuildV3Sample()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(V3SampleSeed, 64, Difficulty.Peaceful));
        (int tx, int ty) = Heroes.HeroTestWorld.NearestNode(sim, ResourceKind.Tree);
        sim.Enqueue(new GiveItemCommand("wood", 40));
        sim.Enqueue(new GiveItemCommand("berries", 7));
        sim.Enqueue(new GatherCommand(tx, ty));
        sim.Step();
        (int cx, int cy) = Buildings.BuildingTestWorld.FindSpot(sim, "campfire", minDistance: 3);
        sim.Enqueue(new PlaceBuildingCommand("campfire", cx, cy));
        sim.Step();
        Heroes.HeroTestWorld.StepUntil(sim, () => sim.World!.Buildings.Values[0].State == Core.Buildings.BuildingState.Active);
        (int hx, int hy) = Buildings.BuildingTestWorld.FindSpot(sim, "house", minDistance: 3);
        sim.Enqueue(new PlaceBuildingCommand("house", hx, hy));
        sim.Step();
        Heroes.HeroTestWorld.StepUntil(sim, () => sim.World!.Tiles.IsResourceChanged(tx, ty));
        (int mx, int my) = Heroes.HeroTestWorld.StraightTarget(sim, 6);
        sim.Enqueue(new MoveHeroCommand(mx, my, Sprint: true));
        sim.Step();
        sim.Enqueue(new GatherCommand(tx, ty));
        return sim;
    }

    /// <summary>Seed of <see cref="BuildV4Sample"/>; part of the frozen save_v4.sav expectations.</summary>
    public const ulong V4SampleSeed = 0x5A5E_0004;

    /// <summary>
    /// Script of the frozen <c>Golden/save_v4.sav</c> (first format with residents, the reached era, jobs, reservations,
    /// field outputs, storage filters, the food state, flora and walk counters; made again on 16.10.2026 when the job
    /// board and then the stumps, saplings, trails and roads joined format v4 before its release): a normal 64² game; 150
    /// wood and 30 berries given in tick 1; a campfire, two houses, a storage (that refuses money) and a field built near
    /// the hero; a blacksmith, an automatic resident (a lumberjack) and a farmer spawned at the center; then mornings run
    /// until the first newcomer arrives. Three ticks into its walk, while the others work, the first tree in tile order that
    /// nobody works is felled (a stump) and a sapling is planted on the first free tile of the row three below the
    /// center (straight into the tile layer, as a lumberjack and a planter would), and the save is taken with a pending
    /// spawn of a farmer. The residents' walks left counters on their tiles.
    /// </summary>
    public static Simulation BuildV4Sample()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(V4SampleSeed, 64, Difficulty.Normal));
        sim.Enqueue(new GiveItemCommand("wood", 150));
        sim.Enqueue(new GiveItemCommand("berries", 30));
        sim.Step();
        foreach (string building in new[] { "campfire", "house", "house", "storage", "field" })
        {
            (int x, int y) = Buildings.BuildingTestWorld.FindSpot(sim, building);
            sim.Enqueue(new PlaceBuildingCommand(building, x, y));
            sim.Step();
            Heroes.HeroTestWorld.StepUntil(sim, () => sim.World!.Buildings.Values[^1].State == Core.Buildings.BuildingState.Active);
        }

        sim.Enqueue(new SetStorageFilterCommand(sim.World!.Buildings.Entities[3], 0b0111));
        sim.Enqueue(new SpawnResidentCommand("blacksmith"));
        sim.Enqueue(new SpawnResidentCommand());
        sim.Enqueue(new SpawnResidentCommand("farmer"));
        sim.Step();
        Heroes.HeroTestWorld.StepUntil(sim, () => sim.World!.Residents.Count == 4, limit: 100_000);
        sim.Step(3);
        GameWorld world = sim.World!;
        int tree = Enumerable.Range(0, world.Size * world.Size).First(i => world.Tiles.GetEffectiveResource(i % world.Size, i / world.Size) == ResourceKind.Tree
            && !world.Reservations.IsReserved(Core.Jobs.ReservationTarget.OfTile(i)));
        Assert.True(world.Tiles.TakeResource(tree % world.Size, tree / world.Size, int.MaxValue) > 0);
        Core.Buildings.Building center = world.Buildings.Get(Core.Buildings.Construction.FindCenter(sim));
        int sx = Enumerable.Range(0, world.Size).First(x => world.Tiles.CanPlant(x, center.Y + 3));
        Assert.True(world.Tiles.Plant(sx, center.Y + 3));
        sim.Enqueue(new SpawnResidentCommand("farmer"));
        return sim;
    }

    /// <summary>
    /// A new 64² game where the hero walks (sprinting) to a node, gathers from it and is then sent elsewhere; a marker
    /// occupies a tile. Runs <paramref name="ticks"/> ticks after the orders.
    /// </summary>
    public static Simulation BuildHeroGame(int ticks)
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(0x4E50, 64, Difficulty.Normal));
        (int tx, int ty) = Heroes.HeroTestWorld.NearestNode(sim, ResourceKind.Tree);
        sim.Enqueue(new GiveItemCommand("stone", 120));
        sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(4, 4)));
        sim.Enqueue(new GatherCommand(tx, ty));
        sim.Step();
        Assert.True(sim.World!.Tiles.TryOccupy(4, 4, sim.Entities.Registry.AliveEntities().Last()));
        sim.Step(ticks);
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

        sim.Rng[RngStreamId.Events].NextUInt64();
        sim.Enqueue(new SpawnMarkerCommand(new Position(1, 2)));
        sim.Enqueue(new DestroyEntityCommand(sim.Entities.Registry.AliveEntities().First()));
        return sim;
    }
}
