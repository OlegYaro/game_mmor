using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Jobs;

namespace Core.Residents;

/// <summary>What a resident is doing (ADR 0009). Values are part of the state hash; save names are in the save mapper. Append only.</summary>
public enum ResidentState : byte
{
    /// <summary>A newcomer walking from where it appeared to the settlement center.</summary>
    Arriving = 0,

    /// <summary>In the settlement, waiting for work.</summary>
    Idle = 1,

    /// <summary>Walking: to a job's target, with a load to a storage, or to a free tile next to the center.</summary>
    Walking = 2,

    /// <summary>Working next to a job's target (<see cref="JobPhase.Working"/>).</summary>
    Working = 3,

    /// <summary>Leaving the settlement for good (food shortage): walks to where newcomers come from and disappears there.</summary>
    Leaving = 4,
}

/// <summary>
/// The twelve skills of a resident (GDD E6, <see cref="SkillNames.All"/> order), each 0 to <see cref="MaxLevel"/>,
/// packed into one word: skill <c>i</c> is bits <c>5i .. 5i + 4</c>. The four high bits are always 0.
/// </summary>
/// <param name="Bits">The packed levels.</param>
public readonly record struct SkillSet(ulong Bits) : IStateHashable
{
    /// <summary>Number of skills.</summary>
    public const int Count = 12;

    /// <summary>Highest skill level.</summary>
    public const int MaxLevel = 20;

    private const int BitsPerSkill = 5;
    private const ulong SkillMask = (1UL << BitsPerSkill) - 1;

    /// <summary>The set with the given levels, in <see cref="SkillNames.All"/> order.</summary>
    /// <exception cref="ArgumentException">Not exactly <see cref="Count"/> levels, or a level outside 0..<see cref="MaxLevel"/>.</exception>
    public static SkillSet From(IReadOnlyList<int> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count != Count)
        {
            throw new ArgumentException($"A resident has {Count} skills, not {levels.Count}.", nameof(levels));
        }

        ulong bits = 0;
        for (int i = 0; i < Count; i++)
        {
            if (levels[i] is < 0 or > MaxLevel)
            {
                throw new ArgumentException($"Skill level {levels[i]} is outside 0..{MaxLevel}.", nameof(levels));
            }

            bits |= (ulong)levels[i] << (i * BitsPerSkill);
        }

        return new SkillSet(bits);
    }

    /// <summary>The level of skill <paramref name="skill"/> (index in <see cref="SkillNames.All"/>).</summary>
    public int Get(int skill)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skill);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(skill, Count);
        return (int)((Bits >> (skill * BitsPerSkill)) & SkillMask);
    }

    /// <summary>All levels in <see cref="SkillNames.All"/> order.</summary>
    public int[] ToArray()
    {
        int[] levels = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            levels[i] = Get(i);
        }

        return levels;
    }

    /// <summary>True when every level is at most <see cref="MaxLevel"/> and the unused high bits are 0.</summary>
    public bool IsValid()
    {
        if (Bits >> (Count * BitsPerSkill) != 0)
        {
            return false;
        }

        for (int i = 0; i < Count; i++)
        {
            if (Get(i) > MaxLevel)
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
        writer.WriteUInt64(Bits);
    }
}

/// <summary>
/// A resident of the player's settlement (FR-NPC-01, FR-NPC-02, ADR 0009). It also has a <see cref="Position"/>, a
/// <see cref="Heroes.Mover"/> (it walks with the shared agent movement), a <see cref="Fog.Revealer"/> of the villager
/// kind and a <see cref="ResidentJob"/> (its job and load).
/// </summary>
/// <param name="Name">Index of the first name in the <c>human_names</c> list (<see cref="NameListDef"/>).</param>
/// <param name="Profession">Profession (<see cref="ProfessionDef"/>).</param>
/// <param name="Skills">The twelve skill levels.</param>
/// <param name="Home">The house it lives in, or <see cref="EntityId.None"/> when homeless.</param>
/// <param name="State">What it is doing.</param>
/// <param name="Priorities">Its priority 0–4 for every job type (FR-NPC-06); the profession's types start at 1.</param>
public readonly record struct Resident(int Name, DefId Profession, SkillSet Skills, EntityId Home, ResidentState State, WorkPriorities Priorities) : IComponent
{
    /// <summary>Writes the name index, profession, skills, home, state (one byte) and priorities.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Name);
        writer.WriteInt32(Profession.Value);
        Skills.WriteState(writer);
        Home.WriteState(writer);
        writer.WriteByte((byte)State);
        Priorities.WriteState(writer);
    }
}
