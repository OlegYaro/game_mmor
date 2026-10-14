namespace Core.Determinism;

/// <summary>Hash of one subsystem's canonical state.</summary>
public readonly record struct SubsystemHash(string Name, ulong Hash);

/// <summary>
/// Per-subsystem state hashes in a fixed order plus a combined hash. Used by replay and CI checks to report
/// which subsystem diverged first (ADR 0005, point 5).
/// </summary>
public sealed class SubsystemHashes : IStateHashable
{
    private readonly List<SubsystemHash> _entries = [];
    private ulong? _combined;

    /// <summary>Number of subsystems.</summary>
    public int Count => _entries.Count;

    /// <summary>Entries in insertion order.</summary>
    public IReadOnlyList<SubsystemHash> Entries => _entries;

    /// <summary>
    /// XXH3-64 of this set's canonical form (count, then each name and hash in order). Depends on names,
    /// order and values, so two sets match only if every subsystem matches.
    /// </summary>
    public ulong Combined
    {
        get
        {
            if (_combined is null)
            {
                var writer = new StateWriter();
                WriteState(writer);
                _combined = StateHasher.HashBytes(writer.WrittenSpan);
            }

            return _combined.Value;
        }
    }

    /// <summary>Appends a subsystem hash. Order of calls is part of the combined hash.</summary>
    /// <exception cref="ArgumentException">The name is empty or already present.</exception>
    public void Add(string name, ulong hash)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (TryGetHash(name, out _))
        {
            throw new ArgumentException($"Subsystem '{name}' is already hashed.", nameof(name));
        }

        _entries.Add(new SubsystemHash(name, hash));
        _combined = null;
    }

    /// <summary>Looks up a subsystem hash by exact (ordinal) name.</summary>
    public bool TryGetHash(string name, out ulong hash)
    {
        foreach (SubsystemHash entry in _entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                hash = entry.Hash;
                return true;
            }
        }

        hash = 0;
        return false;
    }

    /// <summary>
    /// Name of the first subsystem, in order, whose hash differs from <paramref name="expected"/>, or whose
    /// name or presence differs; null when both sets are identical.
    /// </summary>
    public string? FindFirstMismatch(SubsystemHashes expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        int count = Math.Max(Count, expected.Count);
        for (int i = 0; i < count; i++)
        {
            if (i >= Count)
            {
                return expected._entries[i].Name;
            }

            SubsystemHash actual = _entries[i];
            if (i >= expected.Count || actual != expected._entries[i])
            {
                return actual.Name;
            }
        }

        return null;
    }

    /// <summary>Writes the count as Int32, then each name (length-prefixed UTF-8) and hash (UInt64) in order.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(_entries.Count);
        foreach (SubsystemHash entry in _entries)
        {
            writer.WriteString(entry.Name);
            writer.WriteUInt64(entry.Hash);
        }
    }
}
