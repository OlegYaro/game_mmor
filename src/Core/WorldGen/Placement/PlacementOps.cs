using Core.Determinism;

namespace Core.WorldGen.Placement;

/// <summary>Integer helpers shared by the placement steps (5–8).</summary>
internal static class PlacementOps
{
    /// <summary>A 64-bit hash of a tile index (or any integer) under a salt; the per-tile "white noise" of the steps.</summary>
    public static ulong Hash(ulong salt, long value) => SplitMix64.Mix(salt ^ SplitMix64.Mix((ulong)value + SplitMix64.Gamma));

    /// <summary>A value in [<paramref name="min"/>, <paramref name="max"/>] from a hash.</summary>
    public static int InRange(ulong hash, int min, int max) => min + (int)(hash % (ulong)(max - min + 1));

    /// <summary>A roll in 0..999 from a hash (independent of <see cref="InRange"/> on the same hash: uses the high bits).</summary>
    public static int Permille(ulong hash) => (int)((hash >> 40) % 1000);

    /// <summary>Squared Euclidean distance.</summary>
    public static long DistanceSquared(int x0, int y0, int x1, int y1)
    {
        long dx = x1 - x0;
        long dy = y1 - y0;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Seeded Fisher–Yates shuffle.</summary>
    public static void Shuffle(List<int> items, Xoshiro256StarStar rng)
    {
        for (int k = items.Count - 1; k > 0; k--)
        {
            int j = rng.NextInt(k + 1);
            (items[k], items[j]) = (items[j], items[k]);
        }
    }

    /// <summary>
    /// Buildable tiles (B2): land without a river whose 8 neighbours on the map all have the same height step.
    /// </summary>
    public static bool[] Buildable(TerrainMap map)
    {
        int size = map.Size;
        bool[] result = new bool[map.TileCount];
        byte[] heights = map.HeightArray;
        for (int y = 1; y < size - 1; y++)
        {
            for (int x = 1; x < size - 1; x++)
            {
                int i = (y * size) + x;
                if ((map.FeatureArray[i] & (TileFeatures.Water | TileFeatures.River)) != 0)
                {
                    continue;
                }

                byte h = heights[i];
                result[i] = heights[i - size - 1] == h && heights[i - size] == h && heights[i - size + 1] == h
                    && heights[i - 1] == h && heights[i + 1] == h
                    && heights[i + size - 1] == h && heights[i + size] == h && heights[i + size + 1] == h;
            }
        }

        return result;
    }

    /// <summary>Half widths of the rows of an integer disc of radius <paramref name="radius"/>: row dy spans x ± w[dy + r].</summary>
    public static int[] DiscHalfWidths(int radius)
    {
        int[] widths = new int[(2 * radius) + 1];
        long r2 = (long)radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int w = 0;
            while (((long)(w + 1) * (w + 1)) + ((long)dy * dy) <= r2)
            {
                w++;
            }

            widths[dy + radius] = w;
        }

        return widths;
    }
}

/// <summary>Counts set tiles of a mask inside discs in O(radius) using per-row prefix sums.</summary>
internal sealed class DiscCounter
{
    private readonly int _size;
    private readonly int[] _prefix; // (size + 1) entries per row: _prefix[y * (size + 1) + x] = set tiles in row y before x.

    public DiscCounter(bool[] mask, int size)
    {
        _size = size;
        _prefix = new int[(size + 1) * size];
        for (int y = 0; y < size; y++)
        {
            int row = y * (size + 1);
            for (int x = 0; x < size; x++)
            {
                _prefix[row + x + 1] = _prefix[row + x] + (mask[(y * size) + x] ? 1 : 0);
            }
        }
    }

    /// <summary>Set tiles within the disc around (<paramref name="cx"/>, <paramref name="cy"/>), clipped to the map.</summary>
    public int Count(int cx, int cy, int[] halfWidths)
    {
        int radius = halfWidths.Length / 2;
        int total = 0;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if ((uint)y >= (uint)_size)
            {
                continue;
            }

            int w = halfWidths[dy + radius];
            int x0 = Math.Max(0, cx - w);
            int x1 = Math.Min(_size - 1, cx + w);
            if (x0 <= x1)
            {
                int row = y * (_size + 1);
                total += _prefix[row + x1 + 1] - _prefix[row + x0];
            }
        }

        return total;
    }

    /// <summary>Tiles of the disc that lie on the map.</summary>
    public int Area(int cx, int cy, int[] halfWidths)
    {
        int radius = halfWidths.Length / 2;
        int total = 0;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if ((uint)y < (uint)_size)
            {
                int w = halfWidths[dy + radius];
                total += Math.Max(0, Math.Min(_size - 1, cx + w) - Math.Max(0, cx - w) + 1);
            }
        }

        return total;
    }
}

/// <summary>Points bucketed in square cells for "is anything closer than d" queries.</summary>
internal sealed class SpacingGrid
{
    private readonly int _cell;
    private readonly int _cellsPerSide;
    private readonly List<(int X, int Y)>[] _cells;

    /// <param name="size">Map side.</param>
    /// <param name="cell">Cell side; queries with a distance up to <paramref name="cell"/> look at 3x3 cells only.</param>
    public SpacingGrid(int size, int cell)
    {
        _cell = Math.Max(1, cell);
        _cellsPerSide = (size / _cell) + 1;
        _cells = new List<(int, int)>[_cellsPerSide * _cellsPerSide];
    }

    public void Add(int x, int y)
    {
        int c = ((y / _cell) * _cellsPerSide) + (x / _cell);
        (_cells[c] ??= []).Add((x, y));
    }

    /// <summary>True when every stored point is at least <paramref name="distance"/> away (Euclidean).</summary>
    public bool IsFar(int x, int y, int distance)
    {
        long d2 = (long)distance * distance;
        int reach = (distance + _cell - 1) / _cell;
        int gx = x / _cell;
        int gy = y / _cell;
        for (int cy = Math.Max(0, gy - reach); cy <= Math.Min(_cellsPerSide - 1, gy + reach); cy++)
        {
            for (int cx = Math.Max(0, gx - reach); cx <= Math.Min(_cellsPerSide - 1, gx + reach); cx++)
            {
                List<(int X, int Y)>? points = _cells[(cy * _cellsPerSide) + cx];
                if (points is null)
                {
                    continue;
                }

                foreach ((int px, int py) in points)
                {
                    if (PlacementOps.DistanceSquared(x, y, px, py) < d2)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
