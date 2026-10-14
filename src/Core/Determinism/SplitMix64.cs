namespace Core.Determinism;

/// <summary>
/// SplitMix64 generator (Steele, Lea, Flood; reference C by S. Vigna). Used only to seed
/// <see cref="Xoshiro256StarStar"/> and to derive stream seeds, never directly by game logic.
/// </summary>
public struct SplitMix64
{
    /// <summary>Golden-ratio increment added to the state on every step.</summary>
    public const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>Creates a generator whose state is <paramref name="seed"/>.</summary>
    public SplitMix64(ulong seed)
    {
        State = seed;
    }

    /// <summary>Current state; the next output is <c>Mix(State + Gamma)</c>.</summary>
    public ulong State { get; private set; }

    /// <summary>Advances the state and returns the next 64-bit output.</summary>
    public ulong NextUInt64()
    {
        State += Gamma;
        return Mix(State);
    }

    /// <summary>The SplitMix64 output finalizer: a bijective 64-bit mixing function with <c>Mix(0) == 0</c>.</summary>
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
