using Core.Content;
using Core.Determinism;
using Core.Pathfinding;
using Core.WorldGen;

namespace Core.Tiles;

/// <summary>
/// The walk layer of <see cref="TileChanges"/> (FR-WORLD-15, docs/research/roads-from-paths.md): per tile a walk counter
/// that grows by <see cref="RoadsBalanceDef.StepIncrement"/> with every step onto the tile and fades by 1 every
/// <see cref="RoadsBalanceDef.DecayEveryTicks"/>, and the tile's class (plain, trail, road) with hysteresis.
/// </summary>
/// <remarks>
/// <para><b>Lazy fading.</b> A tile stores the counter right after its last step and the tick of that step; the counter at
/// tick <c>t</c> is <c>max(0, counter − (t − tick) / decay)</c>, integer division. Nothing walks the map: reading a tile
/// computes it, a step writes it.</para>
/// <para><b>Class.</b> A step first lets the class fall to what the faded counter still keeps (a road below
/// <see cref="RoadsBalanceDef.RoadKeepThreshold"/> becomes a trail, a trail below
/// <see cref="RoadsBalanceDef.TrailKeepThreshold"/> plain ground), then adds the increment and raises the class at
/// <see cref="RoadsBalanceDef.TrailThreshold"/> and <see cref="RoadsBalanceDef.RoadThreshold"/>. Between two steps the
/// counter only falls, so the class only falls, and the tick of the next fall is known at the step:
/// <c>tick + (counter − keep + 1) × decay</c>. Those ticks wait in a sorted set and fall due in
/// <see cref="AdvanceTo"/> at the start of each tick, so the stored class is always the class of the current tick,
/// whoever reads it and whenever. The set is derived state: a load rebuilds it.</para>
/// <para><b>State.</b> Saves and the hash hold, in chunk order, the tiles whose counter is above 0 now: counter after the
/// last step, its tick and the current class. Tiles that faded to 0 are behaviourally equal to untouched ones; the daily
/// <see cref="Compact"/> drops them from memory, which changes neither the hash nor the behaviour.</para>
/// </remarks>
internal sealed class WalkLayer
{
    private readonly RoadsBalanceDef _roads;
    private readonly int _size;
    private readonly int _chunksPerSide;
    private readonly Chunk?[] _chunks;
    private readonly SortedSet<(long Due, int Tile)> _falls = [];
    private readonly Action<int> _touch;

    // Trails and roads per chunk, and the chunk-aligned rectangle around every chunk that has one (derived, rebuilt lazily).
    private readonly int[] _cheapPerChunk;
    private TileArea _cheapArea = TileArea.None;
    private bool _cheapAreaDirty;

    // Straight path cost and step duration (permille) per RoadClass value.
    private readonly int[] _costs;
    private readonly int[] _permille;

    public WalkLayer(RoadsBalanceDef roads, int size, Action<int> touch)
    {
        _roads = roads;
        _size = size;
        _chunksPerSide = ChunkCoord.CountPerSide(size);
        _chunks = new Chunk?[_chunksPerSide * _chunksPerSide];
        _cheapPerChunk = new int[_chunks.Length];
        _touch = touch;
        _permille = [PermilleBase, roads.TrailCostPermille, roads.RoadCostPermille];
        _costs = [.. _permille.Select(p => Math.Max(PathCosts.MinTileCost, PathCosts.Straight * p / PermilleBase))];
    }

    /// <summary>Thousandths: costs and step durations are given relative to plain ground (1000).</summary>
    public const int PermilleBase = 1000;

    // log2 of the chunk side (32) and the mask of a local coordinate.
    private const int ChunkShift = 5;
    private const int ChunkMaskBits = ChunkCoord.Size - 1;

    /// <summary>The tick the layer reads at (the simulation's last executed tick).</summary>
    public long Tick { get; private set; }

    /// <summary>Tiles that are trails now.</summary>
    public int Trails { get; private set; }

    /// <summary>Tiles that are roads now.</summary>
    public int Roads { get; private set; }

    /// <summary>Tiles kept in memory (walked and not compacted yet, including faded ones).</summary>
    public int Entries { get; private set; }

    /// <summary>Lower bound of <see cref="Cost"/> over all tiles now: the road cost while a road exists, else the trail cost while a trail exists, else plain ground.</summary>
    public int MinCost => _costs[Roads > 0 ? (int)RoadClass.Road : Trails > 0 ? (int)RoadClass.Trail : (int)RoadClass.None];

    /// <summary>True while no tile is a trail or a road (every tile costs plain ground).</summary>
    public bool IsUniform => Trails == 0 && Roads == 0;

