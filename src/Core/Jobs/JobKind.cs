using Core.Content;
using Core.WorldGen;

namespace Core.Jobs;

/// <summary>
/// A job type of the job board (ADR 0009, <see cref="JobTypes"/>). Values are part of the state hash; save and data names
/// are <see cref="JobKinds.Names"/>. Append only.
/// </summary>
public enum JobKind : byte
{
    /// <summary>No job.</summary>
    None = 0,

    /// <summary>Carry items to a storage: a building's output, or the resident's own load.</summary>
    Haul = 1,

    /// <summary>Fell a tree and carry the wood to a storage.</summary>
    Chop = 2,

    /// <summary>Plant a sapling on a free tile next to a stump (FR-WORLD-04); it grows into a tree.</summary>
    Plant = 3,

    /// <summary>Break stone off a rock and carry it to a storage.</summary>
    Quarry = 4,

    /// <summary>Pick berries from a bush and carry them to a storage.</summary>
    Forage = 5,

    /// <summary>Work a slot of a field: every cycle puts the yield into the field's output.</summary>
    Farm = 6,
}

/// <summary>Where a resident is in its job (ADR 0009: the linear job body). Values are hashed; save names are <see cref="JobKinds.PhaseNames"/>. Append only.</summary>
public enum JobPhase : byte
{
    /// <summary>No job.</summary>
    None = 0,

    /// <summary>Walking to the job's target (a node, a field, the building whose output it hauls).</summary>
    ToTarget = 1,

    /// <summary>Working next to the target; <see cref="ResidentJob.WorkLeft"/> counts down.</summary>
    Working = 2,

    /// <summary>Carrying the load to the reserved storage, where it is handed in on arrival.</summary>
    ToStorage = 3,
}

/// <summary>Names, order and data links of the job types.</summary>
public static class JobKinds
{
    /// <summary>Number of job types (every <see cref="JobKind"/> but <see cref="JobKind.None"/>).</summary>
    public const int Count = 6;

    /// <summary>Stable names indexed by <see cref="JobKind"/> value (index 0 is <see cref="JobKind.None"/>); the job type names of the data. Append only.</summary>
    public static IReadOnlyList<string> Names { get; } = ["none", "haul", "chop", "plant", "quarry", "forage", "farm"];

    /// <summary>Stable save names of <see cref="JobPhase"/>, indexed by value. Append only.</summary>
    public static IReadOnlyList<string> PhaseNames { get; } = ["none", "to_target", "working", "to_storage"];

    /// <summary>
    /// Order of the job types inside one priority tier (ADR 0009: the type order comes right after the tier): food first
    /// (field, then berries), then the raw materials, hauling last, as the work tab of RimWorld puts hauling near the end.
    /// </summary>
    public static IReadOnlyList<JobKind> TierOrder { get; } = [JobKind.Farm, JobKind.Forage, JobKind.Chop, JobKind.Quarry, JobKind.Plant, JobKind.Haul];

    /// <summary>The stable name of <paramref name="kind"/>.</summary>
    public static string NameOf(JobKind kind) =>
        (int)kind < Names.Count ? Names[(int)kind] : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown job kind.");

    /// <summary>The job type named <paramref name="name"/> (not <c>none</c>); false for anything else.</summary>
    public static bool TryParse(string? name, out JobKind kind)
    {
        for (int i = 1; i < Names.Count; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.Ordinal))
            {
                kind = (JobKind)i;
                return true;
            }
        }

        kind = JobKind.None;
        return false;
    }

    /// <summary>The gathering job for nodes of <paramref name="resource"/>, or <see cref="JobKind.None"/>.</summary>
    public static JobKind GatherJobFor(ResourceKind resource) => resource switch
    {
        ResourceKind.Tree => JobKind.Chop,
        ResourceKind.Stone => JobKind.Quarry,
        ResourceKind.BerryBush => JobKind.Forage,
        _ => JobKind.None,
    };

    /// <summary>The node kind a gathering job works on, or <see cref="ResourceKind.None"/> for the other jobs.</summary>
    public static ResourceKind ResourceOf(JobKind kind) => kind switch
    {
        JobKind.Chop => ResourceKind.Tree,
        JobKind.Quarry => ResourceKind.Stone,
        JobKind.Forage => ResourceKind.BerryBush,
        _ => ResourceKind.None,
    };

    /// <summary>True for the jobs that gather from a node (<see cref="ResourceOf"/> is a node kind).</summary>
    public static bool IsGathering(JobKind kind) => ResourceOf(kind) != ResourceKind.None;

    /// <summary>
    /// The skill (index in <see cref="SkillNames.All"/>) that speeds up <paramref name="kind"/>: the skill of the first
    /// profession in the data that lists the job type; -1 when none does.
    /// </summary>
    public static int SkillOf(ContentCatalog content, JobKind kind)
    {
        ArgumentNullException.ThrowIfNull(content);
        string name = NameOf(kind);
        foreach (ProfessionDef profession in content.Professions)
        {
            foreach (string job in profession.JobTypes)
            {
                if (string.Equals(job, name, StringComparison.Ordinal))
                {
                    return IndexOf(SkillNames.All, profession.Skill);
                }
            }
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
