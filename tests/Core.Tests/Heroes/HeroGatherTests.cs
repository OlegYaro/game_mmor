using Core.Content;
using Core.Heroes;
using Core.Sim;
using Core.Tests.Content;
using Core.Tests.Sim;
using Core.WorldGen;
using static Core.Tests.Heroes.HeroTestWorld;

namespace Core.Tests.Heroes;

/// <summary>Hero gathering (FR-HERO-02, FR-WORLD-05): approach, yield, depletion, full backpack, invalid targets.</summary>
public sealed class HeroGatherTests
{
    private static HeroBalanceDef Balance => SimTestData.Real.Hero;

    private static GatherDef Rule(string source) => Balance.Gathering.Single(g => g.Source == source);

    private static Core.Content.DefId Item(string id) => SimTestData.Real.TryGetId(id, out Core.Content.DefId item) ? item : throw new InvalidOperationException(id);

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    [Trait("Req", "FR-WORLD-05")]
    public void TheHeroWalksWithinRangeAndGathersTheYieldEveryActionFromTheTileLayer()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        (int x, int y) = NearestNode(sim, ResourceKind.Tree);
        GatherDef rule = Rule("tree");
        int stock = world.Tiles.GetEffectiveAmount(x, y);

        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();
        Assert.Equal(HeroActivity.Gathering, HeroOf(sim).Activity);
        Assert.Equal((x, y), (HeroOf(sim).TargetX, HeroOf(sim).TargetY));
        Assert.Equal(Balance.InteractionRange, MoverOf(sim).GoalRange);

        StepUntil(sim, () => !MoverOf(sim).IsMoving);
        (int hx, int hy) = HeroTile(sim);
        Assert.Equal(Balance.InteractionRange, HeroRules.Distance(hx, hy, x, y));
        Assert.Equal(0, InventoryOf(sim).Count(Item("wood")));

        // Work starts in the tick of arrival: the first yield comes TicksPerAction ticks after it, counting that tick.
        sim.Step(rule.TicksPerAction - 2);
        Assert.Equal(0, InventoryOf(sim).Count(Item("wood")));
        sim.Step();
        Assert.Equal(rule.YieldPerAction, InventoryOf(sim).Count(Item("wood")));
        Assert.Equal(stock - rule.YieldPerAction, world.Tiles.GetEffectiveAmount(x, y));
        Assert.Equal(0, HeroOf(sim).ActionTicks);

        sim.Step(rule.TicksPerAction);
        Assert.Equal(2 * rule.YieldPerAction, InventoryOf(sim).Count(Item("wood")));
        Assert.Equal(HeroActivity.Gathering, HeroOf(sim).Activity);
        Assert.Equal(ResourceKind.Tree, world.Map.GetResource(x, y));
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    [Trait("Req", "FR-WORLD-05")]
    public void AnUsedUpNodeDisappearsAndGatheringStops()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        (int x, int y) = NearestNode(sim, ResourceKind.Tree);
        GatherDef rule = Rule("tree");
        int leave = rule.YieldPerAction + 5;
        world.Tiles.TakeResource(x, y, world.Tiles.GetEffectiveAmount(x, y) - leave);

        sim.Enqueue(new GatherCommand(x, y));
        StepUntil(sim, () => HeroOf(sim).Activity == HeroActivity.Idle && sim.Tick > 1);

        Assert.Equal(HeroStopReason.Depleted, HeroOf(sim).LastStop);
        Assert.Equal(leave, InventoryOf(sim).Count(Item("wood")));
        Assert.Equal(ResourceKind.None, world.Tiles.GetEffectiveResource(x, y));
        Assert.Equal(0, world.Tiles.GetEffectiveAmount(x, y));
        Assert.Equal((-1, -1), (HeroOf(sim).TargetX, HeroOf(sim).TargetY));

        // The tile is no longer a target.
        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();
        Assert.Equal(HeroOrderResult.NoResource, HeroOf(sim).LastOrder);
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void AFullBackpackStopsGatheringAndOnlyWhatFitsIsTaken()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        (int x, int y) = NearestNode(sim, ResourceKind.Tree);
        int stock = world.Tiles.GetEffectiveAmount(x, y);
        int stoneStack = SimTestData.Real.Items.Single(i => i.Id == "stone").StackSize;
        int woodStack = SimTestData.Real.Items.Single(i => i.Id == "wood").StackSize;

        // 29 stacks of stone and 96 wood: room for 4 more wood.
        sim.Enqueue(new GiveItemCommand("stone", (Balance.BackpackStacks - 1) * stoneStack));
        sim.Enqueue(new GiveItemCommand("wood", woodStack - 4));
        sim.Enqueue(new GatherCommand(x, y));
        StepUntil(sim, () => HeroOf(sim).Activity == HeroActivity.Idle && sim.Tick > 1);

        Assert.Equal(HeroStopReason.BackpackFull, HeroOf(sim).LastStop);
        Assert.Equal(woodStack, InventoryOf(sim).Count(Item("wood")));
        Assert.Equal(stock - 4, world.Tiles.GetEffectiveAmount(x, y));

        // Already full: the order is accepted, but the hero stops at once without taking anything.
        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();
        Assert.Equal(HeroOrderResult.Accepted, HeroOf(sim).LastOrder);
        Assert.Equal(HeroActivity.Idle, HeroOf(sim).Activity);
        Assert.Equal(HeroStopReason.BackpackFull, HeroOf(sim).LastStop);
        Assert.Equal(stock - 4, world.Tiles.GetEffectiveAmount(x, y));
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void TilesWithoutAGatherableNodeAreRejected()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        (int hx, int hy) = HeroTile(sim);
        Assert.Equal(ResourceKind.None, world.Tiles.GetEffectiveResource(hx, hy));

