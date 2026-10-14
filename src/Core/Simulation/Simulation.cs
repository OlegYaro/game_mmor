using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Logging;

namespace Core.Sim;

/// <summary>
/// Everything a save must contain to continue a simulation exactly (ADR 0004, ADR 0005, ADR 0007).
/// The calendar date is not stored: it is derived from <see cref="Tick"/>.
/// </summary>
/// <param name="Seed">World seed.</param>
/// <param name="Tick">Last executed tick.</param>
/// <param name="RngStates">State of every RNG stream, indexed by <see cref="RngStreamId"/>.</param>
/// <param name="Entities">Entity registry slots.</param>
/// <param name="Positions">Position components in ascending entity index order.</param>
/// <param name="NextCommandSequence">Sequence number the next enqueued command gets.</param>
/// <param name="PendingCommands">Commands waiting to run, in execution order.</param>
public sealed record SimulationState(
    ulong Seed,
    long Tick,
    IReadOnlyList<Xoshiro256State> RngStates,
    EntityRegistryState Entities,
    IReadOnlyList<ComponentEntry<Position>> Positions,
    long NextCommandSequence,
    IReadOnlyList<PendingCommand> PendingCommands);

/// <summary>
/// The deterministic simulation (ADR 0004, ADR 0005): seed, content, RNG streams, entities, calendar, command queue
/// and log. <see cref="Step"/> runs one 10 Hz tick through a fixed, ordered list of systems.
/// </summary>
/// <remarks>
/// <para><see cref="Tick"/> is the number of the last executed tick; it is 0 before the first <see cref="Step"/>.
/// <see cref="Step"/> first increments it, then runs the systems for the new value.</para>
/// <para>The host changes state only through <see cref="Enqueue"/>. Systems and commands make structural entity
/// changes only through <see cref="Buffer"/>, played back after every system.</para>
/// </remarks>
public sealed class Simulation : IStateHashable
{
    /// <summary>Hash subsystem: tick and calendar date.</summary>
    public const string CalendarHashName = "calendar";

    /// <summary>Hash subsystem: RNG streams.</summary>
    public const string RngHashName = "rng";

    /// <summary>Hash subsystem: entity registry.</summary>
    public const string EntitiesHashName = "entities";

    /// <summary>Hash subsystem: all component stores.</summary>
    public const string ComponentsHashName = "components";

    /// <summary>Hash subsystem: command queue.</summary>
    public const string CommandsHashName = "commands";

    /// <summary>Name of the <see cref="Position"/> store.</summary>
    public const string PositionStoreName = "position";

    private const string EcbCategory = "ecb";

    private readonly ISimSystem[] _systems;
    private readonly List<PendingCommand> _pending = [];
    private readonly StateHasher _hasher = new();
    private readonly StateWriterAdapter _calendarState;
    private readonly StateWriterAdapter _componentState;
    private readonly StateWriterAdapter _commandState;
    private long _nextSequence;
    private bool _stepping;

    /// <summary>Creates a new simulation at tick 0.</summary>
    /// <param name="seed">World seed; every RNG stream derives from it.</param>
    /// <param name="content">Loaded content.</param>
    /// <param name="log">Log to write to; a default <see cref="SimLog"/> when null.</param>
    public Simulation(ulong seed, ContentCatalog content, SimLog? log = null)
        : this(seed, content, log, CreateDefaultSystems())
    {
    }

    // Test hook: runs the given systems instead of the default order.
    internal Simulation(ulong seed, ContentCatalog content, SimLog? log, IEnumerable<ISimSystem> systems)
        : this(content, log, new RngStreams(seed), new EntityRegistry(), systems)
    {
    }

    private Simulation(ContentCatalog content, SimLog? log, RngStreams rng, EntityRegistry registry, IEnumerable<ISimSystem> systems)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(systems);
        Content = content;
        Log = log ?? new SimLog();
        Rng = rng;
        Calendar = new Calendar(content.Time);
        Entities = new EntityWorld(registry);
        Positions = Entities.AddStore<Position>(PositionStoreName);
        _systems = systems.ToArray();
        CheckSystemNames(_systems);
        Now = Calendar.At(0);

