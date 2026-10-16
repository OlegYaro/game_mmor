using System.Runtime.InteropServices;
using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Economy;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Jobs;
using Core.Pathfinding;
using Core.Residents;
using Core.Tiles;
using Core.WorldGen;

namespace Core.Sim;

/// <summary>
/// Saved world part of <see cref="SimulationState"/>: what a save needs to rebuild the <see cref="GameWorld"/>. The
/// map itself is not saved; it is regenerated from the seed and checked against <paramref name="MapHash"/>.
/// </summary>
/// <param name="MapSize">Side of the map in tiles.</param>
/// <param name="GeneratorVersion">World generator version the map was made with (<see cref="WorldGenerator.Version"/>).</param>
/// <param name="MapHash"><see cref="WorldMap.ComputeHash"/> of the map.</param>
/// <param name="ExploredBits">Explored fog layer (<see cref="FogOfWar.ExploredBits"/> layout).</param>
/// <param name="Revealers">Revealer components in ascending entity index order.</param>
/// <param name="Heroes">Hero components in ascending entity index order (empty in saves from before M2).</param>
/// <param name="Inventories">Inventory components in ascending entity index order.</param>
/// <param name="Movers">Mover components in ascending entity index order.</param>
/// <param name="Buildings">Building components in ascending entity index order.</param>
/// <param name="Tiles">Tile changes against the generated map (<see cref="TileChanges"/>).</param>
/// <param name="Residents">Resident components in ascending entity index order.</param>
/// <param name="ReachedEra">The highest era the player's settlement has reached (<see cref="GameWorld.ReachedEra"/>).</param>
/// <param name="Jobs">Resident job components in ascending entity index order.</param>
/// <param name="WorkOutputs">Work output components of buildings in ascending entity index order.</param>
/// <param name="StorageFilters">Storage filter components in ascending entity index order.</param>
/// <param name="Reservations">Reservations and banned targets of the job board.</param>
/// <param name="DaysWithoutFood">Days in a row the settlement could not eat its fill (<see cref="GameWorld.DaysWithoutFood"/>).</param>
/// <param name="LastLeaveDay">Day the last hungry resident left during this shortage; -1 when none did (<see cref="GameWorld.LastLeaveDay"/>).</param>
public sealed record WorldState(
    int MapSize,
    int GeneratorVersion,
    ulong MapHash,
    IReadOnlyList<ulong> ExploredBits,
    IReadOnlyList<ComponentEntry<Revealer>> Revealers,
    IReadOnlyList<ComponentEntry<Hero>> Heroes,
    IReadOnlyList<ComponentEntry<Inventory>> Inventories,
    IReadOnlyList<ComponentEntry<Mover>> Movers,
    IReadOnlyList<ComponentEntry<Building>> Buildings,
    TileChangesState Tiles,
    IReadOnlyList<ComponentEntry<Resident>> Residents,
    DefId ReachedEra,
    IReadOnlyList<ComponentEntry<ResidentJob>> Jobs,
    IReadOnlyList<ComponentEntry<WorkOutput>> WorkOutputs,
    IReadOnlyList<ComponentEntry<StorageFilter>> StorageFilters,
    ReservationsState Reservations,
    int DaysWithoutFood,
    long LastLeaveDay);

