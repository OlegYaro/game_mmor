using Core.Content;
using Core.Entities;
using Core.Jobs;

namespace Core.Tests.Jobs;

/// <summary>The reservation manager (FR-NPC-07, ADR 0009): atomic reservations, limits, the single release point, bans, canonical order.</summary>
public sealed class ReservationManagerTests
{
    private static readonly EntityId _a = new(5, 1);
    private static readonly EntityId _b = new(3, 2);
    private static readonly EntityId _field = new(9, 1);
    private static readonly EntityId _storage = new(7, 4);
    private static readonly DefId _wood = new(12);

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void OneTileOneHolderAndTheSecondReservationOfATargetFails()
    {
        var manager = new ReservationManager();
        ReservationTarget tree = ReservationTarget.OfTile(100);

        Assert.True(manager.TryReserveAll(_a, [new ReservationRequest(tree, default, 1, 1)]));
        Assert.False(manager.TryReserveAll(_b, [new ReservationRequest(tree, default, 1, 1)]));
        Assert.False(manager.TryReserveAll(_a, [new ReservationRequest(tree, default, 1, 5)])); // already held by the same resident

        Assert.True(manager.IsReserved(tree));
        Assert.Equal(1, manager.HolderCount(tree));
        Assert.Equal([new Reservation(tree, _a, default, 1)], manager.Reservations);
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void ReservingIsAllOrNothing()
    {
        var manager = new ReservationManager();
        ReservationTarget output = ReservationTarget.OfBuilding(ReservationLayer.Output, _field);
        ReservationTarget room = ReservationTarget.OfBuilding(ReservationLayer.Storage, _storage);
        Assert.True(manager.TryReserveAll(_b, [new ReservationRequest(room, _wood, 15, 20)]));

        // The output part fits, the storage part does not (15 + 10 > 20): nothing is reserved.
        Assert.False(manager.TryReserveAll(_a, [new ReservationRequest(output, _wood, 10, 30), new ReservationRequest(room, _wood, 10, 20)]));
        Assert.False(manager.IsReserved(output));
        Assert.Equal(15, manager.AmountReserved(room));

        // Bad requests: a zero amount, the same target twice.
        Assert.False(manager.TryReserveAll(_a, [new ReservationRequest(output, _wood, 0, 30)]));
        Assert.False(manager.TryReserveAll(_a, [new ReservationRequest(output, _wood, 1, 30), new ReservationRequest(output, _wood, 1, 30)]));

        Assert.True(manager.TryReserveAll(_a, [new ReservationRequest(output, _wood, 10, 30), new ReservationRequest(room, _wood, 5, 20)]));
        Assert.Equal(20, manager.AmountReserved(room));
        Assert.Equal([new Reservation(room, _b, _wood, 15), new Reservation(room, _a, _wood, 5)], manager.IncomingTo(_storage)); // holder order
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void WorkSlotsTakeAsManyHoldersAsTheLimitAndReleaseAllFreesEverythingOfOneHolder()
    {
        var manager = new ReservationManager();
        ReservationTarget slot = ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, _field);
        ReservationTarget tree = ReservationTarget.OfTile(7);
        Assert.True(manager.TryReserveAll(_a, [new ReservationRequest(slot, default, 1, 2), new ReservationRequest(tree, default, 1, 1)]));
        Assert.True(manager.TryReserveAll(_b, [new ReservationRequest(slot, default, 1, 2)]));
        Assert.False(manager.TryReserveAll(new EntityId(8, 1), [new ReservationRequest(slot, default, 1, 2)]));
        Assert.Equal(2, manager.HolderCount(slot));
        Assert.Equal(2, manager.HeldBy(_a).Count());

        Assert.Equal(2, manager.ReleaseAll(_a));
        Assert.Equal(0, manager.ReleaseAll(_a));
        Assert.Empty(manager.HeldBy(_a));
        Assert.Equal(1, manager.HolderCount(slot));
        Assert.False(manager.IsReserved(tree));
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void ReservationsStayInKeyOrderWhateverTheOrderTheyWereMadeIn()
    {
        var first = new ReservationManager();
        var second = new ReservationManager();
        ReservationRequest[] requests =
        [
            new(ReservationTarget.OfBuilding(ReservationLayer.Storage, _storage), _wood, 3, 100),
            new(ReservationTarget.OfTile(42), default, 1, 1),
            new(ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, _field), default, 1, 3),
            new(ReservationTarget.OfTile(7), default, 1, 1),
        ];
        EntityId[] holders = [_a, _b, new EntityId(1, 1), new EntityId(2, 9)];
        for (int i = 0; i < requests.Length; i++)
        {
            Assert.True(first.TryReserveAll(holders[i], [requests[i]]));
            Assert.True(second.TryReserveAll(holders[^(i + 1)], [requests[^(i + 1)]]));
        }

        Assert.Equal(first.Reservations, second.Reservations);
        Assert.Equal(
            [ReservationLayer.Tile, ReservationLayer.Tile, ReservationLayer.WorkSlot, ReservationLayer.Storage],
            first.Reservations.Select(r => r.Target.Layer));
        Assert.Equal(7, first.Reservations[0].Target.Index);
        Assert.Equal(Hash(first), Hash(second));
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    public void BansEndAtTheirTickAndPruneForgetsThemAndTheDead()
    {
        var registry = new EntityRegistry();
        EntityId alive = registry.Create();
        EntityId gone = registry.Create();
        var manager = new ReservationManager();
        ReservationTarget tree = ReservationTarget.OfTile(3);
        manager.Ban(alive, tree, 600);
        manager.Ban(alive, tree, 700); // replaces the older ban
        manager.Ban(gone, tree, 900);
        Assert.True(manager.TryReserveAll(gone, [new ReservationRequest(tree, default, 1, 1)]));

        Assert.True(manager.IsBanned(alive, tree, 699));
        Assert.False(manager.IsBanned(alive, tree, 700));
        Assert.False(manager.IsBanned(alive, ReservationTarget.OfTile(4), 0));
        Assert.Equal(2, manager.Bans.Count);

        registry.Destroy(gone);
        manager.Prune(100, registry);
        Assert.Equal([new JobBan(alive, tree, 700)], manager.Bans);
        Assert.Empty(manager.Reservations);
        manager.Prune(700, registry);
        Assert.Empty(manager.Bans);
    }

    [Fact]
    [Trait("Req", "FR-NPC-07")]
    [Trait("Req", "FR-SAVE-04")]
    public void LoadTakesACapturedStateAndRejectsDisorderOrBadValues()
    {
        var manager = new ReservationManager();
        Assert.True(manager.TryReserveAll(_a, [new ReservationRequest(ReservationTarget.OfTile(1), default, 1, 1)]));
        Assert.True(manager.TryReserveAll(_b, [new ReservationRequest(ReservationTarget.OfTile(2), default, 1, 1)]));
        manager.Ban(_a, ReservationTarget.OfTile(9), 50);
        manager.Ban(_b, ReservationTarget.OfTile(9), 60);
        ReservationsState state = manager.CaptureState();

        var copy = new ReservationManager();
        copy.Load(state);
        Assert.Equal(manager.Reservations, copy.Reservations);
        Assert.Equal(manager.Bans, copy.Bans);
        Assert.Equal(Hash(manager), Hash(copy));

        Assert.Throws<ArgumentException>(() => copy.Load(state with { Reservations = [.. state.Reservations.Reverse()] }));
        Assert.Throws<ArgumentException>(() => copy.Load(state with { Bans = [.. state.Bans.Reverse()] }));
        Assert.Throws<ArgumentException>(() => copy.Load(state with { Reservations = [state.Reservations[0] with { Amount = 0 }] }));
        Assert.Throws<ArgumentException>(() => copy.Load(state with { Bans = [state.Bans[0] with { UntilTick = -1 }] }));
        Assert.Throws<ArgumentException>(() => copy.Load(state with
        {
            Reservations = [state.Reservations[0] with { Target = new ReservationTarget((ReservationLayer)9, 1, 0) }],
        }));
        Assert.Equal(manager.Reservations, copy.Reservations); // a refused load changes nothing
    }

    [Fact]
    public void TargetsCompareByLayerIndexAndGenerationAndPrintTheirLayer()
    {
        Assert.True(ReservationTarget.Compare(ReservationTarget.OfTile(900), ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, new EntityId(0, 1))) < 0);
        Assert.True(ReservationTarget.Compare(ReservationTarget.OfBuilding(ReservationLayer.Output, new EntityId(4, 1)), ReservationTarget.OfBuilding(ReservationLayer.Output, new EntityId(4, 2))) < 0);
        Assert.Equal(0, ReservationTarget.Compare(ReservationTarget.OfTile(3), ReservationTarget.OfTile(3)));
        Assert.Equal(_field, ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, _field).Building);
        Assert.Equal("tile 12", ReservationTarget.OfTile(12).ToString());
        Assert.Equal("storage #7v4", ReservationTarget.OfBuilding(ReservationLayer.Storage, _storage).ToString());
        Assert.Equal(["tile", "work_slot", "output", "storage"], ReservationManager.LayerNames);
    }

    private static ulong Hash(ReservationManager manager) => new Core.Determinism.StateHasher().Hash(manager);
}
