using Core.Entities;

namespace Core.Residents;

/// <summary>What happened to a resident (<see cref="ResidentEvent"/>). Append only.</summary>
public enum ResidentEventKind : byte
{
    /// <summary>A newcomer appeared at the edge of the settlement's world (a site of good people or the map edge) and walks in.</summary>
    Arrived = 0,

    /// <summary>A resident was created next to the center by a command (developer console, specialists).</summary>
    Spawned = 1,

    /// <summary>The resident's house was demolished and no other house had a free place: it is homeless now.</summary>
    Evicted = 2,

    /// <summary>A homeless resident (or one whose house was demolished) moved into a house with a free place.</summary>
    Housed = 3,

    /// <summary>
    /// The settlement went hungry too long (A5): the resident gave up its home and job and walks away (state
    /// <see cref="ResidentState.Leaving"/>).
    /// </summary>
    LeftHungry = 4,

    /// <summary>A leaving resident reached the edge of the settlement's world and is gone.</summary>
    Left = 5,
}

/// <summary>
/// A resident event for the game's event panel ("a newcomer arrived", "lost the home"). Presentation only: not part of
/// the state, not saved, not hashed (like the log); <see cref="Sim.GameWorld.ResidentEvents"/> keeps the latest ones.
/// </summary>
/// <param name="Sequence">Running number (1, 2, ...) since the simulation object was created; the game remembers the last one it showed.</param>
/// <param name="Tick">Tick it happened in.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Resident">The resident.</param>
/// <param name="Name">The resident's first name.</param>
/// <param name="Profession">The resident's profession id.</param>
/// <param name="Home">The resident's home after the event; <see cref="EntityId.None"/> when homeless.</param>
/// <param name="X">Tile column of the resident when it happened.</param>
/// <param name="Y">Tile row of the resident when it happened.</param>
public sealed record ResidentEvent(
    long Sequence,
    long Tick,
    ResidentEventKind Kind,
    EntityId Resident,
    string Name,
    string Profession,
    EntityId Home,
    int X,
    int Y);
