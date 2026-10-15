using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Save;
using Core.Sim;
using Core.Tests.Replay;
using Core.Tests.Sim;
using Core.Tiles;
using Core.WorldGen;

namespace Core.Tests.Save;

/// <summary>
/// Committed samples of every past save format version must load forever (ADR 0007). The expectations below are
/// frozen together with the file; they do not come from running today's code.
/// </summary>
public sealed class SaveGoldenTests
{
    private const string V1Sample = "save_v1.sav";
    private const string V2Sample = "save_v2.sav";
    private const string V3Sample = "save_v3.sav";

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-04")]
    public void SaveV1SampleLoads()
    {
        CreateSampleIfMissing();

        SaveLoadResult result = SaveGame.Read(GoldenFiles.ReadBytes(V1Sample), SimTestData.Real);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, result.SourceFormatVersion);
        Assert.Equal(SaveFormat.CodecBrotliJson, result.Header!.Codec);
        Assert.Equal(0, result.Header.WorldGenVersion);
        Assert.Equal(SaveTestData.V1SampleSeed, result.Header.Seed);

        Simulation sim = result.Simulation!;
        Assert.Equal(9, sim.Tick);
        Assert.Equal(SaveTestData.V1SampleSeed, sim.Seed);
        Assert.Equal([new EntityId(1, 2), new EntityId(2, 1)], sim.Entities.Registry.AliveEntities());
        Assert.Equal(3, sim.Entities.Registry.Capacity);
        Assert.Equal(
            [new ComponentEntry<Position>(new EntityId(1, 2), new Position(7, 7)), new ComponentEntry<Position>(new EntityId(2, 1), new Position(1000, 1000))],
            sim.Positions.GetEntries());
        Assert.Equal(8, sim.NextCommandSequence);
        Assert.Equal(
            [new PendingCommand(10, 6, new SpawnMarkerCommand(new Position(11, -22))), new PendingCommand(10, 7, new DestroyEntityCommand(new EntityId(2, 1)))],
            sim.PendingCommands);

        // RNG: AI advanced once, combat twice, the rest untouched.
        var fresh = new RngStreams(SaveTestData.V1SampleSeed);
        fresh[RngStreamId.Ai].NextUInt64();
        fresh[RngStreamId.Combat].NextUInt64();
        fresh[RngStreamId.Combat].NextUInt64();
        Assert.Equal(fresh.GetStates(), sim.Rng.GetStates());

        // And it keeps running: the pending spawn reuses slot 0 with generation 2.
        sim.Step();
        Assert.Equal([new EntityId(0, 2), new EntityId(1, 2)], sim.Entities.Registry.AliveEntities());

        // v1 -> v2: a core simulation without a world, Normal difficulty, no fog.
        Assert.Null(sim.World);
        Assert.Equal(Difficulty.Normal, sim.Difficulty);
        Assert.Equal(
            [Simulation.CalendarHashName, Simulation.RngHashName, Simulation.EntitiesHashName, Simulation.ComponentsHashName, Simulation.CommandsHashName],
            sim.ComputeHashes().Entries.Select(e => e.Name));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-04")]
    public void SaveV1SampleIsUpgradedByTheShippedMigrationOnly()
    {
        SaveLoadResult withoutMigrations = SaveGame.Read(GoldenFiles.ReadBytes(V1Sample), SimTestData.Real, null, []);

        Assert.Equal(SaveErrorKind.MissingMigration, withoutMigrations.Error!.Kind);
        Assert.Contains("v1 to v2", withoutMigrations.Error.Message, StringComparison.Ordinal);
    }

