namespace Core.Fog;

/// <summary>
/// Run-length form of a fog bit layer, used by saves (ADR 0007): alternating run lengths over the tiles in index
/// order (<c>y * Size + x</c>), starting with a run of 0-bits (unknown tiles), then 1-bits (explored), and so on.
/// </summary>
/// <remarks>
/// Canonical: only the first run may be 0 (when tile 0 is explored), every other run is positive, and the runs add up
/// to the tile count. A typical explored area is a few blobs, i.e. two runs per touched row; a fully revealed map
/// is <c>[0, tileCount]</c>.
/// </remarks>
internal static class FogRunLength
{
    private const int BitsPerWord = 64;

    /// <summary>Encodes the first <paramref name="tileCount"/> bits of <paramref name="bits"/>.</summary>
    public static int[] Encode(ReadOnlySpan<ulong> bits, int tileCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tileCount);
        if (tileCount == 0)
        {
            return [];
        }

        List<int> runs = [];
        bool current = false;
        int run = 0;
        for (int start = 0; start < tileCount; start += BitsPerWord)
        {
            ulong word = bits[start / BitsPerWord];
            int count = Math.Min(BitsPerWord, tileCount - start);
            ulong used = count == BitsPerWord ? ulong.MaxValue : (1UL << count) - 1;
            if ((word & used) == (current ? used : 0))
            {
                run += count;
                continue;
            }

            for (int b = 0; b < count; b++)
            {
                bool bit = ((word >> b) & 1) != 0;
                if (bit != current)
                {
                    runs.Add(run);
                    run = 0;
                    current = bit;
                }

                run++;
            }
        }

        runs.Add(run);
        return [.. runs];
    }

    /// <summary>Decodes runs into a bit layer of <see cref="FogOfWar.WordCountFor"/>(<paramref name="tileCount"/>) words.</summary>
    /// <exception cref="InvalidDataException">The runs are not canonical or do not cover exactly <paramref name="tileCount"/> tiles.</exception>
    public static ulong[] Decode(IReadOnlyList<int> runs, int tileCount)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentOutOfRangeException.ThrowIfNegative(tileCount);
        var bits = new ulong[FogOfWar.WordCountFor(tileCount)];
        if (tileCount == 0)
        {
            return runs.Count == 0 ? bits : throw new InvalidDataException("Fog runs must be empty without a map.");
        }

        if (runs.Count == 0)
        {
            throw new InvalidDataException("Fog runs are empty.");
        }

        long position = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            int run = runs[i];
            if (run < 0 || (run == 0 && i > 0))
            {
                throw new InvalidDataException($"Fog run {i} is {run}; only the first run may be 0 and none may be negative.");
            }

            if (position + run > tileCount)
            {
                throw new InvalidDataException($"Fog runs cover more than the {tileCount} tiles of the map.");
            }

            if (i % 2 == 1)
            {
                FogOfWar.SetRange(bits, (int)position, (int)(position + run - 1));
            }

            position += run;
        }

        if (position != tileCount)
        {
            throw new InvalidDataException($"Fog runs cover {position} of {tileCount} tiles.");
        }

        return bits;
    }
}
