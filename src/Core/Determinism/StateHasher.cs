using System.IO.Hashing;

namespace Core.Determinism;

/// <summary>
/// Hashes canonical state bytes with XXH3-64 (seed 0) from System.IO.Hashing (ADR 0005).
/// An instance reuses one <see cref="StateWriter"/>, so per-tick hashing does not allocate a buffer each time.
/// </summary>
public sealed class StateHasher
{
    private readonly StateWriter _writer = new();

    /// <summary>XXH3-64 with seed 0 of <paramref name="data"/>.</summary>
    public static ulong HashBytes(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);

    /// <summary>Hash of the bytes <paramref name="state"/> writes through <see cref="IStateHashable.WriteState"/>.</summary>
    public ulong Hash(IStateHashable state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _writer.Reset();
        state.WriteState(_writer);
        return HashBytes(_writer.WrittenSpan);
    }

    /// <summary>Hashes each subsystem separately, keeping the given order (the fixed system order of the simulation).</summary>
    /// <exception cref="ArgumentException">Two subsystems share a name.</exception>
    public SubsystemHashes HashSubsystems(params ReadOnlySpan<(string Name, IStateHashable State)> subsystems)
    {
        var result = new SubsystemHashes();
        foreach ((string name, IStateHashable state) in subsystems)
        {
            result.Add(name, Hash(state));
        }

        return result;
    }
}
