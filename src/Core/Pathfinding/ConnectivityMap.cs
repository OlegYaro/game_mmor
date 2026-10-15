namespace Core.Pathfinding;

/// <summary>
/// Connected components of the walkable tiles (ADR 0003): answers "can a walker get from A to B" in O(1) so that A*
/// never floods the whole map towards an unreachable goal.
/// </summary>
/// <remarks>
/// <para>Two levels, after Sean Barrett's stb_connected_components (docs/research/pathfinding.md §3). The map is cut
/// into <see cref="CellSize"/>×<see cref="CellSize"/> cells (four per 32×32 chunk); a <b>region</b> is a connected set
/// of tiles inside one cell (one byte per tile holds its local region id, at most 128 regions per cell). For each
/// cell the region pairs joined across its east and south borders are cached. Components are a union-find over all
/// regions and those border links, flattened so a query is two array reads.</para>
/// <para>Updates: <see cref="Invalidate"/> marks the cell of a changed tile dirty. The next query (or
/// <see cref="Rebuild"/>) re-floods only the dirty cells (256 tiles each), refreshes the border links of those cells
/// and of their west and north neighbours, then reruns the union-find over the region graph, which is a few hundred
/// thousand cheap operations even at 2048² and needs no tile reads. Changes of many tiles in a tick cost one pass.</para>
/// <para>Why the first build is eager (one pass over the whole map) and not lazy per chunk: whether two tiles are
/// connected is a global property. A lazy scheme can only build chunks on demand and then search the region graph per
/// query (RimWorld's BFS over regions), which loses the O(1) answer and, for an unreachable goal, still floods every
/// chunk reachable from the start: the very cost the map exists to avoid. The full build is a single flood of all tiles
/// (measured in <c>PathfindingBenchmarkTests</c>: 20–50 ms at 2048² in Release, next to seconds of world generation)
/// and takes about one byte per tile plus a few bytes per region (5.2 MiB at 2048²). The build is still
/// deferred to the first query, so creating the map is free.</para>
/// <para>The answers follow <see cref="PassabilityGrid"/>: straight steps only, which loses nothing because the grid
/// allows a diagonal step only when both straight detours are allowed. A second instance over a grid with a different
/// blocker gives the "with breach" layer of ADR 0003 (M4).</para>
/// </remarks>
public sealed class ConnectivityMap
{
    /// <summary>Side of a cell in tiles; a 32×32 chunk holds 2×2 cells.</summary>
    public const int CellSize = 16;

    /// <summary>Component value of an impassable tile.</summary>
    public const int NoComponent = -1;

    private const byte NoRegion = 0xFF;
    private const int MaxLinksPerBorder = CellSize;

    private readonly PassabilityGrid _grid;
    private readonly int _size;

    // Local region id per tile (NoRegion when impassable).
    private readonly byte[] _tileRegion;
    private readonly byte[] _regionCount;

    // Region pairs (local id in this cell, local id in the neighbour) joined across the east / south border.
    private readonly byte[] _eastLinks;
    private readonly byte[] _eastLinkCount;
    private readonly byte[] _southLinks;
    private readonly byte[] _southLinkCount;

    private readonly bool[] _dirty;
    private readonly int[] _dirtyCells;
    private readonly int[] _regionBase;
    private readonly int[] _floodStack = new int[CellSize * CellSize];
    private int _dirtyCount;
    private int[] _component = [];

    /// <summary>Creates the map over <paramref name="grid"/>; nothing is computed until the first query.</summary>
    public ConnectivityMap(PassabilityGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _grid = grid;
        _size = grid.Size;
        CellsPerSide = (_size + CellSize - 1) / CellSize;
        int cells = CellsPerSide * CellsPerSide;
        _tileRegion = new byte[grid.TileCount];
        _regionCount = new byte[cells];
        _eastLinks = new byte[cells * MaxLinksPerBorder * 2];
        _eastLinkCount = new byte[cells];
        _southLinks = new byte[cells * MaxLinksPerBorder * 2];
        _southLinkCount = new byte[cells];
        _dirty = new bool[cells];
        _dirtyCells = new int[cells];
        _regionBase = new int[cells + 1];
        InvalidateAll();
    }

    /// <summary>Cells along one side.</summary>
    public int CellsPerSide { get; }

    /// <summary>Number of regions after the last rebuild.</summary>
    public int RegionCount => _regionBase[^1];

    /// <summary>Total number of cell floods since creation (diagnostics and tests: an update must touch only dirty cells).</summary>
    public long CellsRebuilt { get; private set; }

