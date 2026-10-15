using Core.Content;
using Core.Determinism;
using Core.Entities;

namespace Core.Buildings;

/// <summary>What a building is doing (ADR 0012). Values are part of the state hash; save names are in the save mapper. Append only.</summary>
public enum BuildingState : byte
{
    /// <summary>Being built (level 1): not working yet; <see cref="Building.TicksLeft"/> counts down the level's build ticks.</summary>
    Constructing = 0,

    /// <summary>Finished and working.</summary>
    Active = 1,

    /// <summary>
    /// Being upgraded to <see cref="Building.Level"/> (already paid): not working; residents stay, a storage keeps its
    /// stock but is not available. <see cref="Building.TicksLeft"/> counts down the new level's build ticks.
    /// </summary>
    Upgrading = 2,

    /// <summary>
    /// Being moved to (<see cref="Building.TargetX"/>, <see cref="Building.TargetY"/>): not working, a storage is not
    /// available (E3). Both the old and the new footprint are occupied until the move ends.
    /// </summary>
    Moving = 3,
}

/// <summary>
/// A building of a settlement (FR-BUILD-02, FR-BUILD-09, FR-BUILD-13, ADR 0012): its type, level, state and footprint.
/// Upgrading and moving keep the entity (and its id), only the level or the position changes. A storage also has an
/// <see cref="Heroes.Inventory"/> (its stock, in stacks) once it is built.
/// </summary>
/// <param name="Def">Building type (<see cref="BuildingDef"/>).</param>
/// <param name="Level">
/// Level, from 1. It is the level already paid for: while <see cref="BuildingState.Constructing"/> it is 1, while
/// <see cref="BuildingState.Upgrading"/> it is the new level (the building still looks like and works as nothing until done).
/// </param>
/// <param name="State">What the building is doing.</param>
/// <param name="TicksLeft">Ticks until the current construction, upgrade or move ends; 0 when active.</param>
/// <param name="X">Column of the footprint's top-left tile.</param>
/// <param name="Y">Row of the footprint's top-left tile.</param>
/// <param name="Rotation">Quarter turns, 0 to 3; odd values swap the footprint's width and height. Always 0 for a type that is not rotatable.</param>
/// <param name="TargetX">Column of the new top-left tile while moving; -1 otherwise.</param>
/// <param name="TargetY">Row of the new top-left tile while moving; -1 otherwise.</param>
/// <param name="TargetRotation">Rotation at the new place while moving; -1 otherwise.</param>
/// <param name="Settlement">
/// Owning settlement: <see cref="PlayerSettlement"/> in M2 (settlements become entities with ADR 0013, and this
/// number then becomes a reference to one).
/// </param>
public readonly record struct Building(
    DefId Def,
    int Level,
    BuildingState State,
    int TicksLeft,
    int X,
    int Y,
    int Rotation,
    int TargetX,
    int TargetY,
    int TargetRotation,
    int Settlement) : IComponent
{
    /// <summary>The player's settlement.</summary>
    public const int PlayerSettlement = 1;

    /// <summary>Value of the target fields when the building is not moving.</summary>
    public const int NoTarget = -1;

    /// <summary>Writes every field in declaration order (Int32, the state as one byte).</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Def.Value);
        writer.WriteInt32(Level);
        writer.WriteByte((byte)State);
        writer.WriteInt32(TicksLeft);
        writer.WriteInt32(X);
        writer.WriteInt32(Y);
        writer.WriteInt32(Rotation);
        writer.WriteInt32(TargetX);
        writer.WriteInt32(TargetY);
        writer.WriteInt32(TargetRotation);
        writer.WriteInt32(Settlement);
    }
}
