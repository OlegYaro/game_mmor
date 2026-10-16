using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Logging;
using Core.Residents;
using Core.WorldGen;

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
/// <param name="Difficulty">Difficulty; always <see cref="Difficulty.Normal"/> without a world.</param>
/// <param name="World">The world part (map identity, explored fog, revealers, hero, tile changes), or null for a simulation without a world.</param>
public sealed record SimulationState(
    ulong Seed,
    long Tick,
    IReadOnlyList<Xoshiro256State> RngStates,
    EntityRegistryState Entities,
    IReadOnlyList<ComponentEntry<Position>> Positions,
    long NextCommandSequence,
    IReadOnlyList<PendingCommand> PendingCommands,
    Difficulty Difficulty = Difficulty.Normal,
    WorldState? World = null);

/// <summary>
/// The deterministic simulation (ADR 0004, ADR 0005): seed, content, RNG streams, entities, calendar, command queue
/// and log. <see cref="Step"/> runs one 10 Hz tick through a fixed, ordered list of systems.
/// </summary>
/// <remarks>
/// <para>Two kinds exist. A game (<see cref="CreateNewGame"/>) has a <see cref="World"/>: generated map, tile changes,
/// fog of war, revealers, the hero, buildings, residents and a difficulty; it adds the <c>game</c>, <c>fog</c>,
/// <c>tiles</c> and <c>residents</c> hash subsystems and the <c>revealer</c>, <c>hero</c>, <c>inventory</c>,
/// <c>mover</c>, <c>building</c> and <c>resident</c> stores. A core simulation (the constructor; M0 tests and replays)
/// has no world and keeps exactly the M0 state layout.</para>
/// <para><see cref="Tick"/> is the number of the last executed tick; it is 0 before the first <see cref="Step"/>.
/// <see cref="Step"/> first increments it, then runs the systems for the new value.</para>
/// <para>The host changes state only through <see cref="Enqueue"/>. Systems and commands make structural entity
/// changes only through <see cref="Buffer"/>, played back after every system. The exceptions are the building orders
/// and new residents: a placement creates its entity and a demolition or cancel destroys it at once, so that a later
/// order of the same tick already sees the reserved or freed tiles, and a resident is created at once, so that the
/// population, the reached era and the house places count it in the same tick (nothing iterates the stores then).</para>
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

    /// <summary>Hash subsystem (games only): difficulty and map identity.</summary>
    public const string GameHashName = "game";

    /// <summary>Hash subsystem (games only): fog of war layers.</summary>
    public const string FogHashName = "fog";

    /// <summary>Hash subsystem (games only): tile changes against the generated map.</summary>
    public const string TilesHashName = "tiles";

    /// <summary>Hash subsystem (games only): the reached era and the resident components.</summary>
    public const string ResidentsHashName = "residents";

    /// <summary>Name of the <see cref="Position"/> store.</summary>
    public const string PositionStoreName = "position";

    /// <summary>Name of the <see cref="Revealer"/> store (games only; registered after <see cref="PositionStoreName"/>).</summary>
    public const string RevealerStoreName = "revealer";

    /// <summary>Name of the <see cref="Hero"/> store (games only; registered after <see cref="RevealerStoreName"/>).</summary>
    public const string HeroStoreName = "hero";

    /// <summary>Name of the <see cref="Inventory"/> store (games only; registered after <see cref="HeroStoreName"/>).</summary>
    public const string InventoryStoreName = "inventory";

    /// <summary>Name of the <see cref="Mover"/> store (games only; registered after <see cref="InventoryStoreName"/>).</summary>
    public const string MoverStoreName = "mover";

    /// <summary>Name of the <see cref="Buildings.Building"/> store (games only; registered after <see cref="MoverStoreName"/>).</summary>
    public const string BuildingStoreName = "building";

    /// <summary>Name of the <see cref="Resident"/> store (games only; registered after <see cref="BuildingStoreName"/>).</summary>
    public const string ResidentStoreName = "resident";

    private const string EcbCategory = "ecb";

    private readonly ISimSystem[] _systems;
    private readonly List<PendingCommand> _pending = [];
    private readonly StateHasher _hasher = new();
    private readonly StateWriterAdapter _calendarState;
    private readonly StateWriterAdapter _componentState;
    private readonly StateWriterAdapter _commandState;
    private readonly StateWriterAdapter _gameState;
    private readonly StateWriterAdapter _residentState;
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
        : this(content, log, new RngStreams(seed), new EntityRegistry(), systems, Difficulty.Normal, null, 0)
    {
    }

    private Simulation(ContentCatalog content, SimLog? log, RngStreams rng, EntityRegistry registry, IEnumerable<ISimSystem> systems,
        Difficulty difficulty, WorldMap? map, ulong mapHash)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(systems);
        Content = content;
        Log = log ?? new SimLog();
        Rng = rng;
        Calendar = new Calendar(content.Time);
        Entities = new EntityWorld(registry);
        Positions = Entities.AddStore<Position>(PositionStoreName);
        Difficulty = difficulty;
        if (map is not null)
        {
            World = new GameWorld(map, mapHash, Entities, ResidentRules.FirstEra(content));
        }

        _systems = systems.ToArray();
        CheckSystemNames(_systems);
        Now = Calendar.At(0);

        _calendarState = new StateWriterAdapter(WriteCalendarState);
        _componentState = new StateWriterAdapter(Entities.WriteComponentState);
        _commandState = new StateWriterAdapter(WriteCommandState);
        _gameState = new StateWriterAdapter(WriteGameState);
        _residentState = new StateWriterAdapter(w => World?.WriteResidentState(w));
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

    /// <summary>The world (map, tile changes, fog, revealers, hero) of a game; null for a core simulation.</summary>
    public GameWorld? World { get; }

    /// <summary>Difficulty chosen at new game (FR-SAVE-05); <see cref="Difficulty.Normal"/> without a world.</summary>
    public Difficulty Difficulty { get; }

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

    /// <summary>
    /// Restores a simulation from <see cref="CaptureState"/> output (the save code maps its DTOs to this). A game's
    /// map is regenerated from the seed and must hash to <see cref="WorldState.MapHash"/>; the visible fog is recomputed.
    /// A game state without a hero (saves from before M2) gets one: the entity with the hero revealer becomes the hero
    /// with the balance defaults, an empty inventory and an idle mover.
    /// </summary>
    /// <exception cref="ArgumentException">The state is inconsistent.</exception>
    /// <exception cref="WorldGenMismatchException">The regenerated map differs from the saved one.</exception>
    public static Simulation Restore(ContentCatalog content, SimulationState state, SimLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(state.Tick, nameof(state));
        if (!Enum.IsDefined(state.Difficulty) || (state.World is null && state.Difficulty != Difficulty.Normal))
        {
            throw new ArgumentException($"Difficulty {(int)state.Difficulty} is invalid here (a simulation without a world is always Normal).", nameof(state));
        }

        WorldMap? map = null;
        ulong mapHash = 0;
        if (state.World is { } world)
        {
            if (!WorldGenerator.IsSupportedVersion(world.GeneratorVersion))
            {
                throw new ArgumentException($"World generator v{world.GeneratorVersion} is not available in this build.", nameof(state));
            }

            map = WorldGenerator.Generate(new WorldGenRequest(state.Seed, world.MapSize, content.WorldGen, world.GeneratorVersion));
            mapHash = map.ComputeHash();
            if (mapHash != world.MapHash)
            {
                throw new WorldGenMismatchException(world.MapHash, mapHash);
            }
        }

        var sim = new Simulation(
            content,
            log,
            new RngStreams(state.Seed, state.RngStates.ToArray()),
            EntityRegistry.FromState(state.Entities),
            CreateDefaultSystems(),
            state.Difficulty,
            map,
            mapHash);

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

        if (state.World is not null && sim.World is not null)
        {
            foreach (ComponentEntry<Revealer> entry in state.World.Revealers)
            {
                if (!sim.Entities.IsAlive(entry.Entity))
                {
                    throw new ArgumentException($"Revealer belongs to {entry.Entity}, which is not alive.", nameof(state));
                }

                if (!Enum.IsDefined(entry.Value.Kind))
                {
                    throw new ArgumentException($"Revealer of {entry.Entity} has unknown kind {(int)entry.Value.Kind}.", nameof(state));
                }

                sim.World.Revealers.Add(entry.Entity, entry.Value);
            }

            sim.World.Fog.LoadExplored(state.World.ExploredBits);
            HeroRules.Restore(sim, state.World);
            BuildingRules.Restore(sim, state.World);
            ResidentRules.Restore(sim, state.World);
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

        // Visible is derived state: rebuild it exactly as the fog system left it after the last tick.
        sim.RefreshFog();
        return sim;
    }

    /// <summary>
    /// Starts a new game (FR-SAVE-05, FR-WORLD-01/08, FR-HERO-01): generates the world from the seed and size, spawns
    /// the hero on the start tile (position, <see cref="RevealerKind.Hero"/> revealer, <see cref="Hero"/> from the
    /// balance, an <see cref="Inventory"/> holding the balance's start items and an idle <see cref="Mover"/>) and applies
    /// the initial fog. The simulation is at tick 0 with an empty command queue, ready for a replay recorder.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid options.</exception>
    public static Simulation CreateNewGame(ContentCatalog content, NewGameOptions options, SimLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return CreateNewGame(content, options, log, WorldGenerator.Generate(new WorldGenRequest(options.Seed, options.MapSize, content.WorldGen)));
    }

    // Test hook: starts a game on a map generated before for the same seed and size (maps never change after
    // generation, so tests that create many games share one and skip the world generation).
    internal static Simulation CreateNewGame(ContentCatalog content, NewGameOptions options, SimLog? log, WorldMap map)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(map);
        options.Validate();
        if (map.Seed != options.Seed || map.Size != options.MapSize)
        {
            throw new ArgumentException($"The map was generated for seed {map.Seed} and size {map.Size}, not for these options.", nameof(map));
        }

        var sim = new Simulation(content, log, new RngStreams(options.Seed), new EntityRegistry(), CreateDefaultSystems(),
            options.Difficulty, map, map.ComputeHash());
        GameWorld world = sim.World!;

        EntityId hero = sim.Entities.Create();
        sim.Positions.Add(hero, TileCenter(map.Start.X, map.Start.Y));
        world.Revealers.Add(hero, new Revealer(RevealerKind.Hero));
        HeroRules.AddHero(sim, hero, withStartItems: true);
        sim.RefreshFog();

        sim.Log.Write(0, LogLevel.Info, "world", "New game: seed {seed}, map {size}, difficulty {difficulty}, start {x},{y}",
            LogField.Of("seed", options.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)), LogField.Of("size", map.Size),
            LogField.Of("difficulty", DifficultyNames.ToName(options.Difficulty)), LogField.Of("x", map.Start.X), LogField.Of("y", map.Start.Y));
        foreach (string warning in map.Warnings)
        {
            sim.Log.Write(0, LogLevel.Warning, "world", "{warning}", LogField.Of("warning", warning));
        }

        return sim;
    }

    /// <summary>Position of the centre of tile (<paramref name="tileX"/>, <paramref name="tileY"/>).</summary>
    public static Position TileCenter(int tileX, int tileY)
    {
        Position corner = Position.FromTile(tileX, tileY);
        return new Position(corner.X + (Position.UnitsPerTile / 2), corner.Y + (Position.UnitsPerTile / 2));
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
            _pending.ToArray(),
            Difficulty,
            World is null
                ? null
                : new WorldState(World.Size, World.Map.Version, World.MapHash, World.Fog.CopyExplored(), World.Revealers.GetEntries(),
                    World.Heroes.GetEntries(), World.Inventories.GetEntries(), World.Movers.GetEntries(), World.Buildings.GetEntries(),
                    World.Tiles.CaptureState(), World.Residents.GetEntries(), World.ReachedEra));
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
    /// Hashes of each subsystem in fixed order: calendar, rng, entities, components, commands, and for a game also
    /// game (difficulty, map identity), fog, tiles and residents (reached era and resident components). The log is
    /// excluded (see <see cref="SimLog"/>).
    /// </summary>
    public SubsystemHashes ComputeHashes() => World is null
        ? _hasher.HashSubsystems(
            (CalendarHashName, _calendarState),
            (RngHashName, Rng),
            (EntitiesHashName, Entities.Registry),
            (ComponentsHashName, _componentState),
            (CommandsHashName, _commandState))
        : _hasher.HashSubsystems(
            (CalendarHashName, _calendarState),
            (RngHashName, Rng),
            (EntitiesHashName, Entities.Registry),
            (ComponentsHashName, _componentState),
            (CommandsHashName, _commandState),
            (GameHashName, _gameState),
            (FogHashName, World.Fog),
            (TilesHashName, World.Tiles),
            (ResidentsHashName, _residentState));

    /// <summary>Writes the full canonical state: each subsystem of <see cref="ComputeHashes"/>, in the same order.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteCalendarState(writer);
        Rng.WriteState(writer);
        Entities.Registry.WriteState(writer);
        Entities.WriteComponentState(writer);
        WriteCommandState(writer);
        if (World is not null)
        {
            WriteGameState(writer);
            World.Fog.WriteState(writer);
            World.Tiles.WriteState(writer);
            World.WriteResidentState(writer);
        }
    }

    // Called by FogSystem, after a restore and at new game.
    internal void RefreshFog() => World?.RefreshFog(Content.Fog, Positions, Now.IsNight);

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
        new ConstructionSystem(),
        new ResidentSystem(),
        new MovementSystem(),
        new GatherSystem(),
        new FogSystem(),
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

    private void WriteGameState(StateWriter writer)
    {
        writer.WriteByte((byte)Difficulty);
        World?.WriteMapState(writer);
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
