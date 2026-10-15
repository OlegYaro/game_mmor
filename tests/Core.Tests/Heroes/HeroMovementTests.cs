using Core.Content;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Pathfinding;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Heroes.HeroTestWorld;

namespace Core.Tests.Heroes;

/// <summary>Hero walking (FR-HERO-01): timing, sprint and stamina, order validation, blocked paths, determinism.</summary>
public sealed class HeroMovementTests
{
    private static HeroBalanceDef Balance => SimTestData.Real.Hero;

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void WalkingTakesStepTicksPerStraightTileAndPositionMovesEveryTick()
    {
        Simulation sim = NewGame();
        const int Steps = 6;
        (int tx, int ty) = StraightTarget(sim, Steps);
        (int hx, int hy) = HeroTile(sim);
        int travel = Steps * Balance.StepTicks;

        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step();

        Hero hero = HeroOf(sim);
        Assert.Equal(HeroActivity.Walking, hero.Activity);
        Assert.Equal(HeroOrderResult.Accepted, hero.LastOrder);
        Mover mover = MoverOf(sim);
        Assert.Equal(Steps + 1, mover.Path.Count);
        Assert.Equal(Mover.ProgressPerTick, mover.StepProgress);
        Assert.Equal(sim.World!.Pathfinder.Grid.Index(hx, hy), mover.CurrentTile);

        // The position moves a third of a tile per tick (integer fixed point), towards the target.
        Position start = Simulation.TileCenter(hx, hy);
        Position now = sim.Positions.Get(Hero(sim));
        Assert.Equal(start.X + (Math.Sign(tx - hx) * Position.UnitsPerTile / Balance.StepTicks), now.X);
        Assert.Equal(start.Y + (Math.Sign(ty - hy) * Position.UnitsPerTile / Balance.StepTicks), now.Y);

        sim.Step(travel - 2);
        Assert.Equal(HeroActivity.Walking, HeroOf(sim).Activity);
        Assert.NotEqual(Simulation.TileCenter(tx, ty), sim.Positions.Get(Hero(sim)));

        sim.Step();
        Assert.Equal(Simulation.TileCenter(tx, ty), sim.Positions.Get(Hero(sim)));
        Assert.Equal(HeroActivity.Idle, HeroOf(sim).Activity);
        Assert.Equal(HeroStopReason.Arrived, HeroOf(sim).LastStop);
        Assert.Equal(Mover.Idle, MoverOf(sim));
        Assert.Equal(Balance.Stamina, HeroOf(sim).Stamina);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void PathTicksAreTheSumOfStepCostsIncludingDiagonals()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        (int hx, int hy) = HeroTile(sim);
        int tx = hx + 7;
        int ty = hy + 4;
        Assert.NotEqual(FogState.Unknown, world.Fog.GetState(tx, ty));

        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step();
        IReadOnlyList<int> path = MoverOf(sim).Path;
        int units = 0;
        for (int i = 1; i < path.Count; i++)
        {
            bool diagonal = path[i] % 64 != path[i - 1] % 64 && path[i] / 64 != path[i - 1] / 64;
            units += Mover.StepCost(Balance.StepTicks, diagonal);
        }

        Assert.Contains(path.Zip(path.Skip(1)), p => p.First % 64 != p.Second % 64 && p.First / 64 != p.Second / 64);
        int ticks = 1 + StepUntil(sim, () => !MoverOf(sim).IsMoving);
        Assert.Equal((units + Mover.ProgressPerTick - 1) / Mover.ProgressPerTick, ticks);
        Assert.Equal((tx, ty), HeroTile(sim));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void SprintIsFasterAndSpendsStaminaThenItRegenerates()
    {
        Simulation sim = NewGame();
        const int Steps = 6;
        (int tx, int ty) = StraightTarget(sim, Steps);
        int sprintTicks = Steps * Balance.StepTicks * 1000 / Balance.SprintPermille;

        sim.Enqueue(new MoveHeroCommand(tx, ty, Sprint: true));
        sim.Step(sprintTicks);

        Assert.Equal((tx, ty), HeroTile(sim));
        Assert.Equal(HeroStopReason.Arrived, HeroOf(sim).LastStop);
        Assert.Equal(Balance.Stamina - (sprintTicks * Balance.SprintStaminaDrain), HeroOf(sim).Stamina);

        sim.Step(5);
        Assert.Equal(Balance.Stamina - (sprintTicks * Balance.SprintStaminaDrain) + (5 * Balance.StaminaRegen), HeroOf(sim).Stamina);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void WithoutStaminaTheSprintIsWalkingSpeedAndStaminaDoesNotRegenerateWhileSprintingIsOrdered()
    {
        Simulation sim = NewGame();
        const int Steps = 6;
        (int tx, int ty) = StraightTarget(sim, Steps);
        ref Hero hero = ref sim.World!.Heroes.Get(Hero(sim));
        hero = hero with { Stamina = 3 };

        sim.Enqueue(new MoveHeroCommand(tx, ty, Sprint: true));
        sim.Step(2);
        Assert.Equal(0, HeroOf(sim).Stamina);
        Assert.Equal(1, MoverOf(sim).StepIndex); // 2 × 1500 units: exactly one 3000-unit step
        Assert.Equal(0, MoverOf(sim).StepProgress);

        // Two sprinting ticks covered 3000 units; the remaining 15 000 take 15 walking ticks.
        int remaining = ((Steps * Balance.StepTicks * Mover.ProgressPerTick) - (2 * Balance.SprintPermille)) / Mover.ProgressPerTick;
        sim.Step(remaining - 1);
        Assert.True(MoverOf(sim).IsMoving);
        Assert.Equal(0, HeroOf(sim).Stamina);
        sim.Step();
        Assert.False(MoverOf(sim).IsMoving);
        Assert.Equal((tx, ty), HeroTile(sim));

        sim.Step();
        Assert.Equal(Balance.StaminaRegen, HeroOf(sim).Stamina);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void InvalidTargetsAreRejectedWithoutChangingTheHero()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        (int ux, int uy) = FindTile(sim, (x, y) => world.Fog.GetState(x, y) == FogState.Unknown);

        AssertRejected(sim, new MoveHeroCommand(-1, 5), HeroOrderResult.OutsideMap);
        AssertRejected(sim, new MoveHeroCommand(64, 5), HeroOrderResult.OutsideMap);
        AssertRejected(sim, new MoveHeroCommand(ux, uy), HeroOrderResult.Unexplored);

        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        (int wx, int wy) = FindTile(sim, (x, y) => world.Map.Terrain.IsWater(x, y) || world.Map.Terrain.IsRiver(x, y));
        AssertRejected(sim, new MoveHeroCommand(wx, wy), HeroOrderResult.Impassable);

        // A passable tile walled in by occupied tiles is unreachable; the walled tile itself is impassable.
        (int tx, int ty) = StraightTarget(sim, 8);
        EntityId wall = sim.Entities.Create();
        foreach ((int dx, int dy) in Ring())
        {
            world.Tiles.TryOccupy(tx + dx, ty + dy, wall);
        }

        AssertRejected(sim, new MoveHeroCommand(tx, ty), HeroOrderResult.Unreachable);
        AssertRejected(sim, new MoveHeroCommand(tx + 1, ty), HeroOrderResult.Impassable);
        Assert.Equal(HeroStopReason.None, HeroOf(sim).LastStop);
        Assert.Contains(sim.Log.GetEvents(), e => e.Category == "hero" && e.Template.Contains("rejected", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ARejectedOrderKeepsTheCurrentWalk()
    {
        Simulation sim = NewGame();
        (int tx, int ty) = StraightTarget(sim, 6);
        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step(2);
        Mover before = MoverOf(sim);

        sim.Enqueue(new MoveHeroCommand(-5, -5));
        sim.Step();

        Assert.Equal(HeroOrderResult.OutsideMap, HeroOf(sim).LastOrder);
        Assert.Equal(HeroActivity.Walking, HeroOf(sim).Activity);
        Assert.Equal(before.Path, MoverOf(sim).Path);
        StepUntil(sim, () => !MoverOf(sim).IsMoving);
        Assert.Equal((tx, ty), HeroTile(sim));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void ANewOrderMidStepFinishesTheStepFirstAndStopDoesTheSame()
    {
        Simulation sim = NewGame();
        (int hx, int hy) = HeroTile(sim);
        (int tx, int ty) = StraightTarget(sim, 6);
        int dx = Math.Sign(tx - hx);
        int dy = Math.Sign(ty - hy);
        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step();
        Position midStep = sim.Positions.Get(Hero(sim));

        // Back to the start: the first step (one third done) is finished, then the hero turns around.
        sim.Enqueue(new MoveHeroCommand(hx, hy));
        sim.Step();
        Mover turned = MoverOf(sim);
        int grid(int x, int y) => (y * 64) + x;
        Assert.Equal([grid(hx, hy), grid(hx + dx, hy + dy), grid(hx, hy)], turned.Path);
        Assert.Equal(2 * Mover.ProgressPerTick, turned.StepProgress);
        Assert.NotEqual(midStep, sim.Positions.Get(Hero(sim)));
        int back = StepUntil(sim, () => !MoverOf(sim).IsMoving);
        Assert.Equal(1 + Balance.StepTicks, back);
        Assert.Equal((hx, hy), HeroTile(sim));

        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step();
        sim.Enqueue(new StopHeroCommand());
        sim.Step();
        Assert.Equal(HeroActivity.Idle, HeroOf(sim).Activity);
        Assert.Equal(HeroStopReason.Stopped, HeroOf(sim).LastStop);
        sim.Step(Balance.StepTicks);
        Assert.False(MoverOf(sim).IsMoving);
        Assert.Equal((hx + dx, hy + dy), HeroTile(sim));
        Assert.Equal(HeroStopReason.Stopped, HeroOf(sim).LastStop);

        sim.Enqueue(new StopHeroCommand());
        sim.Step();
        Assert.Equal(Mover.Idle, MoverOf(sim));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "FR-WORLD-05")]
    public void TheHeroWalksAroundATileOccupiedOnItsWayAndStopsWhenWalledIn()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        (int hx, int hy) = HeroTile(sim);
        (int tx, int ty) = StraightTarget(sim, 8);
        int dx = Math.Sign(tx - hx);
        int dy = Math.Sign(ty - hy);
        sim.Enqueue(new MoveHeroCommand(tx, ty));
        sim.Step();
        EntityId building = sim.Entities.Create();
        int bx = hx + (4 * dx);
        int by = hy + (4 * dy);
        Assert.True(world.Tiles.TryOccupy(bx, by, building));

        StepUntil(sim, () => !MoverOf(sim).IsMoving);

        Assert.Equal((tx, ty), HeroTile(sim));
        Assert.Equal(HeroStopReason.Arrived, HeroOf(sim).LastStop);

        // Walk back, then wall the hero in after the first step: the next step is blocked and no path is left.
        sim.Enqueue(new MoveHeroCommand(hx, hy));
        sim.Step(Balance.StepTicks);
        (int cx, int cy) = HeroTile(sim);
        foreach ((int rx, int ry) in Ring())
        {
            world.Tiles.TryOccupy(cx + rx, cy + ry, building);
        }

        sim.Step(2);
        Assert.False(MoverOf(sim).IsMoving);
        Assert.Equal(HeroActivity.Idle, HeroOf(sim).Activity);
        Assert.Equal(HeroStopReason.PathBlocked, HeroOf(sim).LastStop);
        Assert.Equal(Simulation.TileCenter(cx, cy), sim.Positions.Get(Hero(sim)));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-04")]
    public void TwoRunsWithTheSameOrdersGiveIdenticalHashesEveryTick()
    {
        Simulation a = NewGame();
        Simulation b = NewGame();
        (int tx, int ty) = StraightTarget(a, 7);
        (int gx, int gy) = NearestNode(a, Core.WorldGen.ResourceKind.Tree);
        for (int tick = 0; tick < 400; tick++)
        {
            foreach (Simulation sim in new[] { a, b })
            {
                if (tick == 3)
                {
                    sim.Enqueue(new MoveHeroCommand(tx, ty, Sprint: true));
                }

                if (tick == 60)
                {
                    sim.Enqueue(new GatherCommand(gx, gy));
                }

                sim.Step();
            }

            Assert.Null(a.ComputeHashes().FindFirstMismatch(b.ComputeHashes()));
        }

        Assert.True(InventoryOf(a).UsedStacks > 0);
    }

    private static void AssertRejected(Simulation sim, ISimCommand command, HeroOrderResult expected)
    {
        Mover mover = MoverOf(sim);
        Position position = sim.Positions.Get(Hero(sim));
        sim.Enqueue(command);
        sim.Step();
        Assert.Equal(expected, HeroOf(sim).LastOrder);
        Assert.Equal(HeroActivity.Idle, HeroOf(sim).Activity);
        Assert.Equal(mover, MoverOf(sim));
        Assert.Equal(position, sim.Positions.Get(Hero(sim)));
    }

    private static (int X, int Y) FindTile(Simulation sim, Func<int, int, bool> match)
    {
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (match(x, y))
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No matching tile.");
    }

    private static IEnumerable<(int Dx, int Dy)> Ring()
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx != 0 || dy != 0)
                {
                    yield return (dx, dy);
                }
            }
        }
    }
}