        _calendarState = new StateWriterAdapter(WriteCalendarState);
        _componentState = new StateWriterAdapter(Entities.WriteComponentState);
        _commandState = new StateWriterAdapter(WriteCommandState);
    }

    /// <summary>World seed.</summary>
    public ulong Seed => Rng.WorldSeed;

    /// <summary>Content the simulation was built with.</summary>
    public ContentCatalog Content { get; }

    /// <summary>Per-subsystem random streams.</summary>
    public RngStreams Rng { get; }

    /// <summary>Entity registry and component stores.</summary>
    public EntityWorld Entities { get; }

    /// <summary>Position components.</summary>
    public ComponentStore<Position> Positions { get; }

    /// <summary>Tick-to-date conversion.</summary>
    public Calendar Calendar { get; }

    /// <summary>Date of <see cref="Tick"/>; updated by the clock system.</summary>
    public CalendarDate Now { get; internal set; }

    /// <summary>Structured log. Not part of the state (see <see cref="SimLog"/>).</summary>
    public SimLog Log { get; }

    /// <summary>Last executed tick (0 before the first step).</summary>
    public long Tick { get; private set; }

    /// <summary>Structural-change buffer for systems and commands; played back after every system.</summary>
    public EntityCommandBuffer Buffer { get; } = new();

    /// <summary>Systems in execution order.</summary>
    public IReadOnlyList<ISimSystem> Systems => _systems;

    /// <summary>Commands waiting to run, in execution order.</summary>
    public IReadOnlyList<PendingCommand> PendingCommands => _pending;

    /// <summary>Sequence number the next enqueued command gets.</summary>
    public long NextCommandSequence => _nextSequence;

    /// <summary>Restores a simulation from <see cref="CaptureState"/> output (the save code maps its DTOs to this).</summary>
    /// <exception cref="ArgumentException">The state is inconsistent.</exception>
    public static Simulation Restore(ContentCatalog content, SimulationState state, SimLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(state.Tick, nameof(state));
        var sim = new Simulation(
            content,
            log,
            new RngStreams(state.Seed, state.RngStates.ToArray()),
            EntityRegistry.FromState(state.Entities),
            CreateDefaultSystems());

        sim.Tick = state.Tick;
        sim.Now = sim.Calendar.At(state.Tick);
        foreach (ComponentEntry<Position> entry in state.Positions)
        {
            if (!sim.Entities.IsAlive(entry.Entity))
            {
                throw new ArgumentException($"Position belongs to {entry.Entity}, which is not alive.", nameof(state));
            }

            sim.Positions.Add(entry.Entity, entry.Value);
        }

        long previousTick = state.Tick + 1;
        long previousSequence = long.MinValue;
        foreach (PendingCommand pending in state.PendingCommands)
        {
            ArgumentNullException.ThrowIfNull(pending.Command, nameof(state));
            bool ordered = pending.ExecuteTick > previousTick
                || (pending.ExecuteTick == previousTick && pending.Sequence > previousSequence);
            if (pending.ExecuteTick <= state.Tick || !ordered || pending.Sequence >= state.NextCommandSequence)
            {
                throw new ArgumentException("Pending commands must be in the future, in (tick, sequence) order, below NextCommandSequence.", nameof(state));
            }

            previousTick = pending.ExecuteTick;
            previousSequence = pending.Sequence;
            sim._pending.Add(pending);
        }

        sim._nextSequence = state.NextCommandSequence;
        return sim;
    }

    /// <summary>Snapshot of the full simulation state for saving.</summary>
    /// <exception cref="InvalidOperationException">Called during <see cref="Step"/>.</exception>
    public SimulationState CaptureState()
    {
        ThrowIfStepping();
        return new SimulationState(
            Seed,
            Tick,
            Rng.GetStates(),
            Entities.Registry.GetState(),
            Positions.GetEntries(),
            _nextSequence,
            _pending.ToArray());
    }

    /// <summary>Queues a command for tick <c><see cref="Tick"/> + 1</c> (also when called from inside a step).</summary>
    public PendingCommand Enqueue(ISimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = new PendingCommand(Tick + 1, _nextSequence++, command);

        // Keep (tick, sequence) order; with monotonic enqueues this is always an append.
        int index = _pending.Count;
        while (index > 0 && _pending[index - 1].ExecuteTick > pending.ExecuteTick)
        {
            index--;
        }

        _pending.Insert(index, pending);
        return pending;
    }

    /// <summary>Runs one tick: increments <see cref="Tick"/>, then every system in order, playing back <see cref="Buffer"/> after each.</summary>
    /// <exception cref="InvalidOperationException">Called re-entrantly from inside a step.</exception>
    public void Step()
    {
        ThrowIfStepping();
        _stepping = true;
        try
        {
            Tick++;
            foreach (ISimSystem system in _systems)
            {
                system.Update(this);
                int skipped = Buffer.Playback(Entities);
                if (skipped > 0)
                {
                    Log.Write(Tick, LogLevel.Warning, EcbCategory, "{skipped} buffered operations of {system} targeted dead entities",
                        LogField.Of("skipped", skipped), LogField.Of("system", system.Name));
                }
            }
        }
        finally
        {
            _stepping = false;
        }
    }

    /// <summary>Runs <paramref name="ticks"/> steps.</summary>
    public void Step(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (int i = 0; i < ticks; i++)
        {
            Step();
        }
    }

    /// <summary>
    /// Hashes of each subsystem in fixed order: calendar, rng, entities, components, commands. The log is excluded
    /// (see <see cref="SimLog"/>).
    /// </summary>
    public SubsystemHashes ComputeHashes() => _hasher.HashSubsystems(
        (CalendarHashName, _calendarState),
        (RngHashName, Rng),
        (EntitiesHashName, Entities.Registry),
        (ComponentsHashName, _componentState),
        (CommandsHashName, _commandState));

    /// <summary>Writes the full canonical state: each subsystem of <see cref="ComputeHashes"/>, in the same order.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteCalendarState(writer);
        Rng.WriteState(writer);
        Entities.Registry.WriteState(writer);
        Entities.WriteComponentState(writer);
        WriteCommandState(writer);
    }

    // Called by CommandSystem.
    internal void ExecuteDueCommands()
    {
        int due = 0;
        while (due < _pending.Count && _pending[due].ExecuteTick <= Tick)
        {
            due++;
        }

        if (due == 0)
        {
            return;
        }

        // Detach first, so commands that enqueue more commands only ever add to the future.
        PendingCommand[] batch = [.. _pending.GetRange(0, due)];
        _pending.RemoveRange(0, due);
        foreach (PendingCommand pending in batch)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.Write(Tick, LogLevel.Debug, "commands", "Executing {kind} #{sequence}",
                    LogField.Of("kind", pending.Command.Kind), LogField.Of("sequence", pending.Sequence));
            }

            pending.Command.Execute(this);
        }
    }

    private static ISimSystem[] CreateDefaultSystems() =>
    [
        new ClockSystem(),
        new CommandSystem(),
    ];

    private static void CheckSystemNames(ISimSystem[] systems)
    {
        for (int i = 0; i < systems.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(systems[i].Name, systems[j].Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"System '{systems[i].Name}' is registered twice.", nameof(systems));
                }
            }
        }
    }

    private void WriteCalendarState(StateWriter writer)
    {
        writer.WriteInt64(Tick);
        Now.WriteState(writer);
    }

    private void WriteCommandState(StateWriter writer)
    {
        writer.WriteInt64(_nextSequence);
        writer.WriteInt32(_pending.Count);
        foreach (PendingCommand pending in _pending)
        {
            pending.WriteState(writer);
        }
    }

    private void ThrowIfStepping()
    {
        if (_stepping)
        {
            throw new InvalidOperationException("Not allowed while a step is running.");
        }
    }

    private sealed class StateWriterAdapter(Action<StateWriter> write) : IStateHashable
    {
        public void WriteState(StateWriter writer) => write(writer);
    }
}
