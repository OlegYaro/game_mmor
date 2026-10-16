using Core.Fog;
using Core.Geometry;
using Core.Sim;
using Core.Tiles;
using Core.WorldGen;
using Godot;

namespace Game.World;

/// <summary>The three layers of one 32×32 chunk (ADR 0010): flat ground, road overlays and y-sorted objects. Cells use map coordinates.</summary>
public sealed class WorldChunk
{
    internal WorldChunk(TileMapLayer ground, TileMapLayer roads, TileMapLayer objects)
    {
        Ground = ground;
        Roads = roads;
        Objects = objects;
    }

    /// <summary>The chunk shown, valid while <see cref="IsActive"/>.</summary>
    public ChunkCoord Coord { get; internal set; }

    /// <summary>Ground layer (no y-sort, one rendering quadrant per chunk).</summary>
    public TileMapLayer Ground { get; }

    /// <summary>
    /// Road overlay layer: trail and road diamonds over the ground of explored tiles (no y-sort, same z as the ground, drawn
    /// right after it). Cells use map coordinates.
    /// </summary>
    public TileMapLayer Roads { get; }

    /// <summary>Objects layer (y-sorted under the shared objects root).</summary>
    public TileMapLayer Objects { get; }

    /// <summary>True while the chunk is shown; false while pooled.</summary>
    public bool IsActive { get; internal set; }

    /// <summary>Fog chunk version the objects layer was built for.</summary>
    internal long FogVersion { get; set; }

    /// <summary>Tile-change (resources, flora, roads, occupancy) chunk version the objects layer was built for.</summary>
    internal long TileVersion { get; set; }
}

/// <summary>
/// Creates chunk layers around the camera and returns far ones to a pool (ADR 0010). Only reads the map and the fog:
/// ground cells come from the terrain, objects (resources, points of interest, sites, the start marker) appear only on
/// explored tiles and are rebuilt when the fog of their chunk changes.
/// </summary>
public sealed class ChunkManager
{
    private const int GroundZBase = -1000;
    private const int VisibleMargin = 1;
    private const int KeepMargin = 2;

    private readonly Node2D _groundRoot;
    private readonly Node2D _objectsRoot;
    private readonly List<WorldChunk> _pool = [];
    private readonly Dictionary<ChunkCoord, WorldChunk> _active = [];
    private readonly Dictionary<ChunkCoord, List<(int X, int Y, WorldObject Object)>> _placed = [];
    private readonly List<ChunkCoord> _wanted = [];
    private readonly List<ChunkCoord> _release = [];
    private readonly byte[] _fogChunk = new byte[ChunkCoord.Size * ChunkCoord.Size];
    private readonly bool[] _taken = new bool[ChunkCoord.Size * ChunkCoord.Size];
    private readonly List<(int X, int Y, WorldObject Object)> _objects = [];

    private WorldArt? _art;
    private GameWorld? _world;
    private bool[] _visible = [];
    private bool[] _needed = [];
    private bool[] _keep = [];
    private Rect2 _lastView;
    private int _chunksPerSide;

    /// <summary>Creates a manager that parents ground layers under <paramref name="groundRoot"/> and objects under <paramref name="objectsRoot"/>.</summary>
    public ChunkManager(Node2D groundRoot, Node2D objectsRoot)
    {
        _groundRoot = groundRoot;
        _objectsRoot = objectsRoot;
    }

    /// <summary>The art in use while bound.</summary>
    public WorldArt? Art => _art;

    /// <summary>Active chunks by coordinate.</summary>
    public IReadOnlyDictionary<ChunkCoord, WorldChunk> Active => _active;

    /// <summary>Chunks waiting in the pool.</summary>
    public int PooledCount => _pool.Count;

    /// <summary>Chunks wanted (visible plus margin) but not built yet.</summary>
    public int PendingCount { get; private set; }

    /// <summary>Time the last <see cref="Update"/> took, in microseconds (statistics).</summary>
    public ulong LastUpdateUsec { get; private set; }

    /// <summary>Chunks built by the last <see cref="Update"/> (statistics).</summary>
    public int LastBuilds { get; private set; }

    /// <summary>Total chunk builds since binding (statistics).</summary>
    public long BuildCount { get; private set; }

