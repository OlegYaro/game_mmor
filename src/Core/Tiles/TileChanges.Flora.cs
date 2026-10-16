using Core.Content;
using Core.Determinism;
using Core.WorldGen;

namespace Core.Tiles;

/// <summary>Flora of <see cref="TileChanges"/>: stumps, saplings and grown trees (FR-WORLD-04, gdd-issues B4).</summary>
/// <remarks>
/// <para><b>Felling.</b> A tree whose stock runs out leaves a stump stamped with the tick it was felled
/// (<see cref="TakeResource"/>). <b>Planting.</b> A sapling goes on a free tile (<see cref="CanPlant"/>), stamped with
/// the tick it was planted.</para>
/// <para><b>Growing</b> happens once a game day, at its first tick, in one pass over the tiles with flora
/// (<see cref="GrowFlora"/>), never per tick and never on reading a tile. A sapling becomes a tree once
/// <see cref="RegrowthBalanceDef.SaplingGrowDays"/> days have passed since it was planted. A stump becomes a tree once
/// <see cref="RegrowthBalanceDef.StumpRegrowDaysFar"/> days have passed since the felling, or already after
/// <see cref="RegrowthBalanceDef.StumpRegrowDaysNearTree"/> days when a living tree stands within
/// <see cref="RegrowthBalanceDef.NearTreeRadius"/> (Chebyshev) at that day's pass. Trees that grew back count as living
/// trees, so a felled grove grows back from its edge inwards, one ring of the radius a day, and an isolated clearing
/// after the far days. The pass first decides every tile against the state before the pass and only then applies the
/// changes, so neither the order of the tiles nor anything read earlier changes the result: the result depends only
/// on the saved state and the tick, which keeps replays and save/load identical (ADR 0005).</para>
/// <para><b>Which tree.</b> On a tile whose generated node is a tree, the tree comes back as the map's tree with its
/// generated stock (the tile is unchanged again). Elsewhere a grown tree is flora with a stock between the world's
/// <see cref="TreeParams.StockMin"/> and <see cref="TreeParams.StockMax"/> from a hash of the seed and the tile, so it is
/// the same whatever happened before.</para>
/// </remarks>
public sealed partial class TileChanges
{
    // Salt of the grown-tree stock hash, so it differs from the generator's per-tile values.
    private const ulong GrownStockSalt = 0x7EE5_70C4_0000_0004UL;

    /// <summary>Number of tiles with flora (stumps, saplings, grown trees).</summary>
    public int FloraTiles => _flora.Count;

    /// <summary>Flora of tile (<paramref name="x"/>, <paramref name="y"/>): a stump, a sapling, a tree grown off the map's trees, or none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public FloraState GetFlora(int x, int y) => _flora.StateAt(ChunkIndex(x, y), LocalIndex(x, y));

    /// <summary>Tick the tile took its flora state (felled, planted, grown); -1 without flora.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public long GetFloraTick(int x, int y)
    {
        int chunkIndex = ChunkIndex(x, y);
        int local = LocalIndex(x, y);
        return _flora.StateAt(chunkIndex, local) == FloraState.None ? -1 : _flora[chunkIndex]!.Ticks[local];
    }

    /// <summary>
    /// True when a sapling may be planted on tile (<paramref name="x"/>, <paramref name="y"/>): land without water or a
    /// river, no node (<see cref="GetEffectiveResource"/>), no flora and no building.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public bool CanPlant(int x, int y) =>
        GetEffectiveResource(x, y) == ResourceKind.None && GetFlora(x, y) == FloraState.None && !IsOccupied(x, y)
        && !_map.Terrain.IsWater(x, y) && !_map.Terrain.IsRiver(x, y);

    /// <summary>Adds the index (<c>y * Size + x</c>) of every tile whose flora is <paramref name="state"/> to <paramref name="tiles"/>, in chunk order.</summary>
    internal void CollectFlora(FloraState state, List<int> tiles)
    {
        for (int chunkIndex = 0; chunkIndex < _flora.ChunkCount && _flora.Count > 0; chunkIndex++)
        {
            if (_flora[chunkIndex] is not { } chunk)
            {
                continue;
            }

            for (int local = 0; local < TilesPerChunk; local++)
            {
                if (IsSet(chunk.Mask, local) && chunk.States[local] == (byte)state)
                {
                    (int x, int y) = TileOf(chunkIndex, local);
                    tiles.Add((y * Size) + x);
                }
            }
        }
    }

