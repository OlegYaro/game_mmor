using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Fog;
using Core.Jobs;
using Core.Pathfinding;
using Core.Residents;
using Core.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Jobs;

/// <summary>
/// Doing the jobs (FR-NPC-06, FR-NPC-07, FR-ECO-01, ADR 0009): gatherers carry their load to a storage, wait with it when
/// no storage takes it, never lose it; the field fills its output and carriers haul it; unreachable targets are skipped
/// or banned; idle residents spread out next to the center; residents reveal the fog around them.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class JobWorkTests
{
    [Fact]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-ECO-01")]
    public void ALumberjackChopsUntilTheLoadIsFullAndCarriesItToTheStorageWithoutLosingAny()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId lumberjack = Spawn(sim, "lumberjack");
        int tree = JobOf(sim, lumberjack).TargetTile;
        int capacity = sim.Content.Economy.HaulCapacity;

        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.ToStorage);
        ResidentJob carrying = JobOf(sim, lumberjack);
        Assert.Equal(capacity, carrying.CarryCount);
        Assert.Equal(Item(sim, "wood"), carrying.CarryItem);
        Assert.Equal(storage, carrying.Storage);
        Assert.Equal(tree, carrying.TargetTile);
        Assert.Contains(sim.World!.Reservations.Reservations,
            r => r.Target == ReservationTarget.OfBuilding(ReservationLayer.Storage, storage) && r.Holder == lumberjack && r.Amount == capacity);
        Assert.Equal(capacity, TakenFromNodes(sim, ResourceKind.Tree));

        StepUntil(sim, () => Stored(sim, storage, "wood") > 0);
        Assert.Equal(capacity, Stored(sim, storage, "wood"));
        Assert.False(JobOf(sim, lumberjack).IsCarrying);

        sim.Step(3000);
        Assert.Equal(TakenFromNodes(sim, ResourceKind.Tree), Stored(sim, storage, "wood") + Carried(sim, "wood"));
        Assert.True(Stored(sim, storage, "wood") >= 3 * capacity);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-ECO-01")]
    public void WithoutAStorageNobodyGathersUntilOneIsBuilt()
    {
        // No storage takes wood: the lumberjack does not chop at all (the load would only block it, see the next test).
        (Simulation sim, _) = NewSettlement(storage: false);
        EntityId lumberjack = Spawn(sim, "lumberjack");
        SetPriorities(sim, lumberjack, (JobKind.Chop, 1));
        int capacity = sim.Content.Economy.HaulCapacity;
        sim.Step(500);
        Assert.Equal(JobKind.None, JobOf(sim, lumberjack).Kind);
        Assert.False(JobOf(sim, lumberjack).IsCarrying);
        Assert.Equal(0, TakenFromNodes(sim, ResourceKind.Tree));
        Assert.Empty(sim.World!.Reservations.Reservations);

        EntityId storage = Build(sim, "storage");
        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.ToStorage);
        Assert.Equal(capacity, JobOf(sim, lumberjack).CarryCount);
        StepUntil(sim, () => Stored(sim, storage, "wood") > 0);
        Assert.Equal(capacity, Stored(sim, storage, "wood"));
        sim.Step(1000);
        Assert.Equal(TakenFromNodes(sim, ResourceKind.Tree), Stored(sim, storage, "wood") + Carried(sim, "wood"));
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    public void AFullStorageOrAFilterThatRefusesTheItemKeepsTheLoadWithTheResident()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId lumberjack = Spawn(sim, "lumberjack");
        SetPriorities(sim, lumberjack, (JobKind.Chop, 1));
        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.Working);
        Fill(sim, storage, "stone", 50 * 19);
        Fill(sim, storage, "berries", 100); // the last stack, filled while it chops: no room for wood
        StepUntil(sim, () => JobOf(sim, lumberjack).Kind == JobKind.None && JobOf(sim, lumberjack).IsCarrying);
        Assert.Equal(0, Storages.RoomFor(sim, storage, Item(sim, "wood")));

        // Room for wood, but the storage takes only food now.
        ref Core.Heroes.Inventory stock = ref sim.World!.Inventories.Get(storage);
        stock = stock.Remove(Item(sim, "stone"), 50, out _);
        int food = ItemCategories.BitOf(ItemCategories.Food);
        sim.Enqueue(new SetStorageFilterCommand(storage, food));
        sim.Step();
        Assert.Equal(new StorageFilter(food), sim.World.StorageFilters.Get(storage));
        Assert.False(Storages.Accepts(sim, storage, Item(sim, "wood")));
        Assert.True(Storages.Accepts(sim, storage, Item(sim, "berries")));
        sim.Step(100);
        Assert.Equal(0, Stored(sim, storage, "wood"));
        Assert.True(JobOf(sim, lumberjack).IsCarrying);

        // Everything allowed again: the filter component goes away and the wood goes in; the delivery is a haul job of the
        // resident's own load, and then it chops again.
        sim.Enqueue(new SetStorageFilterCommand(storage, ItemCategories.AllMask));
        sim.Step();
        Assert.False(sim.World.StorageFilters.Has(storage));
        StepUntil(sim, () => JobOf(sim, lumberjack).Kind == JobKind.Haul);
        Assert.Equal((JobPhase.ToStorage, EntityId.None), (JobOf(sim, lumberjack).Phase, JobOf(sim, lumberjack).Building));
        StepUntil(sim, () => Stored(sim, storage, "wood") > 0);
        Assert.Equal(sim.Content.Economy.HaulCapacity, Stored(sim, storage, "wood"));
        StepUntil(sim, () => JobOf(sim, lumberjack).Kind == JobKind.Chop);
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    public void TheLoadGoesToTheNearestStorageThatTakesIt()
    {
        (Simulation sim, EntityId near) = NewSettlement();
        EntityId far = Build(sim, "storage", minDistance: 6);
        sim.Enqueue(new SetStorageFilterCommand(near, ItemCategories.BitOf(ItemCategories.Food)));
        sim.Enqueue(new SetStorageFilterCommand(far, ItemCategories.AllMask & ~ItemCategories.BitOf(ItemCategories.Food)));
        sim.Step();

        EntityId lumberjack = Spawn(sim, "lumberjack");
        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.ToStorage);
        Assert.Equal(far, JobOf(sim, lumberjack).Storage);
        StepUntil(sim, () => Stored(sim, far, "wood") > 0);
        Assert.Equal(0, Stored(sim, near, "wood"));

        // Bad filter orders change nothing.
        sim.Enqueue(new SetStorageFilterCommand(near, 1 << 9));
        sim.Enqueue(new SetStorageFilterCommand(Construction.FindCenter(sim), 1));
        sim.Step();
        GameWorld world = sim.World!;
        Assert.Equal(ItemCategories.BitOf(ItemCategories.Food), Storages.FilterOf(world, near));
        Assert.Equal(ItemCategories.AllMask, Storages.FilterOf(world, Construction.FindCenter(sim)));
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-ECO-01")]
    public void TheFieldFillsItsOutputAndACarrierHaulsItToTheStorage()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId field = Build(sim, "field");
        WorkDef work = Construction.LevelOf(sim.Content, sim.World!.Buildings.Get(field)).Work!;
        EntityId farmer = Spawn(sim, "farmer");
        Assert.Equal(JobKind.Farm, JobOf(sim, farmer).Kind);

        StepUntil(sim, () => sim.World.WorkOutputs.Get(field).Count > 0);
        Assert.Equal(work.Yield, sim.World.WorkOutputs.Get(field).Count);
        Assert.Equal(0, Stored(sim, storage, "vegetable"));

        EntityId carrier = Spawn(sim, "carrier");
        ResidentJob haul = JobOf(sim, carrier);
        Assert.Equal(JobKind.Haul, haul.Kind);
        Assert.Equal(field, haul.Building);
        Assert.Equal(storage, haul.Storage);
        Assert.Contains(sim.World.Reservations.Reservations, r => r.Target == ReservationTarget.OfBuilding(ReservationLayer.Output, field) && r.Holder == carrier);

        StepUntil(sim, () => Stored(sim, storage, "vegetable") > 0);
        sim.Step(4000);
        int produced = sim.World.WorkOutputs.Get(field).Count + Stored(sim, storage, "vegetable") + Carried(sim, "vegetable");
        Assert.Equal(0, produced % work.Yield); // nothing appears or disappears on the way
        Assert.True(Stored(sim, storage, "vegetable") > 3 * work.Yield);
        Assert.InRange(sim.World.WorkOutputs.Get(field).Count, 0, sim.Content.Get<ItemDef>(Item(sim, "vegetable")).StackSize);
        Assert.Equal(0, sim.World.Residents.Get(farmer).Priorities.Get(JobKind.Haul)); // the farmer never hauls
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    [Trait("Req", "FR-ECO-01")]
    public void AStorageThatStopsTakingItemsOnTheWayEndsTheHaulAndTheOutputWaitsForIt()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId field = Build(sim, "field");
        sim.World!.WorkOutputs.Get(field) = new WorkOutput(12);
        EntityId carrier = Spawn(sim, "carrier");
        Assert.Equal((JobKind.Haul, JobPhase.ToTarget, storage), (JobOf(sim, carrier).Kind, JobOf(sim, carrier).Phase, JobOf(sim, carrier).Storage));

        // The storage starts to move while the carrier walks to the field: the haul ends in that tick, before anything is
        // taken (review R1 of M3a: a haul on its way to the source needs the storage it reserved room in), and with no
        // other storage the output waits at the field.
        Building building = BuildingOf(sim, storage);
        (int x, int y) = FindSpot(sim, "storage", minDistance: 4, near: (building.X, building.Y), moving: storage);
        sim.Enqueue(new MoveBuildingCommand(storage, x, y, 0));
        sim.Step();
        Assert.Equal(BuildingState.Moving, BuildingOf(sim, storage).State);
        Assert.Equal((JobKind.None, 0), (JobOf(sim, carrier).Kind, JobOf(sim, carrier).CarryCount));
        Assert.Empty(sim.World.Reservations.HeldBy(carrier));
        Assert.Equal(12, sim.World.WorkOutputs.Get(field).Count);

        StepUntil(sim, () => Stored(sim, storage, "vegetable") > 0);
        Assert.Equal(12, Stored(sim, storage, "vegetable"));
        Assert.Equal(0, sim.World.WorkOutputs.Get(field).Count);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void AFullFieldOutputStopsTheFarmer()
    {
        (Simulation sim, _) = NewSettlement();
        EntityId field = Build(sim, "field");
        WorkDef work = Construction.LevelOf(sim.Content, sim.World!.Buildings.Get(field)).Work!;
        int stack = sim.Content.Get<ItemDef>(Item(sim, work.Item)).StackSize;
        sim.World.WorkOutputs.Get(field) = new WorkOutput(stack - work.Yield + 1);

        EntityId farmer = Spawn(sim, "farmer");

        Assert.Equal(JobKind.Forage, JobOf(sim, farmer).Kind); // the field has no room for a cycle
        Assert.False(sim.World.Reservations.IsReserved(ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, field)));
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void ANodeNobodyCanReachIsSkipped()
    {
        (Simulation sim, _) = NewSettlement();
        int spawnTile = Arrivals.MeetingTiles(sim)[0];
        (int X, int Y) from = (spawnTile % sim.World!.Size, spawnTile / sim.World.Size);
        int nearest = NodesByRing(sim, ResourceKind.Tree, from)[0];
        Enclose(sim, nearest);

        List<int> trees = NodesByRing(sim, ResourceKind.Tree, from);
        EntityId lumberjack = Spawn(sim, "lumberjack");

        Assert.DoesNotContain(nearest, trees);
        Assert.Equal(trees[0], JobOf(sim, lumberjack).TargetTile);
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void ATargetThatBecomesUnreachableOnTheWayIsBannedForTheCooldown()
    {
        (Simulation sim, _) = NewSettlement();
        EntityId lumberjack = Spawn(sim, "lumberjack");
        int tree = JobOf(sim, lumberjack).TargetTile;
        Assert.True(sim.World!.Movers.Get(lumberjack).Path.Count > 3);
        sim.Step();

        Enclose(sim, tree);
        long failed = StepUntil(sim, () => sim.World.Reservations.Bans.Count > 0) + sim.Tick;

        JobBan ban = Assert.Single(sim.World.Reservations.Bans);
        Assert.Equal(new JobBan(lumberjack, ReservationTarget.OfTile(tree), sim.Tick + sim.Content.Economy.JobRetryCooldownTicks), ban);
        Assert.NotEqual(tree, JobOf(sim, lumberjack).TargetTile); // it took another tree at once
        Assert.Contains(sim.Log.GetEvents(), e => e.Category == "jobs" && e.Render().Contains("cannot reach", StringComparison.Ordinal));

        sim.Step(sim.Content.Economy.JobRetryCooldownTicks);
        Assert.Empty(sim.World.Reservations.Bans);
        Assert.True(failed > 0);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void IdleResidentsSpreadOutNextToTheCenterOneTilePerResident()
    {
        (Simulation sim, _) = NewSettlement();
        var idle = new List<EntityId>();
        for (int i = 0; i < 10; i++)
        {
            idle.Add(Spawn(sim, "blacksmith"));
        }

        StepUntil(sim, () => idle.All(e => !sim.World!.Movers.Get(e).IsMoving && sim.World.Residents.Get(e).State == ResidentState.Idle), 500);
        sim.Step(30);

        (int cx, int cy) = Residents.ResidentTestWorld.CenterTile(sim);
        (int X, int Y)[] tiles = idle.Select(e => Residents.ResidentTestWorld.TileOf(sim, e)).ToArray();
        Assert.Equal(tiles.Length, tiles.Distinct().Count());
        Assert.All(tiles, t => Assert.InRange(AgentMovement.Distance(t.X, t.Y, cx, cy), 1, 2)); // ten residents need two rings around a 1 × 1 center
        Assert.All(idle, e => Assert.False(sim.World!.Movers.Get(e).IsMoving));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    [Trait("Req", "FR-NPC-02")]
    public void ResidentsRevealTheFogAroundThem()
    {
        (Simulation sim, _) = NewSettlement();
        EntityId resident = Spawn(sim, "blacksmith");
        Assert.Equal(new Revealer(RevealerKind.Villager), sim.World!.Revealers.Get(resident));

        // Without the hero only the residents see: their circle stays visible, the rest of the hero's circle fades.
        sim.Enqueue(new DestroyEntityCommand(sim.World.HeroEntity));
        sim.Step(2);
        (int x, int y) = Residents.ResidentTestWorld.TileOf(sim, resident);
        int radius = Revealer.RadiusOf(sim.Content.Fog, RevealerKind.Villager, sim.Now.IsNight);
        Assert.Equal(FogState.Visible, sim.World.Fog.GetState(x, y));
        Assert.Equal(FogState.Visible, sim.World.Fog.GetState(x + radius - 1, y));
        int outside = x + radius + 2 < sim.World.Size ? x + radius + 2 : x - radius - 2;
        Assert.Equal(FogState.Explored, sim.World.Fog.GetState(outside, y));
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void ARemovedResidentLeavesNoReservationBehind()
    {
        (Simulation sim, _) = NewSettlement();
        EntityId lumberjack = Spawn(sim, "lumberjack");
        Assert.NotEmpty(sim.World!.Reservations.Reservations);

        sim.Enqueue(new DestroyEntityCommand(lumberjack));
        sim.Step(2);

        Assert.Empty(sim.World.Reservations.Reservations);
        Assert.False(sim.World.Jobs.Has(lumberjack));
    }

    // Occupies every free neighbour of `tile` with a marker entity, so nobody can stand next to it.
    private static void Enclose(Simulation sim, int tile)
    {
        GameWorld world = sim.World!;
        EntityId wall = sim.Entities.Create();
        int x = tile % world.Size;
        int y = tile / world.Size;
        for (int d = 0; d < PassabilityGrid.DirectionCount; d++)
        {
            int nx = x + PassabilityGrid.DirectionX(d);
            int ny = y + PassabilityGrid.DirectionY(d);
            if (world.Map.Terrain.Contains(nx, ny) && !world.Tiles.IsOccupied(nx, ny))
            {
                Assert.True(world.Tiles.TryOccupy(nx, ny, wall));
            }
        }
    }

    // Items taken from the nodes of `kind` since the map was generated.
    private static int TakenFromNodes(Simulation sim, ResourceKind kind)
    {
        GameWorld world = sim.World!;
        int taken = 0;
        for (int y = 0; y < world.Size; y++)
        {
            for (int x = 0; x < world.Size; x++)
            {
                if (world.Map.GetResource(x, y) == kind)
                {
                    taken += world.Map.GetResourceAmount(x, y) - world.Tiles.GetEffectiveAmount(x, y);
                }
            }
        }

        return taken;
    }
}
