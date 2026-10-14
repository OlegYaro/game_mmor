using Core.Determinism;

namespace Core.Sim;

/// <summary>
/// One step of the fixed system order in <see cref="Simulation.Step"/> (ADR 0004). Systems keep no state of their
/// own: everything that affects later ticks lives in <see cref="Simulation"/> so it is hashed and saved.
/// Structural entity changes go through <see cref="Simulation.Buffer"/>, which is played back after each system.
/// </summary>
public interface ISimSystem
{
    /// <summary>Stable name, used in logs and in the system-order check.</summary>
    string Name { get; }

    /// <summary>Runs the system for the current <see cref="Simulation.Tick"/>.</summary>
    void Update(Simulation sim);
}

/// <summary>
/// A player (or console, or replay) command. Commands enqueued while <see cref="Simulation.Tick"/> is T execute
/// in tick T + 1 (ADR 0005). Implementations are immutable records: the queue, saves and replays share instances.
/// </summary>
public interface ISimCommand : IStateHashable
{
    /// <summary>Stable type name for saves and replays; never rename an existing one.</summary>
    string Kind { get; }

    /// <summary>Applies the command. Structural entity changes go through <see cref="Simulation.Buffer"/>.</summary>
    void Execute(Simulation sim);
}

/// <summary>A command waiting in the queue, ordered by <see cref="ExecuteTick"/>, then <see cref="Sequence"/>.</summary>
/// <param name="ExecuteTick">Tick in which the command runs.</param>
/// <param name="Sequence">Enqueue counter; breaks ties between commands of the same tick.</param>
/// <param name="Command">The command.</param>
public readonly record struct PendingCommand(long ExecuteTick, long Sequence, ISimCommand Command) : IStateHashable
{
    /// <summary>Writes the tick, the sequence, the kind and then the command payload.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt64(ExecuteTick);
        writer.WriteInt64(Sequence);
        writer.WriteString(Command.Kind);
        Command.WriteState(writer);
    }
}