    /// <summary>Plants a sapling on tile (<paramref name="x"/>, <paramref name="y"/>) at <see cref="Tick"/>; false (and no change) when <see cref="CanPlant"/> is false.</summary>
    internal bool Plant(int x, int y)
    {
        if (!CanPlant(x, y))
        {
            return false;
        }

        int chunkIndex = ChunkIndex(x, y);
        _flora.Set(chunkIndex, LocalIndex(x, y), FloraState.Sapling, Tick, 0);
        Touch(chunkIndex);
        return true;
    }

    /// <summary>
    /// The daily growth pass (see the remarks): saplings and stumps whose time has come become trees. Called at the first
    /// tick of a game day; <paramref name="ticksPerDay"/> converts the days of <paramref name="regrowth"/> to ticks.
    /// </summary>
    /// <returns>How many tiles became trees.</returns>
    internal int GrowFlora(RegrowthBalanceDef regrowth, TreeParams trees, int ticksPerDay)
    {
        ArgumentNullException.ThrowIfNull(regrowth);
        ArgumentNullException.ThrowIfNull(trees);
        if (_flora.Count == 0)
        {
            return 0;
        }

        long saplingTicks = (long)regrowth.SaplingGrowDays * ticksPerDay;
        long nearTicks = (long)regrowth.StumpRegrowDaysNearTree * ticksPerDay;
        long farTicks = (long)regrowth.StumpRegrowDaysFar * ticksPerDay;
        var grown = new List<(int Chunk, int Local)>();
        for (int chunkIndex = 0; chunkIndex < _flora.ChunkCount; chunkIndex++)
        {
            if (_flora[chunkIndex] is not { } chunk)
            {
                continue;
            }

            for (int local = 0; local < TilesPerChunk; local++)
            {
                if (!IsSet(chunk.Mask, local))
                {
                    continue;
                }

                long age = Tick - chunk.Ticks[local];
                bool grows = (FloraState)chunk.States[local] switch
                {
                    FloraState.Sapling => age >= saplingTicks,
                    FloraState.Stump => age >= farTicks || (age >= nearTicks && HasTreeNear(chunkIndex, local, regrowth.NearTreeRadius)),
                    _ => false,
                };
                if (grows)
                {
                    grown.Add((chunkIndex, local));
                }
            }
        }

        foreach ((int chunkIndex, int local) in grown)
        {
            GrowTree(chunkIndex, local, trees);
        }

        return grown.Count;
    }

    // A grown tree gives wood like any other; at 0 it leaves a stump.
    private int TakeFromGrownTree(int chunkIndex, int local, int amount)
    {
        FloraLayer.Chunk chunk = _flora[chunkIndex]!;
        int stock = chunk.Stocks[local];
        int taken = Math.Min(stock, amount);
        if (taken == 0)
        {
            return 0;
        }

        if (stock == taken)
        {
            _flora.Set(chunkIndex, local, FloraState.Stump, Tick, 0);
        }
        else
        {
            _flora.Set(chunkIndex, local, FloraState.Tree, chunk.Ticks[local], stock - taken);
        }

        Touch(chunkIndex);
        return taken;
    }

