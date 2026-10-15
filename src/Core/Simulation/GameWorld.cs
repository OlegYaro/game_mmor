using System.Runtime.InteropServices;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Pathfinding;
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
/// <param name="Tiles">Tile changes against the generated map (<see cref="TileChanges"/>).</param>
public sealed record WorldState(
    int MapSize,
    int GeneratorVersion,
    ulong MapHash,
    IReadOnlyList<ulong> ExploredBits,
    IReadOnlyList<ComponentEntry<Revealer>> Revealers,
    IReadOnlyList<ComponentEntry<Hero>> Heroes,
    IReadOnlyList<ComponentEntry<Inventory>> Inventories,
    IReadOnlyList<ComponentEntry<Mover>> Movers,
    TileChangesState Tiles);

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
/// </remarks>
public sealed class GameWorld
{
    private readonly List<RevealSource> _sources = [];
    private Pathfinder? _pathfinder;

    internal GameWorld(WorldMap map, ulong mapHash, EntityWorld entities)
    {
        Map = map;
        MapHash = mapHash;
        Revealers = entities.AddStore<Revealer>(Simulation.RevealerStoreName);
        Heroes = entities.AddStore<Hero>(Simulation.HeroStoreName);
        Inventories = entities.AddStore<Inventory>(Simulation.InventoryStoreName);
        Movers = entities.AddStore<Mover>(Simulation.MoverStoreName);
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
