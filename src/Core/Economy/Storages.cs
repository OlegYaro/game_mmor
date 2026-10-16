using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Jobs;
using Core.Sim;

namespace Core.Economy;

/// <summary>
/// Read-only storage rules (FR-ECO-01): which storage takes an item (available, filter), how much room it has for a
/// load counting the loads already on their way (<see cref="ReservationLayer.Storage"/> reservations), and the nearest
/// storage with room. A storage's stock is its <see cref="Inventory"/>. One item fills at most
/// <see cref="EconomyBalanceDef.StorageStacksPerItem"/> stacks of a storage, so wood and stone cannot crowd food and the
/// tax out of the only storage (decision of M3a wave 3, found by the bot simulation).
/// </summary>
public static class Storages
{
    /// <summary>The categories <paramref name="storage"/> takes: its <see cref="StorageFilter"/>, or every category without one.</summary>
    public static int FilterOf(GameWorld world, EntityId storage)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.StorageFilters.TryGet(storage, out StorageFilter filter) ? filter.Categories : ItemCategories.AllMask;
    }

    /// <summary>
    /// True when <paramref name="storage"/> takes <paramref name="item"/> now: an available storage
    /// (<see cref="Construction.IsAvailableStorage"/>) whose filter allows the item's category.
    /// </summary>
    public static bool Accepts(Simulation sim, EntityId storage, DefId item)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world || !world.Buildings.TryGet(storage, out Building building) || !Construction.IsAvailableStorage(world, storage, building))
        {
            return false;
        }

        return !world.StorageFilters.TryGet(storage, out StorageFilter filter) || filter.Allows(sim.Content.Get<ItemDef>(item));
    }

    /// <summary>
    /// How many more <paramref name="item"/> <paramref name="storage"/> can take once every load on its way has arrived:
    /// free room in its stacks, at most up to <see cref="EconomyBalanceDef.StorageStacksPerItem"/> full stacks of the item;
    /// 0 when it does not take the item (<see cref="Accepts"/>).
    /// </summary>
    public static int RoomFor(Simulation sim, EntityId storage, DefId item)
    {
        if (!Accepts(sim, storage, item))
        {
            return 0;
        }

        GameWorld world = sim.World!;
        Inventory stock = world.Inventories.Get(storage);
        foreach (Reservation incoming in world.Reservations.IncomingTo(storage))
        {
            stock = stock.Add(incoming.Item, incoming.Amount, sim.Content.Get<ItemDef>(incoming.Item).StackSize, out _);
        }

        int stackSize = sim.Content.Get<ItemDef>(item).StackSize;
        return Math.Max(0, Math.Min(stock.SpaceFor(item, stackSize), ItemCap(sim, stackSize) - stock.Count(item)));
    }

    /// <summary>
    /// The storage nearest to tile (<paramref name="x"/>, <paramref name="y"/>) (squared distance to the nearest tile of
    /// its footprint, ties to the lower entity index) with room for at least one <paramref name="item"/>
    /// (<see cref="RoomFor"/>), skipping storages <paramref name="holder"/> is banned from; <see cref="EntityId.None"/>
    /// when there is none. <paramref name="room"/> is that storage's room.
    /// </summary>
    public static EntityId FindNearest(Simulation sim, int x, int y, DefId item, EntityId holder, out int room)
    {
        ArgumentNullException.ThrowIfNull(sim);
        room = 0;
        EntityId best = EntityId.None;
        if (sim.World is not { } world)
        {
            return best;
        }

        long bestDistance = long.MaxValue;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (!world.Inventories.Has(owners[i]) || !Construction.IsStorage(Construction.DefOf(sim.Content, buildings[i])))
            {
                continue;
            }

            long distance = DistanceSquared(sim.Content, buildings[i], x, y);
            if (distance >= bestDistance
                || world.Reservations.IsBanned(holder, ReservationTarget.OfBuilding(ReservationLayer.Storage, owners[i]), sim.Tick))
            {
                continue;
            }

            int space = RoomFor(sim, owners[i], item);
            if (space > 0)
            {
                best = owners[i];
                bestDistance = distance;
                room = space;
            }
        }

        return best;
    }

    /// <summary>
    /// The footprint tile of <paramref name="building"/> nearest to (<paramref name="x"/>, <paramref name="y"/>): the
    /// point clamped into the footprint rectangle.
    /// </summary>
    public static (int X, int Y) NearestTile(ContentCatalog content, Building building, int x, int y)
    {
        (int width, int height) = Construction.FootprintOf(Construction.DefOf(content, building), building.Rotation);
        return (Math.Clamp(x, building.X, building.X + width - 1), Math.Clamp(y, building.Y, building.Y + height - 1));
    }

    /// <summary>Squared distance from (<paramref name="x"/>, <paramref name="y"/>) to the nearest footprint tile of <paramref name="building"/>.</summary>
    public static long DistanceSquared(ContentCatalog content, Building building, int x, int y)
    {
        (int nx, int ny) = NearestTile(content, building, x, y);
        long dx = nx - x;
        long dy = ny - y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// Puts up to <paramref name="count"/> <paramref name="item"/> into <paramref name="storage"/> if it accepts them, no
    /// more than its stacks and the per-item cap hold; returns how many went in.
    /// </summary>
    internal static int Deposit(Simulation sim, EntityId storage, DefId item, int count)
    {
        if (count <= 0 || !Accepts(sim, storage, item))
        {
            return 0;
        }

        ref Inventory stock = ref sim.World!.Inventories.Get(storage);
        int stackSize = sim.Content.Get<ItemDef>(item).StackSize;
        int fits = Math.Min(count, ItemCap(sim, stackSize) - stock.Count(item));
        if (fits <= 0)
        {
            return 0;
        }

        stock = stock.Add(item, fits, stackSize, out int added);
        return added;
    }

    // Most items of one kind a storage holds: StorageStacksPerItem full stacks.
    private static int ItemCap(Simulation sim, int stackSize) => (int)Math.Min(int.MaxValue, (long)sim.Content.Economy.StorageStacksPerItem * stackSize);

    /// <summary>
    /// Puts up to <paramref name="count"/> <paramref name="item"/> into the storages that accept it, in entity order;
    /// returns how many went in (the rest is the caller's to report).
    /// </summary>
    internal static int DeliverToStorages(Simulation sim, DefId item, int count)
    {
        GameWorld world = sim.World!;
        int left = count;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        for (int i = 0; i < owners.Length && left > 0; i++)
        {
            left -= Deposit(sim, owners[i], item, left);
        }

        return count - left;
    }
}
