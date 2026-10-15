using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Save;
using Core.Sim;
using Core.Tests.Replay;
using Core.Tests.Sim;
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

        // It keeps running: the pending reveal explores everything.
        sim.Step();
        FogCounts after = world.Fog.CountStates();
        Assert.Equal(0, after.Unknown);
        Assert.Equal(V2Frozen.Counts.Visible, after.Visible);
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
