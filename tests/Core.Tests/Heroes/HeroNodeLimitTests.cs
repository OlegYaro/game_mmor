using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Pathfinding;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.Heroes;

/// <summary>
/// Hero paths on large generated maps where the node-limited search stalls (FR-HERO-01, ADR 0003): the limited search
/// ends on the tile closest to the goal by the heuristic (a cliff edge or a shore next to it), and a new limited search
/// from there gets no further (a partial path of the start alone), although the goal is reachable around the obstacle.
/// </summary>
/// <remarks>
/// The cases come from scans of random reachable pairs at most 40 tiles apart (about 2% of them stalled on 2048²).
/// Each test first checks that the case still exercises the node limit, so a change of the world generator that
/// moves the obstacles fails loudly here instead of silently testing nothing.
/// </remarks>
public sealed class HeroNodeLimitTests
{
    private static readonly Lazy<WorldMap> _map1024 = new(() => WorldGenerator.Generate(new WorldGenRequest(12345, 1024, SimTestData.Real.WorldGen)));
    private static readonly Lazy<WorldMap> _map2048 = new(() => WorldGenerator.Generate(new WorldGenRequest(2, 2048, SimTestData.Real.WorldGen)));

    private static HeroBalanceDef Balance => SimTestData.Real.Hero;

    // A revealed game on the shared map with the hero standing idle on (x, y).
    private static Simulation GameWithHeroAt(int size, int x, int y)
    {
        WorldMap map = size == 1024 ? _map1024.Value : _map2048.Value;
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(map.Seed, size, Difficulty.Normal), null, map);
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        sim.Positions.Get(sim.World!.HeroEntity) = Simulation.TileCenter(x, y);
        return sim;
    }

    // The limited search the hero runs from (x, y) stalls short of the goal: a partial path, and the search from its end is the end alone.
    private static void AssertLimitedSearchStalls(Pathfinder pathfinder, int x, int y, int goal, bool adjacent)
    {
        var path = new List<int>();
        int start = pathfinder.Grid.Index(x, y);
        PathGoalKind kind = adjacent ? PathGoalKind.Adjacent : PathGoalKind.Tile;
        PathResult first = pathfinder.Find(start, goal, kind, path, HeroRules.NodeLimit);
        Assert.Equal(PathStatus.Partial, first.Status);
        PathResult again = pathfinder.Find(first.EndTile, goal, kind, path, HeroRules.NodeLimit);
        Assert.Equal(PathStatus.Partial, again.Status);
        Assert.Equal(1, again.Length);
        Assert.Equal(PathStatus.Found, pathfinder.Find(start, goal, kind, path, PathfinderConfig.MaxNodeLimit).Status);
    }

    [Theory]
    [Trait("Req", "FR-HERO-01")]
    [InlineData(1024, 612, 463, 608, 448)] // the first limited search is the start alone: the order was rejected as unreachable
    [InlineData(2048, 115, 1323, 109, 1323)] // 6 tiles apart, 270-tile detour: the hero stopped one tile from the goal
    [InlineData(2048, 761, 1301, 789, 1341)] // the hero stopped two tiles from the goal
    public void TheHeroWalksToAReachableGoalThatTheLimitedSearchCannotGetCloserTo(int size, int sx, int sy, int gx, int gy)
    {
        Simulation sim = GameWithHeroAt(size, sx, sy);
        Pathfinder pathfinder = sim.World!.Pathfinder;
        int goal = pathfinder.Grid.Index(gx, gy);
        Assert.True(pathfinder.IsReachable(pathfinder.Grid.Index(sx, sy), goal));
        AssertLimitedSearchStalls(pathfinder, sx, sy, goal, adjacent: false);

        sim.Enqueue(new MoveHeroCommand(gx, gy));
        sim.Step();
        Assert.Equal(HeroOrderResult.Accepted, HeroTestWorld.HeroOf(sim).LastOrder);
        HeroTestWorld.StepUntil(sim, () => !HeroTestWorld.MoverOf(sim).IsMoving);

        Assert.Equal((gx, gy), HeroTestWorld.HeroTile(sim));
        Assert.Equal(HeroStopReason.Arrived, HeroTestWorld.HeroOf(sim).LastStop);
    }

    [Theory]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "FR-HERO-02")]
    [InlineData(614, 460)] // the first limited search is the start alone: the order was rejected as unreachable
    [InlineData(615, 461)] // a partial path of 4 tiles, then the hero stopped with PathBlocked
    public void TheHeroGathersFromANodeThatTheLimitedSearchCannotGetCloserTo(int tx, int ty)
    {
        const int Sx = 612;
        const int Sy = 463;
        Simulation sim = GameWithHeroAt(1024, Sx, Sy);
        Pathfinder pathfinder = sim.World!.Pathfinder;
        Assert.Equal(ResourceKind.Tree, sim.World.Tiles.GetEffectiveResource(tx, ty));
        Assert.True(HeroRules.Distance(Sx, Sy, tx, ty) > Balance.InteractionRange);
        AssertLimitedSearchStalls(pathfinder, Sx, Sy, pathfinder.Grid.Index(tx, ty), adjacent: true);
        Assert.True(sim.Content.TryGetId("wood", out DefId wood));

        sim.Enqueue(new GatherCommand(tx, ty));
        sim.Step();
        Assert.Equal(HeroOrderResult.Accepted, HeroTestWorld.HeroOf(sim).LastOrder);
        HeroTestWorld.StepUntil(sim, () => !HeroTestWorld.MoverOf(sim).IsMoving);

        (int hx, int hy) = HeroTestWorld.HeroTile(sim);
        Assert.True(HeroRules.Distance(hx, hy, tx, ty) <= Balance.InteractionRange, $"stopped at {hx},{hy}");
        Assert.Equal(HeroActivity.Gathering, HeroTestWorld.HeroOf(sim).Activity);
        HeroTestWorld.StepUntil(sim, () => HeroTestWorld.InventoryOf(sim).Count(wood) > 0, limit: 200);
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "NFR-04")]
    public void TheWiderSearchGivesThePathOfAFreshPathfinder()
    {
        // The wider search runs on the same A* arrays as the limited one before it; the planned path must not depend
        // on what those arrays held: a new pathfinder over the same grid gives the same path.
        Simulation sim = GameWithHeroAt(1024, 612, 463);
        PassabilityGrid grid = sim.World!.Pathfinder.Grid;
        int start = grid.Index(612, 463);
        int goal = grid.Index(608, 448);
        var expected = new List<int>();
        Assert.Equal(PathStatus.Found, new Pathfinder(grid).FindPath(start, goal, expected, PathfinderConfig.MaxNodeLimit).Status);

        sim.Enqueue(new MoveHeroCommand(608, 448));
        sim.Step();

        Mover mover = HeroTestWorld.MoverOf(sim);
        Assert.Equal(expected, mover.Path);
        Assert.Equal(goal, mover.Goal);
    }
}
