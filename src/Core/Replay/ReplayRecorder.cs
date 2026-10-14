using Core.Sim;

namespace Core.Replay;

/// <summary>
/// Records the commands a host enqueues into a new simulation, to produce a <see cref="ReplayFile"/>. The host
/// enqueues through <see cref="Enqueue"/> instead of <see cref="Simulation.Enqueue"/>. Commands that commands enqueue
/// themselves are not recorded: they are reproduced by the simulation.
/// </summary>
public sealed class ReplayRecorder
{
    private readonly Simulation _simulation;
    private readonly List<ReplayCommand> _commands = [];

    /// <summary>Starts recording <paramref name="simulation"/>, which must be new (tick 0).</summary>
    /// <exception cref="ArgumentException">The simulation has already run: a replay could not reproduce its state from the seed.</exception>
    public ReplayRecorder(Simulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (simulation.Tick != 0 || simulation.PendingCommands.Count != 0)
        {
            throw new ArgumentException("Replays start from a new simulation (tick 0, empty command queue).", nameof(simulation));
        }

        _simulation = simulation;
    }

    /// <summary>Enqueues <paramref name="command"/> into the simulation and records it.</summary>
    public PendingCommand Enqueue(ISimCommand command)
    {
        PendingCommand pending = _simulation.Enqueue(command);
        _commands.Add(new ReplayCommand(pending.ExecuteTick, command));
        return pending;
    }

    /// <summary>The replay so far; it ends at the current tick, or at the last recorded command if that is later.</summary>
    public ReplayFile ToReplay()
    {
        long endTick = _commands.Count == 0 ? _simulation.Tick : Math.Max(_simulation.Tick, _commands[^1].Tick);
        return new ReplayFile(_simulation.Seed, _simulation.Content.ContentHash, endTick, _commands,
            _simulation.World?.Size ?? 0, _simulation.Difficulty);
    }
}