/// <summary>
/// The world of a game simulation (<see cref="Simulation.CreateNewGame"/>): the generated map, the tile changes, the
/// fog of war, the revealers and the hero. Simulations made with the plain constructor (the M0 core, tests) have none.
/// </summary>
/// <remarks>
/// <para>Renderer notes: map layers are whole-map spans indexed <c>y * Size + x</c> (<see cref="WorldMap"/>,
/// <see cref="TerrainMap"/>). The map itself never changes; what changed since generation (gathered and vanished
/// nodes, occupied tiles) is in <see cref="Tiles"/>: read resources through <see cref="TileChanges.GetEffectiveResource"/>
/// and <see cref="TileChanges.GetEffectiveAmount"/>, and redraw chunks whose <see cref="TileChanges.GetChunkVersion"/>
/// grew. Fog: <see cref="FogOfWar"/>.</para>
/// <para>Hero: <see cref="HeroEntity"/>, its <see cref="Position"/> (fixed point, updated every tick while walking, so
/// the game interpolates between two ticks), <see cref="Heroes"/>, <see cref="Inventories"/> and <see cref="Movers"/>
/// (the planned path). Orders are <see cref="MoveHeroCommand"/>, <see cref="GatherCommand"/> and
/// <see cref="StopHeroCommand"/> through <see cref="Simulation.Enqueue"/>.</para>
/// <para>Buildings: <see cref="Buildings"/>, the placement check and the territory in <see cref="Construction"/>, the
/// outcome of every building order in <see cref="BuildingEvents"/>.</para>
/// <para>Residents: <see cref="Residents"/> (with a <see cref="Position"/> and a <see cref="Movers"/> entry each, so the
/// game interpolates them like the hero), housing in <see cref="Housing"/>, the arrival odds in <see cref="Arrivals"/>,
/// arrivals and housing changes in <see cref="ResidentEvents"/>; the era never falls below <see cref="ReachedEra"/>.</para>
/// <para>Work (ADR 0009): each resident's job, load and priorities in <see cref="Jobs"/> and <see cref="Resident.Priorities"/>,
/// the held targets in <see cref="Reservations"/>; a field's waiting output in <see cref="WorkOutputs"/>; storage filters
/// in <see cref="StorageFilters"/> (read-only rules in <see cref="Storages"/>). Food and tax: <see cref="DaysWithoutFood"/>,
/// the daily outcome in <see cref="EconomyEvents"/>, the numbers in <see cref="FoodSupply"/>.</para>
/// </remarks>
public sealed class GameWorld
{
    /// <summary>How many of the latest <see cref="BuildingEvents"/> are kept.</summary>
    public const int BuildingEventCapacity = 64;

    /// <summary>How many of the latest <see cref="ResidentEvents"/> are kept.</summary>
    public const int ResidentEventCapacity = 64;

    /// <summary>How many of the latest <see cref="EconomyEvents"/> are kept.</summary>
    public const int EconomyEventCapacity = 64;

    private readonly List<RevealSource> _sources = [];
    private readonly List<BuildingEvent> _buildingEvents = [];
    private readonly List<ResidentEvent> _residentEvents = [];
    private readonly List<EconomyEvent> _economyEvents = [];
    private Pathfinder? _pathfinder;

    internal GameWorld(WorldMap map, ulong mapHash, EntityWorld entities, DefId firstEra)
    {
        Map = map;
        MapHash = mapHash;
        Revealers = entities.AddStore<Revealer>(Simulation.RevealerStoreName);
        Heroes = entities.AddStore<Hero>(Simulation.HeroStoreName);
        Inventories = entities.AddStore<Inventory>(Simulation.InventoryStoreName);
        Movers = entities.AddStore<Mover>(Simulation.MoverStoreName);
        Buildings = entities.AddStore<Building>(Simulation.BuildingStoreName);
        Residents = entities.AddStore<Resident>(Simulation.ResidentStoreName);
        Jobs = entities.AddStore<ResidentJob>(Simulation.JobStoreName);
        WorkOutputs = entities.AddStore<WorkOutput>(Simulation.WorkOutputStoreName);
        StorageFilters = entities.AddStore<StorageFilter>(Simulation.StorageFilterStoreName);
        ReachedEra = firstEra;
        LastLeaveDay = NoLeaveDay;
        Fog = new FogOfWar(map.Size);
        Tiles = new TileChanges(map);
    }

    /// <summary>The generated map (immutable).</summary>
    public WorldMap Map { get; }

    /// <summary><see cref="WorldMap.ComputeHash"/> of <see cref="Map"/>, computed once (console <c>worldhash</c>).</summary>
    public ulong MapHash { get; }

    /// <summary>Side of the map in tiles.</summary>
    public int Size => Map.Size;

    /// <summary>Fog of war.</summary>
    public FogOfWar Fog { get; }

    /// <summary>Revealer components (store <see cref="Simulation.RevealerStoreName"/>).</summary>
    public ComponentStore<Revealer> Revealers { get; }

    /// <summary>Hero components (store <see cref="Simulation.HeroStoreName"/>); exactly one in a game.</summary>
    public ComponentStore<Hero> Heroes { get; }

