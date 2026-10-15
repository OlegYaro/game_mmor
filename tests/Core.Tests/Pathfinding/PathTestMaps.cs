using Core.Determinism;
using Core.Pathfinding;
using Core.WorldGen;

namespace Core.Tests.Pathfinding;

/// <summary>A test blocker over a bool array.</summary>
internal sealed class TestBlocker(int size) : ITileBlocker
{
    private readonly bool[] _blocked = new bool[size * size];

    public int Size { get; } = size;

    public bool IsBlocked(int x, int y) => _blocked[(y * Size) + x];

    public void Set(int x, int y, bool blocked) => _blocked[(y * Size) + x] = blocked;
}

/// <summary>A test cost layer over an int array.</summary>
internal sealed class TestCost(int size, int minCost) : ITileCost
{
    private readonly int[] _costs = Enumerable.Repeat(PathCosts.Straight, size * size).ToArray();

    public int Size { get; } = size;

    public int MinCost { get; } = minCost;

    public int GetCost(int x, int y) => _costs[(y * Size) + x];

    public void Set(int x, int y, int cost) => _costs[(y * Size) + x] = cost;
}

/// <summary>A small map with its blocker and cost layers, plus an independent reference of the movement rules.</summary>
internal sealed class TestMap
{
    public TestMap(TerrainMap terrain, TestBlocker blocker, TestCost? cost = null)
    {
        Terrain = terrain;
        Blocker = blocker;
        Cost = cost;
        Grid = new PassabilityGrid(terrain, blocker, cost);
        Pathfinder = new Pathfinder(Grid);
    }

    public TerrainMap Terrain { get; }

    public TestBlocker Blocker { get; }

    public TestCost? Cost { get; }

    public PassabilityGrid Grid { get; }

    public Pathfinder Pathfinder { get; }

    public int Size => Terrain.Size;

    public int Index(int x, int y) => (y * Size) + x;

    /// <summary>Reference: height of a walkable tile, -1 otherwise (written from the rules, not from the grid).</summary>
    public int RefHeight(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Size || y >= Size || Blocker.IsBlocked(x, y))
        {
            return -1;
        }

        TileFeatures f = Terrain.GetFeatures(x, y);
        return (f & (TileFeatures.Water | TileFeatures.River)) != 0 ? -1 : Terrain.GetHeight(x, y);
    }

    /// <summary>Reference: may a walker step from (x, y) by (dx, dy), each -1..1?</summary>
    public bool RefCanStep(int x, int y, int dx, int dy)
    {
        int c = RefHeight(x, y);
        int d = RefHeight(x + dx, y + dy);
        if (!Joins(c, d))
        {
            return false;
        }

        if (dx == 0 || dy == 0)
        {
            return true;
        }

        int a = RefHeight(x + dx, y);
        int b = RefHeight(x, y + dy);
        return Joins(c, a) && Joins(c, b) && Joins(a, d) && Joins(b, d);
    }

    // Both tiles walkable and at most one height step apart (B2: two or more is a cliff).
    private static bool Joins(int a, int b) => a >= 0 && b >= 0 && Math.Abs(a - b) <= 1;

    public int RefStepCost(int toX, int toY, bool diagonal)
    {
        int cost = Cost?.GetCost(toX, toY) ?? PathCosts.Straight;
        return diagonal ? cost * 14 / 10 : cost;
    }

    /// <summary>Reference Dijkstra from a tile: cheapest cost to every tile, int.MaxValue where unreachable.</summary>
    public int[] Dijkstra(int start)
    {
        int[] dist = Enumerable.Repeat(int.MaxValue, Size * Size).ToArray();
        if (RefHeight(start % Size, start / Size) < 0)
        {
            return dist;
        }

        var open = new PriorityQueue<int, int>();
        dist[start] = 0;
        open.Enqueue(start, 0);
        while (open.TryDequeue(out int current, out int d))
        {
            if (d != dist[current])
            {
                continue;
            }

            int x = current % Size;
            int y = current / Size;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dy) == 0 || !RefCanStep(x, y, dx, dy))
                    {
                        continue;
                    }

                    int next = Index(x + dx, y + dy);
                    int nd = d + RefStepCost(x + dx, y + dy, dx != 0 && dy != 0);
                    if (nd < dist[next])
                    {
                        dist[next] = nd;
                        open.Enqueue(next, nd);
                    }
                }
            }
        }

        return dist;
    }

    /// <summary>Reference flood fill over straight steps: a component id per tile, -1 on impassable tiles.</summary>
    public int[] ReferenceComponents()
    {
        int[] comp = Enumerable.Repeat(-1, Size * Size).ToArray();
        int next = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < comp.Length; i++)
        {
            if (comp[i] >= 0 || RefHeight(i % Size, i / Size) < 0)
            {
                continue;
            }

            comp[i] = next;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                int x = current % Size;
                int y = current / Size;
                foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    if (RefCanStep(x, y, dx, dy) && comp[Index(x + dx, y + dy)] < 0)
                    {
                        comp[Index(x + dx, y + dy)] = next;
                        stack.Push(Index(x + dx, y + dy));
                    }
                }
            }

            next++;
        }

        return comp;
    }

    /// <summary>Asserts that a path is a chain of allowed steps from start to end with the given total cost.</summary>
    public void AssertValidPath(IReadOnlyList<int> path, int start, int cost)
    {
        Assert.NotEmpty(path);
        Assert.Equal(start, path[0]);
        int sum = 0;
        for (int i = 1; i < path.Count; i++)
        {
            int ax = path[i - 1] % Size;
            int ay = path[i - 1] / Size;
            int bx = path[i] % Size;
            int by = path[i] / Size;
            int dx = bx - ax;
            int dy = by - ay;
            Assert.InRange(dx, -1, 1);
            Assert.InRange(dy, -1, 1);
            Assert.True((dx | dy) != 0, "Path repeats a tile.");
            Assert.True(RefCanStep(ax, ay, dx, dy), $"Step ({ax},{ay}) -> ({bx},{by}) breaks the movement rules.");
            sum += RefStepCost(bx, by, dx != 0 && dy != 0);
        }

        Assert.Equal(cost, sum);
    }
}

