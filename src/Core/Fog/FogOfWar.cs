using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Core.Determinism;
using Core.WorldGen;

namespace Core.Fog;

/// <summary>What the player knows about a tile (FR-WORLD-06, B3). The numeric values are the per-tile bytes of <see cref="FogOfWar.CopyStates(Span{byte})"/>.</summary>
public enum FogState : byte
{
    /// <summary>Never seen.</summary>
    Unknown = 0,

    /// <summary>Seen before, not seen now. Stays at least this forever.</summary>
    Explored = 1,

    /// <summary>Seen now by some revealer.</summary>
    Visible = 2,
}

/// <summary>Number of tiles in each <see cref="FogState"/>.</summary>
/// <param name="Unknown">Never seen.</param>
/// <param name="Explored">Seen before, not now.</param>
/// <param name="Visible">Seen now.</param>
public readonly record struct FogCounts(int Unknown, int Explored, int Visible);

/// <summary>A circle of sight: source tile and radius in tiles.</summary>
internal readonly record struct RevealSource(int X, int Y, int Radius);

/// <summary>
/// Fog of war of a square map (FR-WORLD-06): two bit layers over all tiles, <c>explored</c> and <c>visible</c>, with
/// <c>visible ⊆ explored</c>. Tile (x, y) is bit <c>i = y * Size + x</c>: word <c>i / 64</c>, bit <c>i % 64</c>
/// (least significant first). The state of a tile is <c>explored + visible</c> (0 Unknown, 1 Explored, 2 Visible).
/// </summary>
/// <remarks>
/// <para>Explored is permanent and saved. Visible is derived: it is recomputed by the fog system from the revealers
/// and the time of day, only when the list of sight circles changed since the last computation.</para>
/// <para>Change notification for the renderer (not part of the state): <see cref="Version"/> grows whenever any bit
/// may have changed, and <see cref="ChunkVersions"/> holds, per 32×32 chunk, the <see cref="Version"/> of its last
/// possible change. A renderer remembers the version it drew and re-uploads only chunks with a newer one.</para>
/// </remarks>
public sealed class FogOfWar : IStateHashable
{
    private const int BitsPerWord = 64;

    private readonly ulong[] _explored;
    private readonly ulong[] _visible;
    private readonly long[] _chunkVersions;
    private RevealSource[] _sources = [];
    private int _sourceCount;

    /// <summary>Creates an all-unknown fog for a map of <paramref name="size"/>² tiles.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a positive multiple of <see cref="ChunkCoord.Size"/>.</exception>
    internal FogOfWar(int size)
    {
        if (size <= 0 || size % ChunkCoord.Size != 0 || size > WorldGenRequest.MaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"Fog size must be a multiple of {ChunkCoord.Size} up to {WorldGenRequest.MaxSize}.");
        }

