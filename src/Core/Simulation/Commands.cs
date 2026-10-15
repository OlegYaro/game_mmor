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

/// <summary>
/// Destroys an entity; ignored when it is no longer alive. A building is not destroyed (warning): its tiles and refund
/// are handled by <see cref="DemolishBuildingCommand"/> and <see cref="CancelBuildingCommand"/>.
/// </summary>
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
        if (Buildings.BuildingRules.IsBuilding(sim, Entity))
        {
            sim.Log.Write(sim.Tick, LogLevel.Warning, Buildings.BuildingRules.LogCategory, "destroy_entity ignored: {entity} is a building (demolish it)",
                LogField.Of("entity", Entity.ToString()));
            return;
        }

        sim.Buffer.Destroy(Entity);
    }

    /// <summary>Writes the entity id.</summary>
    public void WriteState(StateWriter writer) => Entity.WriteState(writer);
}

/// <summary>Marks the whole map explored (console <c>reveal</c>, NFR-07); does nothing without a world.</summary>
public sealed record RevealAllCommand : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "reveal_all";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is null)
        {
            sim.Log.Write(sim.Tick, LogLevel.Warning, "fog", "reveal_all ignored: the simulation has no world");
            return;
        }

        sim.World.Fog.ExploreAll();
        sim.Log.Write(sim.Tick, LogLevel.Info, "fog", "Whole map revealed");
    }

    /// <summary>No payload.</summary>
    public void WriteState(StateWriter writer) => ArgumentNullException.ThrowIfNull(writer);
}
