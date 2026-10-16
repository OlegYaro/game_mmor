using Core.Content;
using Core.Determinism;
using Core.Entities;

namespace Core.Economy;

/// <summary>
/// The output of a building where residents work (<see cref="BuildingLevelDef.Work"/>, a field): items of the level's
/// <see cref="WorkDef.Item"/> waiting for a carrier. It holds at most one stack of the item (its
/// <see cref="ItemDef.StackSize"/>); a full output stops the work. Added when the building is first finished.
/// </summary>
/// <param name="Count">Items waiting.</param>
public readonly record struct WorkOutput(int Count) : IComponent
{
    /// <summary>Writes the count.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Count);
    }
}

/// <summary>
/// The item categories a storage takes in (FR-ECO-01): bit <c>1 &lt;&lt; i</c> for category
/// <see cref="ItemCategories.All"/>[i]. A storage without this component takes every category (the default); a filter
/// that allows everything is removed, so the state has one form per setting. Items without a category pass every
/// filter. The filter limits what goes in, not what may be spent: items already stored stay available.
/// </summary>
/// <param name="Categories">The allowed categories.</param>
public readonly record struct StorageFilter(int Categories) : IComponent
{
    /// <summary>True when items of <paramref name="item"/>'s category may go in.</summary>
    public bool Allows(ItemDef item)
    {
        ArgumentNullException.ThrowIfNull(item);
        int bit = ItemCategories.BitOf(item.Category);
        return bit == 0 || (Categories & bit) != 0;
    }

    /// <summary>Writes the mask.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Categories);
    }
}
