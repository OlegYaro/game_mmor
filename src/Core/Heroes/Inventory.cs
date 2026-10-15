using Core.Content;
using Core.Determinism;
using Core.Entities;

namespace Core.Heroes;

/// <summary>One backpack or equipment slot: a stack of one item type; <c>default</c> is the empty slot.</summary>
/// <param name="Item">Item definition (<see cref="ItemDef"/>); invalid for an empty slot.</param>
/// <param name="Count">Number of items, from 1 to the item's stack size; 0 for an empty slot.</param>
public readonly record struct ItemSlot(DefId Item, int Count)
{
    /// <summary>True for an empty slot.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Writes the item id value and the count as Int32.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Item.Value);
        writer.WriteInt32(Count);
    }
}

/// <summary>
/// The hero's items (FR-HERO-04): a backpack of a fixed number of stacks and the equipment slots
/// (<see cref="HeroBalanceDef.EquipmentSlots"/>, by index). Immutable: changes return a new inventory, so a snapshot
/// taken for a save never changes afterwards.
/// </summary>
/// <remarks>
/// Deterministic layout: <see cref="Add"/> first tops up the existing stacks of the item in slot order, then fills
/// empty slots in slot order; <see cref="Remove"/> takes from the last stack of the item backwards. Stack sizes come
/// from the item definitions (<see cref="ItemDef.StackSize"/>). Equipment is data only in M2: nothing equips yet.
/// </remarks>
public readonly struct Inventory : IComponent, IEquatable<Inventory>
{
    /// <summary>Creates an inventory from its slots (copied).</summary>
    /// <param name="backpack">Backpack slots; empty ones are <c>default</c>.</param>
    /// <param name="equipment">Equipment slots in <see cref="HeroBalanceDef.EquipmentSlots"/> order.</param>
    public Inventory(IEnumerable<ItemSlot> backpack, IEnumerable<ItemSlot> equipment)
    {
        ArgumentNullException.ThrowIfNull(backpack);
        ArgumentNullException.ThrowIfNull(equipment);
        Backpack = [.. backpack];
        Equipment = [.. equipment];
    }

    /// <summary>Backpack slots in order; empty slots are <c>default</c>.</summary>
    public IReadOnlyList<ItemSlot> Backpack { get; }

    /// <summary>Equipment slots in <see cref="HeroBalanceDef.EquipmentSlots"/> order; empty slots are <c>default</c>.</summary>
    public IReadOnlyList<ItemSlot> Equipment { get; }

    /// <summary>Number of non-empty backpack slots.</summary>
    public int UsedStacks => Slots.Count(s => !s.IsEmpty);

    private IReadOnlyList<ItemSlot> Slots => Backpack ?? [];

    private IReadOnlyList<ItemSlot> EquipmentSlots => Equipment ?? [];

    /// <summary>Equality by content (slot by slot).</summary>
    public static bool operator ==(Inventory left, Inventory right) => left.Equals(right);

    /// <summary>Inequality by content.</summary>
    public static bool operator !=(Inventory left, Inventory right) => !left.Equals(right);

    /// <summary>An empty inventory with <paramref name="backpackStacks"/> backpack slots and <paramref name="equipmentSlots"/> equipment slots.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public static Inventory Create(int backpackStacks, int equipmentSlots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(backpackStacks);
        ArgumentOutOfRangeException.ThrowIfNegative(equipmentSlots);
        return new Inventory(new ItemSlot[backpackStacks], new ItemSlot[equipmentSlots]);
    }

    /// <summary>An empty inventory shaped by the hero balance (backpack stacks and equipment slots).</summary>
    public static Inventory Create(HeroBalanceDef hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        return Create(hero.BackpackStacks, hero.EquipmentSlots.Count);
    }

    /// <summary>Total number of <paramref name="item"/> in the backpack.</summary>
    public int Count(DefId item)
    {
        int total = 0;
        foreach (ItemSlot stack in Slots)
        {
            total += stack.Item == item ? stack.Count : 0;
        }

        return total;
    }

    /// <summary>How many more <paramref name="item"/> fit: free room in its stacks plus whole empty slots.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The stack size is not positive.</exception>
    public int SpaceFor(DefId item, int stackSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stackSize);
        long space = 0;
        foreach (ItemSlot stack in Slots)
        {
            space += stack.IsEmpty ? stackSize : stack.Item == item ? Math.Max(0, stackSize - stack.Count) : 0;
        }

        return (int)Math.Min(int.MaxValue, space);
    }

    /// <summary>True when all <paramref name="count"/> items fit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative or the stack size is not positive.</exception>
    public bool CanAdd(DefId item, int count, int stackSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return SpaceFor(item, stackSize) >= count;
    }

    /// <summary>
    /// Adds as many of <paramref name="count"/> items as fit (existing stacks first, then empty slots, both in slot
    /// order). <paramref name="added"/> says how many; the rest (<c>count - added</c>) did not fit.
    /// </summary>
    /// <exception cref="ArgumentException">The item id is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative or the stack size is not positive.</exception>
    public Inventory Add(DefId item, int count, int stackSize, out int added)
    {
        if (!item.IsValid)
        {
            throw new ArgumentException($"Item id {item} is not valid.", nameof(item));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stackSize);
        ItemSlot[] slots = [.. Slots];
        int left = count;
        for (int i = 0; i < slots.Length && left > 0; i++)
        {
            if (slots[i].Item == item && !slots[i].IsEmpty && slots[i].Count < stackSize)
            {
                int put = Math.Min(left, stackSize - slots[i].Count);
                slots[i] = slots[i] with { Count = slots[i].Count + put };
                left -= put;
            }
        }

        for (int i = 0; i < slots.Length && left > 0; i++)
        {
            if (slots[i].IsEmpty)
            {
                int put = Math.Min(left, stackSize);
                slots[i] = new ItemSlot(item, put);
                left -= put;
            }
        }

        added = count - left;
        return added == 0 ? this : new Inventory(slots, EquipmentSlots);
    }

    /// <summary>
    /// Removes up to <paramref name="count"/> of <paramref name="item"/>, from the last stack backwards;
    /// <paramref name="removed"/> says how many there were to remove.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public Inventory Remove(DefId item, int count, out int removed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ItemSlot[] slots = [.. Slots];
        int left = count;
        for (int i = slots.Length - 1; i >= 0 && left > 0; i--)
        {
            if (slots[i].Item == item && !slots[i].IsEmpty)
            {
                int take = Math.Min(left, slots[i].Count);
                slots[i] = slots[i].Count == take ? default : slots[i] with { Count = slots[i].Count - take };
                left -= take;
            }
        }

        removed = count - left;
        return removed == 0 ? this : new Inventory(slots, EquipmentSlots);
    }

    /// <summary>Writes the backpack slot count and slots, then the equipment slot count and slots.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Slots.Count);
        foreach (ItemSlot stack in Slots)
        {
            stack.WriteState(writer);
        }

        writer.WriteInt32(EquipmentSlots.Count);
        foreach (ItemSlot stack in EquipmentSlots)
        {
            stack.WriteState(writer);
        }
    }

    /// <inheritdoc />
    public bool Equals(Inventory other) =>
        Slots.SequenceEqual(other.Slots) && EquipmentSlots.SequenceEqual(other.EquipmentSlots);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Inventory other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int hash = (Slots.Count * 31) + EquipmentSlots.Count;
        foreach (ItemSlot stack in Slots)
        {
            hash = (hash * 31) + (stack.Item.Value * 7) + stack.Count;
        }

        return hash;
    }
}
