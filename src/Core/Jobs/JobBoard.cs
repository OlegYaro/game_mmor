using Core.Content;
using Core.Residents;
using Core.Sim;

namespace Core.Jobs;

/// <summary>
/// Read-only rules of the job board for the game and the console (FR-NPC-06, ADR 0009): how a resident picks a job,
/// how fast it works. The rules that change the state are in <see cref="JobRules"/>.
/// </summary>
/// <remarks>
/// <para>Choice (dual utility): the resident goes through its priority tiers 1 to 4 strictly; inside a tier through the
/// job types in <see cref="JobKinds.TierOrder"/>; for one type the candidates are ranked by site priority (every site has
/// <see cref="DefaultSitePriority"/> until the player can change it), then by <see cref="Weight"/> (distance and skill),
/// then by id (tile index or entity index). The first type of the first tier that has a candidate whose targets can all be
/// reserved wins.</para>
/// <para>Game notes: a resident's job and load are its <see cref="ResidentJob"/> in <see cref="GameWorld.Jobs"/>, its
/// priorities <see cref="Resident.Priorities"/>; the held targets are <see cref="GameWorld.Reservations"/>. Orders:
/// <see cref="SetWorkPriorityCommand"/>, <see cref="SetProfessionCommand"/>.</para>
/// </remarks>
public static class JobBoard
{
    /// <summary>Site priority of every building and node (ADR 0009: 1–9, default 5); no order sets another one yet.</summary>
    public const int DefaultSitePriority = 5;

    /// <summary>Base work speed in thousandths: 1000 is the speed of the data's tick numbers.</summary>
    public const int BaseSpeedPermille = 1000;

    /// <summary>
    /// Work speed of <paramref name="resident"/> at <paramref name="kind"/> in thousandths: 1000 plus
    /// <see cref="EconomyBalanceDef.SkillSpeedPermillePerPoint"/> per point of the job's skill (<see cref="JobKinds.SkillOf"/>),
    /// times <see cref="EconomyBalanceDef.ShortageSlowdownPermille"/> / 1000 while the settlement is short of food. At
    /// least 1.
    /// </summary>
    public static int WorkSpeedPermille(Simulation sim, Resident resident, JobKind kind)
    {
        ArgumentNullException.ThrowIfNull(sim);
        EconomyBalanceDef economy = sim.Content.Economy;
        int skill = JobKinds.SkillOf(sim.Content, kind);
        int speed = BaseSpeedPermille + (skill < 0 ? 0 : resident.Skills.Get(skill) * economy.SkillSpeedPermillePerPoint);
        if (sim.World is { DaysWithoutFood: > 0 })
        {
            speed = (int)((long)speed * economy.ShortageSlowdownPermille / BaseSpeedPermille);
        }

        return Math.Max(1, speed);
    }

    /// <summary>
    /// The weight of a candidate inside its tier, type and site priority (lower is better): the ticks a resident needs to
    /// walk <paramref name="distance"/> tiles and do <paramref name="workTicks"/> of work at its speed. For one resident
    /// and one job type the work part is the same for every candidate, so the nearest wins; the skill enters through the
    /// speed.
    /// </summary>
    public static long Weight(Simulation sim, Resident resident, JobKind kind, int distance, int workTicks)
    {
        ArgumentNullException.ThrowIfNull(sim);
        long walk = (long)distance * sim.Content.Economy.ResidentStepTicks * BaseSpeedPermille;
        long work = (long)workTicks * BaseSpeedPermille * BaseSpeedPermille / WorkSpeedPermille(sim, resident, kind);
        return walk + work;
    }

    /// <summary>The job of <paramref name="resident"/>, or an idle job when it is not a resident.</summary>
    public static ResidentJob JobOf(GameWorld world, Entities.EntityId resident)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Jobs.TryGet(resident, out ResidentJob job) ? job : ResidentJob.Idle(0);
    }
}
