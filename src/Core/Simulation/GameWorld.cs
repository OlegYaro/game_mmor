using System.Runtime.InteropServices;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
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
public sealed record WorldState(
    int MapSize,
    int GeneratorVersion,
    ulong MapHash,
    IReadOnlyList<ulong> ExploredBits,
    IReadOnlyList<ComponentEntry<Revealer>> Revealers);

/// <summary>
/// The world of a game simulation (<see cref="Simulation.CreateNewGame"/>): the generated map, the fog of war and the
/// revealers. Simulations made with the plain constructor (the M0 core, tests) have none.
/// </summary>
/// <remarks>
/// Renderer notes: map layers are whole-map spans indexed <c>y * Size + x</c> (<see cref="WorldMap"/>,
/// <see cref="TerrainMap"/>). The map never changes in M1 (tile diffs come in M2). Fog: <see cref="FogOfWar"/>.
/// </remarks>
public sealed class GameWorld
{
    private readonly List<RevealSource> _sources = [];

    internal GameWorld(WorldMap map, ulong mapHash, ComponentStore<Revealer> revealers)
    {
        Map = map;
        MapHash = mapHash;
        Revealers = revealers;
        Fog = new FogOfWar(map.Size);
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
