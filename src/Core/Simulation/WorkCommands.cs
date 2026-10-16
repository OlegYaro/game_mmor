using Core.Determinism;
using Core.Entities;

namespace Core.Sim;

/// <summary>
/// Sets a resident's priority for one job type (FR-NPC-06, window "Residents"): 1 is the highest tier, 4 the lowest, 0
/// means the resident does not take the job; 0 also ends a running job of that type. Ignored (logged) for a stranger,
/// a leaving resident, an unknown job type or a priority outside 0..4.
/// </summary>
/// <param name="Resident">The resident.</param>
/// <param name="JobType">Job type name (<see cref="Jobs.JobKinds.Names"/>, e.g. <c>chop</c>).</param>
/// <param name="Priority">0 to <see cref="Jobs.WorkPriorities.Max"/>.</param>
public sealed record SetWorkPriorityCommand(EntityId Resident, string JobType, int Priority) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "set_work_priority";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        Jobs.JobRules.OrderSetPriority(sim, Resident, JobType, Priority);
    }

    /// <summary>Writes the resident, the job type and the priority.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Resident.WriteState(writer);
        writer.WriteString(JobType);
        writer.WriteInt32(Priority);
    }
}

/// <summary>
/// Gives a resident another ordinary profession (FR-NPC-02, window "Residents"): its priorities become the new
/// profession's defaults (1 for its job types, 0 for the rest) and a running job ends; the load stays with it. Ignored
/// (logged) for a stranger, a leaving resident, or a specialist or unknown profession.
/// </summary>
/// <param name="Resident">The resident.</param>
/// <param name="Profession">Ordinary profession id.</param>
public sealed record SetProfessionCommand(EntityId Resident, string Profession) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "set_profession";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        Jobs.JobRules.OrderSetProfession(sim, Resident, Profession);
    }

    /// <summary>Writes the resident and the profession id.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Resident.WriteState(writer);
        writer.WriteString(Profession);
    }
}

/// <summary>
/// Sets which item categories a storage takes in (FR-ECO-01, window "Storage"): bit <c>1 &lt;&lt; i</c> for
/// <see cref="Content.ItemCategories.All"/>[i]; <see cref="Content.ItemCategories.AllMask"/> takes everything (the
/// default). Items already stored stay. Ignored (logged) for anything but a storage or a mask with unknown bits.
/// </summary>
/// <param name="Storage">The storage building.</param>
/// <param name="Categories">The allowed categories.</param>
public sealed record SetStorageFilterCommand(EntityId Storage, int Categories) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "set_storage_filter";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        Economy.EconomyRules.OrderSetFilter(sim, Storage, Categories);
    }

    /// <summary>Writes the storage and the mask.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Storage.WriteState(writer);
        writer.WriteInt32(Categories);
    }
}

/// <summary>
/// The hero hands its backpack in at a storage (FR-ECO-01): every backpack stack, in slot order, goes in as far as the
/// storage has room and its filter allows; the rest stays in the backpack. The hero must stand within
/// <see cref="Content.HeroBalanceDef.InteractionRange"/> of the storage's footprint, and the storage must be available.
/// The outcome is an <see cref="Economy.EconomyEventKind.HeroDeposit"/> event; a refusal is logged.
/// </summary>
/// <param name="Storage">The storage building.</param>
public sealed record DepositHeroItemsCommand(EntityId Storage) : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "deposit_hero_items";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        Economy.EconomyRules.OrderDeposit(sim, Storage);
    }

    /// <summary>Writes the storage.</summary>
    public void WriteState(StateWriter writer) => Storage.WriteState(writer);
}
