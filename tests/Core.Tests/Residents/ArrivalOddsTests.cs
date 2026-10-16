using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Residents;

/// <summary>
/// The newcomer formula (FR-NPC-01, gdd-issues E2 recalculated): every term with the real numbers (25 per free place, at
/// most 2; 25 for 3 days of food for everyone including the newcomer; 20 without raids; fame / 5; era 0/10/20/30;
/// chance = points × 10 ‰, at most 800 ‰; food counts only in the storages, not in the hero's backpack), and the cases
/// where the chance is 0.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class ArrivalOddsTests
{
    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void WithoutAWorldOrABuiltCenterNobodyComes()
    {
        ArrivalOdds core = Arrivals.ComputeOdds(new Simulation(1, SimTestData.Real));
        Assert.Equal(0, core.ChancePermille);
        Assert.False(core.HasCenter);
        Assert.Equal(0, core.FreePlaces);

        Simulation sim = NewResidentGame();
        ArrivalOdds noCenter = Arrivals.ComputeOdds(sim);
        Assert.False(noCenter.HasCenter);
        Assert.Equal(20, noCenter.Points); // only the raid-free points
        Assert.Equal(0, noCenter.ChancePermille);

        GiveCost(sim, "campfire", 1);
        (int x, int y) = FindSpot(sim, "campfire");
        Place(sim, "campfire", x, y, give: false);
        Assert.False(Arrivals.ComputeOdds(sim).HasCenter); // still under construction
        Assert.Equal(0, Arrivals.ComputeOdds(sim).ChancePermille);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void WithoutFreePlacesTheChanceIsZero()
    {
        Simulation sim = NewResidentGame();
        Build(sim, "campfire");

        ArrivalOdds odds = Arrivals.ComputeOdds(sim);

        Assert.True(odds.HasCenter);
        Assert.Equal(0, odds.FreePlaces);
        Assert.Equal(0, odds.SlotPoints);
        Assert.Equal(20, odds.Points);
        Assert.Equal(0, odds.ChancePermille);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void EveryTermAddsUpAndTheCapHolds()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 1);
        EntityId storage = Build(sim, "storage");

        ArrivalOdds odds = Arrivals.ComputeOdds(sim);
        Assert.Equal(new ArrivalOdds(true, 0, 2, 50, 0, 3, 0, 20, 0, 0, 700), odds);
        Assert.Equal(70, odds.Points);

        // Food in the hero's backpack is the hero's own: residents eat from the storages only.
        Give(sim, "berries", 50);
        Assert.Equal((0L, 0), (Arrivals.ComputeOdds(sim).Food, Arrivals.ComputeOdds(sim).FoodPoints));

        // Food for 3 days for one newcomer: 3 berries (food value 1, 1 a day) in the storage. One short gives no food points.
        StoreFood(sim, storage, 2);
        Assert.Equal(0, Arrivals.ComputeOdds(sim).FoodPoints);
        StoreFood(sim, storage, 1);
        ArrivalOdds fed = Arrivals.ComputeOdds(sim);
        Assert.Equal((3L, 3L, 25), (fed.Food, fed.FoodNeeded, fed.FoodPoints));
        Assert.Equal(95, fed.Points);
        Assert.Equal(800, fed.ChancePermille); // 950 capped at arrivalChanceCapPermille

        // Fame: the hero has none yet, so the divided term is 0.
        Assert.Equal(0, Arrivals.HeroFame);
        Assert.Equal(0, fed.FamePoints);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void AtMostTwoFreePlacesCountAndResidentsTakePlacesAndFood()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 2);
        Assert.Equal((4, 50), (Arrivals.ComputeOdds(sim).FreePlaces, Arrivals.ComputeOdds(sim).SlotPoints));

        Spawn(sim);
        Spawn(sim);
        Spawn(sim);
        ArrivalOdds odds = Arrivals.ComputeOdds(sim);

        Assert.Equal((3, 1, 25), (odds.Population, odds.FreePlaces, odds.SlotPoints));
        Assert.Equal(12, odds.FoodNeeded); // (3 residents + the newcomer) × 1 a day × 3 days
        Assert.Equal(450, odds.ChancePermille);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void HomelessResidentsUseUpFreePlaces()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 1);
        Spawn(sim);
        Spawn(sim);
        EntityId homeless = Spawn(sim);

        Assert.Equal(EntityId.None, ResidentOf(sim, homeless).Home);
        Assert.Equal(1, Housing.CountHomeless(sim.World!));
        Assert.Equal(0, Housing.TotalFreePlaces(sim));
        Assert.Equal(0, Arrivals.ComputeOdds(sim).FreePlaces);
        Assert.Equal(0, Arrivals.ComputeOdds(sim).ChancePermille);

        // A second house: two places, one goes to the homeless resident on the next tick, one is left.
        EntityId house = Build(sim, "house");
        Assert.Equal(house, ResidentOf(sim, homeless).Home);
        Assert.Equal(1, Arrivals.ComputeOdds(sim).FreePlaces);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-BUILD-13")]
    public void TheEraAddsItsPoints()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 3);
        for (int i = 0; i < 4; i++)
        {
            Spawn(sim);
        }

        ArrivalOdds odds = Arrivals.ComputeOdds(sim);

        Assert.Equal("village", Core.Buildings.Construction.CurrentEra(sim).Id);
        Assert.Equal(10, odds.EraPoints);
        Assert.Equal((2, 50), (odds.FreePlaces, odds.SlotPoints));
        Assert.Equal(800, odds.ChancePermille); // 50 + 20 + 10 = 80 points
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "NFR-01")]
    public void AtTheResidentLimitNobodyComes()
    {
        ContentCatalog content = WithEconomy(json => json.Replace("\"maxResidents\": 100", "\"maxResidents\": 2", StringComparison.Ordinal));
        Simulation sim = NewResidentGame(content);
        Settle(sim, houses: 2);
        Spawn(sim);
        Assert.Equal(700, Arrivals.ComputeOdds(sim).ChancePermille); // 3 free places count as 2
        Spawn(sim);

        ArrivalOdds odds = Arrivals.ComputeOdds(sim);
        Assert.Equal(2, odds.FreePlaces);
        Assert.Equal(0, odds.ChancePermille);

        sim.Enqueue(new SpawnResidentCommand());
        sim.Step();
        Assert.Equal(2, sim.World!.Residents.Count); // the console spawn respects the limit too
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-ECO-01")]
    public void FoodInBuiltStoragesCountsButNotInOnesUnderConstructionOrTheBackpack()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 1);
        EntityId storage = Build(sim, "storage");
        sim.Step();
        Assert.True(sim.Content.TryGetId("vegetable", out DefId vegetable));
        ref Inventory stock = ref sim.World!.Inventories.Get(storage);
        stock = stock.Add(vegetable, 7, 100, out _);
        StoreFood(sim, storage, 2);
        Give(sim, "berries", 30); // the backpack does not count

        Assert.Equal(9, Arrivals.ComputeOdds(sim).Food);
        Assert.Equal(25, Arrivals.ComputeOdds(sim).FoodPoints);

        GiveCost(sim, "storage", 1);
        (int x, int y) = FindSpot(sim, "storage");
        EntityId second = Place(sim, "storage", x, y, give: false);
        Assert.False(sim.World.Inventories.Has(second));
        Assert.Equal(9, Arrivals.ComputeOdds(sim).Food);
        Assert.Equal(0, Arrivals.ComputeOdds(new Simulation(1, SimTestData.Real)).Food);
    }
}
