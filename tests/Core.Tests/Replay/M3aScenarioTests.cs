using Core.Economy;
using Core.Entities;
using Core.Jobs;
using Core.Replay;
using Core.Residents;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Replay;

/// <summary>
/// M3a acceptance (GDD §18 P-30, FR-NPC-01, FR-NPC-06, FR-ECO-10, NFR-04): the <c>scenario_m3a</c> replay matches its
/// golden hashes; five newcomers arrive and then feed the settlement for three game days without an order (the food in
/// the storages holds or grows, no shortage); two runs are identical; a save + load in the middle of the work continues
/// exactly like the uninterrupted run.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class M3aScenarioTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-ECO-10")]
    [Trait("Req", "NFR-04")]
    public void FiveNewcomersFeedTheSettlementForThreeDaysWithoutOrders()
    {
        ReplayGolden golden = GoldenFiles.M3a;
        Assert.Equal(M3aScenario.Options, golden.Replay.NewGame);
        Assert.InRange(golden.Replay.EndTick, 1, M3aScenario.TickBudget);
        Assert.Equal(11, golden.Replay.Commands.Count); // three gives, five placements, an upgrade, a walk and the hand-in
        Assert.Equal(3, golden.Replay.Commands.Count(c => c.Command is GiveItemCommand));
        long lastOrder = golden.Replay.Commands.Max(c => c.Tick);

        ReplayPlayer player = ReplayPlayer.Start(golden.Replay, SimTestData.Real);
        var arrivals = new List<ResidentEvent>();
        var foodAtDayStart = new List<long>();
        var economy = new List<EconomyEvent>();
        while (!player.IsFinished)
        {
            player.Step();
            Simulation current = player.Simulation;
            arrivals.AddRange(current.World!.ResidentEvents.Where(e => e.Kind == ResidentEventKind.Arrived && e.Tick == current.Tick));
            economy.AddRange(current.World.EconomyEvents.Where(e => e.Tick == current.Tick));
            if (current.Now.TickOfDay == 0)
            {
                foodAtDayStart.Add(FoodSupply.Stored(current)); // after the meal
            }
        }

        AssertNoDivergence(golden.Expected, player.Hashes);
        Simulation sim = player.Simulation;
        GameWorld world = sim.World!;
        Assert.Equal(M3aScenario.Newcomers, arrivals.Count);
        Assert.True(arrivals[0].Tick > lastOrder, "Nobody may arrive before the settlement is ready.");
        Assert.Equal(M3aScenario.Newcomers, world.Residents.Count);
        Assert.All(world.Residents.Values.ToArray(), r => Assert.NotEqual(EntityId.None, r.Home));

        // The fed days: from the morning after the last newcomer to the end, the stored food never falls and nobody starves.
        int firstFedDay = (int)M3aScenario.LastArrivalDay(sim, arrivals) + 1;
        long[] fed = foodAtDayStart.Skip(firstFedDay - 1).ToArray();
        output.WriteLine($"scenario_m3a ends at tick {golden.Replay.EndTick} (day {sim.Now.Day}); arrivals on days {string.Join(", ", arrivals.Select(a => sim.Calendar.At(a.Tick).Day))}; "
            + $"food at the fed days' starts {string.Join(", ", fed)}");
        Assert.Equal(M3aScenario.FedDays + 1, fed.Length);
        Assert.True(fed[^1] >= fed[0], $"The food stock fell over the fed days: {string.Join(", ", fed)}.");
        Assert.True(fed[0] >= M3aScenario.Newcomers * sim.Content.Economy.FoodPerResidentPerDay);
        Assert.DoesNotContain(economy, e => e.Kind == EconomyEventKind.FoodShortage);
        Assert.Equal(0, world.DaysWithoutFood);
        Assert.Contains(economy, e => e.Kind == EconomyEventKind.Tax && e.Amount > 0);
        Assert.Contains(economy, e => e.Kind == EconomyEventKind.HeroDeposit && e.Amount > 0);

        // Everybody worked: wood, vegetables and gold lie in the storage.
        EntityId storage = M3aScenario.Find(sim, "storage");
        Assert.True(Jobs.JobTestWorld.Stored(sim, storage, "wood") > 0);
        Assert.True(Jobs.JobTestWorld.Stored(sim, storage, "vegetable") > 0);
        Assert.True(Jobs.JobTestWorld.Stored(sim, storage, FoodSupply.TaxItem) > 0);
        Assert.Equal(2, world.Buildings.Get(M3aScenario.Find(sim, "house")).Level); // five places: 3 + 2
        Assert.Equal(0, Housing.TotalFreePlaces(sim));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void TwoRunsOfTheM3aScenarioGiveIdenticalHashesEveryTick()
    {
        ReplayFile replay = GoldenFiles.M3a.Replay;

        HashTrace first = GoldenFiles.RunDense(replay);
        HashTrace second = GoldenFiles.RunDense(replay);

        Assert.Equal(replay.EndTick, first.Count);
        AssertNoDivergence(first, second);
    }

    // Save in the middle of the work (residents walking, working, carrying, holding reservations), load, continue to
    // the end: every tick's hashes equal the uninterrupted run's.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-07")]
    public void SaveLoadInTheMiddleOfTheWorkContinuesLikeTheUninterruptedRun()
    {
        ReplayFile replay = GoldenFiles.M3a.Replay;
        ReplayPlayer reference = ReplayPlayer.Start(replay, SimTestData.Real);
        long busiest = 0;
        int most = 0;
        while (!reference.IsFinished)
        {
            reference.Step();
            int held = reference.Simulation.World!.Reservations.Reservations.Count;
            (busiest, most) = held > most ? (reference.Simulation.Tick, held) : (busiest, most);
        }

        Assert.True(most >= 3, $"The busiest tick holds only {most} reservations.");
        foreach (long saveTick in new[] { busiest, replay.EndTick - 4000 })
        {
            ReplayPlayer before = ReplayPlayer.Start(replay, SimTestData.Real);
            before.RunTo(saveTick);
            Assert.Contains(before.Simulation.World!.Jobs.Values.ToArray(), j => j.Kind != JobKind.None || j.IsCarrying);
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
