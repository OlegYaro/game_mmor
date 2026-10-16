using Core.Buildings;
using Core.DevConsole;
using Core.Entities;
using Core.Logging;
using Core.Sim;
using Core.Tests.Heroes;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using ConsoleType = Core.DevConsole.DevConsole;

namespace Core.Tests.DevConsoleTests;

/// <summary>Building console commands (NFR-07, FR-BUILD-01, 02, 09, 12, 13): build, check, upgrade, movebuilding, demolish, cancelbuilding, buildings.</summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class DevConsoleBuildingTests
{
    private readonly SimLog _log = new();

    private ConsoleType Console(Simulation sim) =>
        new(new DevConsoleContext(new SimulationHolder(sim), new TickScheduler(10), _log, SimTestData.Real, Path.GetTempPath()));

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-BUILD-01")]
    public void CheckPrintsTheReasonAndBuildQueuesThePlacement()
    {
        Simulation sim = NewGame();
        ConsoleType console = Console(sim);
        (int hx, int hy) = HeroTestWorld.HeroTile(sim);

        Assert.Equal($"campfire at {hx},{hy} rot 0: unit_in_the_way at tile {hx},{hy}", console.Execute($"check campfire {hx} {hy}").Text);
        GiveCost(sim, "campfire", 1);
        (int x, int y) = FindSpot(sim, "campfire");
        Assert.Equal($"campfire at {x},{y} rot 0: ok", console.Execute($"check Campfire {x} {y}").Text);
        Assert.Equal($"house at {x},{y} rot 0: no_center", console.Execute($"check house {x} {y}").Text);
        Assert.Equal($"house at 63,5 rot 1: out_of_map at tile 63,5", console.Execute("check house 63 5 1").Text);

        ConsoleResult build = console.Execute($"build campfire {x} {y}");
        Assert.True(build.Success, build.Text);
        Assert.Equal($"build campfire at {x},{y} rot 0 queued for tick 1 (see 'buildings' for the result)", build.Text);
        Assert.Equal(new PlaceBuildingCommand("campfire", x, y), Assert.Single(sim.PendingCommands).Command);
        sim.Step();

        ConsoleResult list = console.Execute("buildings");
        Assert.True(list.Success, list.Text);
        Assert.Equal($"territory: center {x},{y} radius 48 | era camp | population 0", list.Lines[0]);
        EntityId campfire = sim.World!.Buildings.Entities[0];
        Assert.Equal($"{campfire} campfire L1 constructing at {x},{y} rot 0 (1x1) progress 10 (99 ticks left)", list.Lines[1]);
        Assert.Equal($"last order (tick 1): place_building campfire {campfire} at {x},{y}: ok", list.Lines[2]);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-BUILD-09")]
    [Trait("Req", "FR-BUILD-12")]
    [Trait("Req", "FR-BUILD-13")]
    public void EntityOrdersQueueTheirCommandsAndBuildingsShowsTheOutcome()
    {
        Simulation sim = NewGame();
        ConsoleType console = Console(sim);
        Build(sim, "campfire");
        EntityId storage = Build(sim, "storage");
        sim.Step();
        EntityId house = Build(sim, "house");
        Building placed = BuildingOf(sim, house);

        Assert.Equal($"upgrade {house} queued for tick {sim.Tick + 1} (see 'buildings' for the result)", console.Execute($"upgrade {house}").Text);
        sim.Step();
        Assert.EndsWith($"upgrade_building house {house} at {placed.X},{placed.Y}: not_enough_materials", console.Execute("buildings").Lines[^1], StringComparison.Ordinal);

        (int mx, int my) = FindSpot(sim, "house", minDistance: 4, near: (placed.X, placed.Y), moving: house);
        ConsoleResult move = console.Execute($"movebuilding {house.Index} {mx} {my}");
        Assert.True(move.Success, move.Text);
        Assert.Equal(new MoveBuildingCommand(house, mx, my, 0), Assert.Single(sim.PendingCommands).Command);
        sim.Step();
        string[] lines = [.. console.Execute("buildings").Lines];
        Assert.Contains(lines, l => l.StartsWith($"{house} house L1 moving at {placed.X},{placed.Y} rot 0 (2x2) progress ", StringComparison.Ordinal)
            && l.EndsWith($"-> {mx},{my} rot 0", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith($"{storage} storage L1 active", StringComparison.Ordinal) && l.EndsWith("| stock 0/20", StringComparison.Ordinal));

        Assert.True(console.Execute($"cancelbuilding #{house.Index}v{house.Generation}").Success);
        sim.Step();
        Assert.Equal(BuildingState.Active, BuildingOf(sim, house).State);

        Assert.True(console.Execute($"demolish {house.Index}v{house.Generation}").Success);
        sim.Step();
        Assert.False(sim.Entities.IsAlive(house));
        Assert.EndsWith(": ok", console.Execute("buildings").Lines[^1], StringComparison.Ordinal);
        Assert.Contains("| stock 1/20: wood 15", console.Execute("buildings").Text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void BuildingsWithoutBuildingsSaysSo()
    {
        Simulation sim = NewGame();

        ConsoleResult result = Console(sim).Execute("buildings");

        Assert.True(result.Success);
        Assert.Equal(["territory: none (place a center) | era camp | population 0", "no buildings"], result.Lines);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("build")]
    [InlineData("build campfire 1")]
    [InlineData("build campfire a 1")]
    [InlineData("build campfire 1 1 x")]
    [InlineData("build castle 1 1")]
    [InlineData("build wood 1 1")]
    [InlineData("check campfire")]
    [InlineData("upgrade")]
    [InlineData("upgrade 0")]
    [InlineData("upgrade #0v1")]
    [InlineData("upgrade x")]
    [InlineData("upgrade 5 6")]
    [InlineData("demolish 77")]
    [InlineData("cancelbuilding 3v")]
    [InlineData("movebuilding 0 1")]
    [InlineData("movebuilding x 1 1")]
    [InlineData("buildings now")]
    public void BadArgumentsFailWithoutQueueingAnything(string line)
    {
        Simulation sim = NewGame();

        ConsoleResult result = Console(sim).Execute(line);

        Assert.False(result.Success, line);
        Assert.Empty(sim.PendingCommands);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void MoveBuildingRejectsBadCoordinatesForARealBuilding()
    {
        Simulation sim = NewGame();
        EntityId campfire = Build(sim, "campfire");
        ConsoleType console = Console(sim);

        Assert.False(console.Execute($"movebuilding {campfire.Index} a 1").Success);
        Assert.False(console.Execute($"movebuilding {campfire.Index} 1 1 r").Success);
        Assert.True(console.Execute($"movebuilding {campfire.Index} 1 1 0").Success);
        Assert.Single(sim.PendingCommands);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("build campfire 1 1")]
    [InlineData("check campfire 1 1")]
    [InlineData("upgrade 0")]
    [InlineData("movebuilding 0 1 1")]
    [InlineData("buildings")]
    public void BuildingCommandsExplainWhenThereIsNoWorld(string line)
    {
        ConsoleResult result = Console(new Simulation(1, SimTestData.Real)).Execute(line);

        Assert.False(result.Success);
        Assert.Contains("no world", result.Text, StringComparison.Ordinal);
    }
}
