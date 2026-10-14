namespace Core.Determinism;

/// <summary>
/// One <see cref="Xoshiro256StarStar"/> per <see cref="RngStreamId"/>, all derived from the world seed, so that
/// drawing more numbers in one subsystem never shifts the sequence of another (ADR 0005).
/// </summary>
public sealed class RngStreams : IStateHashable
{
    /// <summary>Number of streams; equals the number of <see cref="RngStreamId"/> values.</summary>
    public const int Count = (int)RngStreamId.Factions + 1;

    private readonly Xoshiro256StarStar[] _streams = new Xoshiro256StarStar[Count];

    /// <summary>Creates fresh streams for a new world.</summary>
    public RngStreams(ulong worldSeed)
    {
        WorldSeed = worldSeed;
        for (int i = 0; i < Count; i++)
        {
            _streams[i] = new Xoshiro256StarStar(DeriveSeed(worldSeed, (RngStreamId)i));
        }
    }

    /// <summary>Restores streams from a save. <paramref name="states"/> is indexed by <see cref="RngStreamId"/> value.</summary>
    /// <exception cref="ArgumentException">Wrong number of states, or an all-zero state.</exception>
    public RngStreams(ulong worldSeed, ReadOnlySpan<Xoshiro256State> states)
    {
        if (states.Length != Count)
        {
            throw new ArgumentException($"Expected {Count} stream states, got {states.Length}.", nameof(states));
        }

        WorldSeed = worldSeed;
        for (int i = 0; i < Count; i++)
        {
            _streams[i] = new Xoshiro256StarStar(states[i]);
        }
    }

    /// <summary>The seed the streams were derived from.</summary>
    public ulong WorldSeed { get; }

    /// <summary>The generator for <paramref name="id"/>.</summary>
    public Xoshiro256StarStar this[RngStreamId id] => _streams[Index(id)];

    /// <summary>
    /// Seed of one stream: <c>Mix(worldSeed ^ Mix((id + 1) * Gamma))</c>, where <c>Mix</c> is the SplitMix64 finalizer.
    /// The <c>+ 1</c> keeps stream 0 from using the raw world seed.
    /// </summary>
    public static ulong DeriveSeed(ulong worldSeed, RngStreamId id)
    {
        ulong salt = SplitMix64.Mix(((ulong)Index(id) + 1) * SplitMix64.Gamma);
        return SplitMix64.Mix(worldSeed ^ salt);
    }

    /// <summary>Current state of every stream, indexed by <see cref="RngStreamId"/> value, for saving.</summary>
    public Xoshiro256State[] GetStates()
    {
        var states = new Xoshiro256State[Count];
        for (int i = 0; i < Count; i++)
        {
            states[i] = _streams[i].GetState();
        }

        return states;
    }

    /// <summary>Writes the world seed, then every stream state in <see cref="RngStreamId"/> order.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64(WorldSeed);
        foreach (Xoshiro256StarStar stream in _streams)
        {
            stream.WriteState(writer);
        }
    }

    private static int Index(RngStreamId id)
    {
        int index = (int)id;
        if ((uint)index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown RNG stream.");
        }

        return index;
    }
}