    // The first format with a world. The map is regenerated from the seed, so this sample also guards the world
    // generator: if it fails with WorldGenMismatch, the generator or data/worldgen changed without a version bump.
    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-SAVE-05")]
    [Trait("Req", "FR-WORLD-06")]
    public void SaveV2SampleLoads()
    {
        CreateV2SampleIfMissing();

        SaveLoadResult result = SaveGame.Read(GoldenFiles.ReadBytes(V2Sample), SimTestData.Real);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(2, result.SourceFormatVersion);
        Assert.Equal(2, result.Header!.WorldGenVersion);
        Assert.Equal(SaveTestData.V2SampleSeed, result.Header.Seed);

        Simulation sim = result.Simulation!;
        GameWorld world = sim.World!;
        Assert.Equal(4, sim.Tick);
        Assert.Equal(Difficulty.Hard, sim.Difficulty);
        Assert.Equal(SaveTestData.V2SampleMapSize, world.Size);
        Assert.Equal(V2Frozen.MapHash, ContentHash.Format(world.MapHash));
        Assert.Equal(new StartPoint(V2Frozen.StartX, V2Frozen.StartY), world.Map.Start);
        Assert.Equal([new EntityId(0, 1), new EntityId(1, 1)], sim.Entities.Registry.AliveEntities());
        Assert.Equal([new ComponentEntry<Revealer>(new EntityId(0, 1), new Revealer(RevealerKind.Hero))], world.Revealers.GetEntries());
        Assert.Equal(
            [
                new ComponentEntry<Position>(new EntityId(0, 1), Simulation.TileCenter(V2Frozen.StartX + 3, V2Frozen.StartY)),
                new ComponentEntry<Position>(new EntityId(1, 1), Position.FromTile(5, 6)),
            ],
            sim.Positions.GetEntries());
        Assert.Equal(V2Frozen.Counts, world.Fog.CountStates());
        Assert.Equal(FogState.Visible, world.Fog.GetState(V2Frozen.StartX + 3, V2Frozen.StartY));
        Assert.Equal(
            [new PendingCommand(5, 1, new RevealAllCommand()), new PendingCommand(5, 2, new SpawnMarkerCommand(Position.FromTile(1, 1)))],
            sim.PendingCommands);

        // v2 -> v3: the start marker (the hero revealer) becomes the hero with default data; no tile changes.
        Assert.Equal(new EntityId(0, 1), world.HeroEntity);
        Assert.Equal(Hero.FromBalance(SimTestData.Real.Hero), world.Heroes.Get(world.HeroEntity));
        Assert.Equal(Inventory.Create(SimTestData.Real.Hero), world.Inventories.Get(world.HeroEntity));
        Assert.Equal(Mover.Idle, world.Movers.Get(world.HeroEntity));
        Assert.Single(world.Heroes.GetEntries());
        Assert.Equal(TileChangesState.Empty.Resources, world.Tiles.CaptureState().Resources);
        Assert.Equal(0, world.Tiles.OccupiedTiles);

        // It keeps running: the pending reveal explores everything.
        sim.Step();
        FogCounts after = world.Fog.CountStates();
        Assert.Equal(0, after.Unknown);
        Assert.Equal(V2Frozen.Counts.Visible, after.Visible);

        // The upgraded game saves as the current format and loads back identically.
        byte[] current = SaveGame.ToBytes(sim);
        SaveLoadResult again = SaveGame.Read(current, SimTestData.Real);
        Assert.Equal(SaveFormat.CurrentVersion, again.SourceFormatVersion);
        Assert.Equal(current, SaveGame.ToBytes(again.Simulation!));

        // The hero of an upgraded game takes orders.
        sim.Enqueue(new MoveHeroCommand(V2Frozen.StartX, V2Frozen.StartY));
        sim.Step(20);
        Assert.Equal(Simulation.TileCenter(V2Frozen.StartX, V2Frozen.StartY), sim.Positions.Get(world.HeroEntity));
        Assert.Equal(HeroStopReason.Arrived, world.Heroes.Get(world.HeroEntity).LastStop);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-04")]
    public void SaveV2SampleIsUpgradedByTheShippedMigrationOnly()
    {
        SaveLoadResult withoutV3 = SaveGame.Read(GoldenFiles.ReadBytes(V2Sample), SimTestData.Real, null, [new SaveMigrationV1ToV2()]);

        Assert.Equal(SaveErrorKind.MissingMigration, withoutV3.Error!.Kind);
        Assert.Contains("v2 to v3", withoutV3.Error.Message, StringComparison.Ordinal);
    }

    // The first format with the hero (stats, path, inventory), buildings and tile changes (gathered stock, occupancy).
    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "FR-WORLD-05")]
    [Trait("Req", "FR-BUILD-02")]
    public void SaveV3SampleLoads()
    {
        CreateV3SampleIfMissing();

        SaveLoadResult result = SaveGame.Read(GoldenFiles.ReadBytes(V3Sample), SimTestData.Real);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(3, result.SourceFormatVersion);
        Assert.Equal(SaveTestData.V3SampleSeed, result.Header!.Seed);
        Simulation sim = result.Simulation!;
        GameWorld world = sim.World!;
        Assert.Equal(V3Frozen.Tick, sim.Tick);
        Assert.Equal(Difficulty.Peaceful, sim.Difficulty);
        Assert.Equal(V3Frozen.MapHash, ContentHash.Format(world.MapHash));

        EntityId heroEntity = world.HeroEntity;
        Assert.Equal(new EntityId(0, 1), heroEntity);
        Hero hero = world.Heroes.Get(heroEntity);
        Assert.Equal(HeroActivity.Walking, hero.Activity);
        Assert.Equal(HeroOrderResult.Accepted, hero.LastOrder);
        Assert.Equal(V3Frozen.Stamina, hero.Stamina);
        Mover mover = world.Movers.Get(heroEntity);
        Assert.True(mover.Sprint);
        Assert.Equal(7, mover.Path.Count);
        Assert.Equal(0, mover.StepIndex);
        Assert.Equal(SimTestData.Real.Hero.SprintPermille, mover.StepProgress);
        Assert.Equal(V3Frozen.Goal, mover.Goal);

        Inventory inventory = world.Inventories.Get(heroEntity);
        Assert.True(SimTestData.Real.TryGetId("wood", out DefId wood));
        Assert.True(SimTestData.Real.TryGetId("berries", out DefId berries));
        Assert.Equal(40 - 5 - 20 + V3Frozen.Gathered, inventory.Count(wood)); // campfire and house paid
        Assert.Equal(7, inventory.Count(berries));
        Assert.Equal(2, inventory.UsedStacks);

        Assert.Equal(1, world.Tiles.ChangedResourceTiles);
        Assert.Equal(V3Frozen.TreeStock, world.Tiles.GetEffectiveAmount(V3Frozen.TreeX, V3Frozen.TreeY));
        Assert.True(SimTestData.Real.TryGetId("campfire", out DefId campfire));
        Assert.True(SimTestData.Real.TryGetId("house", out DefId house));
        Assert.Equal(
            [
                new ComponentEntry<Building>(new EntityId(1, 1), new Building(campfire, 1, BuildingState.Active, 0, V3Frozen.CampfireX, V3Frozen.CampfireY, 0, -1, -1, -1, 1)),
                new ComponentEntry<Building>(new EntityId(2, 1), new Building(house, 1, BuildingState.Constructing, V3Frozen.HouseTicksLeft, V3Frozen.HouseX, V3Frozen.HouseY, 0, -1, -1, -1, 1)),
            ],
            world.Buildings.GetEntries());
        Assert.Equal(new EntityId(1, 1), world.Tiles.GetOccupant(V3Frozen.CampfireX, V3Frozen.CampfireY));
        Assert.Equal(new EntityId(2, 1), world.Tiles.GetOccupant(V3Frozen.HouseX + 1, V3Frozen.HouseY + 1));
        Assert.Equal(5, world.Tiles.OccupiedTiles);
        Assert.Equal([new PendingCommand(V3Frozen.Tick + 1, V3Frozen.PendingSequence, new GatherCommand(V3Frozen.TreeX, V3Frozen.TreeY))], sim.PendingCommands);

        // It keeps running: the pending order sends the hero back to the tree, which it gathers again; the house gets built.
        Heroes.HeroTestWorld.StepUntil(sim, () => world.Tiles.GetEffectiveAmount(V3Frozen.TreeX, V3Frozen.TreeY) < V3Frozen.TreeStock);
        Assert.Equal(HeroActivity.Gathering, world.Heroes.Get(heroEntity).Activity);
        Heroes.HeroTestWorld.StepUntil(sim, () => world.Buildings.Get(new EntityId(2, 1)).State == BuildingState.Active);
    }

    // Frozen together with save_v3.sav (values read once from the generated file, never from today's code). The sample
    // was made again on 15.10.2026 when buildings joined format v3 before its release (ADR 0007).
    private static class V3Frozen
    {
        public const long Tick = 103;
        public const string MapHash = "221e3e5aed2fc2d0";
        public const int Stamina = 98;   // one sprinting tick
        public const int Goal = 1763;    // tile (35, 27)
        public const int Gathered = 10;  // one tree action
        public const int TreeX = 27;
        public const int TreeY = 25;
        public const int TreeStock = 62;
        public const long PendingSequence = 6;
        public const int CampfireX = 29;
        public const int CampfireY = 29;
        public const int HouseX = 32;
        public const int HouseY = 24;
        public const int HouseTicksLeft = 98;
    }

    private static void CreateV3SampleIfMissing()
    {
        string path = GoldenFiles.PathOf(V3Sample);
        if (GoldenFiles.Updating && !File.Exists(path) && SaveFormat.CurrentVersion == 3)
        {
            Directory.CreateDirectory(GoldenFiles.Directory);
            File.WriteAllBytes(path, SaveGame.ToBytes(SaveTestData.BuildV3Sample()));
        }
    }

    // Frozen together with save_v2.sav (values read once from the generated file, never from today's code).
    private static class V2Frozen
    {
        // Sample refreshed again on 15.10.2026 for world generator v2 (veins, density cut, P-21): only the map hash and
        // the header's worldGenVersion changed; the start and the fog counts stayed the same.
        public const string MapHash = "d5da9187d3d82518";
        public const int StartX = 32;
        public const int StartY = 32;

        // Values read once from the regenerated file (sample refreshed on 15.10.2026 when the grid moved to 32x16 and the
        // fog radii doubled: a radius-24 disc clipped by the 64x64 map; moving it 3 tiles east left 143 tiles explored only).
        public static readonly FogCounts Counts = new(Unknown: 2160, Explored: 143, Visible: 1793);
    }

    private static void CreateV2SampleIfMissing()
    {
        string path = GoldenFiles.PathOf(V2Sample);
        if (GoldenFiles.Updating && !File.Exists(path) && SaveFormat.CurrentVersion == 2)
        {
            Directory.CreateDirectory(GoldenFiles.Directory);
            File.WriteAllBytes(path, SaveGame.ToBytes(SaveTestData.BuildV2Sample()));
        }
    }

    // Past-version samples are created once and never overwritten, even with UPDATE_GOLDEN=1.
    private static void CreateSampleIfMissing()
    {
        string path = GoldenFiles.PathOf(V1Sample);
        if (GoldenFiles.Updating && !File.Exists(path) && SaveFormat.CurrentVersion == 1)
        {
            Directory.CreateDirectory(GoldenFiles.Directory);
            File.WriteAllBytes(path, SaveGame.ToBytes(SaveTestData.BuildV1Sample()));
        }
    }
}
