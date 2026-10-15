using Core.Content;
using Core.Entities;

namespace Core.Buildings;

/// <summary>
/// Outcome of a placement check (<see cref="Construction.CheckPlacement"/>) or a building order. <see cref="Ok"/> means
/// accepted; every other value but <see cref="None"/> is a reason for a refusal. Not saved; names for the console and the
/// game are <see cref="Construction.NameOf(BuildResult)"/>. Append only.
/// </summary>
public enum BuildResult : byte
{
    /// <summary>No result.</summary>
    None = 0,

    /// <summary>Accepted (the placement is valid).</summary>
    Ok = 1,

    /// <summary>The simulation has no world.</summary>
    NoWorld = 2,

    /// <summary>The building id is not a building type.</summary>
    UnknownBuilding = 3,

    /// <summary>The rotation is not 0..3, or not 0 for a type that cannot be rotated.</summary>
    InvalidRotation = 4,

    /// <summary>A footprint tile is outside the map.</summary>
    OutOfMap = 5,

    /// <summary>A footprint tile is still unknown in the fog of war.</summary>
    NotExplored = 6,

    /// <summary>A footprint tile is water or a river.</summary>
    Water = 7,

    /// <summary>The footprint tiles are not all of the same height.</summary>
    NotFlat = 8,

    /// <summary>A footprint tile holds a resource node (a tree, a rock, a bush, a vein): gather it first.</summary>
    ResourceInTheWay = 9,

    /// <summary>A footprint tile is occupied by another building.</summary>
    Occupied = 10,

    /// <summary>A unit (the hero) stands on, or is stepping onto, a footprint tile.</summary>
    UnitInTheWay = 11,

    /// <summary>The settlement has no center (campfire) yet; only a center can be placed.</summary>
    NoCenter = 12,

    /// <summary>The settlement already has a center; there is exactly one.</summary>
    CenterExists = 13,

    /// <summary>A footprint tile is outside the settlement territory.</summary>
    OutsideTerritory = 14,

    /// <summary>A requirement of the level (era, specialist) is not met.</summary>
    RequirementNotMet = 15,

    /// <summary>The hero's backpack and the available storages do not hold the level's cost.</summary>
    NotEnoughMaterials = 16,

    /// <summary>The entity is not a building.</summary>
    NotABuilding = 17,

    /// <summary>The building is under construction, being upgraded or being moved.</summary>
    Busy = 18,

    /// <summary>The building is already at its last level.</summary>
    MaxLevel = 19,

    /// <summary>Cancel: the building is active, there is nothing to cancel.</summary>
    NothingToCancel = 20,

    /// <summary>Demolish: the settlement center cannot be demolished (E4).</summary>
    CenterNotRemovable = 21,

    /// <summary>Demolish: the last built storage of the settlement cannot be demolished (E4).</summary>
    LastStorage = 22,

    /// <summary>Cancel: the center under construction already has other buildings around it.</summary>
    CenterInUse = 23,

    /// <summary>Move of the center: another building would end up outside the territory around the new place.</summary>
    BuildingsOutsideTerritory = 24,

    /// <summary>Move: the target is the building's current place and rotation.</summary>
    SamePlace = 25,
}

/// <summary>Result of <see cref="Construction.CheckPlacement"/>: the reason and, for tile reasons, the first offending tile.</summary>
/// <param name="Reason"><see cref="BuildResult.Ok"/> or why the placement is refused.</param>
/// <param name="TileX">Column of the first offending tile; -1 when the reason is not about a tile.</param>
/// <param name="TileY">Row of the first offending tile; -1 when the reason is not about a tile.</param>
public readonly record struct PlacementResult(BuildResult Reason, int TileX, int TileY)
{
    /// <summary>A valid placement.</summary>
    public static PlacementResult Valid { get; } = new(BuildResult.Ok, -1, -1);

    /// <summary>True when the placement is valid.</summary>
    public bool IsOk => Reason == BuildResult.Ok;

    /// <summary>A refusal that is not about a tile.</summary>
    public static PlacementResult Refuse(BuildResult reason) => new(reason, -1, -1);
}

/// <summary>
/// One processed building order, for the game's messages (accepted or refused, with the reason). Presentation only:
/// not part of the state, not saved, not hashed (like the log); <see cref="Sim.GameWorld.BuildingEvents"/> keeps the
/// latest ones.
/// </summary>
/// <param name="Sequence">Running number (1, 2, ...) since the simulation object was created; the game remembers the last one it showed.</param>
/// <param name="Tick">Tick the order ran in.</param>
/// <param name="Command">Command kind, e.g. <c>place_building</c>.</param>
/// <param name="Result">Outcome.</param>
/// <param name="Building">The building concerned (the new one for an accepted placement); <see cref="EntityId.None"/> when there is none.</param>
/// <param name="BuildingId">Building type id; empty when unknown.</param>
/// <param name="X">Tile column of the order (placement or move target); -1 when the order has none.</param>
/// <param name="Y">Tile row of the order; -1 when the order has none.</param>
/// <param name="Lost">Items that found no room after a demolition or cancel (storages and backpack full); empty otherwise.</param>
public sealed record BuildingEvent(
    long Sequence,
    long Tick,
    string Command,
    BuildResult Result,
    EntityId Building,
    string BuildingId,
    int X,
    int Y,
    IReadOnlyList<ItemAmountDef> Lost);
