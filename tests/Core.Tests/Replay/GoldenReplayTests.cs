using Core.Determinism;
using Core.Entities;
using Core.Replay;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Replay;

/// <summary>
/// Replay determinism against golden files (NFR-04, ADR 0005, point 6). On failure the message names the first
/// diverging tick and subsystem. Regenerate goldens only on purpose: see <see cref="GoldenFiles"/>.
/// </summary>
public sealed class GoldenReplayTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Req", "NFR-04")]
    public void TwoRunsOfTheSameReplayGiveIdenticalHashesEveryTick()
    {
        ReplayFile replay = GoldenFiles.Basic.Replay;

        HashTrace first = GoldenFiles.RunDense(replay);
        HashTrace second = GoldenFiles.RunDense(replay);

        Assert.Equal(replay.EndTick, first.Count);
        AssertNoDivergence(first, second);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void BasicReplayMatchesGoldenHashes()
    {
        ReplayGolden golden = GoldenFiles.Basic;
        Assert.InRange(golden.Replay.Commands.Count, 20, 500);
        Assert.Contains(golden.Replay.Commands, c => c.Command is DestroyEntityCommand);

        HashTrace actual = GoldenFiles.RunDense(golden.Replay);

        AssertNoDivergence(golden.Expected, actual);
        Assert.Equal(golden.Replay.EndTick, golden.Expected.Last?.Tick);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    public void SaveAtTickNLoadAndContinueEqualsUninterruptedRun()
    {
        ReplayFile replay = GoldenFiles.Basic.Replay;
        HashTrace uninterrupted = GoldenFiles.RunDense(replay);

        foreach (long saveTick in new long[] { 0, 1, 137, replay.EndTick - 1 })
        {
            ReplayPlayer before = ReplayPlayer.Start(replay, SimTestData.Real);
            before.RunTo(saveTick);
            SaveLoadResult loaded = SaveGame.Read(SaveGame.ToBytes(before.Simulation), SimTestData.Real);
            Assert.True(loaded.Succeeded, loaded.Error?.Message);

            var after = new ReplayPlayer(replay, loaded.Simulation!);
            after.RunToEnd();
            var combined = new HashTrace();
            combined.AddRange(before.Hashes);
            combined.AddRange(after.Hashes);

            Assert.Equal(replay.EndTick, combined.Count);
            AssertNoDivergence(uninterrupted, combined);
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DivergenceReportNamesFirstTickAndSubsystem()
    {
        ReplayFile replay = GoldenFiles.Basic.Replay;
        int index = FindIndex(replay, c => c.Command is SpawnMarkerCommand && c.Tick > 50);
        ReplayCommand original = replay.Commands[index];
        var moved = (SpawnMarkerCommand)original.Command;
        ReplayCommand[] altered = [.. replay.Commands];
        altered[index] = original with { Command = new SpawnMarkerCommand(new Position(moved.Position.X + 1, moved.Position.Y)) };
        var alteredReplay = new ReplayFile(replay.Seed, replay.ContentHash, replay.EndTick, altered);

        HashTrace actual = GoldenFiles.RunDense(alteredReplay);

        // Dense reference: exact tick. Only the position differs, so the entity registry still matches.
        ReplayDivergence? dense = ReplayComparer.FindFirstDivergence(GoldenFiles.RunDense(replay), actual);
        Assert.NotNull(dense);
        output.WriteLine(dense.Describe());
        Assert.Equal(original.Tick, dense.Tick);
        Assert.Equal(Simulation.ComponentsHashName, dense.Subsystem);
        Assert.Equal(original.Tick - 1, dense.LastMatchingTick);
        Assert.NotEqual(dense.ExpectedHash, dense.ActualHash);
        Assert.Contains($"tick {original.Tick}", dense.Describe(), StringComparison.Ordinal);
        Assert.Contains("'components'", dense.Describe(), StringComparison.Ordinal);

        // Sampled golden: first checkpoint at or after the change, same subsystem.
        ReplayDivergence? sampled = ReplayComparer.FindFirstDivergence(GoldenFiles.Basic.Expected, actual);
        Assert.NotNull(sampled);
        output.WriteLine(sampled.Describe());
        Assert.InRange(sampled.Tick, original.Tick, original.Tick + 10);
        Assert.Equal(Simulation.ComponentsHashName, sampled.Subsystem);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DroppedDestroyCommandIsReportedInEntitiesSubsystem()
    {
        ReplayFile replay = GoldenFiles.Basic.Replay;
        HashTrace reference = GoldenFiles.RunDense(replay);

        // Find a destroy that actually removes an entity (the registry changes in that tick).
        int index = -1;
        for (int i = 0; i < replay.Commands.Count && index < 0; i++)
        {
            long candidate = replay.Commands[i].Tick;
            if (replay.Commands[i].Command is DestroyEntityCommand && OnlyCommandInTick(replay, i)
                && reference.TryGet(candidate, out SubsystemHashes at) && reference.TryGet(candidate - 1, out SubsystemHashes before)
                && Hash(at, Simulation.EntitiesHashName) != Hash(before, Simulation.EntitiesHashName))
            {
                index = i;
            }
        }

        Assert.True(index >= 0, "The golden replay has no effective destroy command.");
        long tick = replay.Commands[index].Tick;
        var altered = new ReplayFile(replay.Seed, replay.ContentHash, replay.EndTick, replay.Commands.Where((_, i) => i != index));

        ReplayDivergence? divergence = ReplayComparer.FindFirstDivergence(reference, GoldenFiles.RunDense(altered));

        Assert.NotNull(divergence);
        output.WriteLine(divergence.Describe());
        Assert.Equal(tick, divergence.Tick);
        Assert.Equal(Simulation.EntitiesHashName, divergence.Subsystem);
    }

    // M0 acceptance (plan, "Тесты и CI"): 10 000 ticks with scheduled commands and a save + load in the middle;
    // every sampled hash and the final hash match the golden recorded by an uninterrupted run.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-SAVE-02")]
    public void ScenarioTenThousandTicksWithSaveAndLoadMatchesGolden()
    {
        ReplayGolden golden = GoldenFiles.Scenario;
        Assert.Equal(10_000, golden.Replay.EndTick);
        Assert.True(golden.Replay.Commands.Count >= 50, "The scenario should schedule a meaningful number of commands.");

        ReplayPlayer first = ReplayPlayer.Start(golden.Replay, SimTestData.Real);
        first.RunTo(golden.Replay.EndTick / 2);
        string path = Path.Combine(Path.GetTempPath(), $"core-tests-scenario-{Environment.ProcessId}{SaveFormat.FileExtension}");
        SaveLoadResult loaded;
        try
        {
            SaveGame.WriteFile(first.Simulation, path);
            loaded = SaveGame.ReadFile(path, SimTestData.Real);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Assert.Null(loaded.Simulation!.ComputeHashes().FindFirstMismatch(first.Simulation.ComputeHashes()));
        var second = new ReplayPlayer(golden.Replay, loaded.Simulation);
        second.RunToEnd();
        var actual = new HashTrace();
        actual.AddRange(first.Hashes);
        actual.AddRange(second.Hashes);

        AssertNoDivergence(golden.Expected, actual);
        Assert.Equal(golden.Expected.Last!.Value.Tick, actual.Last!.Value.Tick);
        Assert.Equal(golden.Expected.Last.Value.Hashes.Combined, actual.Last.Value.Hashes.Combined);
        Assert.True(second.IsFinished);
    }

    // M1 scenario: a game with a world, fog through nightfall and dawn, a reveal command, and a save + load in the
    // middle (between nightfall and the reveal); every sampled hash matches the golden of an uninterrupted run.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-WORLD-01")]
    [Trait("Req", "FR-WORLD-06")]
    public void WorldScenarioWithFogRevealAndSaveLoadMatchesGolden()
    {
        ReplayGolden golden = GoldenFiles.World;
        Assert.Equal(GoldenFiles.WorldOptions, golden.Replay.NewGame);
        Assert.Contains(golden.Replay.Commands, c => c.Command is RevealAllCommand && c.Tick == GoldenFiles.WorldRevealTick);
        Assert.Contains(golden.Replay.Commands, c => c.Command is DestroyEntityCommand);
        Assert.True(golden.Expected.TryGet(golden.Replay.EndTick, out SubsystemHashes last));
        Assert.Equal(
            [Simulation.CalendarHashName, Simulation.RngHashName, Simulation.EntitiesHashName, Simulation.ComponentsHashName,
                Simulation.CommandsHashName, Simulation.GameHashName, Simulation.FogHashName, Simulation.TilesHashName],
            last.Entries.Select(e => e.Name));

        ReplayPlayer first = ReplayPlayer.Start(golden.Replay, SimTestData.Real);
        first.RunTo(6500);
        Assert.True(first.Simulation.Now.IsNight);
        SaveLoadResult loaded = SaveGame.Read(SaveGame.ToBytes(first.Simulation), SimTestData.Real);
        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Assert.Null(loaded.Simulation!.ComputeHashes().FindFirstMismatch(first.Simulation.ComputeHashes()));
        var second = new ReplayPlayer(golden.Replay, loaded.Simulation);
        second.RunToEnd();
        var actual = new HashTrace();
        actual.AddRange(first.Hashes);
        actual.AddRange(second.Hashes);

        AssertNoDivergence(golden.Expected, actual);
        Assert.Equal(golden.Expected.Last!.Value.Hashes.Combined, actual.Last!.Value.Hashes.Combined);
        Assert.Equal(0, loaded.Simulation.World!.Fog.CountStates().Unknown);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-WORLD-06")]
    public void ReplacedRevealCommandIsReportedInFogSubsystem()
    {
        // Swap the reveal for a no-op (destroying a never-issued id) so the command sequence numbers stay the same and
        // only the fog differs.
        ReplayFile replay = GoldenFiles.World.Replay;
        ReplayCommand[] altered = [.. replay.Commands.Select(c => c.Command is RevealAllCommand
            ? c with { Command = new DestroyEntityCommand(new EntityId(9999, 1)) }
            : c)];
        HashTrace reference = GoldenFiles.RunDense(replay);

        ReplayDivergence? divergence = ReplayComparer.FindFirstDivergence(reference, GoldenFiles.RunDense(
            new ReplayFile(replay.Seed, replay.ContentHash, replay.EndTick, altered, replay.MapSize, replay.Difficulty)));

        Assert.NotNull(divergence);
        output.WriteLine(divergence.Describe());
        Assert.Equal(GoldenFiles.WorldRevealTick, divergence.Tick);
        Assert.Equal(Simulation.FogHashName, divergence.Subsystem);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void GoldenReplayFilesAreCanonical()
    {
        // The committed text is exactly what ToJson/ToText produce, so regenerating gives no spurious diff.
        Assert.Equal(Normalize(GoldenFiles.ReadText("replay_basic.json")), GoldenFiles.Basic.Replay.ToJson());
        Assert.Equal(Normalize(GoldenFiles.ReadText("replay_basic.hashes.txt")), GoldenFiles.Basic.Expected.ToText());
        Assert.Equal(Normalize(GoldenFiles.ReadText("scenario_m0.json")), GoldenFiles.Scenario.Replay.ToJson());
        Assert.Equal(Normalize(GoldenFiles.ReadText("scenario_world_m1.json")), GoldenFiles.World.Replay.ToJson());
        Assert.Equal(Normalize(GoldenFiles.ReadText("scenario_world_m1.hashes.txt")), GoldenFiles.World.Expected.ToText());
    }

    private void AssertNoDivergence(HashTrace expected, HashTrace actual)
    {
        ReplayDivergence? divergence = ReplayComparer.FindFirstDivergence(expected, actual);
        if (divergence is not null)
        {
            output.WriteLine(divergence.Describe());
            Assert.Fail(divergence.Describe() +
                $" If the change is intended, regenerate goldens with {GoldenFiles.UpdateVariable}=1 tools/test.sh and review the diff.");
        }
    }

    private static int FindIndex(ReplayFile replay, Func<ReplayCommand, bool> match)
    {
        for (int i = 0; i < replay.Commands.Count; i++)
        {
            if (match(replay.Commands[i]))
            {
                return i;
            }
        }

        throw new InvalidOperationException("No matching command in the golden replay.");
    }

    private static bool OnlyCommandInTick(ReplayFile replay, int index) =>
        replay.Commands.Count(c => c.Tick == replay.Commands[index].Tick) == 1;

    private static ulong Hash(SubsystemHashes hashes, string name) =>
        hashes.TryGetHash(name, out ulong hash) ? hash : throw new InvalidOperationException(name);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
