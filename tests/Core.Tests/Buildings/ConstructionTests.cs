using Core.Buildings;
using Core.Entities;
using Core.Heroes;
using Core.Logging;
using Core.Sim;
using Core.Tests.Heroes;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Buildings;

/// <summary>Placing and constructing (FR-BUILD-02, A2): payment at once, reserved tiles, build ticks, the storage stock.</summary>
public sealed class ConstructionTests
{
    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void PlacingPaysAtOnceReservesTheFootprintAndBuildsInBuildTicks()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        GiveCost(sim, "campfire", 1);
        Give(sim, "wood", 2);
        (int x, int y) = FindSpot(sim, "campfire");
        int buildTicks = SimTestData.Real.Buildings.Single(b => b.Id == "campfire").Levels[0].BuildTicks;

        BuildingEvent placed = Order(sim, new PlaceBuildingCommand("campfire", x, y));

        Assert.Equal(BuildResult.Ok, placed.Result);
        Assert.Equal(PlaceBuildingCommand.KindName, placed.Command);
        Assert.Equal(("campfire", x, y, sim.Tick), (placed.BuildingId, placed.X, placed.Y, placed.Tick));
        Assert.Equal(2, Count(sim, "wood")); // 5 paid at once
        EntityId campfire = placed.Building;
        Building building = BuildingOf(sim, campfire);
        Assert.Equal(BuildingState.Constructing, building.State);
        Assert.Equal((1, x, y, 0, Building.PlayerSettlement), (building.Level, building.X, building.Y, building.Rotation, building.Settlement));
        Assert.Equal(buildTicks - 1, building.TicksLeft); // the order's tick counts
        Assert.Equal(10, Construction.ProgressPermille(sim.Content, building));
        Assert.Equal(campfire, world.Tiles.GetOccupant(x, y));
        Assert.False(world.Pathfinder.Grid.IsPassable(x, y));

        sim.Step(buildTicks - 2);
        Assert.Equal(1, BuildingOf(sim, campfire).TicksLeft);
        Assert.Equal(990, Construction.ProgressPermille(sim.Content, BuildingOf(sim, campfire)));
        sim.Step();
        Assert.Equal(BuildingState.Active, BuildingOf(sim, campfire).State);
        Assert.Equal(0, BuildingOf(sim, campfire).TicksLeft);
        Assert.Equal(1000, Construction.ProgressPermille(sim.Content, BuildingOf(sim, campfire)));
        Assert.False(world.Inventories.Has(campfire)); // not a storage
        Assert.Contains(sim.Log.GetEvents(), e => e.Category == BuildingRules.LogCategory && e.Render().Contains("constructing done", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void AStorageGetsItsStockWhenBuilt()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        GiveCost(sim, "storage", 1);
        (int x, int y) = FindSpot(sim, "storage");
        EntityId storage = Place(sim, "storage", x, y, give: false);

        Assert.False(sim.World!.Inventories.Has(storage));
        StepUntilActive(sim, storage);
        sim.Step(); // the stock is added by the buffer right after the system

        Inventory stock = sim.World.Inventories.Get(storage);
        Assert.Equal(SimTestData.Real.Buildings.Single(b => b.Id == "storage").Levels[0].Storage, stock.Backpack.Count);
        Assert.Empty(stock.Equipment);
        Assert.Equal(0, stock.UsedStacks);
        Assert.Equal(1, sim.World.Heroes.Count); // a stock is not a hero
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void ASecondPlacementInTheSameTickSeesTheFirst()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        GiveCost(sim, "house", 1);
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");

        sim.Enqueue(new PlaceBuildingCommand("house", x, y));
        sim.Enqueue(new PlaceBuildingCommand("house", x, y));
        sim.Step();

        IReadOnlyList<BuildingEvent> events = sim.World!.BuildingEvents;
        Assert.Equal(BuildResult.Ok, events[^2].Result);
        Assert.Equal(BuildResult.Occupied, events[^1].Result);
        Assert.Equal(EntityId.None, events[^1].Building);
        Assert.Equal(20, Count(sim, "wood")); // paid once
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void ARefusedPlacementChangesNothing()
    {
        Simulation sim = NewGame();
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "campfire");
        int entities = sim.Entities.Registry.AliveCount;

        BuildingEvent refused = Order(sim, new PlaceBuildingCommand("house", x, y));

        Assert.Equal(BuildResult.NoCenter, refused.Result);
        Assert.Equal(entities, sim.Entities.Registry.AliveCount);
        Assert.Equal(0, sim.World!.Buildings.Count);
        Assert.Equal(0, sim.World.Tiles.OccupiedTiles);
        Assert.Equal(20, Count(sim, "wood"));
        Assert.Contains(sim.Log.GetEvents(), e => e.Category == BuildingRules.LogCategory && e.Render().Contains("no_center", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void DestroyEntityDoesNotRemoveABuilding()
    {
        Simulation sim = NewGame();
        EntityId campfire = Build(sim, "campfire");

        sim.Enqueue(new DestroyEntityCommand(campfire));
        sim.Step();

        Assert.True(sim.World!.Buildings.Has(campfire));
        Assert.Equal(1, sim.World.Tiles.OccupiedTiles);
        Assert.Contains(sim.Log.GetEvents(), e => e.Level == LogLevel.Warning && e.Render().Contains("is a building", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void BuildingOrdersWithoutAWorldAreIgnoredWithAWarning()
    {
        var core = new Simulation(1, SimTestData.Real);
        var id = new EntityId(0, 1);
        core.Enqueue(new PlaceBuildingCommand("campfire", 1, 1));
        core.Enqueue(new CancelBuildingCommand(id));
        core.Enqueue(new UpgradeBuildingCommand(id));
        core.Enqueue(new MoveBuildingCommand(id, 1, 1));
        core.Enqueue(new DemolishBuildingCommand(id));
        core.Step();

        Assert.Equal(5, core.Log.GetEvents().Count(e => e.Category == BuildingRules.LogCategory && e.Level == LogLevel.Warning));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void BuildingEventsKeepTheLatestOnes()
    {
        Simulation sim = NewGame();
        for (int i = 0; i < GameWorld.BuildingEventCapacity + 5; i++)
        {
            sim.Enqueue(new UpgradeBuildingCommand(new EntityId(50, 1)));
        }

        sim.Step();

        GameWorld world = sim.World!;
        Assert.Equal(GameWorld.BuildingEventCapacity, world.BuildingEvents.Count);
        Assert.Equal(GameWorld.BuildingEventCapacity + 5, world.LastBuildingEventSequence);
        Assert.Equal(6, world.BuildingEvents[0].Sequence);
        Assert.All(world.BuildingEvents, e => Assert.Equal(BuildResult.NotABuilding, e.Result));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void MaterialsComeFromTheBackpackFirstThenTheStorages()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId storage = Build(sim, "storage");
        sim.Step();
        Assert.True(sim.Content.TryGetId("wood", out Core.Content.DefId wood));
        ref Inventory stock = ref sim.World!.Inventories.Get(storage);
        stock = stock.Add(wood, 30, 100, out _);
        Give(sim, "wood", 5);
        Assert.Equal(35, Count(sim, "wood"));

        (int x, int y) = FindSpot(sim, "house");
        Assert.Equal(BuildResult.Ok, Order(sim, new PlaceBuildingCommand("house", x, y)).Result);

        Assert.Equal(0, BackpackCount(sim, "wood"));
        Assert.Equal(15, sim.World.Inventories.Get(storage).Count(wood));
        Assert.Equal(15, Count(sim, "wood"));
    }
}
