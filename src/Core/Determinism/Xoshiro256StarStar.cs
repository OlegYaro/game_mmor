using System.Numerics;

namespace Core.Determinism;

/// <summary>
/// xoshiro256** 1.0 (Blackman, Vigna), seeded through <see cref="SplitMix64"/> as the authors recommend.
/// The only random source allowed in Core (ADR 0005). Integer-only; the whole state is four <c>ulong</c>.
/// </summary>
/// <remarks>
/// Bounded results use Lemire's nearly-divisionless method on the upper 32 bits of each output, so they are
/// unbiased and every platform consumes the same number of outputs for the same call sequence.
/// </remarks>
public sealed class Xoshiro256StarStar : IStateHashable
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>Seeds the state with the first four outputs of <c>SplitMix64(seed)</c>.</summary>
    public Xoshiro256StarStar(ulong seed)
    {
        var seeder = new SplitMix64(seed);
        _s0 = seeder.NextUInt64();
        _s1 = seeder.NextUInt64();
        _s2 = seeder.NextUInt64();
        _s3 = seeder.NextUInt64();
    }

    /// <summary>Restores a generator from a saved state.</summary>
    /// <exception cref="ArgumentException">The state is all zero.</exception>
    public Xoshiro256StarStar(Xoshiro256State state)
    {
        SetState(state);
    }

    /// <summary>Returns the current state for saving.</summary>
    public Xoshiro256State GetState() => new(_s0, _s1, _s2, _s3);

    /// <summary>Replaces the current state; the sequence continues exactly as from the saved point.</summary>
    /// <exception cref="ArgumentException">The state is all zero.</exception>
    public void SetState(Xoshiro256State state)
    {
        if (state.IsZero)
        {
            throw new ArgumentException("xoshiro256** state must not be all zero.", nameof(state));
        }

        _s0 = state.S0;
        _s1 = state.S1;
        _s2 = state.S2;
        _s3 = state.S3;
    }

    /// <summary>Returns the next 64-bit output of the reference algorithm.</summary>
    public ulong NextUInt64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>Returns the upper 32 bits of the next output (the strongest bits).</summary>
    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>Uniform integer in <c>[0, maxExclusive)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is not positive.</exception>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)NextUInt32Below((uint)maxExclusive);
    }

    /// <summary>Uniform integer in <c>[minInclusive, maxExclusive)</c>; any int range up to 2^32 - 1 wide.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is not greater than <paramref name="minInclusive"/>.</exception>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxExclusive, minInclusive);
        uint range = (uint)((long)maxExclusive - minInclusive);
        return (int)(minInclusive + NextUInt32Below(range));
    }

    /// <summary>Uniform integer in <c>[0, 1000)</c>.</summary>
    public int NextPermille() => (int)NextUInt32Below(1000);

    /// <summary>
    /// True with probability <c>permille / 1000</c>. Values at or below 0 never succeed, at or above 1000 always do.
    /// Always consumes exactly one draw so the call count does not depend on the data.
    /// </summary>
    public bool ChancePermille(int permille) => NextPermille() < permille;

    /// <summary>
    /// True with probability <c>percent / 100</c>. Values at or below 0 never succeed, at or above 100 always do.
    /// Always consumes exactly one draw.
    /// </summary>
    public bool ChancePercent(int percent) => (int)NextUInt32Below(100) < percent;

    /// <summary>Writes the four state words in order (S0..S3).</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64(_s0);
        writer.WriteUInt64(_s1);
        writer.WriteUInt64(_s2);
        writer.WriteUInt64(_s3);
    }

    // Lemire, "Fast Random Integer Generation in an Interval" (2019). bound must be > 0.
    private uint NextUInt32Below(uint bound)
    {
        ulong product = (ulong)NextUInt32() * bound;
        uint low = (uint)product;
        if (low < bound)
        {
            uint threshold = (0u - bound) % bound;
            while (low < threshold)
            {
                product = (ulong)NextUInt32() * bound;
                low = (uint)product;
            }
        }

        return (uint)(product >> 32);
    }
}
