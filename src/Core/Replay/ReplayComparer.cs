using System.Globalization;
using Core.Content;
using Core.Determinism;

namespace Core.Replay;

/// <summary>The first point where a run differs from the expected hashes.</summary>
/// <param name="Tick">First compared tick that differs.</param>
/// <param name="Subsystem">First subsystem (in fixed order) that differs, or <see cref="ReplayComparer.MissingTick"/>.</param>
/// <param name="ExpectedHash">Expected hash of <paramref name="Subsystem"/>, or null when absent.</param>
/// <param name="ActualHash">Actual hash of <paramref name="Subsystem"/>, or null when absent.</param>
/// <param name="LastMatchingTick">Last compared tick that matched, or null; with sampled expectations the actual
/// first divergence lies after it and at or before <paramref name="Tick"/>.</param>
public sealed record ReplayDivergence(long Tick, string Subsystem, ulong? ExpectedHash, ulong? ActualHash, long? LastMatchingTick)
{
    /// <summary>One line for test output and logs.</summary>
    public string Describe()
    {
        string window = LastMatchingTick is { } last
            ? string.Create(CultureInfo.InvariantCulture, $" (last matching compared tick: {last})")
            : " (no compared tick matched)";
        return string.Create(CultureInfo.InvariantCulture,
            $"Replay diverged at tick {Tick} in subsystem '{Subsystem}': expected {Hex(ExpectedHash)}, actual {Hex(ActualHash)}{window}.");
    }

    /// <inheritdoc />
    public override string ToString() => Describe();

    private static string Hex(ulong? hash) => hash is { } value ? ContentHash.Format(value) : "<none>";
}

/// <summary>Compares a run's hashes with expected ones and reports the first divergence (ADR 0005, NFR-04).</summary>
public static class ReplayComparer
{
    /// <summary>Subsystem name reported when the actual run has no hashes for an expected tick.</summary>
    public const string MissingTick = "(tick not recorded)";

    /// <summary>
    /// Checks every tick of <paramref name="expected"/> (which may be sampled) against <paramref name="actual"/>
    /// in tick order. Returns the first tick whose hashes differ and the first differing subsystem, or null when all
    /// expected ticks match. Extra ticks in <paramref name="actual"/> are not compared.
    /// </summary>
    public static ReplayDivergence? FindFirstDivergence(HashTrace expected, HashTrace actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        long? lastMatching = null;
        foreach (TickHashes entry in expected.Entries)
        {
            if (!actual.TryGet(entry.Tick, out SubsystemHashes actualHashes))
            {
                return new ReplayDivergence(entry.Tick, MissingTick, entry.Hashes.Combined, null, lastMatching);
            }

            string? subsystem = actualHashes.FindFirstMismatch(entry.Hashes);
            if (subsystem is not null)
            {
                return new ReplayDivergence(entry.Tick, subsystem, Lookup(entry.Hashes, subsystem), Lookup(actualHashes, subsystem), lastMatching);
            }

            lastMatching = entry.Tick;
        }

        return null;
    }

    private static ulong? Lookup(SubsystemHashes hashes, string name) => hashes.TryGetHash(name, out ulong hash) ? hash : null;
}
