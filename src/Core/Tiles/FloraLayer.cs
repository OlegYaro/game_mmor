namespace Core.Tiles;

/// <summary>
/// Storage of the flora layer of <see cref="TileChanges"/> (stumps, saplings, grown trees): sparse per 32×32 chunk, a
/// dense chunk once one of its tiles has flora. The rules live in <see cref="TileChanges"/>; this class only keeps
/// the values and the counts.
/// </summary>
internal sealed class FloraLayer(int chunks)
{
    private readonly Chunk?[] _chunks = new Chunk?[chunks];

    /// <summary>Tiles with flora.</summary>
    public int Count { get; private set; }

    /// <summary>Number of chunks of the map.</summary>
    public int ChunkCount => _chunks.Length;

    /// <summary>The chunk at <paramref name="chunkIndex"/>, or null when none of its tiles has flora.</summary>
    public Chunk? this[int chunkIndex] => _chunks[chunkIndex];

    /// <summary>State of a tile (<see cref="FloraState.None"/> without flora).</summary>
    public FloraState StateAt(int chunkIndex, int local) =>
        Count != 0 && _chunks[chunkIndex] is { } chunk ? (FloraState)chunk.States[local] : FloraState.None;

    /// <summary>Gives a tile flora (new or changed).</summary>
    public void Set(int chunkIndex, int local, FloraState state, long tick, int stock)
    {
        Chunk chunk = _chunks[chunkIndex] ??= new Chunk();
        if (!ChunkMask.IsSet(chunk.Mask, local))
        {
            ChunkMask.Set(chunk.Mask, local);
            chunk.Count++;
            Count++;
        }

        chunk.States[local] = (byte)state;
        chunk.Ticks[local] = tick;
        chunk.Stocks[local] = (ushort)stock;
    }

    /// <summary>Removes a tile's flora; false when it had none.</summary>
    public bool Remove(int chunkIndex, int local)
    {
        if (_chunks[chunkIndex] is not { } chunk || !ChunkMask.IsSet(chunk.Mask, local))
        {
            return false;
        }

        ChunkMask.Clear(chunk.Mask, local);
        chunk.States[local] = 0;
        chunk.Ticks[local] = 0;
        chunk.Stocks[local] = 0;
        Count--;
        if (--chunk.Count == 0)
        {
            _chunks[chunkIndex] = null;
        }

        return true;
    }

    /// <summary>Forgets everything (a failed load).</summary>
    public void Clear()
    {
        Array.Clear(_chunks);
        Count = 0;
    }

    /// <summary>One chunk: the mask and per tile the state, the tick of the state and a grown tree's stock.</summary>
    public sealed class Chunk
    {
        public ulong[] Mask { get; } = new ulong[ChunkMask.Words];

        public byte[] States { get; } = new byte[ChunkMask.TilesPerChunk];

        public long[] Ticks { get; } = new long[ChunkMask.TilesPerChunk];

        public ushort[] Stocks { get; } = new ushort[ChunkMask.TilesPerChunk];

        public int Count { get; set; }
    }
}