    /// <summary>Inventory components (store <see cref="Simulation.InventoryStoreName"/>).</summary>
    public ComponentStore<Inventory> Inventories { get; }

    /// <summary>Mover components (store <see cref="Simulation.MoverStoreName"/>).</summary>
    public ComponentStore<Mover> Movers { get; }

    /// <summary>Building components (store <see cref="Simulation.BuildingStoreName"/>); see <see cref="Construction"/>.</summary>
    public ComponentStore<Building> Buildings { get; }

    /// <summary>Resident components (store <see cref="Simulation.ResidentStoreName"/>); see <see cref="Housing"/> and <see cref="Arrivals"/>.</summary>
    public ComponentStore<Resident> Residents { get; }

    /// <summary>Resident job components (store <see cref="Simulation.JobStoreName"/>): one per resident.</summary>
    public ComponentStore<ResidentJob> Jobs { get; }

    /// <summary>Work output components (store <see cref="Simulation.WorkOutputStoreName"/>) of finished buildings where residents work.</summary>
    public ComponentStore<WorkOutput> WorkOutputs { get; }

    /// <summary>Storage filter components (store <see cref="Simulation.StorageFilterStoreName"/>); a storage without one takes everything.</summary>
    public ComponentStore<StorageFilter> StorageFilters { get; }

    /// <summary>Reservations and banned targets of the job board (subsystem <see cref="Simulation.JobsHashName"/>).</summary>
    public ReservationManager Reservations { get; } = new();

    /// <summary>
    /// Days in a row the settlement could not eat its fill (FR-ECO-10, A5); 0 when the last meal was complete. While it
    /// is above 0 residents work at <see cref="EconomyBalanceDef.ShortageSlowdownPermille"/>. Saved and hashed
    /// (subsystem <see cref="Simulation.EconomyHashName"/>).
    /// </summary>
    public int DaysWithoutFood { get; internal set; }

    /// <summary>Day the last hungry resident left during the current shortage; <see cref="NoLeaveDay"/> when none did.</summary>
    public long LastLeaveDay { get; internal set; }

    /// <summary>Value of <see cref="LastLeaveDay"/> before anyone left in a shortage.</summary>
    public const long NoLeaveDay = -1;

    /// <summary>
    /// The latest economy events (food warnings and shortages, tax, the hero's deposits), oldest first (at most
    /// <see cref="EconomyEventCapacity"/>). Presentation only: not part of the state, not saved, not hashed.
    /// </summary>
    public IReadOnlyList<EconomyEvent> EconomyEvents => _economyEvents;

    /// <summary>Sequence of the latest economy event; 0 before the first.</summary>
    public long LastEconomyEventSequence { get; private set; }

    /// <summary>
    /// The highest era (<see cref="EraDef"/>) the player's settlement has reached. The era grows with the population but
    /// never falls back (<see cref="Construction.CurrentEra"/>), so the territory never shrinks under existing buildings.
    /// Part of the state: saved and hashed (subsystem <see cref="Simulation.ResidentsHashName"/>).
    /// </summary>
    public DefId ReachedEra { get; internal set; }

    /// <summary>
    /// The latest resident events (arrivals, spawns, evictions, housing), oldest first (at most
    /// <see cref="ResidentEventCapacity"/>), for the game's event panel. Presentation only: not part of the state, not
    /// saved (empty after a load), not hashed.
    /// </summary>
    public IReadOnlyList<ResidentEvent> ResidentEvents => _residentEvents;

    /// <summary>Sequence of the latest resident event; 0 before the first.</summary>
    public long LastResidentEventSequence { get; private set; }

    /// <summary>
    /// The latest processed building orders, oldest first (at most <see cref="BuildingEventCapacity"/>), for the game's
    /// messages. Presentation only: not part of the state, not saved (empty after a load), not hashed.
    /// </summary>
    public IReadOnlyList<BuildingEvent> BuildingEvents => _buildingEvents;

    /// <summary>Sequence of the latest building event; 0 before the first.</summary>
    public long LastBuildingEventSequence { get; private set; }

    /// <summary>Changes of the tiles against the generated map: resource stocks and occupancy (FR-WORLD-05).</summary>
    public TileChanges Tiles { get; }

