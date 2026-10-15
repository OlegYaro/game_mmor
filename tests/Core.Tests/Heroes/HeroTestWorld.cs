using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Pathfinding;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.Heroes;

/// <summary>Helpers for hero tests on a small real game (64², fixed seed).</summary>
internal static class HeroTestWorld
{
    public const ulong Seed = 0xF06;

    public static Simulation NewGame(ulong seed = Seed) =>
        Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(seed, 64, Difficulty.Normal));

    public static EntityId Hero(Simulation sim) => sim.World!.HeroEntity;

    public static Hero HeroOf(Simulation sim) => sim.World!.Heroes.Get(Hero(sim));

    public static Mover MoverOf(Simulation sim) => sim.World!.Movers.Get(Hero(sim));

    public static Inventory InventoryOf(Simulation sim) => sim.World!.Inventories.Get(Hero(sim));

    public static (int X, int Y) HeroTile(Simulation sim)
    {
        Position p = sim.Positions.Get(Hero(sim));
        return (p.TileX, p.TileY);
    }

    /// <summary>
    /// An explored tile <paramref name="steps"/> straight steps from the hero whose cheapest path is the straight line
    /// (cost 10 per step), so walking there takes exactly <c>steps × stepTicks</c> ticks.
    /// </summary>
    public static (int X, int Y) StraightTarget(Simulation sim, int steps)
    {
        GameWorld world = sim.World!;
        (int hx, int hy) = HeroTile(sim);
        Pathfinder pathfinder = world.Pathfinder;
        var path = new List<int>();
        foreach ((int dx, int dy) in new[] { (1, 0), (0, 1), (-1, 0), (0, -1) })
        {
            int x = hx + (dx * steps);
            int y = hy + (dy * steps);
            if (world.Map.Terrain.Contains(x, y) && world.Fog.GetState(x, y) != FogState.Unknown && pathfinder.Grid.IsPassable(x, y)
                && pathfinder.FindPath(pathfinder.Grid.Index(hx, hy), pathfinder.Grid.Index(x, y), path, 100_000) is { Status: PathStatus.Found } result
                && result.Cost == steps * PathCosts.Straight)
            {
                return (x, y);
            }
        }

        throw new InvalidOperationException($"No straight {steps}-step target around the hero.");
    }

    /// <summary>
    /// The explored, reachable node of <paramref name="kind"/> nearest to the hero (ties by y, then x) at a Chebyshev
    /// distance of at least <paramref name="minDistance"/>.
    /// </summary>
    public static (int X, int Y) NearestNode(Simulation sim, ResourceKind kind, int minDistance = 4)
    {
        GameWorld world = sim.World!;
        (int hx, int hy) = HeroTile(sim);
        (int X, int Y)? best = null;
        int bestDistance = int.MaxValue;
        for (int y = 0; y < world.Size; y++)
        {
            for (int x = 0; x < world.Size; x++)
            {
                int distance = Math.Max(Math.Abs(x - hx), Math.Abs(y - hy));
                if (world.Tiles.GetEffectiveResource(x, y) == kind && world.Fog.GetState(x, y) != FogState.Unknown && distance >= minDistance
                    && distance < bestDistance && world.Pathfinder.TryFindApproachTile(world.Pathfinder.Grid.Index(hx, hy), world.Pathfinder.Grid.Index(x, y), 100_000, out _))
                {
                    best = (x, y);
                    bestDistance = distance;
                }
            }
        }

        return best ?? throw new InvalidOperationException($"No reachable explored {kind} near the hero.");
    }

    /// <summary>Runs until <paramref name="done"/> holds; fails after <paramref name="limit"/> ticks.</summary>
    public static int StepUntil(Simulation sim, Func<bool> done, int limit = 5000)
    {
        for (int ticks = 0; ticks < limit; ticks++)
        {
            if (done())
            {
                return ticks;
            }

            sim.Step();
        }

        throw new InvalidOperationException($"Condition not reached in {limit} ticks.");
    }
}