    /// <summary>
    /// The rectangle of whole chunks around every chunk with a trail or a road (<see cref="ITileCost.CheapArea"/>):
    /// every tile outside it is plain ground. Rebuilt from the per-chunk counts only after a chunk got its first or lost
    /// its last trail or road.
    /// </summary>
    public TileArea CheapArea
    {
        get
        {
            if (_cheapAreaDirty)
            {
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                for (int i = 0; i < _cheapPerChunk.Length; i++)
                {
                    if (_cheapPerChunk[i] > 0)
                    {
                        (int cx, int cy) = (i % _chunksPerSide, i / _chunksPerSide);
                        (minX, minY, maxX, maxY) = (Math.Min(minX, cx), Math.Min(minY, cy), Math.Max(maxX, cx), Math.Max(maxY, cy));
                    }
                }

                _cheapArea = minX > maxX
                    ? TileArea.None
                    : new TileArea(minX * ChunkCoord.Size, minY * ChunkCoord.Size, ((maxX + 1) * ChunkCoord.Size) - 1, ((maxY + 1) * ChunkCoord.Size) - 1);
                _cheapAreaDirty = false;
            }

            return _cheapArea;
        }
    }

    /// <summary>Class of tile (<paramref name="x"/>, <paramref name="y"/>) (on the map) now.</summary>
    public RoadClass ClassAt(int x, int y) => IsUniform || _chunks[ChunkOf(x, y)] is not { } chunk
        ? RoadClass.None
        : (RoadClass)chunk.Classes[ChunkMask.LocalIndex(x, y)];

    /// <summary>Straight path cost of entering the tile (on the map) now; the path search's hot path, so shifts instead of divisions.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int Cost(int x, int y)
    {
        Chunk? chunk = _chunks[((y >> ChunkShift) * _chunksPerSide) + (x >> ChunkShift)];
        return chunk is null ? PathCosts.Straight : _costs[chunk.Classes[((y & ChunkMaskBits) << ChunkShift) | (x & ChunkMaskBits)]];
    }

    /// <summary>Duration of a step onto the tile in thousandths of a step on plain ground.</summary>
    public int StepPermille(int x, int y) => _permille[(int)ClassAt(x, y)];

    /// <summary>Walk counter of the tile now (faded).</summary>
    public int CounterAt(int x, int y)
    {
        if (_chunks[ChunkOf(x, y)] is not { } chunk)
        {
            return 0;
        }

        int local = ChunkMask.LocalIndex(x, y);
        return ChunkMask.IsSet(chunk.Mask, local) ? Faded(chunk.Counters[local], chunk.Ticks[local], Tick) : 0;
    }

    /// <summary>
    /// Moves the layer's clock to <paramref name="tick"/> (not backwards) and lets every class fall whose fall tick has
    /// come; each fall marks its chunk changed.
    /// </summary>
    public void AdvanceTo(long tick)
    {
        Tick = tick;
        while (_falls.Count > 0 && _falls.Min.Due <= tick)
        {
            (long Due, int Tile) due = _falls.Min;
            _falls.Remove(due);
            int tile = due.Tile;
            int x = tile % _size;
            int y = tile / _size;
            int chunkIndex = ChunkOf(x, y);
            Chunk chunk = _chunks[chunkIndex]!;
            int local = ChunkMask.LocalIndex(x, y);
            var before = (RoadClass)chunk.Classes[local];
            RoadClass after = Fall(before, Faded(chunk.Counters[local], chunk.Ticks[local], tick));
            SetClass(chunk, chunkIndex, local, before, after);
            _touch(chunkIndex);
            AddFall(chunk, local, tile);
        }
    }

    /// <summary>A walker stepped onto tile (<paramref name="x"/>, <paramref name="y"/>) at <see cref="Tick"/>.</summary>
    public void Step(int x, int y)
    {
        int chunkIndex = ChunkOf(x, y);
        Chunk chunk = _chunks[chunkIndex] ??= new Chunk();
        int local = ChunkMask.LocalIndex(x, y);
        int tile = (y * _size) + x;
        if (!ChunkMask.IsSet(chunk.Mask, local))
        {
            ChunkMask.Set(chunk.Mask, local);
            chunk.Count++;
            Entries++;
        }

        var before = (RoadClass)chunk.Classes[local];
        if (before != RoadClass.None)
        {
            _falls.Remove((FallTick(chunk.Counters[local], chunk.Ticks[local], before), tile));
        }

        int faded = Faded(chunk.Counters[local], chunk.Ticks[local], Tick);
        int counter = Math.Min(_roads.MaxCounter, faded + _roads.StepIncrement);
        RoadClass after = Fall(before, faded);
        if (counter >= _roads.RoadThreshold)
        {
            after = RoadClass.Road;
        }
        else if (counter >= _roads.TrailThreshold && after == RoadClass.None)
        {
            after = RoadClass.Trail;
        }

        chunk.Counters[local] = (byte)counter;
        chunk.Ticks[local] = Tick;
        SetClass(chunk, chunkIndex, local, before, after);
        if (after != before)
        {
            _touch(chunkIndex);
        }

        AddFall(chunk, local, tile);
    }