    /// <summary>Starts showing <paramref name="world"/>.</summary>
    public void Bind(GameWorld world, WorldArt art)
    {
        Unbind();
        _world = world;
        _art = art;
        _chunksPerSide = ChunkCoord.CountPerSide(world.Size);
        int count = _chunksPerSide * _chunksPerSide;
        _visible = new bool[count];
        _needed = new bool[count];
        _keep = new bool[count];
        _lastView = default;

        WorldMap map = world.Map;
        Place(map.Start.X, map.Start.Y, WorldObject.StartMarker);
        foreach (PointOfInterest poi in map.PointsOfInterest)
        {
            Place(poi.X, poi.Y, WorldArt.ObjectOf(poi.Kind));
        }

        foreach (SettlementSite site in map.SettlementSites)
        {
            Place(site.X, site.Y, WorldObject.SettlementSite);
        }
    }

    /// <summary>Releases every chunk to the pool and forgets the world.</summary>
    public void Unbind()
    {
        foreach (WorldChunk chunk in _active.Values)
        {
            Deactivate(chunk);
        }

        _active.Clear();
        _placed.Clear();
        _world = null;
        _art = null;
        PendingCount = 0;
    }

    /// <summary>
    /// Brings the chunk set in line with the camera's world rectangle: releases chunks beyond the keep margin, then
    /// builds missing chunks (visible first, nearest first) until <paramref name="maxBuilds"/> or
    /// <paramref name="budgetUsec"/> is used up (at least one build per call when any is missing).
    /// </summary>
    public void Update(Rect2 view, int maxBuilds, ulong budgetUsec)
    {
        if (_world is null || _art is null)
        {
            return;
        }

        ulong start = Time.GetTicksUsec();
        long builds = BuildCount;
        if (view != _lastView)
        {
            _lastView = view;
            ComputeSets(view);
            ReleaseFar();
        }

        RefreshFogObjects();
        BuildMissing(view, maxBuilds, budgetUsec);
        LastBuilds = (int)(BuildCount - builds);
        LastUpdateUsec = Time.GetTicksUsec() - start;
    }

    /// <summary>Builds every wanted chunk for <paramref name="view"/> now, without a budget (smoke test, benchmark start).</summary>
    public void BuildAll(Rect2 view)
    {
        _lastView = default;
        Update(view, int.MaxValue, ulong.MaxValue);
    }

    /// <summary>Number of object cells a chunk must show for the current fog (smoke test).</summary>
    public int ExpectedObjectCount(ChunkCoord coord)
    {
        if (_world is null)
        {
            return 0;
        }

        CollectObjects(coord);
        return _objects.Count;
    }

    private void Place(int x, int y, WorldObject obj)
    {
        ChunkCoord coord = ChunkCoord.OfTile(x, y);
        if (!_placed.TryGetValue(coord, out List<(int, int, WorldObject)>? list))
        {
            list = [];
            _placed[coord] = list;
        }

        list.Add((x, y, obj));
    }

