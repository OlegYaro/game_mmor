using Core.Determinism;

namespace Core.WorldGen;

/// <summary>
/// The whole generated world (ADR 0006 steps 1–9): the <see cref="TerrainMap"/> plus the resource layers, the veins,
/// points of interest, the start and the settlement sites. Layers are whole-map arrays indexed <c>y * Size + x</c>; lists are
/// sorted by (y, x). Immutable once generated.
/// </summary>
public sealed class WorldMap : IStateHashable
{
    internal WorldMap(TerrainMap terrain)
    {
        Terrain = terrain;
        ResourceKindArray = new ResourceKind[terrain.TileCount];
        ResourceAmountArray = new ushort[terrain.TileCount];
    }

    /// <summary>Terrain stage output (heights, biomes, water, rivers, climate).</summary>
    public TerrainMap Terrain { get; }

    /// <summary>World seed.</summary>
    public ulong Seed => Terrain.Seed;

    /// <summary>Side of the map in tiles.</summary>
    public int Size => Terrain.Size;

    /// <summary>Generator version (<see cref="WorldGenerator.Version"/>) that produced the world.</summary>
    public int Version => Terrain.Version;

    /// <summary>Number of tiles.</summary>
    public int TileCount => Terrain.TileCount;

    /// <summary>Resource node kind per tile (<see cref="ResourceKind.None"/> on water and river tiles).</summary>
    public ReadOnlySpan<ResourceKind> ResourceKinds => ResourceKindArray;

    /// <summary>Stock of the resource node per tile (0 where there is none, otherwise at least 1).</summary>
    public ReadOnlySpan<ushort> ResourceAmounts => ResourceAmountArray;

    /// <summary>Ore and coal veins sorted by (y, x) of their centres; their tiles are in the resource layers.</summary>
    public IReadOnlyList<Vein> Veins { get; internal set; } = [];

    /// <summary>Points of interest sorted by (y, x).</summary>
    public IReadOnlyList<PointOfInterest> PointsOfInterest { get; internal set; } = [];

    /// <summary>The hero's start tile.</summary>
    public StartPoint Start { get; internal set; }

    /// <summary>How far the start search had to relax B1.</summary>
    public StartRelaxation StartRelaxation { get; internal set; }

    /// <summary>Reserved settlement sites sorted by (y, x).</summary>
    public IReadOnlyList<SettlementSite> SettlementSites { get; internal set; } = [];

    /// <summary>
    /// Deterministic generation warnings (relaxed start checks, fewer points or sites than targeted), in the order the
    /// steps produced them. Not part of the hash; the game logs them.
    /// </summary>
    public IReadOnlyList<string> Warnings => WarningList;

    internal ResourceKind[] ResourceKindArray { get; }

    internal ushort[] ResourceAmountArray { get; }

    internal List<string> WarningList { get; } = [];

    /// <summary>Resource kind of tile (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public ResourceKind GetResource(int x, int y) => ResourceKindArray[Terrain.Index(x, y)];

    /// <summary>Resource stock of tile (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public int GetResourceAmount(int x, int y) => ResourceAmountArray[Terrain.Index(x, y)];

    /// <summary>Number of tiles of each resource kind, indexed by the <see cref="ResourceKind"/> value.</summary>
    public int[] CountResources()
    {
        int[] counts = new int[WorldNames.Resources.Count + 1];
        foreach (ResourceKind kind in ResourceKindArray)
        {
            counts[(int)kind]++;
        }

        return counts;
    }

    /// <summary>
    /// Writes the canonical world bytes: a tag, version, seed, size, the terrain hash
    /// (<see cref="TerrainMap.ComputeHash"/>), then in this fixed order: resource kinds, resource amounts (16-bit
    /// little-endian), points of interest (count, then kind, x, y each), start (x, y, relaxation), settlement sites
    /// (count, then faction, x, y each), veins (count, then kind, x, y, radius, tiles, stock each).
    /// </summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("world");
        writer.WriteInt32(Version);
        writer.WriteUInt64(Seed);
        writer.WriteInt32(Size);
        writer.WriteUInt64(Terrain.ComputeHash());
        writer.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ResourceKindArray.AsSpan()));
        TerrainMap.WriteUInt16Layer(writer, ResourceAmountArray);
        writer.WriteInt32(PointsOfInterest.Count);
        foreach (PointOfInterest poi in PointsOfInterest)
        {
            writer.WriteByte((byte)poi.Kind);
            writer.WriteInt32(poi.X);
            writer.WriteInt32(poi.Y);
        }

        writer.WriteInt32(Start.X);
        writer.WriteInt32(Start.Y);
        writer.WriteByte((byte)StartRelaxation);
        writer.WriteInt32(SettlementSites.Count);
        foreach (SettlementSite site in SettlementSites)
        {
            writer.WriteByte((byte)site.Faction);
            writer.WriteInt32(site.X);
            writer.WriteInt32(site.Y);
        }

        writer.WriteInt32(Veins.Count);
        foreach (Vein vein in Veins)
        {
            writer.WriteByte((byte)vein.Kind);
            writer.WriteInt32(vein.X);
            writer.WriteInt32(vein.Y);
            writer.WriteInt32(vein.Radius);
            writer.WriteInt32(vein.Tiles);
            writer.WriteInt32(vein.Stock);
        }
    }

    /// <summary>XXH3-64 of <see cref="WriteState"/>: the map hash (golden tests, the <c>worldhash</c> console command).</summary>
    public ulong ComputeHash() => new StateHasher().Hash(this);
}
