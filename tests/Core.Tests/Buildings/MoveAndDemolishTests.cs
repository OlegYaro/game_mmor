using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Buildings;

/// <summary>Moving (FR-BUILD-13, E2, E3) and demolishing (FR-BUILD-12, E4, E5, E6) buildings.</summary>
public sealed class MoveAndDemolishTests
{
    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void AMoveKeepsTheEntityAndHoldsBothFootprintsUntilItEnds()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        Build(sim, "campfire");
        EntityId house = Build(sim, "house");
        Building before = BuildingOf(sim, house);
        (int x, int y) = FindSpot(sim, "house", rotation: 1, minDistance: 4, near: (before.X, before.Y), moving: house);
        int moveTicks = SimTestData.Real.Buildings.Single(b => b.Id == "house").Levels[0].MoveTicks;

        BuildingEvent moved = Order(sim, new MoveBuildingCommand(house, x, y, 1));

        Assert.Equal(BuildResult.Ok, moved.Result);
        Assert.Equal((x, y), (moved.X, moved.Y));
        Building moving = BuildingOf(sim, house);
        Assert.Equal(before with { State = BuildingState.Moving, TicksLeft = moveTicks - 1, TargetX = x, TargetY = y, TargetRotation = 1 }, moving);
        Assert.All(FootprintTiles(before.X, before.Y, 2, 2).Concat(FootprintTiles(x, y, 2, 2)), t => Assert.Equal(house, world.Tiles.GetOccupant(t.X, t.Y)));
        Assert.Equal(BuildResult.Busy, Order(sim, new MoveBuildingCommand(house, before.X, before.Y)).Result);
        Assert.Equal(BuildResult.Busy, Order(sim, new UpgradeBuildingCommand(house)).Result);
        Assert.Equal(BuildResult.Busy, Order(sim, new DemolishBuildingCommand(house)).Result);

        StepUntilActive(sim, house);

