namespace Core.WorldGen.Terrain;

/// <summary>Integer helpers shared by the terrain steps. Everything here is exact integer arithmetic except <see cref="UnitToFixed16"/>.</summary>
internal static class GridOps
{
    /// <summary>Largest 16-bit fixed-point value (represents 1.0).</summary>
    public const int FixedOne = ushort.MaxValue;

    /// <summary>
    /// Converts a value in [0, 1] to 16-bit fixed point, clamping outside values and NaN (to 0), with round-half-up.
    /// This is the only place where a noise double turns into an integer.
    /// </summary>
    public static ushort UnitToFixed16(double value)
    {
        if (!(value > 0.0))
        {
            return 0;
        }

        if (value >= 1.0)
        {
            return ushort.MaxValue;
        }

        return (ushort)(int)((value * FixedOne) + 0.5);
    }

    /// <summary>
    /// Smallest level <c>L</c> such that at least <c>total * permille / 1000</c> values lie below <c>L</c>, given a
    /// histogram of values (index = value).
    /// </summary>
    public static int LevelForPermille(int[] histogram, long total, int permille)
    {
        long target = total * permille / 1000;
        long below = 0;
        int level = 0;
        while (level < histogram.Length && below < target)
        {
            below += histogram[level];
            level++;
        }

        return level;
    }

    /// <summary>
    /// Replaces every value in <paramref name="mask"/> by its rank in permille (0..999): the share of masked values
    /// strictly below it. Unmasked entries of <paramref name="destination"/> become 0.
    /// </summary>
    public static void RankPermille(int[] values, bool[] mask, ushort[] destination)
    {
        int min = int.MaxValue;
        int max = int.MinValue;
        long count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i])
            {
                min = Math.Min(min, values[i]);
                max = Math.Max(max, values[i]);
                count++;
            }
        }

        Array.Clear(destination);
        if (count == 0)
        {
            return;
        }

        // below[v - min] = number of masked values < v.
        long[] below = new long[max - min + 2];
        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i])
            {
                below[values[i] - min + 1]++;
            }
        }

        for (int v = 1; v < below.Length; v++)
        {
            below[v] += below[v - 1];
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i])
            {
                destination[i] = (ushort)(below[values[i] - min] * 1000 / count);
            }
        }
    }

    /// <summary>
    /// Labels the 4-connected regions of tiles with <paramref name="inSet"/> true, in scan order. Returns the label
    /// per tile (-1 outside the set); fills the size of each region and whether it touches the map edge.
    /// </summary>
    public static int[] LabelRegions(bool[] inSet, int size, List<int> regionSizes, List<bool> touchesEdge)
    {
        int count = size * size;
        int[] labels = new int[count];
        Array.Fill(labels, -1);
        int[] stack = new int[count];
        regionSizes.Clear();
        touchesEdge.Clear();
        for (int start = 0; start < count; start++)
        {
            if (!inSet[start] || labels[start] >= 0)
            {
                continue;
            }

            int label = regionSizes.Count;
            int tiles = 0;
            bool edge = false;
            int top = 0;
            stack[top++] = start;
            labels[start] = label;
            while (top > 0)
            {
                int i = stack[--top];
                tiles++;
                int x = i % size;
                int y = i / size;
                edge |= x == 0 || y == 0 || x == size - 1 || y == size - 1;
                Visit(x + 1, y);
                Visit(x - 1, y);
                Visit(x, y + 1);
                Visit(x, y - 1);
            }

            regionSizes.Add(tiles);
            touchesEdge.Add(edge);

            void Visit(int nx, int ny)
            {
                if ((uint)nx < (uint)size && (uint)ny < (uint)size)
                {
                    int j = (ny * size) + nx;
                    if (inSet[j] && labels[j] < 0)
                    {
                        labels[j] = label;
                        stack[top++] = j;
                    }
                }
            }
        }

        return labels;
    }

    /// <summary>
    /// Majority (mode) filter over the 3x3 block, restricted to tiles in <paramref name="mask"/> (others neither
    /// change nor vote). A tile takes the most frequent value of its block when that value has at least
    /// <paramref name="threshold"/> votes; ties keep the current value, otherwise the lowest value wins. Each pass
    /// reads the previous pass's result (double-buffered), so the outcome does not depend on scan order.
    /// </summary>
    public static void MajorityFilter(Span<byte> values, bool[] mask, int size, int passes, int threshold, int categories)
    {
        byte[] previous = new byte[values.Length];
        Span<int> votes = stackalloc int[categories];
        for (int pass = 0; pass < passes; pass++)
        {
            values.CopyTo(previous);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size) + x;
                    if (!mask[i])
                    {
                        continue;
                    }

                    votes.Clear();
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if ((uint)ny >= (uint)size)
                        {
                            continue;
                        }

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int j = (ny * size) + nx;
                            if ((uint)nx < (uint)size && mask[j])
                            {
                                votes[previous[j]]++;
                            }
                        }
                    }

                    int current = previous[i];
                    int best = current;
                    for (int c = 0; c < categories; c++)
                    {
                        if (votes[c] > votes[best])
                        {
                            best = c;
                        }
                    }

                    if (best != current && votes[best] >= threshold)
                    {
                        values[i] = (byte)best;
                    }
                }
            }
        }
    }
}
