using System.Numerics;
using System.Runtime.CompilerServices;

namespace Core.Pathfinding;

/// <summary>
/// A* over a <see cref="PassabilityGrid"/> (ADR 0003): integer costs, a binary heap ordered by (f, h, tile index),
/// neighbours in the fixed direction order, per-tile node records reused through generation stamps.
/// </summary>
/// <remarks>
/// <para>Memory: one 8-byte record per tile (g, and a word with the heap position, the parent direction and the
/// open/closed flags) plus a 16-bit generation stamp per run of 32 records, allocated on the first search: 2 MiB at
/// 512², 32.25 MiB at 2048². A run whose stamp is not the current generation is cleared when the search first
/// touches it (256 bytes), so nothing is cleared up front and untouched parts of the map cost nothing; when the
/// 16-bit counter wraps, the stamps are reset once per 65535 searches.</para>
/// <para>The heap is indexed (decrease-key): a cheaper route to an open tile moves its entry up instead of adding a
/// duplicate, so every pop is a real expansion. The heap array only grows, so after warm-up a search allocates
/// nothing.</para>
/// <para>The heuristic is the octile distance with the grid's minimum step costs, admissible and consistent even
/// when tiles cost less than 10, so a found path is a cheapest one; with a cost layer it adds the plain-ground surcharge
/// for the steps that must land outside the layer's cheap area (<see cref="OutsideBound"/>), which keeps it strong away
/// from trails and roads. The costs, their minimum and the cheap area are read once per search (they may change between
/// searches as trails appear and fade, never during one).</para>
/// </remarks>
internal sealed class AStarSearch
{
    private const int SegmentShift = 5;
    private const int SegmentSize = 1 << SegmentShift;

    // Node.Link layout: bits 0..3 parent direction (RootDirection for the start), bit 4 closed, bit 5 seen,
    // bits 6.. heap position while open.
    private const int DirectionMask = 0x0F;
    private const int RootDirection = 0x08;
    private const int ClosedFlag = 0x10;
    private const int SeenFlag = 0x20;
    private const int FlagBits = 0x3F;
    private const int PositionShift = 6;

    private readonly PassabilityGrid _grid;
    private Node[] _nodes = [];
    private ushort[] _segmentStamp = [];
    private ushort _generation;
    private OpenEntry[] _heap = [];
    private int _heapCount;

    public AStarSearch(PassabilityGrid grid) => _grid = grid;

    /// <summary>Bytes held by the node records, the stamps and the heap (0 before the first search).</summary>
    public long MemoryBytes => (8L * _nodes.Length) + (2L * _segmentStamp.Length) + (16L * _heap.Length);

