using Core.Content;
using Core.DevConsole;
using Core.Logging;
using Core.Sim;
using Core.Tests.Sim;
using ConsoleType = Core.DevConsole.DevConsole;

namespace Core.Tests.DevConsoleTests;

/// <summary>World console commands (NFR-07): world, worldhash, fog, reveal, goto.</summary>
public sealed class DevConsoleWorldTests
{
    private readonly SimLog _log = new();

    private ConsoleType Console(Simulation sim) =>
        new(new DevConsoleContext(new SimulationHolder(sim), new TickScheduler(10), _log, SimTestData.Real, Path.GetTempPath()));

    private static Simulation NewGame() => Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(0xF06, 64, Difficulty.Hard));

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-WORLD-01")]
    public void WorldHashPrintsTheMapHash()
    {
        Simulation sim = NewGame();

        ConsoleResult result = Console(sim).Execute("worldhash");

        Assert.True(result.Success);
        Assert.Equal(ContentHash.Format(sim.World!.MapHash), result.Text);
        Assert.Equal(result.Text, Console(NewGame()).Execute("worldhash").Text);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void WorldPrintsTheSummary()
    {
        Simulation sim = NewGame();

        ConsoleResult result = Console(sim).Execute("world");

        Assert.True(result.Success, result.Text);
        Assert.StartsWith("seed 3846 | size 64x64 | generator v1 | difficulty hard", result.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith($"start {sim.World!.Map.Start.X},{sim.World.Map.Start.Y}", result.Lines[1], StringComparison.Ordinal);
        Assert.StartsWith($"points of interest {sim.World.Map.PointsOfInterest.Count}: chest ", result.Lines[2], StringComparison.Ordinal);
        Assert.StartsWith($"settlement sites {sim.World.Map.SettlementSites.Count}: human ", result.Lines[3], StringComparison.Ordinal);
        Assert.StartsWith("resource tiles: tree ", result.Lines[4], StringComparison.Ordinal);
        Assert.StartsWith($"warnings {sim.World.Map.Warnings.Count}", result.Lines[5], StringComparison.Ordinal);
        Assert.Equal(6 + sim.World.Map.Warnings.Count, result.Lines.Count);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-WORLD-06")]
    public void FogAndRevealWorkThroughTheQueue()
    {
        Simulation sim = NewGame();
        ConsoleType console = Console(sim);
        Core.Fog.FogCounts counts = sim.World!.Fog.CountStates();

        Assert.Equal($"unknown {counts.Unknown} | explored 0 | visible {counts.Visible} | total 4096", console.Execute("fog").Text);

        ConsoleResult reveal = console.Execute("reveal");
        Assert.True(reveal.Success);
        Assert.Equal("reveal queued for tick 1", reveal.Text);
        Assert.IsType<RevealAllCommand>(Assert.Single(sim.PendingCommands).Command);
        Assert.Equal(counts, sim.World.Fog.CountStates()); // nothing happens before the tick runs

        sim.Step();
        Assert.Equal($"unknown 0 | explored {4096 - counts.Visible} | visible {counts.Visible} | total 4096", console.Execute("fog").Text);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void GoToReturnsACameraRequest()
    {
        ConsoleType console = Console(NewGame());

        ConsoleResult ok = console.Execute("goto 10 63");
        Assert.True(ok.Success);
        Assert.Equal(new CameraRequest(10, 63), ok.CameraRequest);
        Assert.Equal("camera to 10,63", ok.Text);

        foreach (string line in new[] { "goto", "goto 1", "goto a b", "goto 64 0", "goto -1 5", "goto 1 2 3" })
        {
            ConsoleResult bad = console.Execute(line);
            Assert.False(bad.Success, line);
            Assert.Null(bad.CameraRequest);
        }

        Assert.Null(console.Execute("fog").CameraRequest);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("world")]
    [InlineData("worldhash")]
    [InlineData("fog")]
    [InlineData("reveal")]
    [InlineData("goto 1 1")]
    public void WorldCommandsExplainWhenThereIsNoWorld(string line)
    {
        var sim = new Simulation(1, SimTestData.Real);

        ConsoleResult result = Console(sim).Execute(line);

        Assert.False(result.Success);
        Assert.Contains("no world", result.Text, StringComparison.Ordinal);
        Assert.Empty(sim.PendingCommands);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("world x")]
    [InlineData("worldhash x")]
    [InlineData("fog x")]
    [InlineData("reveal x")]
    public void ExtraArgumentsShowUsage(string line)
    {
        ConsoleResult result = Console(NewGame()).Execute(line);

        Assert.False(result.Success);
        Assert.StartsWith("Usage: ", result.Text, StringComparison.Ordinal);
    }
}
