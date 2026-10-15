using Core.Determinism;

namespace Core.Sim;

/// <summary>
/// Orders the hero to walk to tile (<paramref name="X"/>, <paramref name="Y"/>) (FR-HERO-01). Rejected without
/// change, except <see cref="Heroes.Hero.LastOrder"/>, when the tile is outside the map, unknown in the fog,
/// impassable or unreachable. Cancels gathering. Ignored (with a warning) without a world or a hero.
/// </summary>
/// <param name="X">Tile column.</param>
/// <param name="Y">Tile row.</param>
/// <param name="Sprint">Sprint while stamina lasts.</param>
public sealed record MoveHeroCommand(int X, int Y, bool Sprint = false) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "move_hero";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        HeroRules.OrderMove(sim, X, Y, Sprint);
    }

    /// <summary>Writes X and Y as Int32, then the sprint flag.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(X);
        writer.WriteInt32(Y);
        writer.WriteBool(Sprint);
    }
}

/// <summary>
/// Orders the hero to gather the resource node on tile (<paramref name="X"/>, <paramref name="Y"/>) (FR-HERO-02): walk
/// within <see cref="Content.HeroBalanceDef.InteractionRange"/>, then take <see cref="Content.GatherDef.YieldPerAction"/>
/// every <see cref="Content.GatherDef.TicksPerAction"/> ticks until the node is used up or the backpack is full.
/// Rejected like <see cref="MoveHeroCommand"/>, and when the tile has no node the hero can gather.
/// </summary>
/// <param name="X">Tile column of the node.</param>
/// <param name="Y">Tile row of the node.</param>
public sealed record GatherCommand(int X, int Y) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "gather";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        HeroRules.OrderGather(sim, X, Y);
    }

    /// <summary>Writes X and Y as Int32.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(X);
        writer.WriteInt32(Y);
    }
}

/// <summary>Stops the hero: it finishes the step it is on and stands; gathering ends.</summary>
public sealed record StopHeroCommand : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "stop_hero";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        HeroRules.OrderStop(sim);
    }

    /// <summary>No payload.</summary>
    public void WriteState(StateWriter writer) => ArgumentNullException.ThrowIfNull(writer);
}

/// <summary>
/// Puts <paramref name="Count"/> items <paramref name="Item"/> into the hero's backpack, as many as fit (console
/// <c>give</c>, NFR-07). Unknown items and non-positive counts are ignored with a warning.
/// </summary>
/// <param name="Item">Item definition id, e.g. <c>wood</c>.</param>
/// <param name="Count">Number of items.</param>
public sealed record GiveItemCommand(string Item, int Count) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "give_item";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        HeroRules.GiveItem(sim, Item, Count);
    }

    /// <summary>Writes the item id and the count.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Item);
        writer.WriteInt32(Count);
    }
}
