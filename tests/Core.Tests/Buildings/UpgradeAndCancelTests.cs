using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Buildings;

/// <summary>Upgrades (FR-BUILD-09) and cancelling (FR-BUILD-12): same entity, payment at start, 100% back on cancel.</summary>
public sealed class UpgradeAndCancelTests
{
    [Fact]
    [Trait("Req", "FR-BUILD-09")]
    public void UpgradeKeepsTheEntityPaysAtStartAndTakesTheLevelsBuildTicks()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId house = Build(sim, "house");
        Building before = BuildingOf(sim, house);

        Assert.Equal(BuildResult.NotEnoughMaterials, Order(sim, new UpgradeBuildingCommand(house)).Result);
        GiveCost(sim, "house", 2);
        BuildingEvent upgraded = Order(sim, new UpgradeBuildingCommand(house));

        Assert.Equal(BuildResult.Ok, upgraded.Result);
        Assert.Equal(house, upgraded.Building);
        Assert.Equal((0, 0), (Count(sim, "wood"), Count(sim, "stone")));
        Building upgrading = BuildingOf(sim, house);
        Assert.Equal(before with { Level = 2, State = BuildingState.Upgrading, TicksLeft = 99 }, upgrading);
        Assert.Equal(PlacementResult.Refuse(BuildResult.Busy), Construction.CheckPlacement(sim, "house", before.X + 3, before.Y, 0, house));
        Assert.Equal(BuildResult.Busy, Order(sim, new UpgradeBuildingCommand(house)).Result);

        Assert.Equal(98, StepUntilActive(sim, house));
        Assert.Equal(before with { Level = 2 }, BuildingOf(sim, house));

        GiveCost(sim, "house", 3);
        Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(house)).Result);
        StepUntilActive(sim, house);
        Assert.Equal(3, BuildingOf(sim, house).Level);
        Assert.Equal(BuildResult.MaxLevel, Order(sim, new UpgradeBuildingCommand(house)).Result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-09")]
    public void UpgradeIsRefusedWhileBuildingAndWhenTheEraIsTooLow()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        GiveCost(sim, "storage", 1);
        (int x, int y) = FindSpot(sim, "storage");
        EntityId storage = Place(sim, "storage", x, y, give: false);

        Assert.Equal(BuildResult.Busy, Order(sim, new UpgradeBuildingCommand(storage)).Result);
        StepUntilActive(sim, storage);
        GiveCost(sim, "storage", 2);
        Assert.Equal(BuildResult.RequirementNotMet, Order(sim, new UpgradeBuildingCommand(storage)).Result); // level 2 needs the village era
        Assert.Equal(BuildResult.NotABuilding, Order(sim, new UpgradeBuildingCommand(sim.World!.HeroEntity)).Result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-09")]
    public void AStorageKeepsItsStockGrowsWhenUpgradedAndIsUnavailableMeanwhile()
    {
        Simulation sim = NewGame(Extended);
        Build(sim, "campfire");
        EntityId depot = Build(sim, "test_depot");
        sim.Step();
        Assert.True(sim.Content.TryGetId("berries", out DefId berries));
        ref Inventory stock = ref sim.World!.Inventories.Get(depot);
        stock = stock.Add(berries, 150, 100, out _);
        Assert.Equal(2, sim.World.Inventories.Get(depot).Backpack.Count);
        Assert.Equal(150, Count(sim, "berries"));

        GiveCost(sim, "test_depot", 2);
        Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(depot)).Result);
        Assert.Equal(0, Count(sim, "berries")); // not available while upgrading
        StepUntilActive(sim, depot);
        sim.Step();

        Inventory grown = sim.World.Inventories.Get(depot);
        Assert.Equal(4, grown.Backpack.Count);
        Assert.Equal(150, grown.Count(berries));
        Assert.Equal(150, Count(sim, "berries"));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void CancellingAConstructionRefundsEverythingAndFreesTheTiles()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");
        EntityId house = Place(sim, "house", x, y, give: false);
        Assert.Equal(0, Count(sim, "wood"));

        BuildingEvent cancelled = Order(sim, new CancelBuildingCommand(house));

        Assert.Equal(BuildResult.Ok, cancelled.Result);
        Assert.Empty(cancelled.Lost);
        Assert.Equal(20, Count(sim, "wood")); // cancelRefundPermille 1000
        Assert.False(sim.Entities.IsAlive(house));
        Assert.All(FootprintTiles(x, y, 2, 2), t => Assert.False(sim.World!.Tiles.IsOccupied(t.X, t.Y)));
        Assert.Equal(BuildResult.NotABuilding, Order(sim, new CancelBuildingCommand(house)).Result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void CancellingTwiceInOneTickRefundsOnce()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");
        EntityId house = Place(sim, "house", x, y, give: false);

        sim.Enqueue(new CancelBuildingCommand(house));
        sim.Enqueue(new CancelBuildingCommand(house));
        sim.Enqueue(new DemolishBuildingCommand(house));
        sim.Step();

        Assert.Equal([BuildResult.Ok, BuildResult.NotABuilding, BuildResult.NotABuilding], sim.World!.BuildingEvents.TakeLast(3).Select(e => e.Result));
        Assert.Equal(20, Count(sim, "wood"));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void CancellingAnUpgradeGoesBackALevelWithAFullRefund()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId house = Build(sim, "house");
        Building active = BuildingOf(sim, house);
        GiveCost(sim, "house", 2);
        Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(house)).Result);

        Assert.Equal(BuildResult.Ok, Order(sim, new CancelBuildingCommand(house)).Result);

        Assert.Equal(active, BuildingOf(sim, house));
        Assert.Equal((30, 10), (Count(sim, "wood"), Count(sim, "stone")));
        Assert.Equal(BuildResult.NothingToCancel, Order(sim, new CancelBuildingCommand(house)).Result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    [Trait("Req", "FR-BUILD-06")]
    public void TheCenterUnderConstructionCannotBeCancelledOnceOthersStandAroundIt()
    {
        Simulation sim = NewGame();
        GiveCost(sim, "campfire", 1);
        (int cx, int cy) = FindSpot(sim, "campfire");
        EntityId campfire = Place(sim, "campfire", cx, cy, give: false);
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");
        EntityId house = Place(sim, "house", x, y, give: false); // the territory exists from the placement on

        Assert.Equal(BuildResult.CenterInUse, Order(sim, new CancelBuildingCommand(campfire)).Result);
        Assert.Equal(BuildResult.Ok, Order(sim, new CancelBuildingCommand(house)).Result);
        Assert.Equal(BuildResult.Ok, Order(sim, new CancelBuildingCommand(campfire)).Result);
        Assert.Equal(25, Count(sim, "wood"));
        Assert.Equal(0, sim.World!.Buildings.Count);
        Assert.Equal(0, sim.World.Tiles.OccupiedTiles);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void ACancelRefundGoesToAStorageFirst()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId storage = Build(sim, "storage");
        sim.Step();
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");
        EntityId house = Place(sim, "house", x, y, give: false);

        Assert.Equal(BuildResult.Ok, Order(sim, new CancelBuildingCommand(house)).Result);

        Assert.True(sim.Content.TryGetId("wood", out DefId wood));
        Assert.Equal(20, sim.World!.Inventories.Get(storage).Count(wood));
        Assert.Equal(0, BackpackCount(sim, "wood"));
        Assert.Equal(1000, SimTestData.Real.Construction.CancelRefundPermille);
    }
}
