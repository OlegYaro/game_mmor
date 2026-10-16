using Core.Content;
using Core.Determinism;
using Core.Entities;

namespace Core.Jobs;

/// <summary>
/// The job a resident holds and what it carries (ADR 0009: a linear job body). Every resident has exactly one; an idle
/// resident has <see cref="JobKind.None"/>. The targets of a running job are reserved in the
/// <see cref="ReservationManager"/>; they are released all at once when the job ends.
/// </summary>
/// <remarks>
/// <para>Bodies: a gathering job (<see cref="JobKind.Chop"/>, <see cref="JobKind.Quarry"/>, <see cref="JobKind.Forage"/>)
/// walks to its node (<see cref="JobPhase.ToTarget"/>), works action after action while the load fits
/// (<see cref="JobPhase.Working"/>), then carries the load to a storage (<see cref="JobPhase.ToStorage"/>).
/// <see cref="JobKind.Farm"/> walks to its field and works one cycle. <see cref="JobKind.Plant"/> walks to a free tile
/// next to a stump (<see cref="TargetTile"/>), works <see cref="RegrowthBalanceDef.PlantActionTicks"/> and leaves a
/// sapling there. <see cref="JobKind.Haul"/> walks to a building,
/// takes its output and carries it to a storage; without a building it only carries the resident's own load.</para>
/// <para>The load stays with the resident when a job ends without handing it in (no storage, cancelled): the
/// resident then hauls it first as soon as a storage has room, and takes no other job while it carries something.</para>
/// </remarks>
/// <param name="Kind">The job type; <see cref="JobKind.None"/> without a job.</param>
/// <param name="Phase">Where in the job body it is.</param>
/// <param name="Building">The field (farm) or the building whose output it hauls; <see cref="EntityId.None"/> otherwise.</param>
/// <param name="TargetTile">The node tile of a gathering job or the tile of a planting job; -1 otherwise.</param>
/// <param name="Storage">The storage it carries the load to; <see cref="EntityId.None"/> until one is reserved.</param>
/// <param name="GoalTile">The tile it walks to or works next to (a node, a footprint tile of the building or storage); -1 without one.</param>
/// <param name="WorkLeft">Work left in the current action or cycle, in thousandths of a tick at base speed.</param>
/// <param name="CarryItem">The carried item; default when it carries nothing.</param>
/// <param name="CarryCount">How many it carries, 0 to <see cref="EconomyBalanceDef.HaulCapacity"/>.</param>
/// <param name="NextThinkTick">
/// First tick at which an idle resident looks for a job again (at once after a job ends; after a look that found nothing
/// <see cref="EconomyBalanceDef.ThinkIntervalTicks"/>, doubling with every further such look up to
/// <see cref="EconomyBalanceDef.IdleThinkMaxIntervalTicks"/>).
/// </param>
/// <param name="IdleLooks">Looks in a row that found nothing (0 while it has a job): it sets the wait before the next look.</param>
public readonly record struct ResidentJob(
    JobKind Kind,
    JobPhase Phase,
    EntityId Building,
    int TargetTile,
    EntityId Storage,
    int GoalTile,
    int WorkLeft,
    DefId CarryItem,
    int CarryCount,
    long NextThinkTick,
    int IdleLooks = 0) : IComponent
{
    /// <summary>Most <see cref="IdleLooks"/> counted (the wait has long reached its cap by then).</summary>
    public const int MaxIdleLooks = 30;

    /// <summary>No job, nothing carried, thinking from <paramref name="tick"/> on.</summary>
    public static ResidentJob Idle(long tick) => new(JobKind.None, JobPhase.None, EntityId.None, -1, EntityId.None, -1, 0, default, 0, tick);

    /// <summary>True while it carries something.</summary>
    public bool IsCarrying => CarryCount > 0;

    /// <summary>The same resident without a job: the load and the think tick stay.</summary>
    public ResidentJob Cleared(long nextThinkTick) => Idle(nextThinkTick) with { CarryItem = CarryItem, CarryCount = CarryCount };

    /// <summary>Writes every field in declaration order (kind and phase as one byte each).</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte((byte)Kind);
        writer.WriteByte((byte)Phase);
        Building.WriteState(writer);
        writer.WriteInt32(TargetTile);
        Storage.WriteState(writer);
        writer.WriteInt32(GoalTile);
        writer.WriteInt32(WorkLeft);
        writer.WriteInt32(CarryItem.Value);
        writer.WriteInt32(CarryCount);
        writer.WriteInt64(NextThinkTick);
        writer.WriteInt32(IdleLooks);
    }
}
