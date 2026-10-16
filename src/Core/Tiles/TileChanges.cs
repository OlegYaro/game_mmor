using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Pathfinding;
using Core.WorldGen;

namespace Core.Tiles;

/// <summary>
/// Saved form of one changed 32×32 chunk of the resource layer (ADR 0007): a 1024-bit mask of the changed tiles and
/// their remaining stocks, packed in mask order (local index <c>ly * 32 + lx</c>, ascending).
/// </summary>
/// <param name="ChunkX">Chunk column.</param>
/// <param name="ChunkY">Chunk row.</param>
/// <param name="Mask">16 words of the changed-tile mask, least significant bit first.</param>
/// <param name="Amounts">Remaining stock of each changed tile, in mask order; 0 means the node is gone.</param>
public sealed record ResourceChunkState(int ChunkX, int ChunkY, IReadOnlyList<ulong> Mask, IReadOnlyList<int> Amounts);

/// <summary>
/// Saved form of one 32×32 chunk of the occupancy layer: a 1024-bit mask of the occupied tiles and their owners in
/// mask order.
/// </summary>
/// <param name="ChunkX">Chunk column.</param>
/// <param name="ChunkY">Chunk row.</param>
/// <param name="Mask">16 words of the occupied-tile mask, least significant bit first.</param>
/// <param name="Owners">Owner entity of each occupied tile, in mask order.</param>
public sealed record OccupancyChunkState(int ChunkX, int ChunkY, IReadOnlyList<ulong> Mask, IReadOnlyList<EntityId> Owners);

/// <summary>Saved form of <see cref="TileChanges"/>: the non-empty chunks of every layer in ascending chunk index order.</summary>
/// <param name="Resources">Changed chunks of the resource layer.</param>
/// <param name="Occupancy">Chunks of the occupancy layer with at least one occupied tile.</param>
/// <param name="Flora">Chunks of the flora layer (stumps, saplings, grown trees).</param>
/// <param name="Walks">Chunks of the walk layer with a tile whose counter is above 0 at the saved tick.</param>
public sealed record TileChangesState(IReadOnlyList<ResourceChunkState> Resources, IReadOnlyList<OccupancyChunkState> Occupancy,
    IReadOnlyList<FloraChunkState> Flora, IReadOnlyList<WalkChunkState> Walks)
{
    /// <summary>No changes.</summary>
    public static TileChangesState Empty { get; } = new([], [], [], []);
}

/// <summary>
/// The difference between the live world and the immutable generated <see cref="WorldMap"/> (FR-WORLD-05, ADR 0007):
/// sparse per 32×32 chunk, four layers (resources, occupancy, flora, walks). The map and its hash never change.
/// </summary>
/// <remarks>
/// <para><b>Resources.</b> A changed tile stores the remaining stock of its node; the kind still comes from the map,
/// and a stock of 0 means the node is gone (<see cref="GetEffectiveResource"/> returns <see cref="ResourceKind.None"/>).
/// A tile is marked only when its stock actually drops, so a changed tile always differs from the map.</para>
/// <para><b>Veins.</b> <see cref="TakeResource"/> works on every kind, vein tiles included: mining a vein tile simply
/// lowers that tile's stock. <see cref="Vein.Stock"/> stays the generated total (the map is immutable); the stock left
/// in a vein is the sum of <see cref="GetEffectiveAmount"/> over its tiles. In M2 nothing mines veins: the hero has no
/// gathering entry for them (mines come in M3), so the generated sums are untouched.</para>
/// <para><b>Occupancy.</b> A tile can be occupied by one entity (a building, wave 3). Occupied tiles are blocked for
/// movement: this class is the <see cref="ITileBlocker"/> of the game's <see cref="PassabilityGrid"/>, and every change
/// is reported to the attached <see cref="Pathfinder"/> in the same call.</para>
/// <para><b>Memory.</b> A changed chunk holds a dense array (2 KiB of stocks, 8 KiB of owners) plus the 128-byte mask;
/// untouched chunks cost one null reference. Saves and the hash hold only the mask and the changed values.</para>
/// <para><b>Flora</b> (FR-WORLD-04, B4; <c>TileChanges.Flora.cs</c>): stumps of felled trees, planted saplings and trees
/// grown where the map has none. Flora decides the tile's node: a stump or a sapling is no node, a grown tree is a tree
/// with its own stock. Building on a tile clears its flora.</para>
/// <para><b>Walks</b> (FR-WORLD-15; <c>TileChanges.Walks.cs</c>, <see cref="WalkLayer"/>): walk counters with lazy fading,
/// the trail and road classes, and the path costs (this class is also the <see cref="ITileCost"/> of the game's grid).</para>
/// <para><b>Clock.</b> Fading and the tick stamps of flora read <see cref="Tick"/>, the simulation's last executed tick,
/// which the simulation advances at the start of every tick (<see cref="AdvanceTo"/>).</para>
/// <para>Renderer notes (not part of the state): <see cref="Version"/> grows on every change and
/// <see cref="GetChunkVersion"/> gives the version of a chunk's last change, like the fog. A chunk's version also grows
/// when a tile of it changes its flora or its road class, also when a road fades into a trail on its own.</para>
/// </remarks>
public sealed partial class TileChanges : ITileBlocker, ITileCost, IStateHashable
{
    /// <summary>Words of a chunk mask (1024 bits).</summary>
    public const int MaskWords = ChunkMask.Words;

