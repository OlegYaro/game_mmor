using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Heroes;
using Core.Jobs;
using Core.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Economy;

/// <summary>
/// The storage (FR-ECO-01): the hero hands the backpack in, room counts the loads on their way, filters decide what goes
/// in (also for refunds), a field's output is refunded on demolition.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class StorageTests
{
    [Fact]
    [Trait("Req", "FR-ECO-01")]
    public void TheHeroHandsTheBackpackInWhatTheFilterAllowsAndTheRestStays()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        Give(sim, "wood", 30);
        Give(sim, "berries", 10);
        sim.Enqueue(new SetStorageFilterCommand(storage, ItemCategories.BitOf(ItemCategories.Food)));

        sim.Enqueue(new DepositHeroItemsCommand(storage));
        sim.Step();

        Assert.Equal(10, Stored(sim, storage, "berries"));
        Assert.Equal(0, Stored(sim, storage, "wood"));
        Assert.Equal(30, BackpackCount(sim, "wood"));
        Assert.Equal(0, BackpackCount(sim, "berries"));
        EconomyEvent deposit = sim.World!.EconomyEvents.Single(e => e.Kind == EconomyEventKind.HeroDeposit);
        Assert.Equal((10, 30), (deposit.Amount, deposit.Lost));

        sim.Enqueue(new SetStorageFilterCommand(storage, ItemCategories.AllMask));
        sim.Enqueue(new DepositHeroItemsCommand(storage));
        sim.Step();
        Assert.Equal(30, Stored(sim, storage, "wood"));
        Assert.Equal(0, Heroes.HeroTestWorld.InventoryOf(sim).UsedStacks);
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    public void TheHeroMustStandNextToAnAvailableStorage()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        Give(sim, "wood", 30);
        Building building = BuildingOf(sim, storage);
        (int hx, int hy) = Heroes.HeroTestWorld.HeroTile(sim);
        (int x, int y) = FindTile(sim, (tx, ty) => sim.World!.Pathfinder.Grid.IsPassable(tx, ty) && sim.World.Pathfinder.IsReachable(hx, hy, tx, ty)
            && sim.World.Fog.GetState(tx, ty) != Core.Fog.FogState.Unknown
            && Storages.DistanceSquared(sim.Content, building, tx, ty) > 25 && Storages.DistanceSquared(sim.Content, building, tx, ty) < 100);
        sim.Enqueue(new MoveHeroCommand(x, y));
        sim.Step();
        StepUntil(sim, () => !Heroes.HeroTestWorld.MoverOf(sim).IsMoving);

        sim.Enqueue(new DepositHeroItemsCommand(storage));
        sim.Enqueue(new DepositHeroItemsCommand(Construction.FindCenter(sim)));
        sim.Enqueue(new DepositHeroItemsCommand(new EntityId(50, 1)));
        sim.Step();

        Assert.Equal(30, BackpackCount(sim, "wood"));
        Assert.Empty(sim.World!.EconomyEvents);
        Assert.Equal(3, sim.Log.GetEvents().Count(e => e.Tick == sim.Tick && e.Category == "economy" && e.Level == Core.Logging.LogLevel.Warning));
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    [Trait("Req", "FR-NPC-07")]
    public void RoomCountsTheLoadsOnTheirWay()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        DefId wood = Item(sim, "wood");
        DefId stone = Item(sim, "stone");
        int stacks = sim.World!.Inventories.Get(storage).Backpack.Count;
        int perItem = sim.Content.Economy.StorageStacksPerItem;
        Assert.True(perItem < stacks);
        Assert.Equal(perItem * 100, Storages.RoomFor(sim, storage, wood)); // one item never takes the whole storage

        Fill(sim, storage, "wood", 150);
        ReservationTarget room = ReservationTarget.OfBuilding(ReservationLayer.Storage, storage);
        Assert.True(sim.World.Reservations.TryReserveAll(new EntityId(40, 1), [new ReservationRequest(room, stone, 60, 1000)]));

        // 150 wood fill two stacks; 60 stone on the way take two more. Each item is capped at its stacks per item.
        Assert.Equal((perItem * 100) - 150, Storages.RoomFor(sim, storage, wood));
        Assert.Equal((perItem * 50) - 60, Storages.RoomFor(sim, storage, stone));
        Assert.Equal(storage, Storages.FindNearest(sim, 0, 0, wood, EntityId.None, out int found));
        Assert.Equal(Storages.RoomFor(sim, storage, wood), found);

        // With other goods in all but one stack, the free stacks limit the room: 50 in the second wood stack, 100 in the free one.
        Fill(sim, storage, "berries", (stacks - 5) * 100);
        Assert.Equal(150, Storages.RoomFor(sim, storage, wood));

        // A deposit (which does not wait for the loads on their way) stops at the cap as well: what does not fit stays out.
        Assert.Equal((perItem * 100) - 150, Storages.Deposit(sim, storage, wood, 1000));
        Assert.Equal(perItem * 100, Stored(sim, storage, "wood"));
        Assert.Equal(0, Storages.Deposit(sim, storage, wood, 1));
        Assert.Equal(0, Storages.RoomFor(sim, storage, wood));

        // A banned storage is skipped; a storage that is not built has no room.
        sim.World.Reservations.Ban(new EntityId(41, 1), room, sim.Tick + 100);
        Assert.Equal(EntityId.None, Storages.FindNearest(sim, 0, 0, wood, new EntityId(41, 1), out _));
        Assert.Equal(0, Storages.RoomFor(sim, Construction.FindCenter(sim), wood));
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    [Trait("Req", "FR-BUILD-12")]
    public void RefundsGoOnlyIntoStoragesThatTakeThem()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId house = Build(sim, "house");
        sim.Enqueue(new SetStorageFilterCommand(storage, ItemCategories.BitOf(ItemCategories.Food)));
        sim.Step();
        int before = BackpackCount(sim, "wood");

        Order(sim, new DemolishBuildingCommand(house));

        Assert.Equal(0, Stored(sim, storage, "wood"));
        Assert.True(BackpackCount(sim, "wood") > before);
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    [Trait("Req", "FR-BUILD-12")]
    public void ADemolishedFieldGivesItsWaitingOutputBack()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        EntityId field = Build(sim, "field");
        sim.World!.WorkOutputs.Get(field) = new WorkOutput(17);

        Order(sim, new DemolishBuildingCommand(field));

        Assert.Equal(17, Stored(sim, storage, "vegetable"));
        Assert.False(sim.World.WorkOutputs.Has(field));
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    public void AFieldGetsItsOutputWhenFinishedAndAStorageNone()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        GiveCost(sim, "field", 1);
        (int x, int y) = FindSpot(sim, "field");
        EntityId field = Place(sim, "field", x, y, give: false);

        Assert.False(sim.World!.WorkOutputs.Has(field));
        StepUntilActive(sim, field);
        Assert.Equal(new WorkOutput(0), sim.World.WorkOutputs.Get(field));
        Assert.False(sim.World.WorkOutputs.Has(storage));
        Assert.False(sim.World.Inventories.Has(field)); // the output is not a stock: nobody spends from it
    }
}
