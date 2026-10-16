using System.Numerics;
using Core.Determinism;
using Core.WorldGen;

namespace Core.Tiles;

/// <summary>
/// Bit masks over the 1024 tiles of a 32×32 chunk (ADR 0007): bit <c>ly * 32 + lx</c> of 16 words, least significant
/// bit first. Shared by every sparse layer of <see cref="TileChanges"/>, so saves and hashes lay all layers out alike.
/// </summary>
internal static class ChunkMask
{
    /// <summary>Tiles in a chunk.</summary>
    public const int TilesPerChunk = ChunkCoord.Size * ChunkCoord.Size;

    /// <summary>Words of a chunk mask (1024 bits).</summary>
    public const int Words = TilesPerChunk / 64;

    /// <summary>Local index of tile (<paramref name="x"/>, <paramref name="y"/>) in its chunk.</summary>
    public static int LocalIndex(int x, int y) => ((y % ChunkCoord.Size) * ChunkCoord.Size) + (x % ChunkCoord.Size);

    public static bool IsSet(ulong[] mask, int local) => (mask[local >> 6] & (1UL << (local & 63))) != 0;

    public static void Set(ulong[] mask, int local) => mask[local >> 6] |= 1UL << (local & 63);

    public static void Clear(ulong[] mask, int local) => mask[local >> 6] &= ~(1UL << (local & 63));

    /// <summary>Number of set bits.</summary>
    public static int Count(IReadOnlyList<ulong> mask)
    {
        int bits = 0;
        foreach (ulong word in mask)
        {
            bits += BitOperations.PopCount(word);
        }

        return bits;
    }

    public static void Write(StateWriter writer, ulong[] mask)
    {
        foreach (ulong word in mask)
        {
            writer.WriteUInt64(word);
        }
    }

    /// <summary>
    /// Checks a saved chunk: inside a <paramref name="chunksPerSide"/>² chunk map, after <paramref name="previous"/> in
    /// chunk index order, a mask of <see cref="Words"/> words with at least one bit and one value per bit. Returns the
    /// chunk index and moves <paramref name="previous"/> to it.
    /// </summary>
    /// <exception cref="ArgumentException">A rule is broken.</exception>
    public static int Validate(int cx, int cy, IReadOnlyList<ulong> mask, int values, int chunksPerSide, ref int previous)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if ((uint)cx >= (uint)chunksPerSide || (uint)cy >= (uint)chunksPerSide)
        {
            throw new ArgumentException($"Chunk ({cx}, {cy}) is outside a {chunksPerSide}x{chunksPerSide} chunk map.", nameof(cx));
        }

        int chunkIndex = (cy * chunksPerSide) + cx;
        if (chunkIndex <= previous)
        {
            throw new ArgumentException("Tile chunks must be in strictly ascending chunk order.", nameof(cx));
        }

        previous = chunkIndex;
        if (mask.Count != Words)
        {
            throw new ArgumentException($"A chunk mask has {Words} words, not {mask.Count}.", nameof(mask));
        }

        int bits = Count(mask);
        if (bits == 0 || bits != values)
        {
            throw new ArgumentException($"Chunk ({cx}, {cy}) has {bits} mask bits and {values} values; both must match and be non-zero.", nameof(mask));
        }

        return chunkIndex;
    }

    /// <summary>Calls <paramref name="action"/> with the local index of every set bit, in ascending order.</summary>
    public static void ForEachSet(ulong[] mask, Action<int> action)
    {
        for (int w = 0; w < mask.Length; w++)
        {
            ulong word = mask[w];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                action((w << 6) + bit);
                word &= word - 1;
            }
        }
    }
}