/// <summary>Builders of test terrains.</summary>
internal static class PathTestMaps
{
    /// <summary>
    /// Parses a square map. Legend: <c>.</c> or <c>0</c>–<c>3</c> land of that height, <c>~</c> lake, <c>o</c> ocean,
    /// <c>r</c> river (height 0), <c>#</c> flat land blocked by the test blocker.
    /// </summary>
    public static TestMap Parse(params string[] rows) => Parse(null, rows);

    public static TestMap Parse(TestCost? cost, params string[] rows)
    {
        int size = rows.Length;
        Assert.All(rows, row => Assert.Equal(size, row.Length));
        var terrain = new TerrainMap(0, size, 0);
        var blocker = new TestBlocker(size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size) + x;
                char c = rows[y][x];
                switch (c)
                {
                    case '.':
                        break;
                    case >= '0' and <= '3':
                        terrain.HeightArray[i] = (byte)(c - '0');
                        break;
                    case '~':
                        terrain.FeatureArray[i] = TileFeatures.Water | TileFeatures.Lake;
                        break;
                    case 'o':
                        terrain.FeatureArray[i] = TileFeatures.Water | TileFeatures.Ocean;
                        break;
                    case 'r':
                        terrain.FeatureArray[i] = TileFeatures.River;
                        break;
                    case '#':
                        blocker.Set(x, y, true);
                        break;
                    default:
                        throw new ArgumentException($"Unknown map char '{c}'.", nameof(rows));
                }
            }
        }

        return new TestMap(terrain, blocker, cost);
    }

    /// <summary>A flat open map.</summary>
    public static TestMap Open(int size) => Parse([.. Enumerable.Repeat(new string('.', size), size)]);

    /// <summary>
    /// A random map: heights smooth over 4×4 blocks with single-step noise (so cliffs occur but do not dominate),
    /// lakes, rivers and blocked tiles at the given permille rates; with <paramref name="variableCost"/> each tile
    /// costs 3..40.
    /// </summary>
    public static TestMap Random(ulong seed, int size, int waterPermille = 60, int blockedPermille = 150, bool variableCost = false)
    {
        var rng = new Xoshiro256StarStar(seed);
        var terrain = new TerrainMap(seed, size, 0);
        var blocker = new TestBlocker(size);
        TestCost? cost = variableCost ? new TestCost(size, 3) : null;
        int blocks = (size + 3) / 4;
        int[] coarse = new int[blocks * blocks];
        for (int i = 0; i < coarse.Length; i++)
        {
            coarse[i] = rng.NextInt(TerrainMap.MaxHeight + 1);
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size) + x;
                int h = coarse[((y / 4) * blocks) + (x / 4)] + (rng.ChancePermille(150) ? rng.NextInt(-1, 2) : 0);
                terrain.HeightArray[i] = (byte)Math.Clamp(h, 0, TerrainMap.MaxHeight);
                int roll = rng.NextPermille();
                if (roll < waterPermille / 2)
                {
                    terrain.FeatureArray[i] = TileFeatures.Water | TileFeatures.Lake;
                    terrain.HeightArray[i] = 0;
                }
                else if (roll < waterPermille)
                {
                    terrain.FeatureArray[i] = TileFeatures.River;
                }
                else if (roll < waterPermille + blockedPermille)
                {
                    blocker.Set(x, y, true);
                }

                cost?.Set(x, y, rng.NextInt(3, 41));
            }
        }

        return new TestMap(terrain, blocker, cost);
    }
}
