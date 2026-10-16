using System.Runtime.CompilerServices;
using Core.WorldGen;

namespace Core.Pathfinding;

/// <summary>
/// Which steps between tiles are allowed (ADR 0003, B2): the single source of the movement rules for A* and the
/// connectivity map. Tiles are indexed <c>y * Size + x</c> like <see cref="TerrainMap"/>.
/// </summary>
/// <remarks>
/// <para>A tile is passable when it is land without a river (water of oceans and lakes and river tiles are
/// impassable) and the <see cref="ITileBlocker"/> does not block it. A straight step between two passable tiles is
/// allowed when their height steps differ by at most <see cref="MaxClimb"/>: a difference of 2 or more is a cliff
/// (B2), in both directions.</para>
/// <para>A diagonal step is allowed only when both straight detours through the two side tiles are allowed and the
/// diagonal itself does not cross a cliff: no cutting a corner of a blocked tile or of a cliff. A consequence used by
/// <see cref="ConnectivityMap"/>: a diagonal step never connects tiles that straight steps do not, so components can be
/// computed over the four straight directions only.</para>
/// <para>Directions are indexed like <see cref="TerrainMap.Directions"/>: 0 E, 1 S, 2 W, 3 N, 4 SE, 5 SW, 6 NW, 7 NE.
/// Straight directions come first, which also fixes the order in which A* looks at neighbours.</para>
/// <para>Speed: the terrain part of the rules is evaluated once into a byte of allowed directions per tile (with the
/// height layer, 2 bytes per tile: 8 MiB at 2048²). A blocker can only remove tiles, so at query time it only clears
/// bits: the step into a blocked neighbour, and for a blocked straight neighbour also the two diagonals past it.</para>
/// </remarks>
public sealed class PassabilityGrid
{
    /// <summary>Largest height difference a single step can climb or descend; 2 or more is a cliff (B2).</summary>
    public const int MaxClimb = 1;

    /// <summary>Number of step directions.</summary>
    public const int DirectionCount = 8;

    /// <summary>Static value of a tile that terrain makes impassable.</summary>
    private const byte Impassable = 0xFF;

    private static readonly int[] _dx = [1, 0, -1, 0, 1, -1, -1, 1];
    private static readonly int[] _dy = [0, 1, 0, -1, 1, 1, -1, -1];

    // Bits a blocked neighbour in direction d removes: the step itself and, for a straight one, the diagonals past it.
    private static readonly int[] _blockedNeighbourBits =
    [
        (1 << 0) | (1 << 4) | (1 << 7),
        (1 << 1) | (1 << 4) | (1 << 5),
        (1 << 2) | (1 << 5) | (1 << 6),
        (1 << 3) | (1 << 6) | (1 << 7),
        1 << 4,
        1 << 5,
        1 << 6,
        1 << 7,
    ];

    // Height step 0..3 per tile, or Impassable for water and rivers.
    private readonly byte[] _static;

    // Allowed directions per tile by the terrain alone.
    private readonly byte[] _staticMask;
    private readonly ITileBlocker _blocker;
    private readonly bool _hasBlocker;
    private readonly ITileCost _cost;
    private readonly bool _uniformCost;
    private readonly int[] _indexOffsets;

