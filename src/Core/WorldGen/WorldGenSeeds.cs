using Core.Determinism;

namespace Core.WorldGen;

/// <summary>
/// Steps of the world generation pipeline (ADR 0006). The numeric values feed sub-seed derivation: never renumber
/// or reuse them, only append. Steps 1–4 form the terrain stage (<see cref="WorldGenerator.GenerateTerrain"/>).
/// </summary>
public enum WorldGenStep
{
    /// <summary>1. Heightfield.</summary>
    Height = 1,

    /// <summary>2. Moisture and temperature.</summary>
    Climate = 2,

    /// <summary>3. Biomes.</summary>
    Biomes = 3,

    /// <summary>4. Rivers.</summary>
    Rivers = 4,

    /// <summary>5. Resources.</summary>
    Resources = 5,

    /// <summary>6. Points of interest.</summary>
    PointsOfInterest = 6,

    /// <summary>7. Start location.</summary>
    Start = 7,

    /// <summary>8. Faction settlements.</summary>
    Settlements = 8,
}

/// <summary>
/// Sub-seeds of the generator: every step (and optionally every chunk) gets its own seed derived from
/// <c>(seed, step, cx, cy)</c>, so steps never share a random sequence and per-chunk work can later run in any order
/// without changing the result (ADR 0006).
/// </summary>
public static class WorldGenSeeds
{
    /// <summary>
    /// Sub-seed of <paramref name="step"/> for chunk (<paramref name="cx"/>, <paramref name="cy"/>); whole-map work
    /// uses chunk (0, 0). A chain of SplitMix64 finalizers over the world seed, the step and the chunk coordinates.
    /// </summary>
    public static ulong Derive(ulong seed, WorldGenStep step, int cx = 0, int cy = 0)
    {
        ulong z = SplitMix64.Mix(seed ^ SplitMix64.Mix(((ulong)(uint)step + 1) * SplitMix64.Gamma));
        z = SplitMix64.Mix(z ^ ((ulong)(uint)cx * 0xD1B54A32D192ED03UL));
        z = SplitMix64.Mix(z ^ ((ulong)(uint)cy * 0xABC98388FB8FAC03UL) ^ 0x5851F42D4C957F2DUL);
        return z;
    }

    /// <summary>A fresh random stream for <paramref name="step"/> and a chunk.</summary>
    public static Xoshiro256StarStar CreateRng(ulong seed, WorldGenStep step, int cx = 0, int cy = 0) =>
        new(Derive(seed, step, cx, cy));

    /// <summary>32-bit seed for a FastNoiseLite instance; <paramref name="layer"/> separates several noises of one step.</summary>
    internal static int NoiseSeed(ulong seed, WorldGenStep step, int layer) =>
        unchecked((int)Derive(seed, step, layer, -1));
}