    /// <summary>Total number of component passes since creation.</summary>
    public long ComponentPasses { get; private set; }

    /// <summary>True when some cell waits for a rebuild.</summary>
    public bool IsDirty => _dirtyCount > 0;

    /// <summary>Approximate bytes held by the map's arrays.</summary>
    public long MemoryBytes =>
        _tileRegion.Length + _regionCount.Length + _eastLinks.Length + _eastLinkCount.Length + _southLinks.Length
        + _southLinkCount.Length + _dirty.Length + (4L * (_dirtyCells.Length + _regionBase.Length + _floodStack.Length + _component.Length));

    /// <summary>Marks the cell of tile (<paramref name="x"/>, <paramref name="y"/>) for a rebuild: call it whenever the tile's passability or height changes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public void Invalidate(int x, int y)
    {
        _ = _grid.Index(x, y);
        MarkDirty(CellOf(x, y));
    }

    /// <summary>Marks every cell for a rebuild.</summary>
    public void InvalidateAll()
    {
        for (int cell = 0; cell < _dirty.Length; cell++)
        {
            MarkDirty(cell);
        }
    }

    /// <summary>Rebuilds the dirty cells and the components now (queries do it on demand).</summary>
    public void Rebuild()
    {
        if (_dirtyCount == 0)
        {
            return;
        }

        // Dirty cells in increasing order, so the work does not depend on the order of Invalidate calls.
        Array.Sort(_dirtyCells, 0, _dirtyCount);
        for (int i = 0; i < _dirtyCount; i++)
        {
            FloodCell(_dirtyCells[i]);
        }

        for (int i = 0; i < _dirtyCount; i++)
        {
            int cell = _dirtyCells[i];
            int cx = cell % CellsPerSide;
            int cy = cell / CellsPerSide;
            ComputeLinks(cell);
            if (cx > 0 && !_dirty[cell - 1])
            {
                ComputeLinks(cell - 1);
            }

            if (cy > 0 && !_dirty[cell - CellsPerSide])
            {
                ComputeLinks(cell - CellsPerSide);
            }
        }

        for (int i = 0; i < _dirtyCount; i++)
        {
            _dirty[_dirtyCells[i]] = false;
        }

        _dirtyCount = 0;
        ComputeComponents();
    }

    /// <summary>
    /// Component id of tile (<paramref name="x"/>, <paramref name="y"/>), or <see cref="NoComponent"/> when it is
    /// impassable. Ids are only comparable with each other until the next rebuild.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public int GetComponent(int x, int y) => GetComponent(_grid.Index(x, y));

    /// <summary>Component id of tile <paramref name="index"/> (see <see cref="GetComponent(int, int)"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the map.</exception>
    public int GetComponent(int index)
    {
        if (!_grid.ContainsIndex(index))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Tile index outside the map.");
        }

        Rebuild();
        byte local = _tileRegion[index];
        if (local == NoRegion)
        {
            return NoComponent;
        }

