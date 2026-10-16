using Core.Content;
using Core.Determinism;

namespace Core.Jobs;

/// <summary>
/// A resident's priority for every job type (ADR 0009, GDD priorities 1–4): 1 is the highest tier, 4 the lowest, 0 means
/// the resident does not take that job. Packed into one word: job type <c>k</c> (its <see cref="JobKind"/> value, 1 to
/// <see cref="JobKinds.Count"/>) is bits <c>3(k − 1) .. 3(k − 1) + 2</c>; the other bits are always 0.
/// </summary>
/// <param name="Bits">The packed priorities.</param>
public readonly record struct WorkPriorities(int Bits) : IStateHashable
{
    /// <summary>The lowest tier.</summary>
    public const int Max = 4;

    /// <summary>The priority of a profession's own job types (<see cref="ForProfession"/>).</summary>
    public const int ProfessionDefault = 1;

    private const int BitsPerKind = 3;
    private const int KindMask = (1 << BitsPerKind) - 1;

    /// <summary>
    /// The defaults of <paramref name="profession"/>: <see cref="ProfessionDefault"/> for each of its
    /// <see cref="ProfessionDef.JobTypes"/>, 0 for every other job type.
    /// </summary>
    public static WorkPriorities ForProfession(ProfessionDef profession)
    {
        ArgumentNullException.ThrowIfNull(profession);
        var priorities = default(WorkPriorities);
        foreach (string job in profession.JobTypes)
        {
            if (JobKinds.TryParse(job, out JobKind kind))
            {
                priorities = priorities.With(kind, ProfessionDefault);
            }
        }

        return priorities;
    }

    /// <summary>The priority of <paramref name="kind"/>, 0 to <see cref="Max"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="JobKind.None"/> or unknown.</exception>
    public int Get(JobKind kind) => (Bits >> Shift(kind)) & KindMask;

    /// <summary>The same priorities with <paramref name="kind"/> set to <paramref name="priority"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not a job type or the priority is outside 0..<see cref="Max"/>.</exception>
    public WorkPriorities With(JobKind kind, int priority)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, Max);
        int shift = Shift(kind);
        return new WorkPriorities((Bits & ~(KindMask << shift)) | (priority << shift));
    }

    /// <summary>True when every priority is 0..<see cref="Max"/> and the unused bits are 0.</summary>
    public bool IsValid()
    {
        if (Bits < 0 || Bits >> (JobKinds.Count * BitsPerKind) != 0)
        {
            return false;
        }

        for (int k = 1; k <= JobKinds.Count; k++)
        {
            if (Get((JobKind)k) > Max)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes the packed word.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Bits);
    }

    private static int Shift(JobKind kind)
    {
        if (kind is JobKind.None || (int)kind > JobKinds.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a job type.");
        }

        return ((int)kind - 1) * BitsPerKind;
    }
}
