using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Logging;
using Core.Pathfinding;

namespace Core.Sim;

/// <summary>How one tick of walking ended for an agent (<see cref="AgentMovement.Advance"/>).</summary>
internal enum WalkOutcome : byte
{
    /// <summary>Still on the way (or idle: nothing to do).</summary>
    Walking = 0,

    /// <summary>Reached the goal (or got within range of it); the mover is idle now.</summary>
    Arrived = 1,

    /// <summary>The way is blocked and no new path leaves the current tile; the mover is idle now.</summary>
    Blocked = 2,
}

/// <summary>
/// Walking shared by every agent with a <see cref="Mover"/> (the hero, residents; ADR 0003, ADR 0009): planning a path,
/// one tick along it, replanning in front of a blocked tile and at the end of a partial path, and the wider search
/// after a stall. Stateless: the walking state is the <see cref="Mover"/>, the agent's position its <see cref="Position"/>.
/// </summary>
/// <remarks>
/// <para>Paths are planned synchronously with a node limit (<see cref="NodeLimit"/>), so no search queue has to be
/// saved: the planned path is part of the <see cref="Mover"/>, which is in the hash and the save. A search that runs out
/// of nodes gives a partial path; at its end the walker plans again from where it stands. A search that stalls (the
/// partial path is the start alone) runs once more over the whole map (<see cref="FindPath"/>).</para>
/// <para>Speed: a step costs <c>stepTicks × 1000</c> progress units straight and ×1.4 diagonal (<see cref="Mover.StepCost"/>);
/// walking adds <see cref="Mover.ProgressPerTick"/> per tick, sprinting more. The agent's rules pick the step ticks and
/// the rate and react to the <see cref="WalkOutcome"/>.</para>
/// </remarks>
internal static class AgentMovement
{
    /// <summary>
    /// Node limit of one path search: the per-request default of the path queue (ADR 0003), an engine budget that keeps
    /// a tick within NFR-02 rather than a balance number.
    /// </summary>
    public static int NodeLimit { get; } = new PathfinderConfig().MaxNodesPerRequest;

    /// <summary>Chebyshev distance in tiles (diagonal steps are allowed, so it is the step count on open ground).</summary>
    public static int Distance(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    /// <summary>
    /// Movement system body: every walker advances along its path, in ascending entity order. The hero's rules handle
    /// heroes (sprint, stamina); any other walker walks at the hero's step pace.
    /// </summary>
    public static void Update(Simulation sim)
    {
        GameWorld? world = sim.World;
        if (world is null)
        {
            return;
        }

        HeroBalanceDef heroBalance = sim.Content.Hero;
        ReadOnlySpan<EntityId> owners = world.Movers.Entities;
        Span<Mover> movers = world.Movers.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            EntityId entity = owners[i];
            if (world.Heroes.Has(entity))
            {
                HeroRules.Walk(sim, world, heroBalance, entity, ref movers[i]);
            }
            else if (movers[i].IsMoving && sim.Positions.Has(entity))
            {
                Advance(sim, world, entity, ref movers[i], heroBalance.StepTicks, Mover.ProgressPerTick, HeroRules.LogCategory);
            }
        }
    }

    /// <summary>
    /// Plans a fresh path for <paramref name="entity"/> to <paramref name="goal"/> (<paramref name="range"/> 0: the tile
    /// itself; otherwise until within that Chebyshev distance). Mid-step, the walker first finishes its step: the new path
    /// starts at the step's end and the current step is kept in front of it.
    /// </summary>
    /// <returns>
    /// False when the goal cannot be reached (or the agent is off the map); true otherwise, with
    /// <paramref name="planned"/> idle when the agent already stands at the goal or within range.
    /// </returns>
    public static bool TryPlan(Simulation sim, GameWorld world, EntityId entity, int goal, int range, bool sprint, out Mover planned)
    {
        PassabilityGrid grid = world.Pathfinder.Grid;
        Mover current = world.Movers.Get(entity);
        if (current.StepProgress > 0 && current.NextTile >= 0)
        {
            if (!TryFindPath(world, current.NextTile, goal, range, out List<int> path))
            {
                planned = Mover.Idle;
                return false;
            }

            planned = new Mover(goal, range, [current.CurrentTile, .. path], 0, current.StepProgress, sprint);
            return true;
        }

        Position position = sim.Positions.Get(entity);
        if (!grid.Contains(position.TileX, position.TileY))
        {
            planned = Mover.Idle;
            return false;
        }

        return TryPlanFrom(world, grid.Index(position.TileX, position.TileY), goal, range, sprint, out planned);
    }

