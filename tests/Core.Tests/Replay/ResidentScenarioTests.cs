using Core.Buildings;
using Core.Entities;
using Core.Replay;
using Core.Residents;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Replay;

/// <summary>
/// M3a residents scenario (FR-NPC-01, FR-NPC-02, NFR-04): the <c>scenario_m3a_residents</c> replay matches its golden
/// hashes, runs the same twice, newcomers arrive only at the arrival hour and at most one a morning, each gets a home
/// and the next profession of the pattern, and a save + load while a newcomer walks in continues exactly like the
/// uninterrupted run.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class ResidentScenarioTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-NPC-02")]
    [Trait("Req", "NFR-04")]
    public void ResidentScenarioMatchesGoldenAndNewcomersSettleIn()
    {
        ReplayGolden golden = GoldenFiles.Residents;
        Assert.Equal(ResidentScenario.Options, golden.Replay.NewGame);
        Assert.InRange(golden.Replay.EndTick, 1, ResidentScenario.TickBudget);
        output.WriteLine($"scenario_m3a_residents ends at tick {golden.Replay.EndTick} with {golden.Replay.Commands.Count} commands");
        Assert.Equal(6, golden.Replay.Commands.Count); // two gives and four placements; nobody orders the residents
        Assert.Equal(2, golden.Replay.Commands.Count(c => c.Command is GiveItemCommand));

        ReplayPlayer player = ReplayPlayer.Start(golden.Replay, SimTestData.Real);
        var arrivals = new List<ResidentEvent>();
        while (!player.IsFinished)
        {
            player.Step();
            arrivals.AddRange(player.Simulation.World!.ResidentEvents.Where(e => e.Kind == ResidentEventKind.Arrived && e.Tick == player.Simulation.Tick));
        }

        AssertNoDivergence(golden.Expected, player.Hashes);
        Simulation sim = player.Simulation;
        GameWorld world = sim.World!;
        Assert.Equal(ResidentScenario.Newcomers, arrivals.Count);
        Assert.All(arrivals, a => Assert.Equal(sim.Calendar.FirstTickOf(sim.Calendar.At(a.Tick).Day, sim.Content.Economy.ArrivalHour), a.Tick));
        Assert.Equal(arrivals.Count, arrivals.Select(a => sim.Calendar.At(a.Tick).Day).Distinct().Count());
        Assert.Equal(["carrier", "lumberjack", "farmer"], arrivals.Select(a => a.Profession));
        Assert.Equal(ResidentScenario.Newcomers, world.Residents.Count);
        Assert.All(world.Residents.Values.ToArray(), r =>
        {
            Assert.Equal(ResidentState.Idle, r.State);
            Assert.NotEqual(EntityId.None, r.Home);
        });
        Assert.Equal(4, world.Buildings.Count);
        Assert.Equal(ResidentScenario.Newcomers, Construction.SettlementPopulation(sim));
        Assert.Equal(1, Housing.TotalFreePlaces(sim));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void TwoRunsOfTheResidentScenarioGiveIdenticalHashesEveryTick()
    {
        ReplayFile replay = GoldenFiles.Residents.Replay;

        HashTrace first = GoldenFiles.RunDense(replay);
        HashTrace second = GoldenFiles.RunDense(replay);

        Assert.Equal(replay.EndTick, first.Count);
        AssertNoDivergence(first, second);
    }

    // Save a few ticks into every newcomer's walk and at fixed ticks, load, continue to the end: every tick's hashes
    // equal the uninterrupted run's.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-01")]
    public void SaveLoadWhileNewcomersWalkInContinuesLikeTheUninterruptedRun()
    {
        ReplayFile replay = GoldenFiles.Residents.Replay;
        ReplayPlayer reference = ReplayPlayer.Start(replay, SimTestData.Real);
        var saveTicks = new SortedSet<long> { 1, replay.EndTick - 1 };
        while (!reference.IsFinished)
        {
            reference.Step();
            Simulation current = reference.Simulation;
            if (current.World!.ResidentEvents.Any(e => e.Kind == ResidentEventKind.Arrived && e.Tick == current.Tick - 7))
            {
                saveTicks.Add(current.Tick);
            }
        }

        Assert.Equal(ResidentScenario.Newcomers + 2, saveTicks.Count);
        foreach (long saveTick in saveTicks)
        {
            ReplayPlayer before = ReplayPlayer.Start(replay, SimTestData.Real);
            before.RunTo(saveTick);
            byte[] bytes = SaveGame.ToBytes(before.Simulation);
            SaveLoadResult loaded = SaveGame.Read(bytes, SimTestData.Real);
            Assert.True(loaded.Succeeded, loaded.Error?.Message);
            Assert.Equal(bytes, SaveGame.ToBytes(loaded.Simulation!));

            var after = new ReplayPlayer(replay, loaded.Simulation!);
            after.RunToEnd();
            var combined = new HashTrace();
            combined.AddRange(before.Hashes);
            combined.AddRange(after.Hashes);

            Assert.Equal(replay.EndTick, combined.Count);
            AssertNoDivergence(reference.Hashes, combined);
        }
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
}
