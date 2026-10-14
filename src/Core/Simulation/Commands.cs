using Core.Determinism;
using Core.Entities;
using Core.Logging;

namespace Core.Sim;

/// <summary>Creates an entity with a <see cref="Position"/> (M0 test command for entities, commands and the hash).</summary>
/// <param name="Position">Where to place the marker.</param>
public sealed record SpawnMarkerCommand(Position Position) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "spawn_marker";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        DeferredEntity marker = sim.Buffer.Create();
        sim.Buffer.Add(marker, Position);
        sim.Log.Write(sim.Tick, LogLevel.Debug, "entities", "Marker spawned at {x},{y}",
            LogField.Of("x", Position.X), LogField.Of("y", Position.Y));
    }

    /// <summary>Writes the position.</summary>
    public void WriteState(StateWriter writer) => Position.WriteState(writer);
}

/// <summary>Destroys an entity; ignored when it is no longer alive.</summary>
/// <param name="Entity">Entity to destroy.</param>
public sealed record DestroyEntityCommand(EntityId Entity) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "destroy_entity";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        sim.Buffer.Destroy(Entity);
    }

    /// <summary>Writes the entity id.</summary>
    public void WriteState(StateWriter writer) => Entity.WriteState(writer);
}
