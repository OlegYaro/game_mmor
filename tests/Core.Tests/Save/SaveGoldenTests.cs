using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Economy;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Jobs;
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
    private const string V4Sample = "save_v4.sav";

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

        // v3 -> v4: no residents, the first era reached.
        Assert.Equal(0, world.Residents.Count);
        Assert.Equal("camp", SimTestData.Real.Get(world.ReachedEra).Id);
        Assert.Equal("camp", Construction.CurrentEra(sim).Id);

        // It keeps running: the pending order sends the hero back to the tree, which it gathers again; the house gets built.
        Heroes.HeroTestWorld.StepUntil(sim, () => world.Tiles.GetEffectiveAmount(V3Frozen.TreeX, V3Frozen.TreeY) < V3Frozen.TreeStock);
        Assert.Equal(HeroActivity.Gathering, world.Heroes.Get(heroEntity).Activity);
        Heroes.HeroTestWorld.StepUntil(sim, () => world.Buildings.Get(new EntityId(2, 1)).State == BuildingState.Active);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-04")]
    public void SaveV3SampleIsUpgradedByTheShippedMigrationOnly()
    {
        SaveLoadResult withoutV4 = SaveGame.Read(GoldenFiles.ReadBytes(V3Sample), SimTestData.Real, null, [new SaveMigrationV1ToV2(), new SaveMigrationV2ToV3()]);

        Assert.Equal(SaveErrorKind.MissingMigration, withoutV4.Error!.Kind);
        Assert.Contains("v3 to v4", withoutV4.Error.Message, StringComparison.Ordinal);
    }

    // The first format with residents (name, profession, skills, home, state, priorities), the reached era, jobs,
    // reservations, field outputs, storage filters and the food state.
    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-NPC-02")]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-NPC-07")]
    [Trait("Req", "FR-ECO-01")]
    public void SaveV4SampleLoads()
    {
        CreateV4SampleIfMissing();

        SaveLoadResult result = SaveGame.Read(GoldenFiles.ReadBytes(V4Sample), SimTestData.Real);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(4, result.SourceFormatVersion);
        Simulation sim = result.Simulation!;
        GameWorld world = sim.World!;
        Assert.Equal(V4Frozen.Tick, sim.Tick);
        Assert.Equal(V4Frozen.MapHash, ContentHash.Format(world.MapHash));
        Assert.Equal("village", SimTestData.Real.Get(world.ReachedEra).Id); // four residents
        Assert.Equal(4, Construction.SettlementPopulation(sim));

        var house1 = new EntityId(2, 1);
        var house2 = new EntityId(3, 1);
        var storage = new EntityId(4, 1);
        var field = new EntityId(5, 1);
        (string Name, string Profession, int[] Skills, EntityId Home, Core.Residents.ResidentState State)[] expected =
        [
            ("Osric", "blacksmith", [3, 6, 0, 0, 4, 2, 4, 2, 3, 0, 3, 2], house1, Core.Residents.ResidentState.Idle),
            ("Loris", "lumberjack", [1, 5, 5, 0, 6, 3, 4, 0, 4, 4, 0, 4], house1, Core.Residents.ResidentState.Working),
            ("Merrin", "farmer", [5, 2, 6, 6, 5, 1, 4, 3, 1, 2, 0, 0], house2, Core.Residents.ResidentState.Working),
            ("Wilmer", "stonemason", [5, 5, 0, 2, 6, 6, 5, 2, 2, 0, 6, 6], house2, Core.Residents.ResidentState.Arriving),
        ];
        EntityId[] residents = [new EntityId(6, 1), new EntityId(7, 1), new EntityId(8, 1), new EntityId(9, 1)];
        Assert.Equal(residents, world.Residents.Entities.ToArray());
        Assert.Equal(expected, world.Residents.Values.ToArray().Select(r => (Core.Residents.ResidentRules.NameOf(sim.Content, r),
            sim.Content.Get(r.Profession).Id, r.Skills.ToArray(), r.Home, r.State)));
        Assert.All(world.Residents.Values.ToArray(), r => Assert.Equal(WorkPriorities.ForProfession(sim.Content.Get<ProfessionDef>(r.Profession)), r.Priorities));

        // Jobs: the lumberjack works its tree with a half load, the farmer works the field; their targets are reserved.
        Assert.True(SimTestData.Real.TryGetId("wood", out DefId wood));
        Assert.Equal(new ResidentJob(JobKind.None, JobPhase.None, EntityId.None, -1, EntityId.None, -1, 0, default, 0, V4Frozen.SmithThinks), world.Jobs.Get(residents[0]));
        Assert.Equal(new ResidentJob(JobKind.Chop, JobPhase.Working, EntityId.None, V4Frozen.Tree, EntityId.None, V4Frozen.Tree, V4Frozen.ChopLeft, wood, 10,
            V4Frozen.LumberjackThought), world.Jobs.Get(residents[1]));
        Assert.Equal(new ResidentJob(JobKind.Farm, JobPhase.Working, field, -1, EntityId.None, V4Frozen.FieldTile, V4Frozen.FarmLeft, default, 0,
            V4Frozen.FarmerThought), world.Jobs.Get(residents[2]));
        Assert.Equal(
            [
                new Reservation(ReservationTarget.OfTile(V4Frozen.Tree), residents[1], default, 1),
                new Reservation(ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, field), residents[2], default, 1),
            ],
            world.Reservations.Reservations);
        Assert.Empty(world.Reservations.Bans);
        Assert.Equal(new WorkOutput(V4Frozen.FieldOutput), world.WorkOutputs.Get(field));
        Assert.Equal([new ComponentEntry<StorageFilter>(storage, new StorageFilter(0b0111))], world.StorageFilters.GetEntries());
        Assert.Equal(V4Frozen.StoredWood, world.Inventories.Get(storage).Count(wood));
        Assert.Equal((0, GameWorld.NoLeaveDay), (world.DaysWithoutFood, world.LastLeaveDay));
        Assert.Equal(new Revealer(RevealerKind.Villager), world.Revealers.Get(residents[3]));

        // Flora: the lumberjack's two felled trees and the sample's own stump and sapling; the residents' walks.
        Assert.Equal(
            [(23, 18, FloraState.Stump, 1015L), (24, 18, FloraState.Stump, 1848L), (41, 4, FloraState.Stump, V4Frozen.Tick), (4, 33, FloraState.Sapling, V4Frozen.Tick)],
            new[] { (23, 18), (24, 18), (41, 4), (4, 33) }.Select(t => (t.Item1, t.Item2, world.Tiles.GetFlora(t.Item1, t.Item2), world.Tiles.GetFloraTick(t.Item1, t.Item2))));
        Assert.Equal(4, world.Tiles.FloraTiles);
        Assert.Equal((V4Frozen.WalkedTiles, 0, 0), (world.Tiles.WalkedTiles, world.Tiles.TrailTiles, world.Tiles.RoadTiles));
        Assert.Equal(V4Frozen.WalkSum, Enumerable.Range(0, 64 * 64).Sum(i => world.Tiles.GetWalkCounter(i % 64, i / 64)));

        Mover walk = world.Movers.Get(residents[3]);
        Assert.True(walk.IsMoving);
        Assert.Equal(V4Frozen.NewcomerGoal, walk.Goal);
        Assert.Equal([new PendingCommand(V4Frozen.Tick + 1, V4Frozen.PendingSequence, new SpawnResidentCommand("farmer"))], sim.PendingCommands);

        // It keeps running: the pending spawn makes five residents; the newcomer reaches the campfire and goes to work;
        // the lumberjack hands its load in.
        sim.Step();
        Assert.Equal(5, Construction.SettlementPopulation(sim));
        Heroes.HeroTestWorld.StepUntil(sim, () => world.Residents.Get(residents[3]).State != Core.Residents.ResidentState.Arriving);
        Position at = sim.Positions.Get(residents[3]);
        Assert.Equal(1, AgentMovement.Distance(at.TileX, at.TileY, V4Frozen.CampfireX, V4Frozen.CampfireY));
        Heroes.HeroTestWorld.StepUntil(sim, () => world.Inventories.Get(storage).Count(wood) > V4Frozen.StoredWood);
    }

    // Frozen together with save_v4.sav (values read once from the generated file, never from today's code). The sample
    // was made again on 16.10.2026 when jobs, reservations, field outputs, storage filters and the food state joined
    // format v4 before its release, and once more the same day for flora and walk counters (ADR 0007); the values of the
    // second making stayed the same, only the flora and walk ones are new.
    private static class V4Frozen
    {
        public const long Tick = 2253;             // day 0, 06:00 + 3 ticks: the first morning brought the newcomer
        public const string MapHash = "0efbce35c5418c88";
        public const long PendingSequence = 11;
        public const int CampfireX = 30;
        public const int CampfireY = 30;
        public const int NewcomerGoal = 1886;      // tile (30, 29), next to the campfire
        public const int Tree = 1110;              // tile (22, 17)
        public const int ChopLeft = 35375;
        public const long LumberjackThought = 2129;
        public const int FieldTile = 1891;         // tile (35, 29), the field's nearest tile
        public const int FarmLeft = 14750;
        public const long FarmerThought = 1917;
        public const long SmithThinks = 2262;
        public const int FieldOutput = 24;
        public const int StoredWood = 141;
        public const int WalkedTiles = 41;
        public const int WalkSum = 389;
    }

    private static void CreateV4SampleIfMissing()
    {
        string path = GoldenFiles.PathOf(V4Sample);
        if (GoldenFiles.Updating && !File.Exists(path) && SaveFormat.CurrentVersion == 4)
        {
            Directory.CreateDirectory(GoldenFiles.Directory);
            File.WriteAllBytes(path, SaveGame.ToBytes(SaveTestData.BuildV4Sample()));
        }
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