    /// <summary>
    /// Runs one search from <paramref name="start"/> (passable) to <paramref name="goal"/>, writing the path into
    /// <paramref name="path"/>. Returns <see cref="PathStatus.Found"/>, <see cref="PathStatus.Partial"/> (limit hit) or
    /// <see cref="PathStatus.Unreachable"/> (the open list ran dry: possible only when the caller skipped the connectivity check).
    /// </summary>
    public PathResult Search(int start, int goal, PathGoalKind kind, int nodeLimit, List<int> path)
    {
        EnsureArrays();
        NextGeneration();
        Node[] nodes = _nodes;
        int size = _grid.Size;
        int goalX = goal % size;
        int goalY = goal / size;
        bool adjacent = kind == PathGoalKind.Adjacent;
        int box = adjacent ? 1 : 0;
        bool uniform = _grid.HasUniformCost;
        int minStraight = _grid.MinStraightCost;
        int minDiagonal = _grid.MinDiagonalCost;
        var outside = new OutsideBound(uniform ? 0 : PathCosts.Straight - minStraight, uniform ? TileArea.None : _grid.CheapArea, goalX, goalY, box);

        _heapCount = 0;
        int startH = Heuristic(start % size, start / size, goalX, goalY, box, minStraight, minDiagonal, outside);
        Touch(start);
        nodes[start] = new Node { G = 0, Link = SeenFlag | RootDirection };
        Push(new OpenEntry(startH, startH, start));

        int best = start;
        int bestH = startH;
        int bestG = 0;
        int expanded = 0;
        while (_heapCount > 0)
        {
            OpenEntry entry = Pop();
            int current = entry.Index;
            int g = entry.F - entry.H;
            int y = current / size;
            int x = current - (y * size);
            if (adjacent ? IsAdjacentGoal(x, y, goalX, goalY) : current == goal)
            {
                return Finish(PathStatus.Found, current, g, expanded, path);
            }

            nodes[current].Link |= ClosedFlag;
            expanded++;
            if (entry.H < bestH || (entry.H == bestH && (g < bestG || (g == bestG && current < best))))
            {
                best = current;
                bestH = entry.H;
                bestG = g;
            }

            if (expanded >= nodeLimit)
            {
                return Finish(PathStatus.Partial, best, bestG, expanded, path);
            }

            int mask = _grid.StepMaskAt(current, x, y);
            while (mask != 0)
            {
                int d = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int next = current + _grid.IndexOffset(d);
                Touch(next);
                ref Node neighbour = ref nodes[next];
                int link = neighbour.Link;
                if ((link & ClosedFlag) != 0)
                {
                    continue;
                }

                int nx = x + PassabilityGrid.DirectionX(d);
                int ny = y + PassabilityGrid.DirectionY(d);
                bool diagonal = d >= 4;
                int step = uniform ? (diagonal ? PathCosts.DiagonalBase : PathCosts.Straight) : _grid.StepCost(nx, ny, diagonal, minStraight);
                int nextG = g + step;
                if ((link & SeenFlag) != 0)
                {
                    if (nextG >= neighbour.G)
                    {
                        continue;
                    }

                    // Cheaper route to an open tile: same h, smaller f, so the entry only moves up.
                    neighbour.G = nextG;
                    neighbour.Link = (link & ~DirectionMask) | d;
                    int position = link >> PositionShift;
                    int h = _heap[position].H;
                    SiftUp(position, new OpenEntry(nextG + h, h, next));
                }
                else
                {
                    neighbour.G = nextG;
                    neighbour.Link = SeenFlag | d;
                    int h = Heuristic(nx, ny, goalX, goalY, box, minStraight, minDiagonal, outside);
                    Push(new OpenEntry(nextG + h, h, next));
                }
            }
        }

        path.Clear();
        return new PathResult(PathStatus.Unreachable, 0, 0, -1, expanded);
    }

    private static bool IsAdjacentGoal(int x, int y, int goalX, int goalY)
    {
        int dx = Math.Abs(x - goalX);
        int dy = Math.Abs(y - goalY);
        return dx <= 1 && dy <= 1 && (dx | dy) != 0;
    }

    // Octile distance to the goal, or for an approach search (box = 1) to the 3×3 box around the target, which holds
    // every goal tile, with the cheapest step costs; plus the plain-ground surcharge for the steps that must land outside
    // the cheap area (see OutsideBound): a lower bound either way, and consistent.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Heuristic(int x, int y, int goalX, int goalY, int box, int straight, int diagonal, in OutsideBound outside)
    {
        int dx = Math.Max(0, Math.Abs(goalX - x) - box);
        int dy = Math.Max(0, Math.Abs(goalY - y) - box);
        int min = Math.Min(dx, dy);
        int max = Math.Max(dx, dy);
        int h = (diagonal * min) + (straight * (max - min));
        return outside.Surcharge == 0 ? h : h + (outside.Surcharge * outside.Steps(x, y, max));
    }

    private PathResult Finish(PathStatus status, int end, int cost, int expanded, List<int> path)
    {
        path.Clear();
        int current = end;
        while (true)
        {
            path.Add(current);
            int direction = _nodes[current].Link & DirectionMask;
            if (direction == RootDirection)
            {
                break;
            }

            current -= _grid.IndexOffset(direction);
        }

        path.Reverse();
        return new PathResult(status, cost, path.Count, end, expanded);
    }

    private void EnsureArrays()
    {
        if (_nodes.Length == 0)
        {
            _nodes = new Node[_grid.TileCount];
            _segmentStamp = new ushort[(_grid.TileCount + SegmentSize - 1) >> SegmentShift];
            _heap = new OpenEntry[256];
        }
    }

    private void NextGeneration()
    {
        if (_generation == ushort.MaxValue)
        {
            Array.Clear(_segmentStamp);
            _generation = 0;
        }

        _generation++;
    }

