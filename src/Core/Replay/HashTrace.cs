using System.Globalization;
using System.Text;
using Core.Content;
using Core.Determinism;

namespace Core.Replay;

/// <summary>Subsystem hashes of the state after one tick.</summary>
/// <param name="Tick">The tick.</param>
/// <param name="Hashes">Hashes in the simulation's fixed subsystem order.</param>
public readonly record struct TickHashes(long Tick, SubsystemHashes Hashes);

/// <summary>
/// Subsystem hashes per tick, in increasing tick order (ADR 0005, point 5). Dense when recorded by a
/// <see cref="ReplayPlayer"/>; usually sampled (<see cref="Sample"/>) when stored as a golden file.
/// </summary>
/// <remarks>
/// <para>Text form, one tick per line, hashes as 16 lowercase hex digits; lines starting with <c>#</c> are comments:</para>
/// <code>
/// &lt;tick&gt; &lt;combined&gt; calendar=&lt;hash&gt; rng=&lt;hash&gt; entities=&lt;hash&gt; components=&lt;hash&gt; commands=&lt;hash&gt;
/// </code>
/// <para>The combined hash is redundant (derived from the others) and is checked on parse, which catches hand edits.</para>
/// </remarks>
public sealed class HashTrace
{
    private const string Header = "# Subsystem state hashes (XXH3-64, hex). Line: <tick> <combined> <subsystem>=<hash> ...";

    private readonly List<TickHashes> _entries = [];

    /// <summary>Entries in increasing tick order.</summary>
    public IReadOnlyList<TickHashes> Entries => _entries;

    /// <summary>Number of ticks.</summary>
    public int Count => _entries.Count;

    /// <summary>The last entry, or null when empty.</summary>
    public TickHashes? Last => _entries.Count == 0 ? null : _entries[^1];

    /// <summary>Appends the hashes of <paramref name="tick"/>.</summary>
    /// <exception cref="ArgumentException">The tick is not above the last one.</exception>
    public void Add(long tick, SubsystemHashes hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (_entries.Count > 0 && tick <= _entries[^1].Tick)
        {
            throw new ArgumentException($"Tick {tick} is not after {_entries[^1].Tick}.", nameof(tick));
        }

        _entries.Add(new TickHashes(tick, hashes));
    }

    /// <summary>Appends all entries of <paramref name="other"/> (for runs split by a save and load).</summary>
    public void AddRange(HashTrace other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (TickHashes entry in other._entries)
        {
            Add(entry.Tick, entry.Hashes);
        }
    }

    /// <summary>Looks up the hashes of <paramref name="tick"/>.</summary>
    public bool TryGet(long tick, out SubsystemHashes hashes)
    {
        int low = 0;
        int high = _entries.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            long midTick = _entries[mid].Tick;
            if (midTick == tick)
            {
                hashes = _entries[mid].Hashes;
                return true;
            }

            if (midTick < tick)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        hashes = null!;
        return false;
    }

    /// <summary>A copy with only the ticks divisible by <paramref name="interval"/>, plus the last tick.</summary>
    public HashTrace Sample(long interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        var sampled = new HashTrace();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Tick % interval == 0 || i == _entries.Count - 1)
            {
                sampled._entries.Add(_entries[i]);
            }
        }

        return sampled;
    }

    /// <summary>Text form (see remarks), LF line ends.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        foreach (TickHashes entry in _entries)
        {
            text.Append(entry.Tick.ToString(CultureInfo.InvariantCulture));
            text.Append(' ').Append(ContentHash.Format(entry.Hashes.Combined));
            foreach (SubsystemHash hash in entry.Hashes.Entries)
            {
                text.Append(' ').Append(hash.Name).Append('=').Append(ContentHash.Format(hash.Hash));
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Parses <see cref="ToText"/> output (CRLF tolerated).</summary>
    /// <exception cref="InvalidDataException">A line is malformed, out of order, or its combined hash does not match.</exception>
    public static HashTrace Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stream = new HashTrace();
        string[] lines = text.Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex].TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(' ');
            int lineNumber = lineIndex + 1;
            if (parts.Length < 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long tick)
                || !ContentHash.TryParse(parts[1], out ulong combined))
            {
                throw new InvalidDataException($"Line {lineNumber}: expected '<tick> <combined> <name>=<hash> ...'.");
            }

            var hashes = new SubsystemHashes();
            foreach (string part in parts.AsSpan(2))
            {
                int equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0 || !ContentHash.TryParse(part[(equals + 1)..], out ulong hash))
                {
                    throw new InvalidDataException($"Line {lineNumber}: '{part}' is not '<name>=<16 hex digits>'.");
                }

                try
                {
                    hashes.Add(part[..equals], hash);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException($"Line {lineNumber}: {ex.Message}", ex);
                }
            }

            if (hashes.Combined != combined)
            {
                throw new InvalidDataException($"Line {lineNumber}: combined hash does not match the subsystem hashes.");
            }

            try
            {
                stream.Add(tick, hashes);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"Line {lineNumber}: {ex.Message}", ex);
            }
        }

        return stream;
    }
}