    /// <summary>Drops the tiles whose counter faded to 0 (plain ground again) and the chunks left empty.</summary>
    public void Compact()
    {
        for (int i = 0; i < _chunks.Length; i++)
        {
            if (_chunks[i] is not { } chunk)
            {
                continue;
            }

            for (int w = 0; w < ChunkMask.Words; w++)
            {
                ulong word = chunk.Mask[w];
                while (word != 0)
                {
                    int local = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    if (chunk.Classes[local] == (byte)RoadClass.None && Faded(chunk.Counters[local], chunk.Ticks[local], Tick) == 0)
                    {
                        ChunkMask.Clear(chunk.Mask, local);
                        chunk.Counters[local] = 0;
                        chunk.Ticks[local] = 0;
                        chunk.Count--;
                        Entries--;
                    }
                }
            }

            if (chunk.Count == 0)
            {
                _chunks[i] = null;
            }
        }
    }

    /// <summary>Writes the number of chunks with a live tile, then per chunk its index, the live mask and per tile counter, tick and class.</summary>
    public void Write(StateWriter writer)
    {
        var live = new List<(int Index, ulong[] Mask)>();
        for (int i = 0; i < _chunks.Length; i++)
        {
            if (LiveMask(i) is { } mask)
            {
                live.Add((i, mask));
            }
        }

        writer.WriteInt32(live.Count);
        foreach ((int index, ulong[] mask) in live)
        {
            Chunk chunk = _chunks[index]!;
            writer.WriteInt32(index);
            ChunkMask.Write(writer, mask);
            ChunkMask.ForEachSet(mask, local =>
            {
                writer.WriteInt32(chunk.Counters[local]);
                writer.WriteInt64(chunk.Ticks[local]);
                writer.WriteByte(chunk.Classes[local]);
            });
        }
    }

    /// <summary>The live tiles (counter above 0 now) for saving, in chunk order.</summary>
    public List<WalkChunkState> Capture()
    {
        var chunks = new List<WalkChunkState>();
        for (int i = 0; i < _chunks.Length; i++)
        {
            if (LiveMask(i) is not { } mask)
            {
                continue;
            }

            Chunk chunk = _chunks[i]!;
            var counters = new List<int>();
            var ticks = new List<long>();
            var classes = new List<RoadClass>();
            ChunkMask.ForEachSet(mask, local =>
            {
                counters.Add(chunk.Counters[local]);
                ticks.Add(chunk.Ticks[local]);
                classes.Add((RoadClass)chunk.Classes[local]);
            });
            chunks.Add(new WalkChunkState(i % _chunksPerSide, i / _chunksPerSide, mask, counters, ticks, classes));
        }

        return chunks;
    }

    /// <summary>
    /// Fills the empty layer from saved chunks read at <see cref="Tick"/>: counters 1..max, ticks 0..now, a counter that
    /// has not faded to 0, and a class the faded counter keeps and the last step's counter implies.
    /// </summary>
    /// <exception cref="ArgumentException">A chunk is inconsistent; the caller clears the layer.</exception>
    public void Load(IReadOnlyList<WalkChunkState> chunks)
    {
        int previous = -1;
        foreach (WalkChunkState? saved in chunks)
        {
            ArgumentNullException.ThrowIfNull(saved, nameof(chunks));
            int values = saved.Counters.Count;
            if (saved.Ticks.Count != values || saved.Classes.Count != values)
            {
                throw new ArgumentException($"Walk chunk ({saved.ChunkX}, {saved.ChunkY}) has different numbers of counters, ticks and classes.", nameof(chunks));
            }

            int chunkIndex = ChunkMask.Validate(saved.ChunkX, saved.ChunkY, saved.Mask, values, _chunksPerSide, ref previous);
            var chunk = new Chunk();
            saved.Mask.ToArray().CopyTo(chunk.Mask, 0);
            int next = 0;
            ChunkMask.ForEachSet(chunk.Mask, local =>
            {
                int counter = saved.Counters[next];
                long tick = saved.Ticks[next];
                RoadClass kind = saved.Classes[next++];
                CheckSaved(saved, local, counter, tick, kind);
                chunk.Counters[local] = (byte)counter;
                chunk.Ticks[local] = tick;
                SetClass(chunk, chunkIndex, local, RoadClass.None, kind);
            });
            chunk.Count = values;
            _chunks[chunkIndex] = chunk;
            Entries += values;
            ChunkMask.ForEachSet(chunk.Mask, local => AddFall(chunk, local,
                (((saved.ChunkY * ChunkCoord.Size) + (local / ChunkCoord.Size)) * _size) + (saved.ChunkX * ChunkCoord.Size) + (local % ChunkCoord.Size)));
        }
    }

