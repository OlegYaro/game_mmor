using Core.DevConsole;
using Core.Entities;
using Core.Jobs;
using Core.Logging;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Jobs.JobTestWorld;
using ConsoleType = Core.DevConsole.DevConsole;

namespace Core.Tests.DevConsoleTests;

/// <summary>Work, food and tile console commands (NFR-07, FR-NPC-06, FR-NPC-07, FR-ECO-10, FR-WORLD-04, FR-WORLD-15): jobs, setprofession, setpriority, food, tile.</summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class DevConsoleWorkTests
{
    private readonly SimLog _log = new();

    private ConsoleType Console(Simulation sim) =>
        new(new DevConsoleContext(new SimulationHolder(sim), new TickScheduler(10), _log, SimTestData.Real, Path.GetTempPath()));

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-NPC-07")]
    public void JobsListsEveryResidentsJobLoadAndPrioritiesThenTheReservationsAndBans()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        ConsoleType console = Console(sim);
        EntityId lumberjack = Spawn(sim, "lumberjack");
        EntityId smith = Spawn(sim, "blacksmith");
        int tree = JobOf(sim, lumberjack).TargetTile;
        sim.World!.Reservations.Ban(smith, ReservationTarget.OfBuilding(ReservationLayer.Storage, storage), sim.Tick + 40);

        ConsoleResult jobs = console.Execute("jobs");

        Assert.True(jobs.Success, jobs.Text);
        Assert.Equal("residents 2 | with a job 1 | reservations 1 | bans 1", jobs.Lines[0]);
        string name = ResidentRules.NameOf(sim.Content, sim.World.Residents.Get(lumberjack));
        Assert.Equal($"{lumberjack} {name} lumberjack walking: chop to_target at node {tree % 64},{tree / 64} | carries nothing | farm=0 forage=0 chop=1 quarry=0 plant=1 haul=0",
            jobs.Lines[1]);
        Assert.StartsWith($"{smith} ", jobs.Lines[2], StringComparison.Ordinal);
        Assert.Contains($": no job (looks again at tick {JobOf(sim, smith).NextThinkTick}) | carries nothing | farm=0 ", jobs.Lines[2], StringComparison.Ordinal);
        Assert.Equal($"reserved tile {tree % 64},{tree / 64} by {lumberjack}: 1", jobs.Lines[3]);
        Assert.Equal($"banned storage of {storage} for {smith} until tick {sim.Tick + 40}", jobs.Lines[4]);

        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.ToStorage);
        string[] carrying = [.. console.Execute("jobs").Lines];
        Assert.Contains(carrying, l => l.Contains($"chop to_storage -> storage {storage} | carries 20 wood", StringComparison.Ordinal));
        Assert.Contains(carrying, l => l == $"reserved storage of {storage} by {lumberjack}: 20 wood");
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-NPC-06")]
    public void SetProfessionAndSetPriorityQueueTheirCommands()
    {
        (Simulation sim, _) = NewSettlement();
        ConsoleType console = Console(sim);
        EntityId resident = Spawn(sim, "lumberjack");

        ConsoleResult profession = console.Execute($"setprofession {resident.Index} Farmer");
        Assert.True(profession.Success, profession.Text);
        Assert.Equal($"profession farmer for {resident} queued for tick {sim.Tick + 1} (see 'jobs' for the result)", profession.Text);
        Assert.Equal(new SetProfessionCommand(resident, "farmer"), Assert.Single(sim.PendingCommands).Command);
        sim.Step();

        ConsoleResult priority = console.Execute($"setpriority {resident} CHOP 2");
        Assert.True(priority.Success, priority.Text);
        Assert.Equal($"priority chop=2 for {resident} queued for tick {sim.Tick + 1} (see 'jobs' for the result)", priority.Text);
        sim.Step();
        Assert.Equal(2, sim.World!.Residents.Get(resident).Priorities.Get(JobKind.Chop));
        Assert.Equal("farmer", sim.Content.Get(sim.World.Residents.Get(resident).Profession).Id);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void BadArgumentsAreRefused()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        ConsoleType console = Console(sim);
        EntityId resident = Spawn(sim, "lumberjack");

        Assert.False(console.Execute("jobs now").Success);
        Assert.False(console.Execute("food today").Success);
        Assert.False(console.Execute("setprofession 1").Success);
        Assert.Equal($"'{storage.Index}' is not a resident. Type 'residents' for a list.", console.Execute($"setprofession {storage.Index} farmer").Text);
        Assert.False(console.Execute("setprofession 7v9 farmer").Success);
        Assert.StartsWith("'blacksmith' is not an ordinary profession. Ordinary professions: builder, carrier, ", console.Execute($"setprofession {resident} blacksmith").Text,
            StringComparison.Ordinal);
        Assert.False(console.Execute($"setpriority {resident} chop").Success);
        Assert.Equal("Unknown job type 'dance'. Job types: haul, chop, plant, quarry, forage, farm.", console.Execute($"setpriority {resident} dance 1").Text);
        Assert.False(console.Execute($"setpriority {resident} chop 5").Success);
        Assert.False(console.Execute($"setpriority {resident} chop x").Success);
        Assert.Empty(sim.PendingCommands);

        ConsoleType core = Console(new Simulation(1, SimTestData.Real));
        Assert.False(core.Execute("jobs").Success);
        Assert.False(core.Execute("food").Success);
        Assert.False(core.Execute("setprofession 1 farmer").Success);
        Assert.False(core.Execute("setpriority 1 chop 1").Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-ECO-10")]
    [Trait("Req", "FR-ECO-06")]
    public void FoodPrintsTheStockTheNeedTheShortageAndTheTax()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        ConsoleType console = Console(sim);
        Assert.Equal("food 0 units in storages | 0 residents eat 0 a day | nobody eats | no shortage", console.Execute("food").Lines[0]);

        Spawn(sim, "blacksmith");
        Spawn(sim, "blacksmith");
        Fill(sim, storage, "berries", 7);
        ConsoleResult food = console.Execute("food");
        Assert.True(food.Success, food.Text);
        Assert.Equal(["food 7 units in storages | 2 residents eat 2 a day | lasts 3 days | no shortage", "tax 4 gold a day (2 x residents x center level 1)"], food.Lines);

        sim.World!.DaysWithoutFood = 2;
        sim.World.LastLeaveDay = 0;
        StepToDay(sim, 1);
        string[] lines = [.. console.Execute("food").Lines];
        Assert.Equal("food 5 units in storages | 2 residents eat 2 a day | lasts 2 days | no shortage", lines[0]);
        Assert.Equal($"last event (tick {sim.Tick}): Tax amount 4 lost 0 days 0", lines[2]);

        sim.World.DaysWithoutFood = 3;
        sim.World.LastLeaveDay = 1;
        Assert.Equal("food 5 units in storages | 2 residents eat 2 a day | lasts 2 days | SHORTAGE 3 days (work at 500‰, last left on day 1)", console.Execute("food").Lines[0]);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-WORLD-15")]
    public void TilePrintsTheFloraAndWalkCountsOrOneTile()
    {
        (Simulation sim, _) = NewSettlement();
        ConsoleType console = Console(sim);
        GameWorld world = sim.World!;
        Assert.Equal("flora 0 tiles (stumps, saplings, grown trees) | walked 0 tiles | trails 0 | roads 0", console.Execute("tile").Text);

        (int x, int y) = Heroes.HeroTestWorld.NearestNode(sim, Core.WorldGen.ResourceKind.Tree);
        world.Tiles.TakeResource(x, y, int.MaxValue);
        for (int i = 0; i < 10; i++)
        {
            world.Tiles.Trample(x, y);
        }

        Assert.Equal($"tile {x},{y}: node None (0) | flora stump since tick {sim.Tick} | walk 40 (trail) | occupant none", console.Execute($"tile {x} {y}").Text);
        Assert.Equal("flora 1 tiles (stumps, saplings, grown trees) | walked 1 tiles | trails 1 | roads 0", console.Execute("tile").Text);
        Assert.False(console.Execute("tile 1").Success);
        Assert.False(console.Execute("tile a b").Success);
        Assert.False(console.Execute($"tile {world.Size} 0").Success);
        Assert.False(Console(new Simulation(1, SimTestData.Real)).Execute("tile").Success);
    }
}