    private void ComputeSets(Rect2 view)
    {
        Array.Clear(_visible);
        Array.Clear(_needed);
        Array.Clear(_keep);
        int n = _chunksPerSide;
        int span = ChunkCoord.Size;

        // Candidate range: tile bounding box of the view's corners (ground picks ignore heights, so pad by a chunk).
        (int x0, int y0, int x1, int y1) = TileBounds(view);
        int cx0 = Math.Clamp(Math.Min(x0, x1) / span - 1, 0, n - 1);
        int cx1 = Math.Clamp(Math.Max(x0, x1) / span + 1, 0, n - 1);
        int cy0 = Math.Clamp(Math.Min(y0, y1) / span - 1, 0, n - 1);
        int cy1 = Math.Clamp(Math.Max(y0, y1) / span + 1, 0, n - 1);
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                if (ChunkRect(cx, cy).Intersects(view))
                {
                    _visible[(cy * n) + cx] = true;
                }
            }
        }

        Dilate(_visible, _needed, VisibleMargin);
        Dilate(_visible, _keep, KeepMargin);
    }

    private void Dilate(bool[] source, bool[] target, int radius)
    {
        int n = _chunksPerSide;
        for (int cy = 0; cy < n; cy++)
        {
            for (int cx = 0; cx < n; cx++)
            {
                if (!source[(cy * n) + cx])
                {
                    continue;
                }

                for (int y = Math.Max(cy - radius, 0); y <= Math.Min(cy + radius, n - 1); y++)
                {
                    for (int x = Math.Max(cx - radius, 0); x <= Math.Min(cx + radius, n - 1); x++)
                    {
                        target[(y * n) + x] = true;
                    }
                }
            }
        }
    }

    private static (int X0, int Y0, int X1, int Y1) TileBounds(Rect2 view)
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        foreach (Vector2 corner in new[] { view.Position, view.Position + new Vector2(view.Size.X, 0), view.End, view.Position + new Vector2(0, view.Size.Y) })
        {
            (int x, int y) = IsoProjection.TileAtPixel((int)Math.Floor(corner.X), (int)Math.Floor(corner.Y));
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return (Math.Max(minX, 0), Math.Max(minY, 0), Math.Max(maxX, 0), Math.Max(maxY, 0));
    }

    // Screen bounding box of a chunk's diamond including the highest blocks.
    private Rect2 ChunkRect(int cx, int cy)
    {
        int x0 = cx * ChunkCoord.Size;
        int y0 = cy * ChunkCoord.Size;
        int last = ChunkCoord.Size - 1;
        float left = IsoProjection.TileCenter(x0, y0 + last).Px - IsoProjection.HalfWidth;
        float right = IsoProjection.TileCenter(x0 + last, y0).Px + IsoProjection.HalfWidth;
        float top = IsoProjection.TileCenter(x0, y0, TerrainMap.MaxHeight).Py - IsoProjection.HalfHeight;
        float bottom = IsoProjection.TileCenter(x0 + last, y0 + last).Py + IsoProjection.HalfHeight;

        // Objects stick up above their tile (the art table gives how far above the ground centre).
        int rise = Math.Max(_art?.ObjectRise ?? 0, IsoProjection.TileHeight);
        return new Rect2(left, top - rise, right - left, bottom - top + rise);
    }

    private void ReleaseFar()
    {
        _release.Clear();
        foreach (KeyValuePair<ChunkCoord, WorldChunk> pair in _active)
        {
            if (!_keep[(pair.Key.Y * _chunksPerSide) + pair.Key.X])
            {
                _release.Add(pair.Key);
            }
        }

        foreach (ChunkCoord coord in _release)
        {
            Deactivate(_active[coord]);
            _active.Remove(coord);
        }
    }

    private void BuildMissing(Rect2 view, int maxBuilds, ulong budgetUsec)
    {
        _wanted.Clear();
        int n = _chunksPerSide;
        for (int i = 0; i < _needed.Length; i++)
        {
            var coord = new ChunkCoord(i % n, i / n);
            if (_needed[i] && !_active.ContainsKey(coord))
            {
                _wanted.Add(coord);
            }
        }

        PendingCount = _wanted.Count;
        if (_wanted.Count == 0)
        {
            return;
        }

        Vector2 centre = view.GetCenter();
        _wanted.Sort((a, b) =>
        {
            bool va = _visible[(a.Y * n) + a.X];
            bool vb = _visible[(b.Y * n) + b.X];
            if (va != vb)
            {
                return va ? -1 : 1;
            }

            return ChunkRect(a.X, a.Y).GetCenter().DistanceSquaredTo(centre).CompareTo(ChunkRect(b.X, b.Y).GetCenter().DistanceSquaredTo(centre));
        });

        ulong start = Time.GetTicksUsec();
        int built = 0;
        foreach (ChunkCoord coord in _wanted)
        {
            if (built >= maxBuilds || (built > 0 && Time.GetTicksUsec() - start >= budgetUsec))
            {
                break;
            }

            Build(coord);
            built++;
        }

        PendingCount = _wanted.Count - built;
    }

    private void Build(ChunkCoord coord)
    {
        WorldChunk chunk = Rent();
        chunk.Coord = coord;
        chunk.IsActive = true;
        _active[coord] = chunk;
        BuildCount++;

        GameWorld world = _world!;
        WorldArt art = _art!;
        TerrainMap terrain = world.Map.Terrain;
        ReadOnlySpan<byte> heights = terrain.Heights;
        ReadOnlySpan<Biome> biomes = terrain.Biomes;
        ReadOnlySpan<TileFeatures> features = terrain.Features;
        int size = world.Size;
        int x0 = coord.X * ChunkCoord.Size;
        int y0 = coord.Y * ChunkCoord.Size;
        TileMapLayer ground = chunk.Ground;
        for (int y = y0; y < y0 + ChunkCoord.Size; y++)
        {
            int row = y * size;
            for (int x = x0; x < x0 + ChunkCoord.Size; x++)
            {
                int i = row + x;
                (int source, Vector2I coords) = art.GroundCell(biomes[i], features[i], heights[i], WorldArt.VariantOf(x, y));
                ground.SetCell(new Vector2I(x, y), source, coords);
            }
        }

        // Back-to-front chunk order for overlapping raised blocks: deeper (larger cx + cy) chunks draw later.
        ground.ZIndex = GroundZBase + coord.X + coord.Y;
        ground.Visible = true;
        BuildObjects(chunk);
        chunk.Roads.ZIndex = ground.ZIndex;
        chunk.Roads.Visible = true;
        chunk.Objects.Visible = true;
    }

    private void BuildObjects(WorldChunk chunk)
    {
        TileMapLayer layer = chunk.Objects;
        layer.Clear();
        chunk.FogVersion = _world!.Fog.GetChunkVersion(chunk.Coord.X, chunk.Coord.Y);
        chunk.TileVersion = _world.Tiles.GetChunkVersion(chunk.Coord.X, chunk.Coord.Y);
        WorldArt art = _art!;
        ReadOnlySpan<byte> heights = _world.Map.Terrain.Heights;
        int size = _world.Size;
        CollectObjects(chunk.Coord);
        foreach ((int x, int y, WorldObject obj) in _objects)
        {
            layer.SetCell(new Vector2I(x, y), WorldArt.ObjectSource, art.ObjectTile(obj), heights[(y * size) + x]);
        }

        BuildRoads(chunk);
    }

    // Trail and road overlays on explored tiles, lifted like the ground tile they lie on. CollectObjects has just filled
    // _fogChunk for this chunk.
    private void BuildRoads(WorldChunk chunk)
    {
        TileMapLayer layer = chunk.Roads;
        layer.Clear();
        TileChanges tiles = _world!.Tiles;
        if (tiles.TrailTiles == 0 && tiles.RoadTiles == 0)
        {
            return;
        }

        WorldArt art = _art!;
        TerrainMap terrain = _world.Map.Terrain;
        int x0 = chunk.Coord.X * ChunkCoord.Size;
        int y0 = chunk.Coord.Y * ChunkCoord.Size;
        for (int ly = 0; ly < ChunkCoord.Size; ly++)
        {
            for (int lx = 0; lx < ChunkCoord.Size; lx++)
            {
                int x = x0 + lx;
                int y = y0 + ly;
                if (_fogChunk[(ly * ChunkCoord.Size) + lx] == (byte)FogState.Unknown
                    || art.OverlayTile(tiles.GetRoadClass(x, y), WorldArt.OverlayVariantOf(x, y)) is not { } cell)
                {
                    continue;
                }

                int height = art.VisualHeight(terrain.GetBiome(x, y), terrain.GetFeatures(x, y), terrain.GetHeight(x, y));
                layer.SetCell(new Vector2I(x, y), WorldArt.GroundSource, cell, height);
            }
        }
    }

    /// <summary>Number of road overlay cells a chunk must show for the current fog and roads (smoke test).</summary>
    public int ExpectedRoadCount(ChunkCoord coord)
    {
        if (_world is null)
        {
            return 0;
        }

        CollectObjects(coord);
        int count = 0;
        for (int ly = 0; ly < ChunkCoord.Size; ly++)
        {
            for (int lx = 0; lx < ChunkCoord.Size; lx++)
            {
                if (_fogChunk[(ly * ChunkCoord.Size) + lx] != (byte)FogState.Unknown
                    && _world.Tiles.GetRoadClass((coord.X * ChunkCoord.Size) + lx, (coord.Y * ChunkCoord.Size) + ly) != RoadClass.None)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // Fills _objects with one object per explored tile of the chunk: specials (start, points, sites; lower
    // WorldObject value wins a shared tile) take precedence over resources.
    private void CollectObjects(ChunkCoord coord)
    {
        _objects.Clear();
        Array.Clear(_taken);
        GameWorld world = _world!;
        FogOfWar fog = world.Fog;
        int x0 = coord.X * ChunkCoord.Size;
        int y0 = coord.Y * ChunkCoord.Size;
        fog.CopyStates(coord, _fogChunk);
        if (_placed.TryGetValue(coord, out List<(int X, int Y, WorldObject Object)>? specials))
        {
            foreach ((int x, int y, WorldObject obj) in specials.OrderBy(s => s.Object))
            {
                int local = ((y - y0) * ChunkCoord.Size) + (x - x0);
                if (_fogChunk[local] != (byte)FogState.Unknown && !_taken[local])
                {
                    _taken[local] = true;
                    _objects.Add((x, y, obj));
                }
            }
        }

        // Effective resources: gathered nodes are gone from the map (Core owns the stock, FR-WORLD-14).
        TileChanges tiles = world.Tiles;
        for (int ly = 0; ly < ChunkCoord.Size; ly++)
        {
            for (int lx = 0; lx < ChunkCoord.Size; lx++)
            {
                int local = (ly * ChunkCoord.Size) + lx;
                if (_fogChunk[local] == (byte)FogState.Unknown || _taken[local])
                {
                    continue;
                }

                // Flora first: a stump or a sapling is no node (a grown tree is a Tree resource below).
                WorldObject? obj = tiles.FloraTiles == 0 ? null : tiles.GetFlora(x0 + lx, y0 + ly) switch
                {
                    FloraState.Stump => WorldObject.Stump,
                    FloraState.Sapling => WorldObject.Sapling,
                    _ => null,
                };
                obj ??= WorldArt.ObjectOf(tiles.GetEffectiveResource(x0 + lx, y0 + ly));
                if (obj is not null)
                {
                    _objects.Add((x0 + lx, y0 + ly, obj.Value));
                }
            }
        }
    }

    private void RefreshFogObjects()
    {
        FogOfWar fog = _world!.Fog;
        TileChanges tiles = _world.Tiles;
        foreach (WorldChunk chunk in _active.Values)
        {
            if (fog.GetChunkVersion(chunk.Coord.X, chunk.Coord.Y) != chunk.FogVersion
                || tiles.GetChunkVersion(chunk.Coord.X, chunk.Coord.Y) != chunk.TileVersion)
            {
                BuildObjects(chunk);
            }
        }
    }

    private WorldChunk Rent()
    {
        if (_pool.Count > 0)
        {
            WorldChunk pooled = _pool[^1];
            _pool.RemoveAt(_pool.Count - 1);
            return pooled;
        }

        TileSet tileSet = _art!.TileSet;
        var ground = new TileMapLayer
        {
            Name = $"Ground{_groundRoot.GetChildCount()}",
            TileSet = tileSet,
            YSortEnabled = false,
            RenderingQuadrantSize = ChunkCoord.Size,
            CollisionEnabled = false,
            NavigationEnabled = false,
            OcclusionEnabled = false,
        };
        var roads = new TileMapLayer
        {
            Name = $"Roads{_groundRoot.GetChildCount()}",
            TileSet = tileSet,
            YSortEnabled = false,
            RenderingQuadrantSize = ChunkCoord.Size,
            CollisionEnabled = false,
            NavigationEnabled = false,
            OcclusionEnabled = false,
        };
        var objects = new TileMapLayer
        {
            Name = $"Objects{_groundRoot.GetChildCount()}",
            TileSet = tileSet,
            YSortEnabled = true,
            CollisionEnabled = false,
            NavigationEnabled = false,
            OcclusionEnabled = false,
        };
        _groundRoot.AddChild(ground);
        _groundRoot.AddChild(roads);
        _objectsRoot.AddChild(objects);
        return new WorldChunk(ground, roads, objects);
    }

    private void Deactivate(WorldChunk chunk)
    {
        chunk.Ground.Clear();
        chunk.Roads.Clear();
        chunk.Objects.Clear();
        chunk.Ground.Visible = false;
        chunk.Roads.Visible = false;
        chunk.Objects.Visible = false;
        chunk.IsActive = false;
        _pool.Add(chunk);
    }
}
