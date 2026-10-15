using Core.DevConsole;
using Core.Heroes;
using Core.Logging;
using Core.Sim;
using Core.Tests.Heroes;
using Core.Tests.Sim;
using ConsoleType = Core.DevConsole.DevConsole;

namespace Core.Tests.DevConsoleTests;

/// <summary>Hero console commands (NFR-07, FR-HERO-01, FR-HERO-04): hero, herogo, give.</summary>
public sealed class DevConsoleHeroTests
{
    private readonly SimLog _log = new();

    private ConsoleType Console(Simulation sim) =>
        new(new DevConsoleContext(new SimulationHolder(sim), new TickScheduler(10), _log, SimTestData.Real, Path.GetTempPath()));

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-HERO-04")]
    public void GiveRunsThroughTheQueueAndHeroPrintsTheInventory()
    {
        Simulation sim = HeroTestWorld.NewGame();
        ConsoleType console = Console(sim);

        ConsoleResult give = console.Execute("give Wood 120");
        Assert.True(give.Success, give.Text);
        Assert.Equal("give 120 wood queued for tick 1", give.Text);
        Assert.Equal(new GiveItemCommand("wood", 120), Assert.Single(sim.PendingCommands).Command);
        Assert.Equal(0, HeroTestWorld.InventoryOf(sim).UsedStacks); // nothing before the tick runs

        sim.Step();
        ConsoleResult hero = console.Execute("hero");

        Assert.True(hero.Success, hero.Text);
        (int x, int y) = HeroTestWorld.HeroTile(sim);
        Assert.StartsWith($"hero #0v1 at tile {x},{y} (", hero.Lines[0], StringComparison.Ordinal);
        Assert.EndsWith("| health 100/100 | stamina 100/100", hero.Lines[0], StringComparison.Ordinal);
        Assert.Equal("attack 2 | defense 2 | spell power 1 | knowledge 1", hero.Lines[1]);
        Assert.Equal("activity idle | last stop none | last order none | path none", hero.Lines[2]);
        Assert.Equal("backpack 2/30: [0] wood 100, [1] wood 20", hero.Lines[3]);
        Assert.Equal("equipment: head -, body -, hands -, legs -, weapon -, shield -, ring1 -, ring2 -, amulet -", hero.Lines[4]);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-HERO-01")]
    public void HeroGoWalksTheHeroAndHeroShowsThePath()
    {
        Simulation sim = HeroTestWorld.NewGame();
        ConsoleType console = Console(sim);
        (int tx, int ty) = HeroTestWorld.StraightTarget(sim, 6);

        ConsoleResult go = console.Execute($"herogo {tx} {ty} sprint");
        Assert.True(go.Success, go.Text);
        Assert.Equal($"hero ordered to {tx},{ty} sprinting for tick 1 (see 'hero' for the result)", go.Text);
        Assert.Equal(new MoveHeroCommand(tx, ty, Sprint: true), Assert.Single(sim.PendingCommands).Command);

        sim.Step();
        Assert.Equal($"activity walking | last stop none | last order accepted | path 6 steps left to {tx},{ty} (sprint)", console.Execute("hero").Lines[2]);

        HeroTestWorld.StepUntil(sim, () => !HeroTestWorld.MoverOf(sim).IsMoving);
        Assert.Equal((tx, ty), HeroTestWorld.HeroTile(sim));
        Assert.Equal("activity idle | last stop arrived | last order accepted | path none", console.Execute("hero").Lines[2]);

        // A rejected order is reported by 'hero' after its tick.
        Assert.True(console.Execute("herogo 0 0").Success);
        sim.Step();
        Assert.Equal(HeroOrderResult.Unexplored, HeroTestWorld.HeroOf(sim).LastOrder);
        Assert.Contains("last order unexplored", console.Execute("hero").Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "FR-HERO-02")]
    public void HeroShowsTheGatheringTarget()
    {
        Simulation sim = HeroTestWorld.NewGame();
        (int x, int y) = HeroTestWorld.NearestNode(sim, Core.WorldGen.ResourceKind.Tree);
        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();

        Assert.StartsWith($"activity gathering {x},{y} (action 0 ticks) | last stop none | last order accepted | path ",
            Console(sim).Execute("hero").Lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("give")]
    [InlineData("give wood")]
    [InlineData("give wood x")]
    [InlineData("give wood 0")]
    [InlineData("give wood -3")]
    [InlineData("give unobtainium 3")]
    [InlineData("herogo")]
    [InlineData("herogo 1")]
    [InlineData("herogo a b")]
    [InlineData("herogo 1 2 run")]
    [InlineData("herogo 64 0")]
    [InlineData("herogo -1 0")]
    [InlineData("hero x")]
    public void BadArgumentsFailWithoutQueueingAnything(string line)
    {
        Simulation sim = HeroTestWorld.NewGame();

        ConsoleResult result = Console(sim).Execute(line);

        Assert.False(result.Success, line);
        Assert.Empty(sim.PendingCommands);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("hero")]
    [InlineData("herogo 1 1")]
    [InlineData("give wood 1")]
    public void HeroCommandsExplainWhenThereIsNoWorldOrNoHero(string line)
    {
        var core = new Simulation(1, SimTestData.Real);
        ConsoleResult noWorld = Console(core).Execute(line);
        Assert.False(noWorld.Success);
        Assert.Contains("no world", noWorld.Text, StringComparison.Ordinal);

        Simulation game = HeroTestWorld.NewGame();
        game.Enqueue(new DestroyEntityCommand(game.World!.HeroEntity));
        game.Step();
        ConsoleResult noHero = Console(game).Execute(line);
        Assert.False(noHero.Success);
        Assert.Contains("no hero", noHero.Text, StringComparison.Ordinal);
        Assert.Empty(game.PendingCommands);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void HeroOrdersWithoutAHeroAreIgnoredWithAWarning()
    {
        Simulation game = HeroTestWorld.NewGame();
        game.Enqueue(new DestroyEntityCommand(game.World!.HeroEntity));
        game.Step();
        game.Enqueue(new MoveHeroCommand(1, 1));
        game.Enqueue(new GatherCommand(1, 1));
        game.Enqueue(new StopHeroCommand());
        game.Enqueue(new GiveItemCommand("wood", 1));
        game.Step();

        Assert.Equal(4, game.Log.GetEvents().Count(e => e.Category == HeroRules.LogCategory && e.Level == LogLevel.Warning));
    }
}
