using Core.Content;
using Core.Entities;
using Core.Jobs;
using Core.Residents;
using Core.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Jobs;

/// <summary>
/// Choosing a job (FR-NPC-06, ADR 0009, dual utility): priority tiers strictly, the type order inside a tier, the nearest
/// target, skill and food in the work speed, priorities and professions by command, determinism.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class JobSelectionTests
{
    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void ANewResidentTakesItsProfessionsJobAtOnceAndItsPrioritiesComeFromTheProfession()
    {
        (Simulation sim, _) = NewSettlement();

        EntityId lumberjack = Spawn(sim, "lumberjack");

        Resident resident = sim.World!.Residents.Get(lumberjack);
        Assert.Equal(WorkPriorities.ProfessionDefault, resident.Priorities.Get(JobKind.Chop));
        Assert.Equal(WorkPriorities.ProfessionDefault, resident.Priorities.Get(JobKind.Plant));
        Assert.Equal(0, resident.Priorities.Get(JobKind.Haul));
        ResidentJob job = JobOf(sim, lumberjack);
        Assert.Equal(JobKind.Chop, job.Kind);
        Assert.Equal(JobPhase.ToTarget, job.Phase);
        Assert.Equal(ResidentState.Walking, resident.State);
        Assert.True(sim.World.Movers.Get(lumberjack).IsMoving);
        Assert.Equal([new Reservation(ReservationTarget.OfTile(job.TargetTile), lumberjack, default, 1)], sim.World.Reservations.Reservations);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void TheNearestNodeWinsRingByRingThenTheLowerTileIndexAndTwoResidentsNeverShareOne()
    {
        (Simulation sim, _) = NewSettlement();
        int spawnTile = Arrivals.MeetingTiles(sim)[0];
        (int X, int Y) from = (spawnTile % sim.World!.Size, spawnTile / sim.World.Size);
        List<int> trees = NodesByRing(sim, ResourceKind.Tree, from);

        EntityId first = Spawn(sim, "lumberjack");
        EntityId second = Spawn(sim, "lumberjack");

        Assert.Equal(trees[0], JobOf(sim, first).TargetTile);
        Assert.Equal(trees[1], JobOf(sim, second).TargetTile); // the nearest is reserved
        Assert.Equal(2, sim.World.Reservations.Reservations.Count(r => r.Target.Layer == ReservationLayer.Tile));

        // Many more lumberjacks: every running gathering job holds a node of its own.
        for (int i = 0; i < 6; i++)
        {
            Spawn(sim, "lumberjack");
        }

        sim.Step(300);
        int[] targets = sim.World.Jobs.Values.ToArray().Where(j => j.Kind == JobKind.Chop).Select(j => j.TargetTile).ToArray();
        Assert.Equal(targets.Length, targets.Distinct().Count());
        Assert.Equal(targets.Order(), sim.World.Reservations.Reservations.Where(r => r.Target.Layer == ReservationLayer.Tile).Select(r => r.Target.Index).Order());
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void TiersAreStrictAndInsideATierTheTypeOrderDecides()
    {
        (Simulation sim, _) = NewSettlement();
        EntityId field = Build(sim, "field");
        Assert.True(sim.World!.WorkOutputs.Has(field));

        // A farmer has farm and forage at 1: the field comes first in the type order.
        EntityId farmer = Spawn(sim, "farmer");
        Assert.Equal(JobKind.Farm, JobOf(sim, farmer).Kind);
        Assert.Equal(field, JobOf(sim, farmer).Building);

        // Forage at 1, farm at 2: the tier wins over the type order.
        EntityId second = Spawn(sim, "blacksmith");
        Assert.Equal(JobKind.None, JobOf(sim, second).Kind); // a specialist without job types does nothing
        SetPriorities(sim, second, (JobKind.Farm, 2), (JobKind.Forage, 1));
        StepUntil(sim, () => JobOf(sim, second).Kind != JobKind.None);
        Assert.Equal(JobKind.Forage, JobOf(sim, second).Kind);

        // A job type at tier 4 alone is still taken when nothing ranks higher.
        EntityId third = Spawn(sim, "blacksmith");
        SetPriorities(sim, third, (JobKind.Farm, 4));
        StepUntil(sim, () => JobOf(sim, third).Kind != JobKind.None);
        Assert.Equal(JobKind.Farm, JobOf(sim, third).Kind);
        Assert.Equal(2, sim.World.Reservations.HolderCount(ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, field)));

        // The field has two slots: a fourth farmer forages instead.
        EntityId fourth = Spawn(sim, "farmer");
        Assert.Equal(JobKind.Forage, JobOf(sim, fourth).Kind);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void PriorityZeroMeansNeverAndTheCommandEndsARunningJobOfThatType()
    {
        (Simulation sim, _) = NewSettlement();
        EntityId lumberjack = Spawn(sim, "lumberjack");
        Assert.Equal(JobKind.Chop, JobOf(sim, lumberjack).Kind);
        SetPriorities(sim, lumberjack, (JobKind.Chop, 1)); // no planting either

        sim.Enqueue(new SetWorkPriorityCommand(lumberjack, "chop", 0));
        sim.Step();

        Assert.Equal(0, sim.World!.Residents.Get(lumberjack).Priorities.Get(JobKind.Chop));
        Assert.Equal(JobKind.None, JobOf(sim, lumberjack).Kind);
        Assert.Empty(sim.World.Reservations.Reservations);
        sim.Step(100);
        Assert.Equal(JobKind.None, JobOf(sim, lumberjack).Kind);

        // Another priority does not interrupt; a higher one takes effect at the next look.
        sim.Enqueue(new SetWorkPriorityCommand(lumberjack, "chop", 3));
        sim.Step();
        StepUntil(sim, () => JobOf(sim, lumberjack).Kind == JobKind.Chop);
        sim.Enqueue(new SetWorkPriorityCommand(lumberjack, "forage", 1));
        sim.Step();
        Assert.Equal(JobKind.Chop, JobOf(sim, lumberjack).Kind); // the running job is not pushed out

        // Bad orders change nothing.
        WorkPriorities before = sim.World.Residents.Get(lumberjack).Priorities;
        sim.Enqueue(new SetWorkPriorityCommand(lumberjack, "dance", 1));
        sim.Enqueue(new SetWorkPriorityCommand(lumberjack, "chop", 5));
        sim.Enqueue(new SetWorkPriorityCommand(new EntityId(60, 1), "chop", 1));
        sim.Step();
        Assert.Equal(before, sim.World.Residents.Get(lumberjack).Priorities);
        Assert.Equal(3, sim.Log.GetEvents().Count(e => e.Tick == sim.Tick && e.Level == Core.Logging.LogLevel.Warning && e.Category == "jobs"));
    }

    [Fact]
    [Trait("Req", "FR-NPC-02")]
    [Trait("Req", "FR-NPC-06")]
    public void ANewProfessionResetsThePrioritiesAndEndsTheJobButSpecialistsCannotBeAssigned()
    {
        (Simulation sim, _) = NewSettlement();
        EntityId resident = Spawn(sim, "lumberjack");
        sim.Enqueue(new SetWorkPriorityCommand(resident, "haul", 2));
        sim.Step();

        sim.Enqueue(new SetProfessionCommand(resident, "carrier"));
        sim.Step();

        Resident r = sim.World!.Residents.Get(resident);
        Assert.Equal("carrier", sim.Content.Get(r.Profession).Id);
        Assert.Equal(WorkPriorities.ForProfession(sim.Content.Get<ProfessionDef>(r.Profession)), r.Priorities);
        Assert.Equal(JobKind.None, JobOf(sim, resident).Kind); // nothing to haul yet
        Assert.Empty(sim.World.Reservations.Reservations);

        sim.Enqueue(new SetProfessionCommand(resident, "blacksmith"));
        sim.Enqueue(new SetProfessionCommand(resident, "astronaut"));
        sim.Step();
        Assert.Equal("carrier", sim.Content.Get(sim.World.Residents.Get(resident).Profession).Id);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-ECO-10")]
    public void SkillSpeedsWorkUpAndAShortageSlowsItDown()
    {
        (Simulation sim, _) = NewSettlement();
        EconomyBalanceDef economy = sim.Content.Economy;
        EntityId resident = Spawn(sim, "lumberjack");
        int mining = SkillNames.All.ToList().IndexOf("mining");
        ref Resident r = ref sim.World!.Residents.Get(resident);
        int[] skills = r.Skills.ToArray();
        skills[mining] = 20;
        r = r with { Skills = SkillSet.From(skills) };
        Resident skilled = r;

        Assert.Equal(mining, JobKinds.SkillOf(sim.Content, JobKind.Chop));
        Assert.Equal(1000 + (20 * economy.SkillSpeedPermillePerPoint), JobBoard.WorkSpeedPermille(sim, skilled, JobKind.Chop));
        Assert.Equal(1000 + (skilled.Skills.Get(SkillNames.All.ToList().IndexOf("construction")) * economy.SkillSpeedPermillePerPoint),
            JobBoard.WorkSpeedPermille(sim, skilled, JobKind.Haul));
        Assert.True(JobBoard.Weight(sim, skilled, JobKind.Chop, 10, 50) < JobBoard.Weight(sim, skilled with { Skills = default }, JobKind.Chop, 10, 50));
        Assert.True(JobBoard.Weight(sim, skilled, JobKind.Chop, 9, 50) < JobBoard.Weight(sim, skilled, JobKind.Chop, 10, 50));

        sim.World.DaysWithoutFood = 1;
        Assert.Equal((1000 + (20 * economy.SkillSpeedPermillePerPoint)) * economy.ShortageSlowdownPermille / 1000,
            JobBoard.WorkSpeedPermille(sim, skilled, JobKind.Chop));
        sim.World.DaysWithoutFood = 0;

        // The first action of the skilled lumberjack takes ceil(ticks × 1000 / speed) ticks of work.
        StepUntil(sim, () => JobOf(sim, resident).Phase == JobPhase.Working);
        int ticks = StepUntil(sim, () => JobOf(sim, resident).CarryCount > 0);
        GatherDef tree = sim.Content.Hero.Gathering.Single(g => g.Source == "tree");
        int speed = JobBoard.WorkSpeedPermille(sim, skilled, JobKind.Chop);
        Assert.Equal(((tree.TicksPerAction * 1000) + speed - 1) / speed, ticks);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-NPC-06")]
    public void TwoRunsOfTheSameSettlementGiveIdenticalHashesEveryTick()
    {
        Simulation[] runs = [Run(), Run()];
        for (int tick = 0; tick < 2000; tick++)
        {
            runs[0].Step();
            runs[1].Step();
            Assert.Null(runs[0].ComputeHashes().FindFirstMismatch(runs[1].ComputeHashes()));
        }

        Assert.NotEmpty(runs[0].World!.Reservations.Reservations);

        static Simulation Run()
        {
            (Simulation sim, _) = NewSettlement();
            Build(sim, "field");
            foreach (string profession in new[] { "lumberjack", "farmer", "carrier", "lumberjack", "farmer" })
            {
                Spawn(sim, profession);
            }

            return sim;
        }
    }
}
