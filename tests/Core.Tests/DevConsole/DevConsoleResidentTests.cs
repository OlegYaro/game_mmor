using Core.DevConsole;
using Core.Entities;
using Core.Logging;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Residents.ResidentTestWorld;
using ConsoleType = Core.DevConsole.DevConsole;

namespace Core.Tests.DevConsoleTests;

/// <summary>Resident console commands (NFR-07, FR-NPC-01, FR-NPC-02): spawnresident and residents.</summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class DevConsoleResidentTests
{
    private readonly SimLog _log = new();

    private ConsoleType Console(Simulation sim) =>
        new(new DevConsoleContext(new SimulationHolder(sim), new TickScheduler(10), _log, SimTestData.Real, Path.GetTempPath()));

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-NPC-02")]
    public void SpawnResidentQueuesTheCommandAndResidentsListsThem()
    {
        Simulation sim = NewResidentGame();
        ConsoleType console = Console(sim);
        EntityId house = Settle(sim, houses: 1)[0];

        ConsoleResult spawn = console.Execute("spawnresident Blacksmith");
        Assert.True(spawn.Success, spawn.Text);
        Assert.Equal($"spawn resident blacksmith queued for tick {sim.Tick + 1} (see 'residents' for the result)", spawn.Text);
        Assert.Equal(new SpawnResidentCommand("blacksmith"), Assert.Single(sim.PendingCommands).Command);
        sim.Step();
        Assert.Equal($"spawn resident (automatic profession) queued for tick {sim.Tick + 1} (see 'residents' for the result)", console.Execute("spawnresident").Text);
        sim.Step();

        ConsoleResult list = console.Execute("residents");
        Assert.True(list.Success, list.Text);
        Assert.Equal("population 2/100 | era camp (reached camp) | free places 0 | homeless 0", list.Lines[0]);
        Assert.Equal("newcomer at 06:00: chance 0‰ from 20 points | center yes | places 0 -> 0 | food 0/9 -> 0 | no raids -> 20 | fame -> 0 | era -> 0", list.Lines[1]);
        EntityId first = sim.World!.Residents.Entities[0];
        Resident resident = sim.World.Residents.Get(first);
        (int x, int y) = TileOf(sim, first);
        Assert.Equal(
            $"{first} {ResidentRules.NameOf(sim.Content, resident)} blacksmith idle at {x},{y} | home {house} | skills {string.Join(" ", resident.Skills.ToArray())}",
            list.Lines[2]);
        Assert.StartsWith($"{sim.World.Residents.Entities[1]} ", list.Lines[3], StringComparison.Ordinal); // the automatic one: population 1 picks the second pattern entry
        Assert.Contains(" lumberjack ", list.Lines[3], StringComparison.Ordinal); // already on its way to a tree
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void BadArgumentsAndGamesWithoutAWorldAreRefused()
    {
        Simulation sim = NewResidentGame();
        ConsoleType console = Console(sim);

        Assert.False(console.Execute("spawnresident a b").Success);
        ConsoleResult unknown = console.Execute("spawnresident astronaut");
        Assert.False(unknown.Success);
        Assert.StartsWith("Unknown profession 'astronaut'. Professions: armorer, ", unknown.Text, StringComparison.Ordinal);
        Assert.False(console.Execute("residents now").Success);
        Assert.Equal("no residents", console.Execute("residents").Lines[^1]);
        Assert.Empty(sim.PendingCommands);

        // Queued without a center: the order runs and is refused in the log.
        Assert.True(console.Execute("spawnresident").Success);
        sim.Step();
        Assert.Equal(0, sim.World!.Residents.Count);
        Assert.Contains(sim.Log.GetEvents(), e => e.Level == LogLevel.Warning && e.Category == ResidentRules.LogCategory);

        ConsoleType core = Console(new Simulation(1, SimTestData.Real));
        Assert.False(core.Execute("spawnresident").Success);
        Assert.False(core.Execute("residents").Success);
    }
}
