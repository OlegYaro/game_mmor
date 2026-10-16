using Core.Pathfinding;

namespace Core.Tiles;

/// <summary>Trails and roads of <see cref="TileChanges"/> (FR-WORLD-15): walk counters, classes and path costs (<see cref="WalkLayer"/>).</summary>
/// <remarks>
/// <para>Path costs (<see cref="ITileCost"/>): plain ground costs <see cref="PathCosts.Straight"/>, a trail and a road that
/// times <see cref="Content.RoadsBalanceDef.TrailCostPermille"/> and <see cref="Content.RoadsBalanceDef.RoadCostPermille"/> / 1000.
/// <see cref="MinCost"/> is the cost of the cheapest class that exists right now, and <see cref="IsUniform"/> holds while
/// no tile is a trail or a road, so A* keeps its plain octile heuristic (and its paths) until the first trail appears;
/// after that the heuristic uses the cheapest class and stays admissible (ADR 0003). Costs change only the price of a
/// path, never the connectivity.</para>
/// </remarks>
public sealed partial class TileChanges
{
    /// <summary>Tiles kept by the walk layer (walked, not yet compacted; faded ones included).</summary>
    public int WalkedTiles => _walks.Entries;

    /// <summary>Tiles that are trails now.</summary>
    public int TrailTiles => _walks.Trails;

    /// <summary>Tiles that are roads now.</summary>
    public int RoadTiles => _walks.Roads;

    /// <inheritdoc />
    public int MinCost => _walks.MinCost;

    /// <inheritdoc />
    public bool IsUniform => _walks.IsUniform;

    /// <inheritdoc />
    /// <remarks>Whole chunks: the rectangle around every chunk that has a trail or a road.</remarks>
    public TileArea CheapArea => _walks.CheapArea;

    /// <summary>Class of tile (<paramref name="x"/>, <paramref name="y"/>) now: plain ground, a trail or a road.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public RoadClass GetRoadClass(int x, int y)
    {
        CheckTile(x, y);
        return _walks.ClassAt(x, y);
    }

    /// <summary>Walk counter of tile (<paramref name="x"/>, <paramref name="y"/>) now, faded to <see cref="Tick"/> (0..maxCounter).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public int GetWalkCounter(int x, int y)
    {
        CheckTile(x, y);
        return _walks.CounterAt(x, y);
    }

    /// <inheritdoc />
    /// <remarks>Called by the pathfinder for on-map tiles only; no range check on this hot path.</remarks>
    public int GetCost(int x, int y) => _walks.Cost(x, y);

    /// <summary>
    /// How long a step onto tile (<paramref name="x"/>, <paramref name="y"/>) takes, in thousandths of a step on plain
    /// ground: 1000, or the trail or road cost permille.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public int GetStepPermille(int x, int y)
    {
        CheckTile(x, y);
        return _walks.StepPermille(x, y);
    }

    /// <summary>
    /// Moves the layer's clock to the simulation's tick (called at the start of every tick and on load): classes whose
    /// counters faded below their keep threshold fall now.
    /// </summary>
    internal void AdvanceTo(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tick, Tick);
        _walks.AdvanceTo(tick);
    }

    /// <summary>A walker stepped onto tile (<paramref name="x"/>, <paramref name="y"/>) at <see cref="Tick"/>: its counter grows and its class may rise.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    internal void Trample(int x, int y)
    {
        CheckTile(x, y);
        _walks.Step(x, y);
    }

    /// <summary>Forgets walked tiles that faded back to plain ground (memory only: state and hash stay the same).</summary>
    internal void CompactWalks() => _walks.Compact();
}