    /// <summary>Builds the static layers from <paramref name="terrain"/> (two bytes per tile).</summary>
    /// <param name="terrain">Generated terrain; only its heights and features are read, once.</param>
    /// <param name="blocker">Dynamic blocking layer; <see cref="NoTileBlocker"/> when null.</param>
    /// <param name="cost">Tile costs; <see cref="UniformTileCost"/> when null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ITileCost.MinCost"/> is outside <see cref="PathCosts.MinTileCost"/>..<see cref="PathCosts.MaxTileCost"/>.</exception>
    public PassabilityGrid(TerrainMap terrain, ITileBlocker? blocker = null, ITileCost? cost = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        _blocker = blocker ?? NoTileBlocker.Instance;
        _hasBlocker = _blocker is not NoTileBlocker;
        _cost = cost ?? UniformTileCost.Instance;
        _uniformCost = _cost is UniformTileCost;
        int minCost = _cost.MinCost;
        if (minCost < PathCosts.MinTileCost || minCost > PathCosts.MaxTileCost)
        {
            throw new ArgumentOutOfRangeException(nameof(cost), minCost, $"Minimum tile cost must be {PathCosts.MinTileCost}..{PathCosts.MaxTileCost}.");
        }

        Size = terrain.Size;
        _indexOffsets = new int[DirectionCount];
        for (int d = 0; d < DirectionCount; d++)
        {
            _indexOffsets[d] = (_dy[d] * Size) + _dx[d];
        }

        _static = new byte[terrain.TileCount];
        ReadOnlySpan<byte> heights = terrain.Heights;
        ReadOnlySpan<TileFeatures> features = terrain.Features;
        for (int i = 0; i < _static.Length; i++)
        {
            _static[i] = (features[i] & (TileFeatures.Water | TileFeatures.River)) != 0 ? Impassable : heights[i];
        }

        _staticMask = new byte[terrain.TileCount];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                _staticMask[(y * Size) + x] = (byte)ComputeStaticMask(x, y);
            }
        }
    }

    /// <summary>Side of the square map in tiles.</summary>
    public int Size { get; }

    /// <summary>Number of tiles.</summary>
    public int TileCount => _static.Length;

    /// <summary>
    /// Lower bound of a straight step's cost right now (<see cref="ITileCost.MinCost"/>; <see cref="PathCosts.Straight"/>
    /// while the costs are uniform); the heuristic uses it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cost layer reports a minimum outside <see cref="PathCosts.MinTileCost"/>..<see cref="PathCosts.MaxTileCost"/>.</exception>
    public int MinStraightCost
    {
        get
        {
            if (HasUniformCost)
            {
                return PathCosts.Straight;
            }

            int min = _cost.MinCost;
            return min is >= PathCosts.MinTileCost and <= PathCosts.MaxTileCost
                ? min
                : throw new InvalidOperationException($"Minimum tile cost {min} is outside {PathCosts.MinTileCost}..{PathCosts.MaxTileCost}.");
        }
    }

    /// <summary>Lower bound of a diagonal step's cost right now.</summary>
    public int MinDiagonalCost => PathCosts.Diagonal(MinStraightCost);

    /// <summary>True while every tile costs <see cref="PathCosts.Straight"/> (no cost layer, or one that is <see cref="ITileCost.IsUniform"/> now).</summary>
    public bool HasUniformCost => _uniformCost || _cost.IsUniform;

    /// <summary>The rectangle outside which every tile costs at least <see cref="PathCosts.Straight"/> (<see cref="ITileCost.CheapArea"/>); empty while the costs are uniform.</summary>
    public TileArea CheapArea => HasUniformCost ? TileArea.None : _cost.CheapArea;

    /// <summary>X offset of direction <paramref name="direction"/> (0..7).</summary>
    public static int DirectionX(int direction) => _dx[direction];

    /// <summary>Y offset of direction <paramref name="direction"/> (0..7).</summary>
    public static int DirectionY(int direction) => _dy[direction];

    /// <summary>True for the diagonal directions 4..7.</summary>
    public static bool IsDiagonal(int direction) => direction >= 4;

    /// <summary>True when the tile is on the map.</summary>
    public bool Contains(int x, int y) => (uint)x < (uint)Size && (uint)y < (uint)Size;

    /// <summary>Index of tile (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public int Index(int x, int y)
    {
        if (!Contains(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Tile ({x}, {y}) is outside the {Size}x{Size} map.");
        }

        return (y * Size) + x;
    }

    /// <summary>Column of tile <paramref name="index"/>.</summary>
    public int XOf(int index) => index % Size;

    /// <summary>Row of tile <paramref name="index"/>.</summary>
    public int YOf(int index) => index / Size;

    /// <summary>True when the index is a tile of the map.</summary>
    public bool ContainsIndex(int index) => (uint)index < (uint)_static.Length;

    /// <summary>True when the tile is on the map, not water or river and not blocked.</summary>
    public bool IsPassable(int x, int y) => Contains(x, y) && _static[(y * Size) + x] != Impassable && !IsBlockedAt(x, y);

    /// <summary>True when the tile is on the map, not water or river and not blocked.</summary>
    public bool IsPassable(int index) => ContainsIndex(index) && IsPassable(index % Size, index / Size);

    /// <summary>True when a step from (<paramref name="x"/>, <paramref name="y"/>) in <paramref name="direction"/> (0..7) is allowed.</summary>
    public bool CanStep(int x, int y, int direction)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)direction, (uint)DirectionCount, nameof(direction));
        return (StepMask(x, y) & (1 << direction)) != 0;
    }

    /// <summary>
    /// Bit <c>d</c> is set when the step from (<paramref name="x"/>, <paramref name="y"/>) in direction <c>d</c> is
    /// allowed (see the class remarks). Zero for an impassable or off-map tile.
    /// </summary>
    public int StepMask(int x, int y) => Contains(x, y) ? StepMaskAt((y * Size) + x, x, y) : 0;

    /// <summary>Like <see cref="StepMask"/> but only the four straight directions (bits 0..3).</summary>
    public int StraightStepMask(int x, int y) => StepMask(x, y) & 0b1111;

    /// <summary>Straight-step cost of entering tile (<paramref name="x"/>, <paramref name="y"/>) from <see cref="ITileCost"/>.</summary>
    /// <exception cref="InvalidOperationException">The cost provider returned a value outside <see cref="MinStraightCost"/>..<see cref="PathCosts.MaxTileCost"/>.</exception>
    public int TileCost(int x, int y) => HasUniformCost ? PathCosts.Straight : CheckedCost(x, y, MinStraightCost);

    /// <summary>Cost of the step into (<paramref name="x"/>, <paramref name="y"/>): the tile cost, or its diagonal for a diagonal step.</summary>
    public int StepCost(int x, int y, bool diagonal)
    {
        int cost = TileCost(x, y);
        return diagonal ? PathCosts.Diagonal(cost) : cost;
    }

    /// <summary>
    /// <see cref="StepCost"/> for a search that already read <paramref name="minCost"/> (<see cref="MinStraightCost"/>)
    /// and knows the costs are not uniform: one call into the cost layer per step.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int StepCost(int x, int y, bool diagonal, int minCost)
    {
        int cost = _cost.GetCost(x, y);
        if ((uint)(cost - minCost) > (uint)(PathCosts.MaxTileCost - minCost))
        {
            ThrowCostOutOfRange(x, y, cost, minCost);
        }

        return diagonal ? PathCosts.Diagonal(cost) : cost;
    }

    /// <summary>Lower bound of the cost from tile (<paramref name="ax"/>, <paramref name="ay"/>) to (<paramref name="bx"/>, <paramref name="by"/>).</summary>
    public int Heuristic(int ax, int ay, int bx, int by) => PathCosts.Octile(bx - ax, by - ay, MinStraightCost, MinDiagonalCost);

    /// <summary>Index offset of direction <paramref name="direction"/> on this map.</summary>
    internal int IndexOffset(int direction) => _indexOffsets[direction];

    /// <summary><see cref="StepMask"/> of an on-map tile given by its index and coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int StepMaskAt(int index, int x, int y)
    {
        int mask = _staticMask[index];
        return _hasBlocker && mask != 0 ? RemoveBlocked(x, y, mask) : mask;
    }

    private int RemoveBlocked(int x, int y, int mask)
    {
        if (_blocker.IsBlocked(x, y))
        {
            return 0;
        }

        for (int d = 0; d < DirectionCount; d++)
        {
            if ((mask & (1 << d)) != 0 && _blocker.IsBlocked(x + _dx[d], y + _dy[d]))
            {
                mask &= ~_blockedNeighbourBits[d];
            }
        }

        return mask;
    }

    private bool IsBlockedAt(int x, int y) => _hasBlocker && _blocker.IsBlocked(x, y);

    private int CheckedCost(int x, int y, int minCost)
    {
        int cost = _cost.GetCost(x, y);
        if (cost < minCost || cost > PathCosts.MaxTileCost)
        {
            ThrowCostOutOfRange(x, y, cost, minCost);
        }

        return cost;
    }

    private static void ThrowCostOutOfRange(int x, int y, int cost, int minCost) =>
        throw new InvalidOperationException($"Tile cost {cost} at ({x}, {y}) is outside {minCost}..{PathCosts.MaxTileCost}.");

    private int ComputeStaticMask(int x, int y)
    {
        int c = StaticHeight(x, y);
        if (c < 0)
        {
            return 0;
        }

        int e = StaticHeight(x + 1, y);
        int s = StaticHeight(x, y + 1);
        int w = StaticHeight(x - 1, y);
        int n = StaticHeight(x, y - 1);
        int mask = (Joins(c, e) ? 1 : 0) | (Joins(c, s) ? 2 : 0) | (Joins(c, w) ? 4 : 0) | (Joins(c, n) ? 8 : 0);
        if ((mask & 0b0011) == 0b0011 && DiagonalJoins(c, e, s, StaticHeight(x + 1, y + 1)))
        {
            mask |= 1 << 4;
        }

        if ((mask & 0b0110) == 0b0110 && DiagonalJoins(c, w, s, StaticHeight(x - 1, y + 1)))
        {
            mask |= 1 << 5;
        }

        if ((mask & 0b1100) == 0b1100 && DiagonalJoins(c, w, n, StaticHeight(x - 1, y - 1)))
        {
            mask |= 1 << 6;
        }

        if ((mask & 0b1001) == 0b1001 && DiagonalJoins(c, e, n, StaticHeight(x + 1, y - 1)))
        {
            mask |= 1 << 7;
        }

        return mask;
    }

    // Height step of a tile the terrain lets one stand on; -1 for water, rivers and off-map.
    private int StaticHeight(int x, int y)
    {
        if (!Contains(x, y))
        {
            return -1;
        }

        byte value = _static[(y * Size) + x];
        return value == Impassable ? -1 : value;
    }

    private static bool Joins(int a, int b) => a >= 0 && b >= 0 && Math.Abs(a - b) <= MaxClimb;

    // c is the start, a and b the two side tiles, d the diagonal target; c-a and c-b are already known to join.
    private static bool DiagonalJoins(int c, int a, int b, int d) => Joins(a, d) && Joins(b, d) && Joins(c, d);
}
