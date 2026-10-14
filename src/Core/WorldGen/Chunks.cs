namespace Core.WorldGen;

/// <summary>Coordinates of a 32x32 chunk (FR-WORLD-13). Chunk (0, 0) covers tiles 0..31 on both axes.</summary>
/// <param name="X">Chunk column.</param>
/// <param name="Y">Chunk row.</param>
public readonly record struct ChunkCoord(int X, int Y)
{
    /// <summary>Tiles per chunk side.</summary>
    public const int Size = 32;

    /// <summary>The chunk containing tile (<paramref name="tileX"/>, <paramref name="tileY"/>); tiles must not be negative.</summary>
    public static ChunkCoord OfTile(int tileX, int tileY)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tileX);
        ArgumentOutOfRangeException.ThrowIfNegative(tileY);
        return new ChunkCoord(tileX / Size, tileY / Size);
    }

    /// <summary>Number of chunks along one side of a square map of <paramref name="mapSize"/> tiles (a multiple of <see cref="Size"/>).</summary>
    public static int CountPerSide(int mapSize) => mapSize / Size;
}

/// <summary>
/// A 32x32 access window into whole-map arrays indexed <c>y * mapSize + x</c>. Chunks are not separate storage
/// (ADR 0006): a window only translates local chunk coordinates to map indices.
/// </summary>
public readonly record struct ChunkWindow
{
    /// <summary>Creates the window of <paramref name="coord"/> on a square map of <paramref name="mapSize"/> tiles.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The chunk lies outside the map.</exception>
    public ChunkWindow(ChunkCoord coord, int mapSize)
    {
        int count = ChunkCoord.CountPerSide(mapSize);
        if ((uint)coord.X >= (uint)count || (uint)coord.Y >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(coord), coord, $"Chunk outside a {count}x{count} chunk map.");
        }

        Coord = coord;
        MapSize = mapSize;
    }

    /// <summary>The chunk.</summary>
    public ChunkCoord Coord { get; }

    /// <summary>Side of the whole map in tiles.</summary>
    public int MapSize { get; }

    /// <summary>Map X of the chunk's first column.</summary>
    public int OriginX => Coord.X * ChunkCoord.Size;

    /// <summary>Map Y of the chunk's first row.</summary>
    public int OriginY => Coord.Y * ChunkCoord.Size;

    /// <summary>True when map tile (<paramref name="x"/>, <paramref name="y"/>) lies in this chunk.</summary>
    public bool Contains(int x, int y) =>
        x >= OriginX && x < OriginX + ChunkCoord.Size && y >= OriginY && y < OriginY + ChunkCoord.Size;

    /// <summary>Map array index of local tile (<paramref name="localX"/>, <paramref name="localY"/>), both in 0..31.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside 0..31.</exception>
    public int MapIndex(int localX, int localY)
    {
        if ((uint)localX >= ChunkCoord.Size || (uint)localY >= ChunkCoord.Size)
        {
            throw new ArgumentOutOfRangeException(localX < 0 || localX >= ChunkCoord.Size ? nameof(localX) : nameof(localY), "Local coordinates are 0..31.");
        }

        return ((OriginY + localY) * MapSize) + OriginX + localX;
    }

    /// <summary>Map index of the first tile of local row <paramref name="localY"/>; the row is 32 consecutive array elements.</summary>
    public int RowStart(int localY) => MapIndex(0, localY);
}
