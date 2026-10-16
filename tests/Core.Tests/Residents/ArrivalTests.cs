using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Heroes;
using Core.Pathfinding;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Residents;

/// <summary>
/// Newcomers (FR-NPC-01, FR-NPC-02): the morning roll from the events stream, at most one a day, where they appear and
/// how they walk in, their name, skills, profession and home.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class ArrivalTests
{
    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void ANewcomerComesOnlyAtTheArrivalHourAndAtMostOneAMorning()
    {
        Simulation sim = NewResidentGame(Certain);
        Settle(sim, houses: 2);
        long morning = ArrivalTick(sim, 0);
        Assert.Equal(2250, morning); // 06:00 of day 0: 6 of 24 hours of a 9000-tick day

        StepTo(sim, morning - 1);
        Assert.Equal(0, sim.World!.Residents.Count);
        sim.Step();
        Assert.Equal(1, sim.World.Residents.Count);
        Assert.Equal(ResidentEventKind.Arrived, sim.World.ResidentEvents[^1].Kind);
        Assert.Equal(morning, sim.World.ResidentEvents[^1].Tick);

        // Four free places, but one newcomer per morning; the next one comes the next morning.
        StepTo(sim, ArrivalTick(sim, 1) - 1);
        Assert.Equal(1, sim.World.Residents.Count);
        sim.Step();
        Assert.Equal(2, sim.World.Residents.Count);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "NFR-04")]
    public void TheRollUsesTheEventsStreamOnlyWhenTheChanceIsAboveZero()
    {
        Simulation sim = NewResidentGame();
        Build(sim, "campfire");
        Xoshiro256State before = sim.Rng[RngStreamId.Events].GetState();
        StepTo(sim, ArrivalTick(sim, 0));
        Assert.Equal(before, sim.Rng[RngStreamId.Events].GetState()); // no places, chance 0, no roll

        // With a house: one permille roll, then (only on success) the name and the twelve skills.
        Build(sim, "house");
        StepTo(sim, ArrivalTick(sim, 1) - 1);
        int chance = Arrivals.ComputeOdds(sim).ChancePermille;
        Assert.Equal(700, chance);
        var expected = new Xoshiro256StarStar(sim.Rng[RngStreamId.Events].GetState());
        bool arrives = expected.ChancePermille(chance);
        if (arrives)
        {
            expected.NextInt(sim.Content.Names[0].Names.Count);
            for (int i = 0; i < SkillSet.Count; i++)
            {
                expected.NextInt(sim.Content.Economy.InitialSkillMax + 1);
            }
        }

        sim.Step();

        Assert.Equal(arrives ? 1 : 0, sim.World!.Residents.Count);
        Assert.Equal(expected.GetState(), sim.Rng[RngStreamId.Events].GetState());
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-NPC-02")]
    public void OnASmallMapWithoutSitesTheNewcomerWalksInFromTheNearestEdge()
    {
        Simulation sim = NewResidentGame(Certain);
        EntityId[] houses = Settle(sim, houses: 1);
        Assert.Empty(sim.World!.Map.SettlementSites);
        StepTo(sim, ArrivalTick(sim, 0));

        EntityId newcomer = Newest(sim);
        Resident resident = ResidentOf(sim, newcomer);
        ResidentEvent arrived = sim.World.ResidentEvents[^1];
        (int cx, int cy) = CenterTile(sim);
        Assert.Equal(ResidentState.Arriving, resident.State);
        AssertComesFromTheNearestEdge(sim, arrived.X, arrived.Y);
        Assert.Equal(houses[0], resident.Home);
        Assert.Equal("carrier", sim.Content.Get(resident.Profession).Id); // the first entry of the pattern
        Assert.Equal(sim.Content.Get<ProfessionDef>(resident.Profession).Id, arrived.Profession);
        Assert.Equal(ResidentRules.NameOf(sim.Content, resident), arrived.Name);
        Assert.Contains(arrived.Name, Assert.Single(sim.Content.Names).Names);
        Assert.All(resident.Skills.ToArray(), level => Assert.InRange(level, 0, sim.Content.Economy.InitialSkillMax));
        Assert.Equal(Core.Jobs.WorkPriorities.ForProfession(sim.Content.Get<ProfessionDef>(resident.Profession)), resident.Priorities);
        Assert.Equal(Core.Jobs.ResidentJob.Idle(sim.Tick), sim.World.Jobs.Get(newcomer));
        Assert.Equal(new Core.Fog.Revealer(Core.Fog.RevealerKind.Villager), sim.World.Revealers.Get(newcomer));
        Mover mover = sim.World.Movers.Get(newcomer);
        Assert.True(mover.IsMoving);
        Assert.Contains(mover.Goal, Arrivals.MeetingTiles(sim));

        // It walks in at the resident pace and waits for work next to the campfire.
        HeroTestWorldStepUntil(sim, () => ResidentOf(sim, newcomer).State == ResidentState.Idle);
        Assert.False(sim.World.Movers.Get(newcomer).IsMoving);
        (int x, int y) = TileOf(sim, newcomer);
        Assert.Equal(1, Core.Sim.AgentMovement.Distance(x, y, cx, cy));
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void TheNewcomerComesFromTheNearestSiteOfGoodPeople()
    {
        Simulation sim = NewLargeGame(Certain);
        SettlementSite site = Assert.Single(sim.World!.Map.SettlementSites, s => !WorldNames.IsEvil(s.Faction));
        Settle(sim, houses: 1);

        StepTo(sim, ArrivalTick(sim, 0));

        ResidentEvent arrived = Assert.Single(sim.World.ResidentEvents, e => e.Kind == ResidentEventKind.Arrived);
        int radius = sim.Content.WorldGen.Settlements.SiteSize / 2;
        Assert.InRange(Core.Sim.AgentMovement.Distance(arrived.X, arrived.Y, site.X, site.Y), 0, radius);
        Assert.True(sim.World.Pathfinder.Grid.IsPassable(arrived.X, arrived.Y));
        Assert.Equal(ResidentState.Arriving, ResidentOf(sim, arrived.Resident).State);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void WithoutACenterThereIsNoSpawnTileAndTheNewcomerWalksToTheNearestMeetingTile()
    {
        Simulation sim = NewResidentGame();
        Assert.Empty(Arrivals.MeetingTiles(sim));
        Assert.Equal(-1, Arrivals.FindSpawnTile(sim, []));
        Assert.Equal(-1, Arrivals.FindSpawnTile(new Simulation(1, SimTestData.Real), [1]));
        Assert.Equal(-1, Arrivals.NearestMeetingTile(sim.World!, -1, [1]));

        Build(sim, "campfire");
        IReadOnlyList<int> meeting = Arrivals.MeetingTiles(sim);
        Assert.InRange(meeting.Count, 1, 8);
        Assert.Equal(meeting.Order().ToArray(), meeting);
        int spawn = Arrivals.FindSpawnTile(sim, meeting);
        PassabilityGrid grid = sim.World!.Pathfinder.Grid;
        AssertComesFromTheNearestEdge(sim, grid.XOf(spawn), grid.YOf(spawn));
        int nearest = Arrivals.NearestMeetingTile(sim.World, spawn, meeting);
        Assert.True(sim.World.Pathfinder.IsReachable(spawn, nearest));
        Assert.All(meeting, tile => Assert.True(
            DistanceSquared(grid.XOf(nearest), grid.YOf(nearest), grid.XOf(spawn), grid.YOf(spawn))
            <= DistanceSquared(grid.XOf(tile), grid.YOf(tile), grid.XOf(spawn), grid.YOf(spawn))));
    }

    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void ProfessionsFollowTheAutomaticPatternByPopulation()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 1);
        IReadOnlyList<string> pattern = sim.Content.Economy.AutoAssignPattern;

        string[] professions = Enumerable.Range(0, pattern.Count + 1)
            .Select(_ => sim.Content.Get(ResidentOf(sim, Spawn(sim)).Profession).Id)
            .ToArray();

        Assert.Equal([.. pattern, pattern[0]], professions);
        Assert.Equal("blacksmith", sim.Content.Get(ResidentOf(sim, Spawn(sim, "blacksmith")).Profession).Id);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "NFR-04")]
    public void TwoRunsOfTheSameArrivalsGiveTheSameHashes()
    {
        Simulation a = NewResidentGame(Certain);
        Simulation b = NewResidentGame(Certain);
        Settle(a, houses: 2);
        Settle(b, houses: 2);

        StepTo(a, ArrivalTick(a, 1) + 300);
        StepTo(b, ArrivalTick(b, 1) + 300);

        Assert.Equal(2, a.World!.Residents.Count);
        Assert.Null(a.ComputeHashes().FindFirstMismatch(b.ComputeHashes()));
    }

    private static long DistanceSquared(int ax, int ay, int bx, int by) => ((long)(ax - bx) * (ax - bx)) + ((long)(ay - by) * (ay - by));

    // The spawn lies on the perpendicular from the map side nearest to the center, and no tile between that side and the
    // spawn is reachable land.
    private static void AssertComesFromTheNearestEdge(Simulation sim, int x, int y)
    {
        (int cx, int cy) = CenterTile(sim);
        PassabilityGrid grid = sim.World!.Pathfinder.Grid;
        int last = grid.Size - 1;
        (int dx, int dy, int distance) = new[] { (0, -1, cy), (-1, 0, cx), (1, 0, last - cx), (0, 1, last - cy) }.MinBy(s => s.Item3);
        Assert.True(dx == 0 ? x == cx : y == cy, $"spawn {x},{y} is not on the line from the nearest side to {cx},{cy}");
        IReadOnlyList<int> meeting = Arrivals.MeetingTiles(sim);
        for (int step = distance; step > 0; step--)
        {
            int tx = cx + (dx * step);
            int ty = cy + (dy * step);
            bool reachable = grid.IsPassable(tx, ty) && meeting.Any(t => sim.World.Pathfinder.IsReachable(grid.Index(tx, ty), t));
            if (tx == x && ty == y)
            {
                Assert.True(reachable);
                return;
            }

            Assert.False(reachable, $"{tx},{ty} is reachable land before the spawn {x},{y}");
        }

        Assert.Fail($"spawn {x},{y} is not between the nearest side and the center");
    }

    private static void HeroTestWorldStepUntil(Simulation sim, Func<bool> done) => Heroes.HeroTestWorld.StepUntil(sim, done, limit: 2000);
}
