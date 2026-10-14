using Core.Content;
using Core.Logging;
using Core.Sim;

namespace Core.DevConsole;

/// <summary>
/// Holds the current <see cref="Simulation"/>. The console's <c>load</c> command replaces it, so the host reads
/// <see cref="Current"/> every frame instead of caching the instance.
/// </summary>
public sealed class SimulationHolder
{
    private Simulation _current;

    /// <summary>Creates a holder.</summary>
    public SimulationHolder(Simulation initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The simulation in use.</summary>
    public Simulation Current
    {
        get => _current;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _current = value;
        }
    }
}

/// <summary>What console commands may touch. The host (Game, tests) provides every piece.</summary>
/// <param name="Simulation">Holder of the running simulation.</param>
/// <param name="Scheduler">Speed and pause.</param>
/// <param name="Log">The log shared by all simulations of this run (also given to simulations restored by <c>load</c>).</param>
/// <param name="Content">Loaded content, used to restore saves.</param>
/// <param name="SaveDirectory">Real filesystem directory for saves, chosen by the host.</param>
public sealed record DevConsoleContext(
    SimulationHolder Simulation,
    TickScheduler Scheduler,
    SimLog Log,
    ContentCatalog Content,
    string SaveDirectory);