    private const int TilesPerChunk = ChunkMask.TilesPerChunk;

    private readonly WorldMap _map;
    private readonly ResourceChunk?[] _resources;
    private readonly OccupancyChunk?[] _occupancy;
    private readonly FloraLayer _flora;
    private readonly WalkLayer _walks;
    private readonly long[] _chunkVersions;
    private Pathfinder? _pathfinder;

    /// <summary>Creates an empty layer (no changes) over <paramref name="map"/>, with the trail and road numbers of <paramref name="roads"/>.</summary>
    public TileChanges(WorldMap map, RoadsBalanceDef roads)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(roads);
        _map = map;
        ChunksPerSide = ChunkCoord.CountPerSide(map.Size);
        int chunks = ChunksPerSide * ChunksPerSide;
        _resources = new ResourceChunk?[chunks];
        _occupancy = new OccupancyChunk?[chunks];
        _flora = new FloraLayer(chunks);
        _chunkVersions = new long[chunks];
        _walks = new WalkLayer(roads, map.Size, Touch);
    }

    /// <summary>The tick lazy values are read at and changes are stamped with: the simulation's last executed tick.</summary>
    public long Tick => _walks.Tick;

    /// <summary>Side of the map in tiles.</summary>
    public int Size => _map.Size;

    /// <summary>Chunks along one side; chunk indices are <c>cy * ChunksPerSide + cx</c>.</summary>
    public int ChunksPerSide { get; }

    /// <summary>Number of tiles whose resource stock differs from the map.</summary>
    public int ChangedResourceTiles { get; private set; }

    /// <summary>Number of occupied tiles.</summary>
    public int OccupiedTiles { get; private set; }

    /// <summary>Grows on every change (presentation only, not part of the state).</summary>
    public long Version { get; private set; }

    /// <summary>Version of the last change of chunk (<paramref name="cx"/>, <paramref name="cy"/>) (presentation only).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The chunk is outside the map.</exception>
    public long GetChunkVersion(int cx, int cy)
    {
        if ((uint)cx >= (uint)ChunksPerSide || (uint)cy >= (uint)ChunksPerSide)
        {
            throw new ArgumentOutOfRangeException(nameof(cx), $"Chunk ({cx}, {cy}) is outside a {ChunksPerSide}x{ChunksPerSide} chunk map.");
        }

        return _chunkVersions[(cy * ChunksPerSide) + cx];
    }

    /// <summary>Resource kind of tile (<paramref name="x"/>, <paramref name="y"/>) now: the map's, or none once its stock is used up.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    /// <remarks>Flora decides first: a stump or a sapling is no node, a grown tree is a tree (<see cref="GetFlora"/>).</remarks>
    public ResourceKind GetEffectiveResource(int x, int y)
    {
        FloraState flora = _flora.StateAt(ChunkIndex(x, y), LocalIndex(x, y));
        if (flora != FloraState.None)
        {
            return flora == FloraState.Tree ? ResourceKind.Tree : ResourceKind.None;
        }

        ResourceKind kind = _map.GetResource(x, y);
        return kind == ResourceKind.None || MapNodeAmount(x, y) > 0 ? kind : ResourceKind.None;
    }

    /// <summary>Stock of tile (<paramref name="x"/>, <paramref name="y"/>) now: a grown tree's, 0 under a stump or a sapling, else the changed value or the map's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public int GetEffectiveAmount(int x, int y)
    {
        int chunkIndex = ChunkIndex(x, y);
        int local = LocalIndex(x, y);
        FloraState flora = _flora.StateAt(chunkIndex, local);
        if (flora == FloraState.None)
        {
            return MapNodeAmount(x, y);
        }

        return flora == FloraState.Tree ? _flora[chunkIndex]!.Stocks[local] : 0;
    }

    /// <summary>True when the stock of tile (<paramref name="x"/>, <paramref name="y"/>) differs from the map.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public bool IsResourceChanged(int x, int y)
    {
        CheckTile(x, y);
        ResourceChunk? chunk = _resources[ChunkIndex(x, y)];
        return chunk is not null && IsSet(chunk.Mask, LocalIndex(x, y));
    }

    /// <summary>
    /// Takes up to <paramref name="amount"/> from the node on tile (<paramref name="x"/>, <paramref name="y"/>) and
    /// returns how much was taken (less when the stock is smaller, 0 without a node). At stock 0 the node is gone; a
    /// tree leaves a stump (stamped with <see cref="Tick"/>) that grows back (FR-WORLD-04).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map or the amount is negative.</exception>
    public int TakeResource(int x, int y, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        int chunkIndex = ChunkIndex(x, y);
        int local = LocalIndex(x, y);
        FloraState flora = _flora.StateAt(chunkIndex, local);
        if (flora != FloraState.None)
        {
            return flora == FloraState.Tree ? TakeFromGrownTree(chunkIndex, local, amount) : 0;
        }

        int stock = MapNodeAmount(x, y);
        int taken = Math.Min(stock, amount);
        ResourceKind kind = _map.GetResource(x, y);
        if (taken == 0 || kind == ResourceKind.None)
        {
            return 0;
        }

        ResourceChunk chunk = _resources[chunkIndex] ??= new ResourceChunk();
        if (!IsSet(chunk.Mask, local))
        {
            Set(chunk.Mask, local);
            ChangedResourceTiles++;
        }

        chunk.Amounts[local] = (ushort)(stock - taken);
        if (stock == taken && kind == ResourceKind.Tree)
        {
            _flora.Set(chunkIndex, local, FloraState.Stump, Tick, 0);
        }

        Touch(chunkIndex);
        return taken;
    }

    /// <summary>True when tile (<paramref name="x"/>, <paramref name="y"/>) is occupied.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public bool IsOccupied(int x, int y) => GetOccupant(x, y) != EntityId.None;

    /// <summary>Owner of tile (<paramref name="x"/>, <paramref name="y"/>), or <see cref="EntityId.None"/> when free.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public EntityId GetOccupant(int x, int y)
    {
        CheckTile(x, y);
        if (OccupiedTiles == 0)
        {
            return EntityId.None;
        }

        OccupancyChunk? chunk = _occupancy[ChunkIndex(x, y)];
        return chunk is null ? EntityId.None : chunk.Owners[LocalIndex(x, y)];
    }

    /// <summary>
    /// Marks tile (<paramref name="x"/>, <paramref name="y"/>) occupied by <paramref name="owner"/>; false (and no
    /// change) when it is already occupied. The tile becomes impassable and the pathfinder is told. A stump or a sapling
    /// on the tile is cleared (a building stands on it now).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    /// <exception cref="ArgumentException">The owner is not a valid id.</exception>
    public bool TryOccupy(int x, int y, EntityId owner)
    {
        if (!owner.IsValid)
        {
            throw new ArgumentException($"{owner} cannot own a tile.", nameof(owner));
        }

        if (IsOccupied(x, y))
        {
            return false;
        }

        int chunkIndex = ChunkIndex(x, y);
        OccupancyChunk chunk = _occupancy[chunkIndex] ??= new OccupancyChunk();
        chunk.Owners[LocalIndex(x, y)] = owner;
        chunk.Count++;
        OccupiedTiles++;
        _flora.Remove(chunkIndex, LocalIndex(x, y));
        Touch(chunkIndex);
        _pathfinder?.NotifyTileChanged(x, y);
        return true;
    }

    /// <summary>
    /// Frees tile (<paramref name="x"/>, <paramref name="y"/>) when <paramref name="owner"/> occupies it; false (and no
    /// change) otherwise. The pathfinder is told.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public bool Release(int x, int y, EntityId owner)
    {
        if (owner == EntityId.None || GetOccupant(x, y) != owner)
        {
            return false;
        }

        int chunkIndex = ChunkIndex(x, y);
        OccupancyChunk chunk = _occupancy[chunkIndex]!;
        chunk.Owners[LocalIndex(x, y)] = EntityId.None;
        if (--chunk.Count == 0)
        {
            _occupancy[chunkIndex] = null;
        }

        OccupiedTiles--;
        Touch(chunkIndex);
        _pathfinder?.NotifyTileChanged(x, y);
        return true;
    }

    /// <summary>Occupied tiles block movement (called by <see cref="PassabilityGrid"/> for on-map tiles only).</summary>
    public bool IsBlocked(int x, int y)
    {
        if (OccupiedTiles == 0)
        {
            return false;
        }

        OccupancyChunk? chunk = _occupancy[((y / ChunkCoord.Size) * ChunksPerSide) + (x / ChunkCoord.Size)];
        return chunk is not null && chunk.Owners[LocalIndex(x, y)] != EntityId.None;
    }

    /// <summary>
    /// Writes a tag, then per layer the number of non-empty chunks and, for each in ascending chunk index, the index,
    /// the 16 mask words and the changed values in mask order (stocks as Int32, owners as entity ids, flora as state,
    /// tick and stock, walks as counter, tick and class of the tiles still walked).
    /// </summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("tiles");
        writer.WriteInt32(_resources.Count(c => c is not null));
        for (int i = 0; i < _resources.Length; i++)
        {
            if (_resources[i] is { } chunk)
            {
                writer.WriteInt32(i);
                WriteMask(writer, chunk.Mask);
                ForEachSet(chunk.Mask, local => writer.WriteInt32(chunk.Amounts[local]));
            }
        }

        writer.WriteInt32(_occupancy.Count(c => c is not null));
        for (int i = 0; i < _occupancy.Length; i++)
        {
            if (_occupancy[i] is { } chunk)
            {
                writer.WriteInt32(i);
                ulong[] mask = chunk.BuildMask();
                WriteMask(writer, mask);
                ForEachSet(mask, local => chunk.Owners[local].WriteState(writer));
            }
        }

        WriteFlora(writer);
        _walks.Write(writer);
    }

    /// <summary>Snapshot of both layers for saving.</summary>
    public TileChangesState CaptureState()
    {
        var resources = new List<ResourceChunkState>();
        for (int i = 0; i < _resources.Length; i++)
        {
            if (_resources[i] is { } chunk)
            {
                var amounts = new List<int>();
                ForEachSet(chunk.Mask, local => amounts.Add(chunk.Amounts[local]));
                resources.Add(new ResourceChunkState(i % ChunksPerSide, i / ChunksPerSide, (ulong[])chunk.Mask.Clone(), amounts));
            }
        }

        var occupancy = new List<OccupancyChunkState>();
        for (int i = 0; i < _occupancy.Length; i++)
        {
            if (_occupancy[i] is { } chunk)
            {
                ulong[] mask = chunk.BuildMask();
                var owners = new List<EntityId>();
                ForEachSet(mask, local => owners.Add(chunk.Owners[local]));
                occupancy.Add(new OccupancyChunkState(i % ChunksPerSide, i / ChunksPerSide, mask, owners));
            }
        }

        return new TileChangesState(resources, occupancy, CaptureFlora(), _walks.Capture());
    }

    /// <summary>
    /// Replaces the (empty) layer with <paramref name="state"/>, read at <see cref="Tick"/>. Chunks must be inside the
    /// map, in strictly ascending chunk index, with a non-empty mask and one value per set bit; a changed stock must
    /// belong to a node of the map and differ from its generated stock; owners must be alive in
    /// <paramref name="registry"/>; flora and walks must be valid at <see cref="Tick"/> (see <see cref="LoadFlora"/> and
    /// <see cref="WalkLayer.Load"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The state is inconsistent; the layer is left empty.</exception>
    /// <exception cref="InvalidOperationException">The layer already has changes.</exception>
    internal void Load(TileChangesState state, EntityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(registry);
        if (ChangedResourceTiles != 0 || OccupiedTiles != 0 || _flora.Count != 0 || _walks.Entries != 0)
        {
            throw new InvalidOperationException("Tile changes can only be loaded into an empty layer.");
        }

        try
        {
            LoadResources(state.Resources);
            LoadOccupancy(state.Occupancy, registry);
            LoadFlora(state.Flora);
            _walks.Load(state.Walks);
        }
        catch (ArgumentException)
        {
            Array.Clear(_resources);
            Array.Clear(_occupancy);
            _flora.Clear();
            _walks.Clear();
            ChangedResourceTiles = 0;
            OccupiedTiles = 0;
            throw;
        }

        _pathfinder?.Connectivity.InvalidateAll();
    }

    /// <summary>Reports every later occupancy change to <paramref name="pathfinder"/> (the one built over this blocker).</summary>
    internal void AttachPathfinder(Pathfinder pathfinder) => _pathfinder = pathfinder;

    private void LoadResources(IReadOnlyList<ResourceChunkState> chunks)
    {
        int previous = -1;
        foreach (ResourceChunkState? saved in chunks)
        {
            ArgumentNullException.ThrowIfNull(saved, nameof(chunks));
            int chunkIndex = ChunkMask.Validate(saved.ChunkX, saved.ChunkY, saved.Mask, saved.Amounts.Count, ChunksPerSide, ref previous);
            var chunk = new ResourceChunk();
            saved.Mask.ToArray().CopyTo(chunk.Mask, 0);
            int next = 0;
            ForEachSet(chunk.Mask, local =>
            {
                int x = (saved.ChunkX * ChunkCoord.Size) + (local % ChunkCoord.Size);
                int y = (saved.ChunkY * ChunkCoord.Size) + (local / ChunkCoord.Size);
                int amount = saved.Amounts[next++];
                if (_map.GetResource(x, y) == ResourceKind.None || amount < 0 || amount >= _map.GetResourceAmount(x, y))
                {
                    throw new ArgumentException($"Tile {x},{y}: stock {amount} is not a lowered stock of a resource node.", nameof(chunks));
                }

                chunk.Amounts[local] = (ushort)amount;
            });
            _resources[chunkIndex] = chunk;
            ChangedResourceTiles += saved.Amounts.Count;
        }
    }

    private void LoadOccupancy(IReadOnlyList<OccupancyChunkState> chunks, EntityRegistry registry)
    {
        int previous = -1;
        foreach (OccupancyChunkState? saved in chunks)
        {
            ArgumentNullException.ThrowIfNull(saved, nameof(chunks));
            int chunkIndex = ChunkMask.Validate(saved.ChunkX, saved.ChunkY, saved.Mask, saved.Owners.Count, ChunksPerSide, ref previous);
            var chunk = new OccupancyChunk();
            int next = 0;
            ForEachSet(saved.Mask.ToArray(), local =>
            {
                EntityId owner = saved.Owners[next++];
                if (!registry.IsAlive(owner))
                {
                    throw new ArgumentException($"Tile owner {owner} is not alive.", nameof(chunks));
                }

                chunk.Owners[local] = owner;
            });
            chunk.Count = saved.Owners.Count;
            _occupancy[chunkIndex] = chunk;
            OccupiedTiles += chunk.Count;
        }
    }

    private void Touch(int chunkIndex) => _chunkVersions[chunkIndex] = ++Version;

    private void CheckTile(int x, int y)
    {
        if ((uint)x >= (uint)Size || (uint)y >= (uint)Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Tile ({x}, {y}) is outside the {Size}x{Size} map.");
        }
    }

    private int ChunkIndex(int x, int y)
    {
        CheckTile(x, y);
        return ((y / ChunkCoord.Size) * ChunksPerSide) + (x / ChunkCoord.Size);
    }

    private static int LocalIndex(int x, int y) => ChunkMask.LocalIndex(x, y);

    private static bool IsSet(ulong[] mask, int local) => ChunkMask.IsSet(mask, local);

    private static void Set(ulong[] mask, int local) => ChunkMask.Set(mask, local);

    private static void WriteMask(StateWriter writer, ulong[] mask) => ChunkMask.Write(writer, mask);

    // Calls `action` with the local index of every set bit, in ascending order.
    private static void ForEachSet(ulong[] mask, Action<int> action) => ChunkMask.ForEachSet(mask, action);

    // Stock of the map's node on the tile with the resource layer's change, flora aside.
    private int MapNodeAmount(int x, int y)
    {
        ResourceChunk? chunk = _resources[ChunkIndex(x, y)];
        int local = LocalIndex(x, y);
        return chunk is not null && IsSet(chunk.Mask, local) ? chunk.Amounts[local] : _map.GetResourceAmount(x, y);
    }

    private sealed class ResourceChunk
    {
        public ulong[] Mask { get; } = new ulong[MaskWords];

        public ushort[] Amounts { get; } = new ushort[TilesPerChunk];
    }

    private sealed class OccupancyChunk
    {
        public EntityId[] Owners { get; } = new EntityId[TilesPerChunk];

        public int Count { get; set; }

        public ulong[] BuildMask()
        {
            var mask = new ulong[MaskWords];
            for (int local = 0; local < Owners.Length; local++)
            {
                if (Owners[local] != EntityId.None)
                {
                    Set(mask, local);
                }
            }

            return mask;
        }
    }
}
