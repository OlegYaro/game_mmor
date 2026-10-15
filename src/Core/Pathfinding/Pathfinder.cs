namespace Core.Pathfinding;

/// <summary>
/// Path search over one map (ADR 0003, FR-HERO-01): the passability rules, the connectivity map and an A* instance
/// whose node arrays are reused by every search. Single-threaded; one instance per map.
/// </summary>
/// <remarks>
/// Every search first checks the connectivity map, so an unreachable goal costs two array reads instead of a flood of
/// the map. Paths are written into a caller-owned <see cref="List{T}"/> of tile indices (start first), so a warmed-up
/// caller allocates nothing per search.
/// </remarks>
public sealed class Pathfinder
{
    private readonly AStarSearch _search;
    private readonly List<int> _scratch = [];

    /// <summary>Creates the pathfinder; the connectivity map is built on the first query.</summary>
    public Pathfinder(PassabilityGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Grid = grid;
        Connectivity = new ConnectivityMap(grid);
        _search = new AStarSearch(grid);
    }

    /// <summary>The movement rules.</summary>
    public PassabilityGrid Grid { get; }

    /// <summary>The connected components of the map.</summary>
    public ConnectivityMap Connectivity { get; }

    /// <summary>Bytes held by the A* node arrays and heap (0 before the first search).</summary>
    public long SearchMemoryBytes => _search.MemoryBytes;

    /// <summary>
    /// Tells the pathfinder that the passability or height of tile (<paramref name="x"/>, <paramref name="y"/>)
    /// changed (a building, a wall, a removed one). Cost-only changes (roads) need no call.
    /// </summary>
    public void NotifyTileChanged(int x, int y) => Connectivity.Invalidate(x, y);

    /// <summary>True when both tiles are passable and connected.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A tile is outside the map.</exception>
    public bool IsReachable(int from, int to) => Connectivity.AreConnected(from, to);

    /// <summary>True when both tiles are passable and connected.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A tile is outside the map.</exception>
    public bool IsReachable(int fromX, int fromY, int toX, int toY) =>
        Connectivity.AreConnected(Grid.Index(fromX, fromY), Grid.Index(toX, toY));

    /// <summary>
    /// Cheapest path from <paramref name="start"/> to <paramref name="goal"/> (tile indices), written into
    /// <paramref name="path"/> (cleared first). <see cref="PathStatus.Blocked"/> when either tile is impassable,
    /// <see cref="PathStatus.Unreachable"/> when they are not connected (no search), <see cref="PathStatus.Partial"/>
    /// when the search used up <paramref name="nodeLimit"/> expanded nodes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A tile is outside the map or the limit is outside 1..<see cref="PathfinderConfig.MaxNodeLimit"/>.</exception>
    public PathResult FindPath(int start, int goal, List<int> path, int nodeLimit)
    {
        ArgumentNullException.ThrowIfNull(path);
        CheckTile(start, nameof(start));
        CheckTile(goal, nameof(goal));
        PathfinderConfig.ValidateNodeLimit(nodeLimit);
        path.Clear();
        int startComponent = Connectivity.GetComponent(start);
        int goalComponent = Connectivity.GetComponent(goal);
        if (startComponent == ConnectivityMap.NoComponent || goalComponent == ConnectivityMap.NoComponent)
        {
            return PathResult.Fail(PathStatus.Blocked);
        }

        if (startComponent != goalComponent)
        {
            return PathResult.Fail(PathStatus.Unreachable);
        }

        return _search.Search(start, goal, PathGoalKind.Tile, nodeLimit, path);
    }

    /// <summary>
    /// Cheapest path from <paramref name="start"/> to the nearest reachable tile among the eight around
    /// <paramref name="target"/> (the target itself may be impassable: a tree, a rock, a building). When the start
    /// already is such a tile, the path is just the start. <see cref="PathStatus.Blocked"/> when the start is
    /// impassable or no neighbour of the target is passable and connected to the start.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A tile is outside the map or the limit is outside 1..<see cref="PathfinderConfig.MaxNodeLimit"/>.</exception>
    public PathResult FindPathToAdjacent(int start, int target, List<int> path, int nodeLimit)
    {
        ArgumentNullException.ThrowIfNull(path);
        CheckTile(start, nameof(start));
        CheckTile(target, nameof(target));
        PathfinderConfig.ValidateNodeLimit(nodeLimit);
        path.Clear();
        int startComponent = Connectivity.GetComponent(start);
        if (startComponent == ConnectivityMap.NoComponent || !HasReachableNeighbour(startComponent, target))
        {
            return PathResult.Fail(PathStatus.Blocked);
        }

        return _search.Search(start, target, PathGoalKind.Adjacent, nodeLimit, path);
    }

    /// <summary>
    /// Runs the search a request asks for (<see cref="FindPath"/> or <see cref="FindPathToAdjacent"/>).
    /// </summary>
    public PathResult Find(int start, int goal, PathGoalKind kind, List<int> path, int nodeLimit) =>
        kind == PathGoalKind.Adjacent ? FindPathToAdjacent(start, goal, path, nodeLimit) : FindPath(start, goal, path, nodeLimit);

    /// <summary>
    /// The tile next to <paramref name="target"/> that a walker at <paramref name="start"/> reaches most cheaply
    /// (see <see cref="FindPathToAdjacent"/>); false when there is none or the limit ran out first.
    /// </summary>
    public bool TryFindApproachTile(int start, int target, int nodeLimit, out int tile)
    {
        PathResult result = FindPathToAdjacent(start, target, _scratch, nodeLimit);
        tile = result.Status == PathStatus.Found ? result.EndTile : -1;
        return result.Status == PathStatus.Found;
    }

    private bool HasReachableNeighbour(int component, int target)
    {
        int x = Grid.XOf(target);
        int y = Grid.YOf(target);
        for (int d = 0; d < PassabilityGrid.DirectionCount; d++)
        {
            int nx = x + PassabilityGrid.DirectionX(d);
            int ny = y + PassabilityGrid.DirectionY(d);
            if (Grid.Contains(nx, ny) && Connectivity.GetComponent(Grid.Index(nx, ny)) == component)
            {
                return true;
            }
        }

        return false;
    }

    private void CheckTile(int index, string name)
    {
        if (!Grid.ContainsIndex(index))
        {
            throw new ArgumentOutOfRangeException(name, index, $"Tile index outside the {Grid.Size}x{Grid.Size} map.");
        }
    }
}