    /// <summary>
    /// Plans a path from tile <paramref name="start"/> for an agent standing there (not mid-step), as
    /// <see cref="TryPlan"/>: false when unreachable; true with an idle <paramref name="planned"/> when already there.
    /// </summary>
    public static bool TryPlanFrom(GameWorld world, int start, int goal, int range, bool sprint, out Mover planned)
    {
        PassabilityGrid grid = world.Pathfinder.Grid;
        if (range > 0 && Distance(grid.XOf(start), grid.YOf(start), grid.XOf(goal), grid.YOf(goal)) <= range)
        {
            planned = Mover.Idle;
            return true; // already close enough
        }

        if (!TryFindPath(world, start, goal, range, out List<int> path))
        {
            planned = Mover.Idle;
            return false;
        }

        planned = path.Count > 1 ? new Mover(goal, range, [.. path], 0, 0, sprint) : Mover.Idle;
        return true;
    }

    /// <summary>
    /// Moves one walker by <paramref name="rate"/> progress units along its path (see <see cref="Mover"/> for the units),
    /// a step costing <paramref name="stepTicks"/> walking ticks straight. Updates the position every tick; on arrival or
    /// when blocked the mover becomes idle on the centre of its tile.
    /// </summary>
    /// <param name="logCategory">Log category of the agent's rules, for the "blocked" message.</param>
    public static WalkOutcome Advance(Simulation sim, GameWorld world, EntityId entity, ref Mover mover, int stepTicks, int rate, string logCategory)
    {
        PassabilityGrid grid = world.Pathfinder.Grid;
        IReadOnlyList<int> path = mover.Path;
        int index = mover.StepIndex;
        int progress = mover.StepProgress + rate;
        bool stepStarts = mover.StepProgress == 0;

        // The range check applies only on a tile (at a step boundary): mid-step the walker always finishes the step.
        while (index < path.Count - 1 && !(stepStarts && InRange(grid, path[index], mover)))
        {
            int from = path[index];
            int to = path[index + 1];
            if (stepStarts && !CanStep(grid, from, to))
            {
                if (!TryReplan(world, from, mover, out path))
                {
                    return Halt(sim, entity, ref mover, from, logCategory);
                }

                index = 0;
                continue;
            }

            int cost = Mover.StepCost(stepTicks, grid.XOf(from) != grid.XOf(to) && grid.YOf(from) != grid.YOf(to));
            if (progress < cost)
            {
                mover = mover with { Path = path, StepIndex = index, StepProgress = progress };
                sim.Positions.Get(entity) = Lerp(grid, from, to, progress, cost);
                return WalkOutcome.Walking;
            }

            progress -= cost;
            index++;
            stepStarts = true;
        }

        // End of the path (or within range of the goal): leftover progress is dropped.
        int tile = path[index];
        sim.Positions.Get(entity) = Center(grid, tile);
        if (InRange(grid, tile, mover) || (mover.GoalRange == 0 && tile == mover.Goal))
        {
            mover = Mover.Idle;
            return WalkOutcome.Arrived;
        }

        // A partial path ran out before the goal: plan again from here.
        if (TryReplan(world, tile, mover, out IReadOnlyList<int> next))
        {
            mover = mover with { Path = next, StepIndex = 0, StepProgress = 0 };
            return WalkOutcome.Walking;
        }

        return Halt(sim, entity, ref mover, tile, logCategory);
    }

    /// <summary>
    /// The path search: to the goal tile itself (<paramref name="range"/> 0) or next to it, first with
    /// <see cref="NodeLimit"/>. When that search stalls, a partial path that is only the start, it runs once more
    /// with <see cref="StallNodeLimit"/>.
    /// </summary>
    /// <remarks>
    /// A partial result comes only after the connectivity check passed (the goal, or for an approach a tile next to
    /// it, is in the start's component), so the goal is known to be reachable. A limited search ends on the explored
    /// tile closest to the goal by the heuristic, often a cliff edge or a shore next to the goal; walking there and
    /// searching again from it explores the same pocket and returns that tile alone. Without the wider search the order
    /// was rejected as unreachable or the walker stopped. The heuristic value of a partial path's end strictly decreases
    /// from one partial search to the next, so a walker following partial paths never cycles: it arrives or stalls, and a
    /// stall is where the wider search runs. A* clears every node record it touches, so the result does not depend on
    /// earlier searches (NFR-04).
    /// </remarks>
    public static PathResult FindPath(Pathfinder pathfinder, int start, int goal, int range, List<int> path)
    {
        PathResult result = Search(pathfinder, start, goal, range, path, NodeLimit);
        if (result.Status == PathStatus.Partial && result.Length <= 1)
        {
            result = Search(pathfinder, start, goal, range, path, StallNodeLimit(pathfinder.Grid));
        }

        return result;
    }

