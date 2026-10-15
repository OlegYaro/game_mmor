using Core.Buildings;
using Core.Entities;
using Core.Replay;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Replay;

/// <summary>
/// M2 acceptance (FR-BUILD-01, 02, 06, 09, 13, FR-HERO-02, NFR-04): the <c>scenario_m2</c> replay matches its golden
/// hashes, runs the same twice, fits the tick budget, ends with the expected settlement, and a save + load in any
/// building state continues exactly like the uninterrupted run.
/// </summary>
public sealed class M2ScenarioTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-BUILD-02")]
    [Trait("Req", "FR-BUILD-09")]
    [Trait("Req", "FR-BUILD-13")]
    public void M2ScenarioMatchesGoldenAndEndsWithTheSettlement()
    {
        ReplayGolden golden = GoldenFiles.M2;
        Assert.Equal(M2Scenario.Options, golden.Replay.NewGame);
        Assert.InRange(golden.Replay.EndTick, 1, M2Scenario.TickBudget);
        output.WriteLine($"scenario_m2 ends at tick {golden.Replay.EndTick} with {golden.Replay.Commands.Count} commands");
        foreach (Type kind in new[] { typeof(GatherCommand), typeof(PlaceBuildingCommand), typeof(UpgradeBuildingCommand), typeof(MoveBuildingCommand) })
        {
            Assert.Contains(golden.Replay.Commands, c => c.Command.GetType() == kind);
        }

        ReplayPlayer player = ReplayPlayer.Start(golden.Replay, SimTestData.Real);
        player.RunToEnd();
        AssertNoDivergence(golden.Expected, player.Hashes);

        Simulation sim = player.Simulation;
        GameWorld world = sim.World!;
        Building campfire = world.Buildings.Get(M2Scenario.Find(sim, "campfire"));
        Building house = world.Buildings.Get(M2Scenario.Find(sim, "house"));
        Building storage = world.Buildings.Get(M2Scenario.Find(sim, "storage"));
        Assert.Equal(3, world.Buildings.Count);
        Assert.Equal((BuildingState.Active, 1), (campfire.State, campfire.Level));
        Assert.Equal((BuildingState.Active, 2, 1), (house.State, house.Level, house.Rotation));
        Assert.Equal(BuildingState.Active, storage.State);
        PlaceBuildingCommand firstHouse = golden.Replay.Commands.Select(c => c.Command).OfType<PlaceBuildingCommand>()
            .Last(c => c.BuildingId == "house");
        Assert.NotEqual((firstHouse.X, firstHouse.Y), (house.X, house.Y)); // moved away from where it was built
        Assert.True(world.Tiles.ChangedResourceTiles > 0, "the hero gathered for real");
        Assert.DoesNotContain(golden.Replay.Commands, c => c.Command is GiveItemCommand { Item: "wood" });
        Assert.Contains(world.BuildingEvents, e => e.Command == PlaceBuildingCommand.KindName && e.Result == BuildResult.NoCenter);
        Assert.Contains(world.BuildingEvents, e => e.Command == UpgradeBuildingCommand.KindName && e.Result == BuildResult.RequirementNotMet);
        Assert.Equal(1000, Construction.ProgressPermille(sim.Content, house));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void TwoRunsOfTheM2ScenarioGiveIdenticalHashesEveryTick()
    {
        ReplayFile replay = GoldenFiles.M2.Replay;

        HashTrace first = GoldenFiles.RunDense(replay);
        HashTrace second = GoldenFiles.RunDense(replay);

        Assert.Equal(replay.EndTick, first.Count);
        AssertNoDivergence(first, second);
    }

    // Save at the first tick of every house state (constructing, active, upgrading, moving) and at a few fixed ticks,
    // load, continue to the end: every tick's hashes equal the uninterrupted run's.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-BUILD-13")]
    public void SaveLoadInEveryBuildingStateContinuesLikeTheUninterruptedRun()
    {
        ReplayFile replay = GoldenFiles.M2.Replay;
        ReplayPlayer reference = ReplayPlayer.Start(replay, SimTestData.Real);
        var saveTicks = new SortedSet<long> { 1, replay.EndTick / 2, replay.EndTick - 1 };
        var seen = new HashSet<BuildingState>();
        while (!reference.IsFinished)
        {
            reference.Step();
            EntityId house = M2Scenario.Find(reference.Simulation, "house");
            if (house != EntityId.None && seen.Add(reference.Simulation.World!.Buildings.Get(house).State))
            {
                saveTicks.Add(reference.Simulation.Tick);
            }
        }

        Assert.Equal(4, seen.Count);
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