        sim.Enqueue(new GatherCommand(hx, hy));
        sim.Step();
        Assert.Equal(HeroOrderResult.NoResource, HeroOf(sim).LastOrder);

        sim.Enqueue(new GatherCommand(70, 1));
        sim.Step();
        Assert.Equal(HeroOrderResult.OutsideMap, HeroOf(sim).LastOrder);
        Assert.Equal(HeroActivity.Idle, HeroOf(sim).Activity);

        // Veins, ore and coal are mined by buildings, never by the hero.
        Assert.Null(HeroRules.FindGatherDef(Balance, ResourceKind.OreVein));
        Assert.Null(HeroRules.FindGatherDef(Balance, ResourceKind.Ore));
        Assert.Null(HeroRules.FindGatherDef(Balance, ResourceKind.Coal));
        Assert.Equal("rock", HeroRules.FindGatherDef(Balance, ResourceKind.Stone)!.Source);
        Assert.Equal("berry_bush", HeroRules.FindGatherDef(Balance, ResourceKind.BerryBush)!.Source);
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void AMoveOrderCancelsGatheringAndANodeTakenAwayStopsTheApproach()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        (int x, int y) = NearestNode(sim, ResourceKind.Tree);
        (int tx, int ty) = StraightTarget(sim, 2);

        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();
        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step();
        Assert.Equal(HeroActivity.Walking, HeroOf(sim).Activity);
        Assert.Equal(0, MoverOf(sim).GoalRange);

        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();
        Assert.Equal(HeroActivity.Gathering, HeroOf(sim).Activity);
        world.Tiles.TakeResource(x, y, world.Tiles.GetEffectiveAmount(x, y));
        sim.Step();
        Assert.Equal(HeroActivity.Idle, HeroOf(sim).Activity);
        Assert.Equal(HeroStopReason.Depleted, HeroOf(sim).LastStop);
        StepUntil(sim, () => !MoverOf(sim).IsMoving, limit: 10);
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    [Trait("Req", "FR-HERO-01")]
    public void AGatherOrderMidStepFinishesTheStepEvenWhenItStartedInRange()
    {
        Simulation sim = NewGame();
        (int x, int y) = NearestNode(sim, ResourceKind.Tree);
        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();
        StepUntil(sim, () => !MoverOf(sim).IsMoving);
        (int ax, int ay) = HeroTile(sim);
        (int tx, int ty) = StraightTarget(sim, 6);
        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step();
        Assert.Equal(Mover.ProgressPerTick, MoverOf(sim).StepProgress);

        // Back to the tree while leaving tile A, which is within range of it: the step is finished, not undone.
        sim.Enqueue(new GatherCommand(x, y));
        sim.Step();
        Assert.Equal(2 * Mover.ProgressPerTick, MoverOf(sim).StepProgress);
        Assert.NotEqual(Simulation.TileCenter(ax, ay), sim.Positions.Get(Hero(sim)));
        Assert.Equal(HeroActivity.Gathering, HeroOf(sim).Activity);

        StepUntil(sim, () => !MoverOf(sim).IsMoving);
        (int hx, int hy) = HeroTile(sim);
        Assert.NotEqual((ax, ay), (hx, hy));
        Assert.InRange(HeroRules.Distance(hx, hy, x, y), 1, Balance.InteractionRange);
        StepUntil(sim, () => InventoryOf(sim).Count(Item("wood")) > Rule("tree").YieldPerAction);
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    [Trait("Req", "NFR-07")]
    public void GiveAddsWhatFitsAndIgnoresUnknownItems()
    {
        Simulation sim = NewGame();

        sim.Enqueue(new GiveItemCommand("berries", 250));
        sim.Enqueue(new GiveItemCommand("dragon_scale", 3));
        sim.Enqueue(new GiveItemCommand("wood", 0));
        sim.Step();

        Assert.Equal(250, InventoryOf(sim).Count(Item("berries")));
        Assert.Equal(3, InventoryOf(sim).UsedStacks);
        Assert.Equal(2, sim.Log.GetEvents().Count(e => e.Category == HeroRules.LogCategory && e.Level == Core.Logging.LogLevel.Warning));

        sim.Enqueue(new GiveItemCommand("wood", 100 * Balance.BackpackStacks));
        sim.Step();
        Assert.Equal(100 * (Balance.BackpackStacks - 3), InventoryOf(sim).Count(Item("wood")));
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void StartItemsComeFromTheBalance()
    {
        InMemoryContentSource source = new();
        foreach (ContentFile file in TestPaths.LoadDataSource().GetFiles())
        {
            source.Add(file.Path, file.Path == "balance/hero.json"
                ? file.Text.Replace("\"startItems\": []", "\"startItems\": [{ \"item\": \"wood\", \"count\": 150 }, { \"item\": \"berries\", \"count\": 5 }]", StringComparison.Ordinal)
                : file.Text);
        }

        ContentCatalog content = ContentCatalog.Load(source).Catalog!;
        Assert.Equal(2, content.Hero.StartItems.Count);

        Simulation sim = Simulation.CreateNewGame(content, new NewGameOptions(Seed, 64, Difficulty.Normal));

        Inventory inventory = sim.World!.Inventories.Get(sim.World.HeroEntity);
        content.TryGetId("wood", out Core.Content.DefId wood);
        content.TryGetId("berries", out Core.Content.DefId berries);
        Assert.Equal([new ItemSlot(wood, 100), new ItemSlot(wood, 50), new ItemSlot(berries, 5)], inventory.Backpack.Take(3));
        Assert.Empty(Balance.StartItems);
    }
}
