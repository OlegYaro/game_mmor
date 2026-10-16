using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Jobs;
using Core.Residents;
using Core.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Economy;

/// <summary>
/// Food and tax (FR-ECO-10, FR-ECO-06, A5): the daily meal from the storages, the warning, the shortage that slows the
/// work and sends residents away one by one, and the daily tax in gold.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class FoodAndTaxTests
{
    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void TheDailyMealEatsFoodItemsInIdOrderFromTheStoragesOnlyAndWarnsWhenLittleIsLeft()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        for (int i = 0; i < 3; i++)
        {
            Spawn(sim, "blacksmith"); // no job: the stock changes only by eating
        }

        Fill(sim, storage, "vegetable", 5);
        Fill(sim, storage, "berries", 2);
        Give(sim, "berries", 10); // the hero's own
        Assert.True(string.CompareOrdinal("berries", "vegetable") < 0);
        Assert.Equal(3, FoodSupply.DailyNeed(sim));
        Assert.Equal(7, FoodSupply.Stored(sim));
        Assert.Equal(2, FoodSupply.DaysLeft(sim));

        StepToDay(sim, 1);

        Assert.Equal(0, Stored(sim, storage, "berries")); // berries first, then one vegetable
        Assert.Equal(4, Stored(sim, storage, "vegetable"));
        Assert.Equal(10, BackpackCount(sim, "berries"));
        Assert.Equal(0, sim.World!.DaysWithoutFood);
        EconomyEvent low = sim.World.EconomyEvents.Single(e => e.Kind == EconomyEventKind.FoodLow);
        Assert.Equal((sim.Tick, 3, 0, 1), (low.Tick, low.Amount, low.Lost, low.DaysLeft));

        // A day with plenty: no warning.
        Fill(sim, storage, "vegetable", 50);
        StepToDay(sim, 2);
        Assert.DoesNotContain(sim.World.EconomyEvents, e => e.Kind == EconomyEventKind.FoodLow && e.Tick == sim.Tick);
        Assert.Equal(51, Stored(sim, storage, "vegetable"));
    }

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void AShortageSlowsTheWorkThenResidentsLeaveOneByOneAndAFullMealEndsIt()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId house = Build(sim, "house");
        EntityId[] residents = [Spawn(sim, "blacksmith"), Spawn(sim, "blacksmith"), Spawn(sim, "blacksmith")];
        EconomyBalanceDef economy = sim.Content.Economy;
        Assert.Equal(2, economy.DaysWithoutFoodUntilFirstLeaves);
        Assert.Equal(1, economy.LeaveIntervalDays);
        Resident first = sim.World!.Residents.Get(residents[0]);
        Assert.Equal(house, first.Home);

        StepToDay(sim, 1);
        Assert.Equal(1, sim.World.DaysWithoutFood);
        EconomyEvent shortage = sim.World.EconomyEvents.Single(e => e.Kind == EconomyEventKind.FoodShortage);
        Assert.Equal((0, 3, 1), (shortage.Amount, shortage.Lost, shortage.DaysLeft));
        Assert.Equal(1000 * economy.ShortageSlowdownPermille / 1000, JobBoard.WorkSpeedPermille(sim, first with { Skills = default }, JobKind.Chop));
        Assert.All(residents, r => Assert.NotEqual(ResidentState.Leaving, sim.World.Residents.Get(r).State));

        StepToDay(sim, 2);
        Assert.Equal(2, sim.World.DaysWithoutFood);
        Assert.Equal(2, sim.World.LastLeaveDay);
        Assert.Equal(ResidentState.Leaving, sim.World.Residents.Get(residents[0]).State); // the lowest id goes first
        Assert.Equal(EntityId.None, sim.World.Residents.Get(residents[0]).Home);
        Assert.Equal(house, sim.World.Residents.Get(residents[2]).Home); // the homeless one took the freed place at once
        Assert.Equal(0, Housing.CountHomeless(sim.World)); // a leaving resident wants no place
        Assert.Equal(2, FoodSupply.Eaters(sim.World));
        ResidentEvent left = sim.World.ResidentEvents.Single(e => e.Kind == ResidentEventKind.LeftHungry);
        Assert.Equal(residents[0], left.Resident);
        Assert.True(sim.World.Movers.Get(residents[0]).IsMoving);

        // It walks away and is gone.
        StepUntil(sim, () => !sim.World.Residents.Has(residents[0]), 9000);
        Assert.False(sim.Entities.IsAlive(residents[0]));
        Assert.Contains(sim.World.ResidentEvents, e => e.Kind == ResidentEventKind.Left && e.Resident == residents[0]);
        Assert.Equal(2, sim.World.Residents.Count);

        StepToDay(sim, 3);
        Assert.Equal(ResidentState.Leaving, sim.World.Residents.Get(residents[1]).State); // the next one a day later
        Assert.Equal(3, sim.World.LastLeaveDay);

        // Enough food for everyone: the shortage ends.
        Fill(sim, storage, "vegetable", 30);
        StepToDay(sim, 4);
        Assert.Equal(0, sim.World.DaysWithoutFood);
        Assert.Equal(GameWorld.NoLeaveDay, sim.World.LastLeaveDay);
        Assert.NotEqual(ResidentState.Leaving, sim.World.Residents.Get(residents[2]).State);
    }

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void ALeavingResidentGivesItsLoadToTheStorage()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId lumberjack = Spawn(sim, "lumberjack");
        StepUntil(sim, () => JobOf(sim, lumberjack).CarryCount > 0);
        int carried = JobOf(sim, lumberjack).CarryCount;

        ResidentRules.Leave(sim, lumberjack);

        Assert.Equal(carried, Stored(sim, storage, "wood"));
        Assert.Equal(ResidentJob.Idle(sim.Tick), JobOf(sim, lumberjack));
        Assert.Empty(sim.World!.Reservations.Reservations);
        while (sim.World.Residents.Has(lumberjack))
        {
            Assert.Equal(JobKind.None, JobOf(sim, lumberjack).Kind); // a leaving resident takes no job
            sim.Step();
        }
    }

    [Fact]
    [Trait("Req", "FR-ECO-06")]
    public void TheTaxIsPaidEveryDayInGoldIntoTheStoragesThatTakeIt()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        for (int i = 0; i < 3; i++)
        {
            Spawn(sim, "blacksmith");
        }

        Fill(sim, storage, "vegetable", 100);
        Assert.Equal(1, FoodSupply.CenterLevel(sim));
        int tax = sim.Content.Economy.TaxPerResidentPerDay * 3 * 1;
        Assert.Equal(tax, FoodSupply.TaxPerDay(sim));

        StepToDay(sim, 1);
        Assert.Equal(tax, Stored(sim, storage, FoodSupply.TaxItem));
        EconomyEvent paid = sim.World!.EconomyEvents.Single(e => e.Kind == EconomyEventKind.Tax);
        Assert.Equal((FoodSupply.TaxItem, tax, 0), (paid.Item, paid.Amount, paid.Lost));

        // A storage that refuses money: the gold is lost and reported.
        sim.Enqueue(new SetStorageFilterCommand(storage, ItemCategories.AllMask & ~ItemCategories.BitOf(ItemCategories.Currency)));
        StepToDay(sim, 2);
        EconomyEvent lost = sim.World.EconomyEvents.Last(e => e.Kind == EconomyEventKind.Tax);
        Assert.Equal((0, tax), (lost.Amount, lost.Lost));
        Assert.Equal(tax, Stored(sim, storage, FoodSupply.TaxItem));
    }

    [Fact]
    [Trait("Req", "FR-ECO-06")]
    [Trait("Req", "FR-ECO-10")]
    public void WithoutResidentsNobodyEatsOrPaysAndAGameWithoutAWorldHasNoFood()
    {
        (Simulation sim, _) = NewSettlement();
        StepToDay(sim, 1);
        Assert.Empty(sim.World!.EconomyEvents);
        Assert.Equal(-1, FoodSupply.DaysLeft(sim));
        Assert.Equal(0, FoodSupply.TaxPerDay(sim));

        var core = new Simulation(1, Core.Tests.Sim.SimTestData.Real);
        core.Step(9001);
        Assert.Equal(0, FoodSupply.Stored(core));
        Assert.Equal(0, FoodSupply.DailyNeed(core));
        Assert.Equal(0, FoodSupply.CenterLevel(core));
        Assert.Equal(0, FoodSupply.TaxPerDay(core));
    }

    [Fact]
    [Trait("Req", "FR-ECO-06")]
    public void ACenterUnderConstructionPaysNoTax()
    {
        Simulation sim = NewGame(Quiet);
        GiveCost(sim, "campfire", 1);
        (int x, int y) = FindSpot(sim, "campfire");
        EntityId center = Place(sim, "campfire", x, y, give: false);
        Assert.Equal(BuildingState.Constructing, BuildingOf(sim, center).State);
        Assert.Equal(0, FoodSupply.CenterLevel(sim));
    }
}