    // A living tree (a map tree with stock left or a grown one) within `radius` of the tile, the tile itself aside.
    private bool HasTreeNear(int chunkIndex, int local, int radius)
    {
        (int x, int y) = TileOf(chunkIndex, local);
        for (int ny = Math.Max(0, y - radius); ny <= Math.Min(Size - 1, y + radius); ny++)
        {
            for (int nx = Math.Max(0, x - radius); nx <= Math.Min(Size - 1, x + radius); nx++)
            {
                if ((nx != x || ny != y) && GetEffectiveResource(nx, ny) == ResourceKind.Tree)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The stump or sapling becomes a tree: the map's own tree where the map has one, a grown tree elsewhere.
    private void GrowTree(int chunkIndex, int local, TreeParams trees)
    {
        (int x, int y) = TileOf(chunkIndex, local);
        if (_map.GetResource(x, y) == ResourceKind.Tree)
        {
            _flora.Remove(chunkIndex, local);
            ResetResource(chunkIndex, local);
        }
        else
        {
            _flora.Set(chunkIndex, local, FloraState.Tree, Tick, GrownStock(x, y, trees));
        }

        Touch(chunkIndex);
    }

    // Removes the resource layer's change of a tile: the map's node and stock are back.
    private void ResetResource(int chunkIndex, int local)
    {
        if (_resources[chunkIndex] is not { } chunk || !IsSet(chunk.Mask, local))
        {
            return;
        }

        ChunkMask.Clear(chunk.Mask, local);
        chunk.Amounts[local] = 0;
        ChangedResourceTiles--;
        if (ChunkMask.Count(chunk.Mask) == 0)
        {
            _resources[chunkIndex] = null;
        }
    }

    // Stock of a tree grown at (x, y): StockMin..StockMax from a hash of the seed and the tile.
    private int GrownStock(int x, int y, TreeParams trees)
    {
        ulong hash = SplitMix64.Mix(_map.Seed ^ GrownStockSalt ^ SplitMix64.Mix(((ulong)(uint)((y * Size) + x) + 1) * SplitMix64.Gamma));
        int span = trees.StockMax - trees.StockMin + 1;
        return trees.StockMin + (int)(hash % (ulong)span);
    }

    private (int X, int Y) TileOf(int chunkIndex, int local) =>
        (((chunkIndex % ChunksPerSide) * ChunkCoord.Size) + (local % ChunkCoord.Size), ((chunkIndex / ChunksPerSide) * ChunkCoord.Size) + (local / ChunkCoord.Size));

    private void WriteFlora(StateWriter writer)
    {
        int chunks = 0;
        for (int i = 0; i < _flora.ChunkCount; i++)
        {
            chunks += _flora[i] is null ? 0 : 1;
        }

        writer.WriteInt32(chunks);
        for (int i = 0; i < _flora.ChunkCount; i++)
        {
            if (_flora[i] is { } chunk)
            {
                writer.WriteInt32(i);
                WriteMask(writer, chunk.Mask);
                ForEachSet(chunk.Mask, local =>
                {
                    writer.WriteByte(chunk.States[local]);
                    writer.WriteInt64(chunk.Ticks[local]);
                    writer.WriteInt32(chunk.Stocks[local]);
                });
            }
        }
    }

    private List<FloraChunkState> CaptureFlora()
    {
        var chunks = new List<FloraChunkState>();
        for (int i = 0; i < _flora.ChunkCount; i++)
        {
            if (_flora[i] is not { } chunk)
            {
                continue;
            }

            var states = new List<FloraState>();
            var ticks = new List<long>();
            var stocks = new List<int>();
            ForEachSet(chunk.Mask, local =>
            {
                states.Add((FloraState)chunk.States[local]);
                ticks.Add(chunk.Ticks[local]);
                stocks.Add(chunk.Stocks[local]);
            });
            chunks.Add(new FloraChunkState(i % ChunksPerSide, i / ChunksPerSide, (ulong[])chunk.Mask.Clone(), states, ticks, stocks));
        }

        return chunks;
    }

    /// <summary>
    /// Loads saved flora after the resources and the occupancy: a stump, a sapling or a tree, stamped no later than
    /// <see cref="Tick"/>, on land that is not occupied and whose map node is absent or used up; a stump or a sapling
    /// has no stock, a grown tree 1..65535 and only where the map has no tree (there the map's tree grows back).
    /// </summary>
    private void LoadFlora(IReadOnlyList<FloraChunkState> chunks)
    {
        int previous = -1;
        foreach (FloraChunkState? saved in chunks)
        {
            ArgumentNullException.ThrowIfNull(saved, nameof(chunks));
            int values = saved.States.Count;
            if (saved.Ticks.Count != values || saved.Stocks.Count != values)
            {
                throw new ArgumentException($"Flora chunk ({saved.ChunkX}, {saved.ChunkY}) has different numbers of states, ticks and stocks.", nameof(chunks));
            }

            int chunkIndex = ChunkMask.Validate(saved.ChunkX, saved.ChunkY, saved.Mask, values, ChunksPerSide, ref previous);
            int next = 0;
            ForEachSet(saved.Mask.ToArray(), local =>
            {
                FloraState state = saved.States[next];
                long tick = saved.Ticks[next];
                int stock = saved.Stocks[next++];
                (int x, int y) = TileOf(chunkIndex, local);
                bool stockOk = state == FloraState.Tree ? stock is >= 1 and <= ushort.MaxValue && _map.GetResource(x, y) != ResourceKind.Tree : stock == 0;
                bool placeOk = !IsOccupied(x, y) && !_map.Terrain.IsWater(x, y) && !_map.Terrain.IsRiver(x, y)
                    && (_map.GetResource(x, y) == ResourceKind.None || MapNodeAmount(x, y) == 0);
                if (state is not (FloraState.Stump or FloraState.Sapling or FloraState.Tree) || tick < 0 || tick > Tick || !stockOk || !placeOk)
                {
                    throw new ArgumentException($"Tile {x},{y}: flora {state} from tick {tick} with stock {stock} is not valid there at tick {Tick}.", nameof(chunks));
                }

                _flora.Set(chunkIndex, local, state, tick, stock);
            });
        }
    }
}
