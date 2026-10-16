namespace Core.Pathfinding;

/// <summary>
/// Dynamic blocking layer on top of the static terrain passability (ADR 0003): buildings, walls, changed tiles.
/// The static terrain (water, rivers, cliffs) is already impassable; a blocker can only remove passable tiles.
/// </summary>
/// <remarks>
/// <para>Answers must be deterministic and must change only together with the simulation state. Whenever the answer
/// for a tile changes, the owner calls <see cref="Pathfinder.NotifyTileChanged"/> (or
/// <see cref="ConnectivityMap.Invalidate"/>) for that tile in the same tick, otherwise reachability answers go stale.</para>
/// </remarks>
public interface ITileBlocker
{
    /// <summary>True when tile (<paramref name="x"/>, <paramref name="y"/>) cannot be entered. Called only for tiles on the map.</summary>
    bool IsBlocked(int x, int y);
}

/// <summary>The default blocker: nothing is blocked beyond the static terrain.</summary>
public sealed class NoTileBlocker : ITileBlocker
{
    private NoTileBlocker()
    {
    }

    /// <summary>The shared instance.</summary>
    public static NoTileBlocker Instance { get; } = new();

    /// <inheritdoc />
    public bool IsBlocked(int x, int y) => false;
}

/// <summary>
/// Integer cost of entering a tile with a straight step (ADR 0003): <see cref="PathCosts.Straight"/> (10) is the
/// normal ground, roads (M3) are cheaper, rough ground or a wall to breach (M4) dearer. A diagonal step into the tile
/// costs <see cref="PathCosts.Diagonal(int)"/> of that value. Costs change only the price of a path, never the
/// connectivity, so changing them needs no <see cref="ConnectivityMap.Invalidate"/>.
/// </summary>
/// <remarks>
/// Costs, <see cref="MinCost"/> and <see cref="IsUniform"/> may change between two searches (trails appear and fade)
/// but never during one, and must depend only on the simulation state, so searches stay deterministic.
/// </remarks>
public interface ITileCost
{
    /// <summary>
    /// Lower bound of <see cref="GetCost"/> over every tile right now, <see cref="PathCosts.MinTileCost"/> or more. The
    /// A* heuristic is computed from it, so it must never exceed a real cost (else paths stop being optimal).
    /// </summary>
    int MinCost { get; }

    /// <summary>
    /// True while every tile costs exactly <see cref="PathCosts.Straight"/>: the search then skips the cost layer and
    /// uses the plain octile heuristic, giving the same paths as without a cost layer.
    /// </summary>
    bool IsUniform { get; }

    /// <summary>
    /// A rectangle that holds every tile cheaper than <see cref="PathCosts.Straight"/> right now: outside it every tile
    /// costs at least <see cref="PathCosts.Straight"/>. It may be larger than needed (<see cref="TileArea.All"/> is always
    /// correct); the smaller it is, the stronger the A* heuristic far from it (ADR 0003).
    /// </summary>
    TileArea CheapArea { get; }

    /// <summary>Straight-step cost of entering tile (<paramref name="x"/>, <paramref name="y"/>), from <see cref="MinCost"/> to <see cref="PathCosts.MaxTileCost"/>.</summary>
    int GetCost(int x, int y);
}

/// <summary>The default cost: every tile costs <see cref="PathCosts.Straight"/>.</summary>
public sealed class UniformTileCost : ITileCost
{
    private UniformTileCost()
    {
    }

    /// <summary>The shared instance.</summary>
    public static UniformTileCost Instance { get; } = new();

    /// <inheritdoc />
    public int MinCost => PathCosts.Straight;

    /// <inheritdoc />
    public bool IsUniform => true;

    /// <inheritdoc />
    public TileArea CheapArea => TileArea.None;

    /// <inheritdoc />
    public int GetCost(int x, int y) => PathCosts.Straight;
}

/// <summary>An inclusive rectangle of tiles (<see cref="ITileCost.CheapArea"/>); empty when a minimum exceeds its maximum.</summary>
/// <param name="MinX">Leftmost column.</param>
/// <param name="MinY">Top row.</param>
/// <param name="MaxX">Rightmost column.</param>
/// <param name="MaxY">Bottom row.</param>
public readonly record struct TileArea(int MinX, int MinY, int MaxX, int MaxY)
{
    // Far beyond any map, yet far from overflowing when a tile coordinate is subtracted.
    private const int Far = 1 << 28;

    /// <summary>Every tile of any map.</summary>
    public static TileArea All { get; } = new(-Far, -Far, Far, Far);

    /// <summary>No tile.</summary>
    public static TileArea None { get; } = new(Far, Far, -Far, -Far);

    /// <summary>True when the area holds no tile.</summary>
    public bool IsEmpty => MinX > MaxX || MinY > MaxY;

    /// <summary>
    /// Chebyshev distance in tiles from (<paramref name="x"/>, <paramref name="y"/>) to the nearest tile of the area: 0
    /// inside, the number of steps needed to enter it outside; <see cref="int.MaxValue"/> / 2 for an empty area.
    /// </summary>
    public int DistanceFrom(int x, int y) => IsEmpty
        ? int.MaxValue / 2
        : Math.Max(Math.Max(Math.Max(MinX - x, x - MaxX), 0), Math.Max(Math.Max(MinY - y, y - MaxY), 0));
}

/// <summary>Integer step costs (ADR 0003): 10 for a straight step, 14 for a diagonal one on normal ground.</summary>
public static class PathCosts
{
    /// <summary>Cost of a straight step on normal ground.</summary>
    public const int Straight = 10;

    /// <summary>Cost of a diagonal step on normal ground.</summary>
    public const int DiagonalBase = 14;

    /// <summary>Smallest allowed tile cost.</summary>
    public const int MinTileCost = 1;

    /// <summary>
    /// Largest allowed tile cost. With the per-request node limit capped at <see cref="PathfinderConfig.MaxNodeLimit"/>
    /// it keeps every path cost (at most one diagonal step per expanded node) inside <see cref="int"/>.
    /// </summary>
    public const int MaxTileCost = 1000;

    /// <summary>Cost of a diagonal step into a tile whose straight cost is <paramref name="straightCost"/>: <c>cost * 14 / 10</c>, rounded down.</summary>
    public static int Diagonal(int straightCost) => straightCost * DiagonalBase / Straight;

    /// <summary>
    /// Octile distance with the given step costs: the cheapest path over open ground, so an admissible and
    /// consistent A* heuristic whenever no step costs less.
    /// </summary>
    public static int Octile(int dx, int dy, int straight, int diagonal)
    {
        dx = Math.Abs(dx);
        dy = Math.Abs(dy);
        int min = Math.Min(dx, dy);
        int max = Math.Max(dx, dy);
        return (diagonal * min) + (straight * (max - min));
    }
}