    /// <summary>
    /// Node limit of the search after a stall: the whole map, capped at <see cref="PathfinderConfig.MaxNodeLimit"/>
    /// (which keeps path costs within <see cref="int"/>). A* stops at the goal, so the search costs only the nodes the
    /// detour needs: on generated 1024² and 2048² maps a median of 35 000–55 000 nodes (3–5 ms in Release) and at most
    /// about 300 000 (under 40 ms), up to a million for a detour across a 2048² map (50–90 ms, ADR 0003).
    /// </summary>
    public static int StallNodeLimit(PassabilityGrid grid) => Math.Min(grid.TileCount, PathfinderConfig.MaxNodeLimit);

    /// <summary>True when <paramref name="mover"/> is a valid saved walking state on a map of <paramref name="size"/>².</summary>
    public static bool IsValidMover(Mover mover, int size)
    {
        int maxTile = size * size;
        IReadOnlyList<int> path = mover.Path;
        if (path.Count == 0)
        {
            return mover == Mover.Idle;
        }

        if (path.Count < 2 || mover.Goal < 0 || mover.Goal >= maxTile || mover.GoalRange < 0 || mover.StepIndex < 0
            || mover.StepIndex >= path.Count - 1 || mover.StepProgress < 0)
        {
            return false;
        }

        for (int i = 0; i < path.Count; i++)
        {
            if (path[i] < 0 || path[i] >= maxTile
                || (i > 0 && Distance(path[i] % size, path[i] / size, path[i - 1] % size, path[i - 1] / size) != 1))
            {
                return false;
            }
        }

        return true;
    }

    // A path from `start` that leaves it, or false: no path, or a stalled partial one.
    private static bool TryFindPath(GameWorld world, int start, int goal, int range, out List<int> path)
    {
        path = [];
        PathResult result = FindPath(world.Pathfinder, start, goal, range, path);
        return result.HasPath && !(result.Status == PathStatus.Partial && result.Length <= 1);
    }

    private static WalkOutcome Halt(Simulation sim, EntityId entity, ref Mover mover, int tile, string logCategory)
    {
        mover = Mover.Idle;
        sim.Positions.Get(entity) = Center(sim.World!.Pathfinder.Grid, tile);
        sim.Log.Write(sim.Tick, LogLevel.Info, logCategory, "{entity} stopped: the way is blocked", LogField.Of("entity", entity.ToString()));
        return WalkOutcome.Blocked;
    }

    // True when a ranged mover (gathering) standing on `tile` is close enough to its goal.
    private static bool InRange(PassabilityGrid grid, int tile, Mover mover) =>
        mover.GoalRange > 0 && Distance(grid.XOf(tile), grid.YOf(tile), grid.XOf(mover.Goal), grid.YOf(mover.Goal)) <= mover.GoalRange;

    private static bool CanStep(PassabilityGrid grid, int from, int to)
    {
        int x = grid.XOf(from);
        int y = grid.YOf(from);
        int dx = grid.XOf(to) - x;
        int dy = grid.YOf(to) - y;
        for (int d = 0; d < PassabilityGrid.DirectionCount; d++)
        {
            if (PassabilityGrid.DirectionX(d) == dx && PassabilityGrid.DirectionY(d) == dy)
            {
                return grid.CanStep(x, y, d);
            }
        }

        return false;
    }

    // A new path from `from` towards the mover's goal; false when there is none or it does not leave `from`.
    private static bool TryReplan(GameWorld world, int from, Mover mover, out IReadOnlyList<int> path)
    {
        var tiles = new List<int>();
        PathResult result = FindPath(world.Pathfinder, from, mover.Goal, mover.GoalRange, tiles);
        path = [.. tiles];
        return result.Length > 1;
    }

    private static PathResult Search(Pathfinder pathfinder, int start, int goal, int range, List<int> path, int nodeLimit) =>
        range == 0 ? pathfinder.FindPath(start, goal, path, nodeLimit) : pathfinder.FindPathToAdjacent(start, goal, path, nodeLimit);

    private static Position Center(PassabilityGrid grid, int tile) => Simulation.TileCenter(grid.XOf(tile), grid.YOf(tile));

    private static Position Lerp(PassabilityGrid grid, int from, int to, int progress, int cost)
    {
        Position a = Center(grid, from);
        Position b = Center(grid, to);
        return new Position(
            a.X + (int)((long)(b.X - a.X) * progress / cost),
            a.Y + (int)((long)(b.Y - a.Y) * progress / cost));
    }
}