        Size = size;
        TileCount = size * size;
        int words = WordCountFor(TileCount);
        _explored = new ulong[words];
        _visible = new ulong[words];
        ChunksPerSide = ChunkCoord.CountPerSide(size);
        _chunkVersions = new long[ChunksPerSide * ChunksPerSide];
    }

    /// <summary>Side of the map in tiles.</summary>
    public int Size { get; }

    /// <summary>Number of tiles.</summary>
    public int TileCount { get; }

    /// <summary>Chunks along one side; <see cref="ChunkVersions"/> is indexed <c>cy * ChunksPerSide + cx</c>.</summary>
    public int ChunksPerSide { get; }

    /// <summary>Explored bits, 64 tiles per word (see the class remarks for the layout).</summary>
    public ReadOnlySpan<ulong> ExploredBits => _explored;

    /// <summary>Visible bits, same layout as <see cref="ExploredBits"/>; always a subset of them.</summary>
    public ReadOnlySpan<ulong> VisibleBits => _visible;

    /// <summary>Grows on every change of any bit (presentation only, not part of the state).</summary>
    public long Version { get; private set; }

    /// <summary>Per chunk, the <see cref="Version"/> of its last possible change (presentation only).</summary>
    public ReadOnlySpan<long> ChunkVersions => _chunkVersions;

    /// <summary>Number of 64-bit words of a bit layer for <paramref name="tileCount"/> tiles.</summary>
    public static int WordCountFor(int tileCount) => (tileCount + BitsPerWord - 1) / BitsPerWord;

    /// <summary>Version of the last possible change of chunk (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The chunk is outside the map.</exception>
    public long GetChunkVersion(int cx, int cy)
    {
        if ((uint)cx >= (uint)ChunksPerSide || (uint)cy >= (uint)ChunksPerSide)
        {
            throw new ArgumentOutOfRangeException(nameof(cx), $"Chunk ({cx}, {cy}) is outside a {ChunksPerSide}x{ChunksPerSide} chunk map.");
        }

        return _chunkVersions[(cy * ChunksPerSide) + cx];
    }

    /// <summary>State of tile (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public FogState GetState(int x, int y)
    {
        int index = Index(x, y);
        return (FogState)(Bit(_explored, index) + Bit(_visible, index));
    }

    /// <summary>True when the tile is Explored or Visible.</summary>
    public bool IsExplored(int x, int y) => Bit(_explored, Index(x, y)) != 0;

    /// <summary>True when the tile is Visible.</summary>
    public bool IsVisible(int x, int y) => Bit(_visible, Index(x, y)) != 0;

    /// <summary>Counts the tiles of each state (popcount over the layers).</summary>
    public FogCounts CountStates()
    {
        int explored = 0;
        int visible = 0;
        for (int i = 0; i < _explored.Length; i++)
        {
            explored += BitOperations.PopCount(_explored[i]);
            visible += BitOperations.PopCount(_visible[i]);
        }

        return new FogCounts(TileCount - explored, explored - visible, visible);
    }

    /// <summary>Writes one <see cref="FogState"/> byte per tile, indexed <c>y * Size + x</c> (fog mask, minimap).</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="TileCount"/>.</exception>
    public void CopyStates(Span<byte> destination) => CopyStates(0, 0, Size, Size, destination);

    /// <summary>Writes the 32×32 states of a chunk, row by row, into <paramref name="destination"/> (1024 bytes).</summary>
    public void CopyStates(ChunkCoord chunk, Span<byte> destination) =>
        CopyStates(chunk.X * ChunkCoord.Size, chunk.Y * ChunkCoord.Size, ChunkCoord.Size, ChunkCoord.Size, destination);

    /// <summary>
    /// Writes the states of the rectangle (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>,
    /// <paramref name="height"/>) row by row into <paramref name="destination"/> (<c>width * height</c> bytes).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is not inside the map.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public void CopyStates(int x, int y, int width, int height, Span<byte> destination)
    {
        if (x < 0 || y < 0 || width < 0 || height < 0 || x > Size - width || y > Size - height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Rectangle ({x}, {y}, {width}, {height}) is not inside the {Size}x{Size} map.");
        }

        if (destination.Length < width * height)
        {
            throw new ArgumentException($"Destination needs {width * height} bytes.", nameof(destination));
        }

        int target = 0;
        for (int row = y; row < y + height; row++)
        {
            int index = (row * Size) + x;
            for (int i = 0; i < width; i++, index++)
            {
                destination[target++] = (byte)(Bit(_explored, index) + Bit(_visible, index));
            }
        }
    }

    /// <summary>Writes a tag, the size, then the explored and the visible words.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("fog");
        writer.WriteInt32(Size);
        WriteWords(writer, _explored);
        WriteWords(writer, _visible);
    }

    /// <summary>
    /// Makes <paramref name="sources"/> the sight circles of the visible layer. When they equal the current ones nothing
    /// happens; otherwise visible is rebuilt and OR-ed into explored.
    /// </summary>
    /// <returns>True when the layers were recomputed.</returns>
    internal bool Update(ReadOnlySpan<RevealSource> sources)
    {
        if (sources.SequenceEqual(_sources.AsSpan(0, _sourceCount)))
        {
            return false;
        }

        long version = ++Version;
        foreach (RevealSource old in _sources.AsSpan(0, _sourceCount))
        {
            Touch(old, version);
        }

        Array.Clear(_visible);
        foreach (RevealSource source in sources)
        {
            Stamp(source);
            Touch(source, version);
        }

        for (int i = 0; i < _explored.Length; i++)
        {
            _explored[i] |= _visible[i];
        }

        if (_sources.Length < sources.Length)
        {
            _sources = new RevealSource[sources.Length];
        }

        sources.CopyTo(_sources);
        _sourceCount = sources.Length;
        return true;
    }

    /// <summary>Marks every tile explored (console <c>reveal</c>).</summary>
    internal void ExploreAll()
    {
        Array.Fill(_explored, ulong.MaxValue);
        ClearPadding(_explored);
        Version++;
        Array.Fill(_chunkVersions, Version);
    }

    /// <summary>Copy of the explored words (saves).</summary>
    internal ulong[] CopyExplored() => [.. _explored];

    /// <summary>
    /// Replaces the explored layer (restoring a save) and clears the visible layer and the circle cache, so the next
    /// <see cref="Update"/> recomputes visible.
    /// </summary>
    /// <exception cref="ArgumentException">Wrong word count, or bits set beyond the last tile.</exception>
    internal void LoadExplored(IReadOnlyList<ulong> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count != _explored.Length)
        {
            throw new ArgumentException($"Explored layer has {words.Count} words; a {Size}x{Size} map needs {_explored.Length}.", nameof(words));
        }

        for (int i = 0; i < _explored.Length; i++)
        {
            _explored[i] = words[i];
        }

        ulong[] check = [.. _explored];
        ClearPadding(check);
        if (!check.AsSpan().SequenceEqual(_explored))
        {
            throw new ArgumentException("Explored layer has bits beyond the last tile.", nameof(words));
        }

        Array.Clear(_visible);
        _sourceCount = 0;
        Version++;
        Array.Fill(_chunkVersions, Version);
    }

    /// <summary>Sets bits <paramref name="first"/>..<paramref name="last"/> (inclusive) of a layer.</summary>
    internal static void SetRange(Span<ulong> bits, int first, int last)
    {
        int firstWord = first / BitsPerWord;
        int lastWord = last / BitsPerWord;
        ulong firstMask = ulong.MaxValue << (first % BitsPerWord);
        ulong lastMask = ulong.MaxValue >> (BitsPerWord - 1 - (last % BitsPerWord));
        if (firstWord == lastWord)
        {
            bits[firstWord] |= firstMask & lastMask;
            return;
        }

        bits[firstWord] |= firstMask;
        bits[(firstWord + 1)..lastWord].Fill(ulong.MaxValue);
        bits[lastWord] |= lastMask;
    }

    private static int Bit(ulong[] bits, int index) => (int)((bits[index / BitsPerWord] >> (index % BitsPerWord)) & 1);

    private static void WriteWords(StateWriter writer, ulong[] words)
    {
        if (BitConverter.IsLittleEndian)
        {
            writer.WriteBytes(MemoryMarshal.AsBytes(words.AsSpan()));
            return;
        }

        // Same bytes as the fast path: Int32 byte count, then each word little-endian.
        writer.WriteInt32(words.Length * sizeof(ulong));
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        foreach (ulong word in words)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, word);
            foreach (byte b in buffer)
            {
                writer.WriteByte(b);
            }
        }
    }

    private int Index(int x, int y)
    {
        if ((uint)x >= (uint)Size || (uint)y >= (uint)Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Tile ({x}, {y}) is outside the {Size}x{Size} map.");
        }

        return (y * Size) + x;
    }

    private void ClearPadding(ulong[] bits)
    {
        int used = TileCount % BitsPerWord;
        if (used != 0)
        {
            bits[^1] &= (1UL << used) - 1;
        }
    }

    // Tiles with dx² + dy² <= r²: per row the half width shrinks as |dy| grows, so one pass from the centre row out.
    private void Stamp(RevealSource source)
    {
        int r = source.Radius;
        if (r <= 0)
        {
            return;
        }

        int rr = r * r;
        int half = r;
        for (int dy = 0; dy <= r; dy++)
        {
            while ((half * half) + (dy * dy) > rr)
            {
                half--;
            }

            StampRow(source.Y + dy, source.X - half, source.X + half);
            if (dy != 0)
            {
                StampRow(source.Y - dy, source.X - half, source.X + half);
            }
        }
    }

    private void StampRow(int y, int x0, int x1)
    {
        if ((uint)y >= (uint)Size)
        {
            return;
        }

        x0 = Math.Max(x0, 0);
        x1 = Math.Min(x1, Size - 1);
        if (x0 <= x1)
        {
            SetRange(_visible, (y * Size) + x0, (y * Size) + x1);
        }
    }

    // Records `version` on every chunk the circle's bounding square overlaps.
    private void Touch(RevealSource source, long version)
    {
        int x0 = Math.Max(source.X - source.Radius, 0);
        int y0 = Math.Max(source.Y - source.Radius, 0);
        int x1 = Math.Min(source.X + source.Radius, Size - 1);
        int y1 = Math.Min(source.Y + source.Radius, Size - 1);
        if (source.Radius <= 0 || x0 > x1 || y0 > y1)
        {
            return;
        }

        for (int cy = y0 / ChunkCoord.Size; cy <= y1 / ChunkCoord.Size; cy++)
        {
            for (int cx = x0 / ChunkCoord.Size; cx <= x1 / ChunkCoord.Size; cx++)
            {
                _chunkVersions[(cy * ChunksPerSide) + cx] = version;
            }
        }
    }
}
