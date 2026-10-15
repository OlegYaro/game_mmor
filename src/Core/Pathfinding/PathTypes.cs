using Core.Entities;

namespace Core.Pathfinding;

/// <summary>Outcome of a path search.</summary>
public enum PathStatus : byte
{
    /// <summary>A cheapest path to the goal was found.</summary>
    Found = 0,

    /// <summary>
    /// The node limit ran out first: the path leads to the explored tile closest to the goal by the heuristic (then the
    /// cheaper one, then the lower index). It may be just the start. The goal is reachable; ask again later.
    /// </summary>
    Partial = 1,

    /// <summary>The goal is passable but in another component than the start; no search was run.</summary>
    Unreachable = 2,

    /// <summary>The start or the goal is impassable (for an approach search: no neighbour of the target is passable and reachable).</summary>
    Blocked = 3,
}

/// <summary>What a search should reach.</summary>
public enum PathGoalKind : byte
{
    /// <summary>The goal tile itself.</summary>
    Tile = 0,

    /// <summary>
    /// Any passable tile among the eight around the goal, the cheapest to reach: approaching a tree, a rock or a
    /// building whose own tile is impassable. The goal tile itself never ends the path.
    /// </summary>
    Adjacent = 1,
}

/// <summary>
/// Result of a search. The tiles of the path (indices <c>y * Size + x</c>, start first, end last) are written into
/// the caller's buffer; <see cref="Length"/> is their count.
/// </summary>
/// <param name="Status">Outcome.</param>
/// <param name="Cost">Cost of the path in step units (10 per straight step on normal ground); 0 without a path.</param>
/// <param name="Length">Number of tiles in the path including the start; 0 without a path.</param>
/// <param name="EndTile">Last tile of the path, or -1 without a path.</param>
/// <param name="ExpandedNodes">Nodes A* expanded (0 when it did not run).</param>
public readonly record struct PathResult(PathStatus Status, int Cost, int Length, int EndTile, int ExpandedNodes)
{
    /// <summary>True for <see cref="PathStatus.Found"/> and <see cref="PathStatus.Partial"/>: there is a path to follow.</summary>
    public bool HasPath => Length > 0;

    internal static PathResult Fail(PathStatus status) => new(status, 0, 0, -1, 0);
}

/// <summary>A path request for <see cref="PathRequestScheduler"/>.</summary>
/// <param name="Entity">The walker; requests are ordered by it after the priority, and it owns at most one pending request.</param>
/// <param name="Start">Start tile index.</param>
/// <param name="Goal">Goal tile index (for <see cref="PathGoalKind.Adjacent"/>, the target to stand next to).</param>
/// <param name="Kind">What to reach.</param>
/// <param name="Priority">Higher runs first within a tick.</param>
/// <param name="Forced">
/// The walker's path became invalid (blocked, goal changed by the game): skip the replan interval. Ordinary replans
/// wait <see cref="PathfinderConfig.ReplanIntervalTicks"/> after the walker's previous search.
/// </param>
public readonly record struct PathRequest(EntityId Entity, int Start, int Goal, PathGoalKind Kind = PathGoalKind.Tile, int Priority = 0, bool Forced = false);

/// <summary>
/// Pathfinding settings (ADR 0003). Balance values come from data files; the defaults here are reasonable for tests
/// and for a 512² map.
/// </summary>
/// <remarks>
/// The default budget keeps a tick's searches within 4 ms (half of NFR-02's tick): about 65 ns per node in Release
/// (<c>PathfindingBenchmarkTests</c>), so 40 000 nodes plus at most one 20 000-node overrun is about 3.9 ms.
/// </remarks>
/// <param name="MaxNodesPerRequest">Node limit of one search; past it the result is <see cref="PathStatus.Partial"/>.</param>
/// <param name="NodeBudgetPerTick">
/// Nodes the queue may spend in one tick. A search is started while the spent count is below it and is never cut by
/// it, so a tick can overrun the budget by at most one search (<see cref="MaxNodesPerRequest"/>).
/// </param>
/// <param name="ReplanIntervalTicks">Minimum ticks between two non-forced searches of one walker.</param>
public sealed record PathfinderConfig(int MaxNodesPerRequest = 20_000, int NodeBudgetPerTick = 40_000, int ReplanIntervalTicks = 10)
{
    /// <summary>
    /// Largest allowed per-request node limit: a path cost is at most one diagonal step of
    /// <see cref="PathCosts.MaxTileCost"/> per expanded node, which must fit in <see cref="int"/> with the heuristic on top.
    /// </summary>
    public const int MaxNodeLimit = 1_500_000;

    /// <summary>Throws when a value is out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive, the node limit exceeds <see cref="MaxNodeLimit"/> or the interval is negative.</exception>
    public void Validate()
    {
        ValidateNodeLimit(MaxNodesPerRequest);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NodeBudgetPerTick);
        ArgumentOutOfRangeException.ThrowIfNegative(ReplanIntervalTicks);
    }

    internal static void ValidateNodeLimit(int nodeLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeLimit, MaxNodeLimit);
    }
}