    /// <summary>
    /// The hero: the entity with the lowest index among those with a <see cref="Hero"/> (the only one in M2), or
    /// <see cref="EntityId.None"/> when there is none (it was destroyed).
    /// </summary>
    public EntityId HeroEntity => Heroes.Count > 0 ? Heroes.Entities[0] : EntityId.None;

    /// <summary>
    /// Path search over this map with <see cref="Tiles"/> as the blocking layer (occupied tiles are impassable). Built on
    /// first use (derived state: never saved or hashed).
    /// </summary>
    public Pathfinder Pathfinder
    {
        get
        {
            if (_pathfinder is null)
            {
                _pathfinder = new Pathfinder(new PassabilityGrid(Map.Terrain, Tiles));
                Tiles.AttachPathfinder(_pathfinder);
            }

            return _pathfinder;
        }
    }

    /// <summary>Appends a building event (dropping the oldest beyond <see cref="BuildingEventCapacity"/>) and returns it.</summary>
    internal BuildingEvent AddBuildingEvent(long tick, string command, BuildResult result, EntityId building, string buildingId, int x, int y,
        IReadOnlyList<ItemAmountDef> lost)
    {
        var added = new BuildingEvent(++LastBuildingEventSequence, tick, command, result, building, buildingId, x, y, lost);
        if (_buildingEvents.Count == BuildingEventCapacity)
        {
            _buildingEvents.RemoveAt(0);
        }

        _buildingEvents.Add(added);
        return added;
    }

    /// <summary>Appends a resident event (dropping the oldest beyond <see cref="ResidentEventCapacity"/>) and returns it.</summary>
    internal ResidentEvent AddResidentEvent(long tick, ResidentEventKind kind, EntityId resident, string name, string profession, EntityId home, int x, int y)
    {
        var added = new ResidentEvent(++LastResidentEventSequence, tick, kind, resident, name, profession, home, x, y);
        if (_residentEvents.Count == ResidentEventCapacity)
        {
            _residentEvents.RemoveAt(0);
        }

        _residentEvents.Add(added);
        return added;
    }

    /// <summary>Appends an economy event (dropping the oldest beyond <see cref="EconomyEventCapacity"/>) and returns it.</summary>
    internal EconomyEvent AddEconomyEvent(long tick, EconomyEventKind kind, string item, int amount, int lost, int daysLeft)
    {
        var added = new EconomyEvent(++LastEconomyEventSequence, tick, kind, item, amount, lost, daysLeft);
        if (_economyEvents.Count == EconomyEventCapacity)
        {
            _economyEvents.RemoveAt(0);
        }

        _economyEvents.Add(added);
        return added;
    }

    /// <summary>Writes the food state: days without food and the last leave day.</summary>
    internal void WriteEconomyState(StateWriter writer)
    {
        writer.WriteInt32(DaysWithoutFood);
        writer.WriteInt64(LastLeaveDay);
    }

    /// <summary>Writes the residents' settlement state: the reached era, then the resident components.</summary>
    internal void WriteResidentState(StateWriter writer)
    {
        writer.WriteInt32(ReachedEra.Value);
        Residents.WriteState(writer);
    }

    /// <summary>
    /// Collects the sight circles of all revealers with a position (ascending entity index) for the current time of
    /// day and updates the fog; it recomputes only when a circle changed.
    /// </summary>
    /// <returns>True when the fog was recomputed.</returns>
    internal bool RefreshFog(FogBalanceDef balance, ComponentStore<Position> positions, bool night)
    {
        _sources.Clear();
        ReadOnlySpan<EntityId> owners = Revealers.Entities;
        Span<Revealer> values = Revealers.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (!positions.TryGet(owners[i], out Position position))
            {
                continue;
            }

            int radius = Revealer.RadiusOf(balance, values[i].Kind, night);
            if (radius > 0)
            {
                _sources.Add(new RevealSource(position.TileX, position.TileY, radius));
            }
        }

        return Fog.Update(CollectionsMarshal.AsSpan(_sources));
    }

    /// <summary>Writes the map identity (size, generator version, map hash); the map content is covered by the hash.</summary>
    internal void WriteMapState(StateWriter writer)
    {
        writer.WriteInt32(Map.Size);
        writer.WriteInt32(Map.Version);
        writer.WriteUInt64(MapHash);
    }
}
