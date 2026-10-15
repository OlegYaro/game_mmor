using Core.Buildings;
using Core.Determinism;
using Core.Entities;

namespace Core.Sim;

/// <summary>
/// Places a new building of type <paramref name="BuildingId"/> with its footprint's top-left tile at
/// (<paramref name="X"/>, <paramref name="Y"/>) (FR-BUILD-01, FR-BUILD-02). Checked by
/// <see cref="Construction.CheckPlacement"/> when it executes; on success level 1 is paid at once and construction
/// starts. The outcome, a refusal included, is a <see cref="GameWorld.BuildingEvents"/> entry.
/// </summary>
/// <param name="BuildingId">Building type id, e.g. <c>campfire</c>.</param>
/// <param name="X">Column of the footprint's top-left tile.</param>
/// <param name="Y">Row of the footprint's top-left tile.</param>
/// <param name="Rotation">Quarter turns, 0 to 3 (0 for a type that cannot be rotated).</param>
public sealed record PlaceBuildingCommand(string BuildingId, int X, int Y, int Rotation = 0) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "place_building";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        BuildingRules.OrderPlace(sim, BuildingId, X, Y, Rotation);
    }

    /// <summary>Writes the building id, X, Y and the rotation.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(BuildingId);
        writer.WriteInt32(X);
        writer.WriteInt32(Y);
        writer.WriteInt32(Rotation);
    }
}

/// <summary>
/// Cancels what <paramref name="Building"/> is doing (FR-BUILD-12): a construction is removed with a refund of
/// <see cref="Content.ConstructionBalanceDef.CancelRefundPermille"/>, an upgrade goes back to the previous level with the
/// same refund of its cost, a move stays at the old place. Refused for an active building.
/// </summary>
/// <param name="Building">The building.</param>
public sealed record CancelBuildingCommand(EntityId Building) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "cancel_building";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        BuildingRules.OrderCancel(sim, Building);
    }

    /// <summary>Writes the entity id.</summary>
    public void WriteState(StateWriter writer) => Building.WriteState(writer);
}

/// <summary>
/// Upgrades an active <paramref name="Building"/> to the next level (FR-BUILD-09): the same entity, the level's cost paid
/// at once, not working for the level's build ticks.
/// </summary>
/// <param name="Building">The building.</param>
public sealed record UpgradeBuildingCommand(EntityId Building) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "upgrade_building";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        BuildingRules.OrderUpgrade(sim, Building);
    }

    /// <summary>Writes the entity id.</summary>
    public void WriteState(StateWriter writer) => Building.WriteState(writer);
}

/// <summary>
/// Moves an active <paramref name="Building"/> so its footprint's top-left tile is (<paramref name="X"/>,
/// <paramref name="Y"/>) (FR-BUILD-13): the same entity, level and stock; it takes the level's move ticks, during which
/// both places stay occupied and the building does not work (E2, E3).
/// </summary>
/// <param name="Building">The building.</param>
/// <param name="X">Column of the new top-left tile.</param>
/// <param name="Y">Row of the new top-left tile.</param>
/// <param name="Rotation">Quarter turns at the new place.</param>
public sealed record MoveBuildingCommand(EntityId Building, int X, int Y, int Rotation = 0) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "move_building";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        BuildingRules.OrderMove(sim, Building, X, Y, Rotation);
    }

    /// <summary>Writes the entity id, X, Y and the rotation.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Building.WriteState(writer);
        writer.WriteInt32(X);
        writer.WriteInt32(Y);
        writer.WriteInt32(Rotation);
    }
}

/// <summary>
/// Demolishes an active <paramref name="Building"/> (FR-BUILD-12): <see cref="Content.ConstructionBalanceDef.DemolishRefundPermille"/>
/// of all paid levels comes back (E5) together with a storage's stock; the center and the last storage cannot be
/// demolished (E4).
/// </summary>
/// <param name="Building">The building.</param>
public sealed record DemolishBuildingCommand(EntityId Building) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "demolish_building";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        BuildingRules.OrderDemolish(sim, Building);
    }

    /// <summary>Writes the entity id.</summary>
    public void WriteState(StateWriter writer) => Building.WriteState(writer);
}