    // Clears the run of 32 records holding tile `index` the first time this search touches it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Touch(int index)
    {
        int segment = index >> SegmentShift;
        if (_segmentStamp[segment] != _generation)
        {
            _segmentStamp[segment] = _generation;
            int first = segment << SegmentShift;
            Array.Clear(_nodes, first, Math.Min(SegmentSize, _nodes.Length - first));
        }
    }

    private void Push(OpenEntry entry)
    {
        if (_heapCount == _heap.Length)
        {
            Array.Resize(ref _heap, _heap.Length * 2);
        }

        SiftUp(_heapCount++, entry);
    }

    // Moves `entry` from slot `i` towards the root, keeping every node's heap position up to date.
    private void SiftUp(int i, OpenEntry entry)
    {
        OpenEntry[] heap = _heap;
        Node[] nodes = _nodes;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            OpenEntry above = heap[parent];
            if (!entry.Precedes(above))
            {
                break;
            }

            heap[i] = above;
            SetPosition(nodes, above.Index, i);
            i = parent;
        }

        heap[i] = entry;
        SetPosition(nodes, entry.Index, i);
    }

    private OpenEntry Pop()
    {
        OpenEntry[] heap = _heap;
        Node[] nodes = _nodes;
        OpenEntry top = heap[0];
        int count = --_heapCount;
        if (count == 0)
        {
            return top;
        }

        OpenEntry last = heap[count];
        int i = 0;
        while (true)
        {
            int child = (2 * i) + 1;
            if (child >= count)
            {
                break;
            }

            if (child + 1 < count && heap[child + 1].Precedes(heap[child]))
            {
                child++;
            }

            OpenEntry below = heap[child];
            if (!below.Precedes(last))
            {
                break;
            }

            heap[i] = below;
            SetPosition(nodes, below.Index, i);
            i = child;
        }

        heap[i] = last;
        SetPosition(nodes, last.Index, i);
        return top;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetPosition(Node[] nodes, int index, int position)
    {
        ref int link = ref nodes[index].Link;
        link = (link & FlagBits) | (position << PositionShift);
    }

    /// <summary>
    /// The part of the heuristic for a cost layer with cheap tiles (trails, roads) in <see cref="ITileCost.CheapArea"/>.
    /// Every step that lands outside the area costs at least plain ground, that is <see cref="Surcharge"/> more than the
    /// cheapest step the octile part counts. From a tile at Chebyshev distance <c>d</c> from the area, a path to the goal
    /// lands outside it at least <c>min(c, max(d − 1, 0) + g)</c> times, where <c>c</c> is the Chebyshev distance to the
    /// goal (a path that never enters the area) and <c>g</c> the goal's distance from the area (a path that enters it).
    /// That count is the exact distance in a relaxed graph where outside landings cost 1 and inside ones 0, so it changes
    /// by at most 1 per step and not at all on a step into the area; adding <see cref="Surcharge"/> times it to the
    /// octile part keeps the heuristic consistent (A* closes nodes for good). Far from the trails it is the plain octile
    /// distance on straight lines; inside the area it adds nothing.
    /// </summary>
    private readonly struct OutsideBound(int surcharge, TileArea area, int goalX, int goalY, int box)
    {
        public readonly int Surcharge = surcharge;
        private readonly TileArea _area = area;
        private readonly int _goalOutside = surcharge == 0 ? 0 : Math.Max(area.DistanceFrom(goalX, goalY) - box, 0);

        // Landings outside the area on any path from (x, y) whose Chebyshev distance to the goal is `steps`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Steps(int x, int y, int steps) => Math.Min(steps, Math.Max(_area.DistanceFrom(x, y) - 1, 0) + _goalOutside);
    }

    /// <summary>Per-tile search record; valid only when its run's stamp equals the current generation.</summary>
    private struct Node
    {
        public int G;
        public int Link;
    }

    /// <summary>
    /// Open-list entry: the key packs f (high 32 bits) and h (low 32 bits), so (key, index) is the full order
    /// (f, h, index) that makes the search independent of heap internals.
    /// </summary>
    private readonly struct OpenEntry(int f, int h, int index)
    {
        public readonly long Key = ((long)f << 32) | (uint)h;
        public readonly int Index = index;

        public int F => (int)(Key >> 32);

        public int H => (int)Key;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Precedes(OpenEntry other) => Key < other.Key || (Key == other.Key && Index < other.Index);
    }
}