        Assert.Equal(before with { X = x, Y = y, Rotation = 1 }, BuildingOf(sim, house));
        Assert.All(FootprintTiles(before.X, before.Y, 2, 2), t => Assert.False(world.Tiles.IsOccupied(t.X, t.Y)));
        Assert.All(FootprintTiles(x, y, 2, 2), t => Assert.Equal(house, world.Tiles.GetOccupant(t.X, t.Y)));
        Assert.Equal(1 + 4, world.Tiles.OccupiedTiles);
        Assert.True(world.Pathfinder.Grid.IsPassable(before.X, before.Y));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void AShortMoveKeepsTheSharedTilesOccupied()
    {
        Simulation sim = PlacementTests.RevealedWithCenterAt((32, 32), out _);
        GameWorld world = sim.World!;
        GiveCost(sim, "house", 1);
        (int x, int y) = FindTile(sim, (tx, ty) => tx + 3 <= world.Size && Construction.CheckPlacement(sim, "house", tx, ty, 0).IsOk
            && Construction.CheckPlacement(sim, "house", tx + 1, ty, 0).IsOk);
        EntityId house = Place(sim, "house", x, y, give: false);
        StepUntilActive(sim, house);

        Assert.Equal(BuildResult.Ok, Order(sim, new MoveBuildingCommand(house, x + 1, y)).Result);
        Assert.Equal(1 + 6, world.Tiles.OccupiedTiles);
        StepUntilActive(sim, house);

        Assert.Equal(1 + 4, world.Tiles.OccupiedTiles);
        Assert.False(world.Tiles.IsOccupied(x, y));
        Assert.Equal(house, world.Tiles.GetOccupant(x + 1, y));
        Assert.Equal(house, world.Tiles.GetOccupant(x + 2, y + 1));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void MovesUseThePlacementRules()
    {
        Simulation sim = PlacementTests.RevealedWithCenterAt((4, 4), out (int X, int Y) center, Extended);
        GameWorld world = sim.World!;
        EntityId depot = Build(sim, "test_depot");
        Building placed = BuildingOf(sim, depot);
        Territory territory = Construction.GetTerritory(sim)!.Value;
        (int tx, int ty) = FindTile(sim, (x, y) => world.Tiles.GetEffectiveResource(x, y) == ResourceKind.Tree && territory.Contains(x, y)
            && !world.Map.Terrain.IsWater(x, y) && !world.Map.Terrain.IsRiver(x, y));

        Assert.Equal(BuildResult.OutsideTerritory, Order(sim, new MoveBuildingCommand(depot, 60, 60)).Result);
        Assert.Equal(BuildResult.Occupied, Order(sim, new MoveBuildingCommand(depot, center.X, center.Y)).Result);
        Assert.Equal(BuildResult.ResourceInTheWay, Order(sim, new MoveBuildingCommand(depot, tx, ty)).Result);
        Assert.Equal(BuildResult.SamePlace, Order(sim, new MoveBuildingCommand(depot, placed.X, placed.Y)).Result);
        Assert.Equal(BuildResult.InvalidRotation, Order(sim, new MoveBuildingCommand(depot, placed.X, placed.Y, 1)).Result);
        Assert.Equal(BuildResult.NotABuilding, Order(sim, new MoveBuildingCommand(world.HeroEntity, 1, 1)).Result);
        Assert.Equal(placed, BuildingOf(sim, depot));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void ABuildingUnderConstructionOrUpgradeCannotMove()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");
        EntityId house = Place(sim, "house", x, y, give: false);

        Assert.Equal(BuildResult.Busy, Order(sim, new MoveBuildingCommand(house, x + 4, y)).Result);
        StepUntilActive(sim, house);
        GiveCost(sim, "house", 2);
        Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(house)).Result);
        Assert.Equal(BuildResult.Busy, Order(sim, new MoveBuildingCommand(house, x + 4, y)).Result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void AStorageIsUnavailableWhileMovingAndKeepsItsStock()
    {
        Simulation sim = NewGame(Extended);
        Build(sim, "campfire");
        EntityId depot = Build(sim, "test_depot");
        sim.Step();
        Assert.True(sim.Content.TryGetId("wood", out DefId wood));
        ref Inventory stock = ref sim.World!.Inventories.Get(depot);
        stock = stock.Add(wood, 120, 100, out _);
        Building placed = BuildingOf(sim, depot);
        (int x, int y) = FindSpot(sim, "test_depot", minDistance: 3, near: (placed.X, placed.Y), moving: depot);

        Assert.Equal(BuildResult.Ok, Order(sim, new MoveBuildingCommand(depot, x, y)).Result);
        Assert.Equal(0, Count(sim, "wood"));
        GiveCost(sim, "house", 1);
        Assert.Equal(20, Count(sim, "wood")); // only the backpack counts

        StepUntilActive(sim, depot);
        Assert.Equal(140, Count(sim, "wood"));
        Assert.Equal(120, sim.World.Inventories.Get(depot).Count(wood));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void CancellingAMoveFreesTheNewPlace()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId house = Build(sim, "house");
        Building before = BuildingOf(sim, house);
        (int x, int y) = FindSpot(sim, "house", minDistance: 4, near: (before.X, before.Y), moving: house);
        Assert.Equal(BuildResult.Ok, Order(sim, new MoveBuildingCommand(house, x, y)).Result);

        Assert.Equal(BuildResult.Ok, Order(sim, new CancelBuildingCommand(house)).Result);

        Assert.Equal(before, BuildingOf(sim, house));
        Assert.All(FootprintTiles(x, y, 2, 2), t => Assert.False(sim.World!.Tiles.IsOccupied(t.X, t.Y)));
        Assert.Equal(1 + 4, sim.World!.Tiles.OccupiedTiles);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    [Trait("Req", "FR-BUILD-06")]
    public void TheCenterMovesOnlyWhereItsTerritoryStillHoldsEveryBuilding()
    {
        Simulation sim = PlacementTests.RevealedWithCenterAt((32, 32), out (int X, int Y) center, Extended);
        GiveCost(sim, "test_depot", 1);
        (int dx, int dy) = FindSpot(sim, "test_depot", near: (center.X - 30, center.Y), minDistance: 0);
        Assert.True(Math.Abs(dx - center.X) > 20, "the depot should stand near the territory's west edge");
        EntityId depot = Place(sim, "test_depot", dx, dy, give: false);
        EntityId campfire = Construction.FindCenter(sim);

        // Somewhere east of the depot by more than the radius the depot would be left outside.
        (int ex, int ey) = FindTile(sim, (x, y) => (x - dx) * (x - dx) + (y - dy) * (y - dy) > 48 * 48
            && Construction.CheckPlacement(sim, "campfire", x, y, 0, campfire).Reason is BuildResult.BuildingsOutsideTerritory);
        Assert.Equal(new PlacementResult(BuildResult.BuildingsOutsideTerritory, dx, dy), Construction.CheckPlacement(sim, "campfire", ex, ey, 0, campfire));

        // A short move keeps it inside; while the center moves, new buildings must fit both territories.
        (int nx, int ny) = FindSpot(sim, "campfire", near: (center.X - 2, center.Y), minDistance: 1, moving: campfire);
        Assert.Equal(BuildResult.Ok, Order(sim, new MoveBuildingCommand(campfire, nx, ny)).Result);
        Assert.True(Construction.IsInTerritory(sim, center.X, center.Y, 1, 1));
        Assert.Equal(new Territory(center.X, center.Y, 48), Construction.GetTerritory(sim));
        StepUntilActive(sim, campfire);
        Assert.Equal(new Territory(nx, ny, 48), Construction.GetTerritory(sim));
        Assert.True(sim.World!.Buildings.Has(depot));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void DemolishRefundsSeventyFivePercentOfAllLevelsAndFreesTheTiles()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId house = Build(sim, "house");
        GiveCost(sim, "house", 2);
        Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(house)).Result);
        StepUntilActive(sim, house);
        Building built = BuildingOf(sim, house);

        BuildingEvent demolished = Order(sim, new DemolishBuildingCommand(house));

        Assert.Equal(BuildResult.Ok, demolished.Result);
        Assert.Empty(demolished.Lost);
        Assert.Equal((37, 7), (BackpackCount(sim, "wood"), BackpackCount(sim, "stone"))); // 750 ‰ of 50 and 10, rounded down
        Assert.False(sim.Entities.IsAlive(house));
        Assert.All(FootprintTiles(built.X, built.Y, 2, 2), t => Assert.False(sim.World!.Tiles.IsOccupied(t.X, t.Y)));
        Assert.Equal(BuildResult.NotABuilding, Order(sim, new DemolishBuildingCommand(house)).Result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void TheCenterAndTheLastStorageCannotBeDemolished()
    {
        Simulation sim = NewGame();
        EntityId campfire = Build(sim, "campfire");
        EntityId first = Build(sim, "storage");
        GiveCost(sim, "storage", 1);
        (int x, int y) = FindSpot(sim, "storage");
        EntityId second = Place(sim, "storage", x, y, give: false);

        Assert.Equal(BuildResult.CenterNotRemovable, Order(sim, new DemolishBuildingCommand(campfire)).Result);
        Assert.Equal(BuildResult.LastStorage, Order(sim, new DemolishBuildingCommand(first)).Result); // the other one is not built yet
        Assert.Equal(BuildResult.Busy, Order(sim, new DemolishBuildingCommand(second)).Result);

        StepUntilActive(sim, second);
        Assert.Equal(BuildResult.Ok, Order(sim, new DemolishBuildingCommand(first)).Result);
        Assert.Equal(BuildResult.LastStorage, Order(sim, new DemolishBuildingCommand(second)).Result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void ADemolishedStoragesStockAndRefundGoToTheOtherStoragesThenTheBackpack()
    {
        Simulation sim = NewGame(Extended);
        Build(sim, "campfire");
        EntityId keep = Build(sim, "test_depot");
        EntityId gone = Build(sim, "test_depot");
        sim.Step();
        Assert.True(sim.Content.TryGetId("berries", out DefId berries));
        Assert.True(sim.Content.TryGetId("wood", out DefId wood));
        ref Inventory stock = ref sim.World!.Inventories.Get(gone);
        stock = stock.Add(berries, 130, 100, out _);
        ref Inventory other = ref sim.World.Inventories.Get(keep);
        other = other.Add(wood, 50, 100, out _); // one free stack left

        BuildingEvent demolished = Order(sim, new DemolishBuildingCommand(gone));

        Assert.Equal(BuildResult.Ok, demolished.Result);
        Assert.Empty(demolished.Lost);
        Assert.Equal(100, sim.World.Inventories.Get(keep).Count(berries)); // fills the free stack
        Assert.Equal(52, sim.World.Inventories.Get(keep).Count(wood));     // 50 + 750 ‰ of 3, the wood stack has room
        Assert.Equal(30, BackpackCount(sim, "berries"));                   // the rest to the backpack
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void WhatDoesNotFitAnywhereIsLostAndReported()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId house = Build(sim, "house");
        Assert.True(sim.Content.TryGetId("stone", out DefId stone));
        ref Inventory backpack = ref sim.World!.Inventories.Get(sim.World.HeroEntity);
        backpack = backpack.Add(stone, 50 * backpack.Backpack.Count, 50, out _);
        Assert.Equal(backpack.Backpack.Count, backpack.UsedStacks);

        BuildingEvent demolished = Order(sim, new DemolishBuildingCommand(house));

        Assert.Equal(BuildResult.Ok, demolished.Result);
        Assert.Equal([new ItemAmountDef("wood", 15)], demolished.Lost);
        Assert.False(sim.Entities.IsAlive(house));
        Assert.Contains(sim.Log.GetEvents(), e => e.Level == Core.Logging.LogLevel.Warning && e.Render().Contains("lost 15 wood", StringComparison.Ordinal));
    }
}
