using Core.Determinism;
using Core.Entities;
using Core.Save;
using Core.Sim;
using Core.Tests.Replay;
using Core.Tests.Sim;

namespace Core.Tests.Save;

/// <summary>
/// Committed samples of every past save format version must load forever (ADR 0007). The expectations below are
/// frozen together with the file; they do not come from running today's code.
/// </summary>
public sealed class SaveGoldenTests
{
    private const string V1Sample = "save_v1.sav";

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