        return _component[_regionBase[CellOf(index % _size, index / _size)] + local];
    }

    /// <summary>True when both tiles are passable and a walker can get from one to the other.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the map.</exception>
    public bool AreConnected(int a, int b)
    {
        int ca = GetComponent(a);
        return ca != NoComponent && ca == GetComponent(b);
    }

    private int CellOf(int x, int y) => ((y / CellSize) * CellsPerSide) + (x / CellSize);

    private void MarkDirty(int cell)
    {
        if (!_dirty[cell])
        {
            _dirty[cell] = true;
            _dirtyCells[_dirtyCount++] = cell;
        }
    }

    // Labels the 4-connected regions of one cell, in row-major order of their first tile.
    private void FloodCell(int cell)
    {
        int x0 = cell % CellsPerSide * CellSize;
        int y0 = cell / CellsPerSide * CellSize;
        int x1 = Math.Min(x0 + CellSize, _size);
        int y1 = Math.Min(y0 + CellSize, _size);
        for (int y = y0; y < y1; y++)
        {
            Array.Fill(_tileRegion, NoRegion, (y * _size) + x0, x1 - x0);
        }

        int regions = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int index = (y * _size) + x;
                if (_tileRegion[index] != NoRegion || !_grid.IsPassable(x, y))
                {
                    continue;
                }

                byte id = (byte)regions++;
                _tileRegion[index] = id;
                int top = 0;
                _floodStack[top++] = index;
                while (top > 0)
                {
                    int current = _floodStack[--top];
                    int cxTile = current % _size;
                    int cyTile = current / _size;
                    int mask = _grid.StraightStepMask(cxTile, cyTile);
                    for (int d = 0; d < 4; d++)
                    {
                        if ((mask & (1 << d)) == 0)
                        {
                            continue;
                        }

                        int nx = cxTile + PassabilityGrid.DirectionX(d);
                        int ny = cyTile + PassabilityGrid.DirectionY(d);
                        if (nx < x0 || nx >= x1 || ny < y0 || ny >= y1)
                        {
                            continue;
                        }

                        int next = (ny * _size) + nx;
                        if (_tileRegion[next] == NoRegion)
                        {
                            _tileRegion[next] = id;
                            _floodStack[top++] = next;
                        }
                    }
                }
            }
        }

        _regionCount[cell] = (byte)regions;
        CellsRebuilt++;
    }

    // Recomputes the links of a cell to its east and south neighbours from the current labels.
    private void ComputeLinks(int cell)
    {
        int cx = cell % CellsPerSide;
        int cy = cell / CellsPerSide;
        int x0 = cx * CellSize;
        int y0 = cy * CellSize;
        int baseOffset = cell * MaxLinksPerBorder * 2;

        int count = 0;
        int east = x0 + CellSize - 1;
        if (cx + 1 < CellsPerSide)
        {
            int yEnd = Math.Min(y0 + CellSize, _size);
            for (int y = y0; y < yEnd; y++)
            {
                count = AddLink(_eastLinks, baseOffset, count, east, y, 1);
            }
        }

        _eastLinkCount[cell] = (byte)count;

        count = 0;
        int south = y0 + CellSize - 1;
        if (cy + 1 < CellsPerSide)
        {
            int xEnd = Math.Min(x0 + CellSize, _size);
            for (int x = x0; x < xEnd; x++)
            {
                count = AddLink(_southLinks, baseOffset, count, x, south, 2);
            }
        }

        _southLinkCount[cell] = (byte)count;
    }

    // Adds the link from tile (x, y) across the border in direction bit (1 east, 2 south) unless it repeats the previous one.
    private int AddLink(byte[] links, int baseOffset, int count, int x, int y, int directionBit)
    {
        if ((_grid.StraightStepMask(x, y) & directionBit) == 0)
        {
            return count;
        }

        int index = (y * _size) + x;
        byte here = _tileRegion[index];
        byte there = _tileRegion[directionBit == 1 ? index + 1 : index + _size];
        if (count > 0 && links[baseOffset + (2 * (count - 1))] == here && links[baseOffset + (2 * (count - 1)) + 1] == there)
        {
            return count;
        }

        links[baseOffset + (2 * count)] = here;
        links[baseOffset + (2 * count) + 1] = there;
        return count + 1;
    }

    // Union-find over all regions (the smaller global id becomes the root, so the result is order-independent),
    // flattened to the root id per region.
    private void ComputeComponents()
    {
        int cells = _regionCount.Length;
        int total = 0;
        for (int cell = 0; cell < cells; cell++)
        {
            _regionBase[cell] = total;
            total += _regionCount[cell];
        }

        _regionBase[cells] = total;
        if (_component.Length < total)
        {
            _component = new int[Math.Max(total, _component.Length * 2)];
        }

        int[] parent = _component;
        for (int r = 0; r < total; r++)
        {
            parent[r] = r;
        }

        for (int cell = 0; cell < cells; cell++)
        {
            int baseOffset = cell * MaxLinksPerBorder * 2;
            int here = _regionBase[cell];
            int eastBase = _regionBase[Math.Min(cell + 1, cells)];
            for (int i = 0; i < _eastLinkCount[cell]; i++)
            {
                Union(parent, here + _eastLinks[baseOffset + (2 * i)], eastBase + _eastLinks[baseOffset + (2 * i) + 1]);
            }

            int southBase = _regionBase[Math.Min(cell + CellsPerSide, cells)];
            for (int i = 0; i < _southLinkCount[cell]; i++)
            {
                Union(parent, here + _southLinks[baseOffset + (2 * i)], southBase + _southLinks[baseOffset + (2 * i) + 1]);
            }
        }

        // Roots are the smallest member, so one forward pass flattens every chain.
        for (int r = 0; r < total; r++)
        {
            parent[r] = parent[parent[r]];
        }

        ComponentPasses++;
    }

    private static int Find(int[] parent, int r)
    {
        while (parent[r] != r)
        {
            parent[r] = parent[parent[r]];
            r = parent[r];
        }

        return r;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra < rb)
        {
            parent[rb] = ra;
        }
        else if (rb < ra)
        {
            parent[ra] = rb;
        }
    }
}