    /// <summary>Forgets everything (a failed load); the clock stays.</summary>
    public void Clear()
    {
        Array.Clear(_chunks);
        Array.Clear(_cheapPerChunk);
        _cheapAreaDirty = true;
        _falls.Clear();
        Trails = 0;
        Roads = 0;
        Entries = 0;
    }

    private void CheckSaved(WalkChunkState saved, int local, int counter, long tick, RoadClass kind)
    {
        int faded = counter >= 1 && counter <= _roads.MaxCounter && tick >= 0 && tick <= Tick ? Faded(counter, tick, Tick) : 0;
        bool classOk = Enum.IsDefined(kind) && Fall(kind, faded) == kind
            && (counter < _roads.RoadThreshold || kind == Fall(RoadClass.Road, faded))
            && (counter < _roads.TrailThreshold || kind >= Fall(RoadClass.Trail, faded));
        if (faded == 0 || !classOk)
        {
            throw new ArgumentException(
                $"Walk chunk ({saved.ChunkX}, {saved.ChunkY}) tile {local}: counter {counter} at tick {tick} with class {kind} is not a live walk at tick {Tick}.",
                nameof(saved));
        }
    }

    // The mask of the chunk's tiles whose counter is above 0 now; null when there is none.
    private ulong[]? LiveMask(int chunkIndex)
    {
        if (_chunks[chunkIndex] is not { } chunk)
        {
            return null;
        }

        var mask = (ulong[])chunk.Mask.Clone();
        bool any = false;
        for (int local = 0; local < ChunkMask.TilesPerChunk; local++)
        {
            if (ChunkMask.IsSet(mask, local))
            {
                if (Faded(chunk.Counters[local], chunk.Ticks[local], Tick) > 0)
                {
                    any = true;
                }
                else
                {
                    ChunkMask.Clear(mask, local);
                }
            }
        }

        return any ? mask : null;
    }

    private void SetClass(Chunk chunk, int chunkIndex, int local, RoadClass before, RoadClass after)
    {
        Trails += (after == RoadClass.Trail ? 1 : 0) - (before == RoadClass.Trail ? 1 : 0);
        Roads += (after == RoadClass.Road ? 1 : 0) - (before == RoadClass.Road ? 1 : 0);
        int cheap = (after == RoadClass.None ? 0 : 1) - (before == RoadClass.None ? 0 : 1);
        if (cheap != 0)
        {
            _cheapPerChunk[chunkIndex] += cheap;
            _cheapAreaDirty |= _cheapPerChunk[chunkIndex] == (cheap > 0 ? 1 : 0);
        }

        chunk.Classes[local] = (byte)after;
    }

    private void AddFall(Chunk chunk, int local, int tile)
    {
        var kind = (RoadClass)chunk.Classes[local];
        if (kind != RoadClass.None)
        {
            _falls.Add((FallTick(chunk.Counters[local], chunk.Ticks[local], kind), tile));
        }
    }

    // The tick at which a tile of `kind` whose counter was `counter` at `tick` falls to a lower class.
    private long FallTick(int counter, long tick, RoadClass kind)
    {
        int keep = kind == RoadClass.Road ? _roads.RoadKeepThreshold : _roads.TrailKeepThreshold;
        return tick + ((long)(counter - keep + 1) * _roads.DecayEveryTicks);
    }

    // The class a tile of `kind` keeps with `counter`: a road below the road keep threshold is a trail, a trail below the trail keep threshold plain ground.
    private RoadClass Fall(RoadClass kind, int counter)
    {
        if (kind == RoadClass.Road && counter < _roads.RoadKeepThreshold)
        {
            kind = RoadClass.Trail;
        }

        return kind == RoadClass.Trail && counter < _roads.TrailKeepThreshold ? RoadClass.None : kind;
    }

    private int Faded(int counter, long since, long now) => (int)Math.Max(0, counter - ((now - since) / _roads.DecayEveryTicks));

    private int ChunkOf(int x, int y) => ((y / ChunkCoord.Size) * _chunksPerSide) + (x / ChunkCoord.Size);

    /// <summary>One chunk: the mask and per tile the counter after the last step, its tick and the current class.</summary>
    private sealed class Chunk
    {
        public ulong[] Mask { get; } = new ulong[ChunkMask.Words];

        public byte[] Counters { get; } = new byte[ChunkMask.TilesPerChunk];

        public long[] Ticks { get; } = new long[ChunkMask.TilesPerChunk];

        public byte[] Classes { get; } = new byte[ChunkMask.TilesPerChunk];

        public int Count { get; set; }
    }
}
