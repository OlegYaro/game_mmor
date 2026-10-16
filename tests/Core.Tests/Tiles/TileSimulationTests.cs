using Core.Entities;
using Core.Heroes;
using Core.Pathfinding;
using Core.Save;
using Core.Sim;
using Core.Tests.Heroes;
using Core.Tests.Sim;
using Core.Tiles;
using Core.WorldGen;

namespace Core.Tests.Tiles;

/// <summary>
/// Flora and walks inside a running game (FR-WORLD-04, FR-WORLD-15): the daily tile system grows stumps back, a save in
/// the middle of a timer continues exactly like the uninterrupted game, walkers trample the tiles they step on, and a
/// step onto a trail or a road is quicker by the class's permille.
/// </summary>
public sealed class TileSimulationTests
{
    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "NFR-04")]
    public void TheDailySystemGrowsAStumpBackAndASaveInTheMiddleContinuesIdentically()
    {
        Simulation sim = HeroTestWorld.NewGame();
        GameWorld world = sim.World!;
        (int x, int y) = HeroTestWorld.NearestNode(sim, ResourceKind.Tree);
        sim.Step(1234);
        Assert.True(world.Tiles.TakeResource(x, y, int.MaxValue) > 0);
        Assert.Equal(FloraState.Stump, world.Tiles.GetFlora(x, y));
        Assert.Equal(1234, world.Tiles.GetFloraTick(x, y));
        int ticksPerDay = sim.Calendar.TicksPerDay;

        // Three days in, save; load and run both up to the day the tree is back (near or far).
        Residents.ResidentTestWorld.StepTo(sim, 3L * ticksPerDay + 17);
        Simulation loaded = SaveGame.Read(SaveGame.ToBytes(sim), SimTestData.Real).Simulation!;
        Assert.Equal(sim.ComputeHashes().Combined, loaded.ComputeHashes().Combined);
        Assert.Equal(FloraState.Stump, loaded.World!.Tiles.GetFlora(x, y));

        long regrown = -1;
        long last = (SimTestData.Real.Regrowth.StumpRegrowDaysFar + 1L) * ticksPerDay;
        while (sim.Tick < last && regrown < 0)
        {
            sim.Step();
            loaded.Step();
            if (world.Tiles.GetEffectiveResource(x, y) == ResourceKind.Tree)
            {
                regrown = sim.Tick;
            }
        }

        Assert.True(regrown > 0, "The stump never grew back.");
        Assert.Equal(0, regrown % ticksPerDay); // at the start of a day
        Assert.True(regrown >= 1234 + ((long)SimTestData.Real.Regrowth.StumpRegrowDaysNearTree * ticksPerDay));
        Assert.Equal(ResourceKind.Tree, loaded.World.Tiles.GetEffectiveResource(x, y));
        Assert.Equal(sim.ComputeHashes().Combined, loaded.ComputeHashes().Combined);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    [Trait("Req", "FR-HERO-01")]
    public void WalkersTrampleTheirTilesAndRoadsMakeThemQuicker()
    {
        Simulation sim = HeroTestWorld.NewGame();
        GameWorld world = sim.World!;
        const int Steps = 8;
        (int hx, int hy) = HeroTestWorld.HeroTile(sim);
        (int tx, int ty) = HeroTestWorld.StraightTarget(sim, Steps);
        int stepTicks = sim.Content.Hero.StepTicks;

        sim.Enqueue(new MoveHeroCommand(tx, ty));
        int plain = HeroTestWorld.StepUntil(sim, () => !HeroTestWorld.MoverOf(sim).IsMoving && HeroTestWorld.HeroTile(sim) == (tx, ty));
        Assert.Equal(Steps * stepTicks, plain); // the order runs and the first step starts in the first tick

        // Every tile stepped onto got one step's worth; the start tile none.
        int dx = Math.Sign(tx - hx);
        int dy = Math.Sign(ty - hy);
        for (int i = 1; i <= Steps; i++)
        {
            Assert.Equal(sim.Content.Roads.StepIncrement, world.Tiles.GetWalkCounter(hx + (i * dx), hy + (i * dy)));
        }

        Assert.Equal(0, world.Tiles.GetWalkCounter(hx, hy));
        Assert.True(world.Tiles.IsUniform);

        // Make the way back a road (steps onto the tiles between) and walk it: each step takes the road permille.
        for (int i = 0; i < Steps; i++)
        {
            while (world.Tiles.GetRoadClass(hx + (i * dx), hy + (i * dy)) != RoadClass.Road)
            {
                world.Tiles.Trample(hx + (i * dx), hy + (i * dy));
            }
        }

        sim.Enqueue(new MoveHeroCommand(hx, hy));
        int road = HeroTestWorld.StepUntil(sim, () => !HeroTestWorld.MoverOf(sim).IsMoving && HeroTestWorld.HeroTile(sim) == (hx, hy));
        int expected = (int)(((long)Steps * stepTicks * sim.Content.Roads.RoadCostPermille + 999) / 1000);
        Assert.Equal(expected, road);
        Assert.True(road < plain);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    [Trait("Req", "FR-NPC-02")]
    public void AStepTakesTheTrailOrRoadShareOfItsPlainTime()
    {
        Simulation sim = HeroTestWorld.NewGame();
        GameWorld world = sim.World!;
        PassabilityGrid grid = world.Pathfinder.Grid;
        (int hx, int hy) = HeroTestWorld.HeroTile(sim);
        (int tx, int ty) = HeroTestWorld.StraightTarget(sim, 2);
        int from = grid.Index(hx, hy);
        int to = grid.Index(hx + Math.Sign(tx - hx), hy + Math.Sign(ty - hy));
        int stepTicks = sim.Content.Economy.ResidentStepTicks;

        Assert.Equal(Mover.StepCost(stepTicks, diagonal: false), AgentMovement.StepCost(world, stepTicks, from, to));
        while (world.Tiles.GetRoadClass(grid.XOf(to), grid.YOf(to)) != RoadClass.Trail)
        {
            world.Tiles.Trample(grid.XOf(to), grid.YOf(to));
        }

        Assert.Equal(Mover.StepCost(stepTicks, diagonal: false) * sim.Content.Roads.TrailCostPermille / 1000, AgentMovement.StepCost(world, stepTicks, from, to));
        while (world.Tiles.GetRoadClass(grid.XOf(to), grid.YOf(to)) != RoadClass.Road)
        {
            world.Tiles.Trample(grid.XOf(to), grid.YOf(to));
        }

        Assert.Equal(Mover.StepCost(stepTicks, diagonal: false) * sim.Content.Roads.RoadCostPermille / 1000, AgentMovement.StepCost(world, stepTicks, from, to));
        Assert.Equal(PathCosts.Straight * sim.Content.Roads.RoadCostPermille / 1000, world.Pathfinder.Grid.TileCost(grid.XOf(to), grid.YOf(to)));
    }
}
