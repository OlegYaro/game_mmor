using Core.Determinism;
using Core.Entities;

namespace Core.Heroes;

/// <summary>
/// Walking state of an entity (FR-HERO-01): goal, the planned path and the progress of the current step. It is part
/// of the hash and the save, so a loaded game keeps walking exactly like the original. Immutable: a new path is a new
/// list.
/// </summary>
/// <remarks>
/// <para>The path is a list of tile indices (<c>y * mapSize + x</c>). The walker is at <c>Path[StepIndex]</c> when
/// <see cref="StepProgress"/> is 0 and moving towards <c>Path[StepIndex + 1]</c> otherwise. An empty path means idle.</para>
/// <para>Progress is counted in thousandths of a walking tick: walking adds 1000 per tick, sprinting
/// <see cref="Content.HeroBalanceDef.SprintPermille"/>. A straight step costs <c>StepTicks × 1000</c>, a diagonal one
/// <c>StepTicks × 1400</c> (the 14/10 ratio of the path costs, ADR 0003). Progress left over from a finished step is
/// carried into the next, so sprinting averages exactly the sprint speed.</para>
/// </remarks>
/// <param name="Goal">Goal tile index; -1 when idle.</param>
/// <param name="GoalRange">
/// 0: reach the goal tile. 1 or more: stop as soon as the Chebyshev distance to the goal is at most this (gathering:
/// <see cref="Content.HeroBalanceDef.InteractionRange"/>); the path then leads to a tile next to the goal.
/// </param>
/// <param name="Path">Planned tiles, the walker's tile first; empty when idle.</param>
/// <param name="StepIndex">Index in <paramref name="Path"/> of the tile the current step starts from.</param>
/// <param name="StepProgress">Progress of the current step in thousandths of a walking tick.</param>
/// <param name="Sprint">True when the walker should sprint while it has stamina.</param>
public readonly record struct Mover(int Goal, int GoalRange, IReadOnlyList<int> Path, int StepIndex, int StepProgress, bool Sprint) : IComponent
{
    /// <summary>Progress units one walking tick adds (and the unit of <see cref="StepProgress"/>).</summary>
    public const int ProgressPerTick = 1000;

    /// <summary>An idle mover.</summary>
    public static Mover Idle { get; } = new(-1, 0, [], 0, 0, false);

    /// <summary>True when there is a path to follow.</summary>
    public bool IsMoving => Tiles.Count > 0;

    /// <summary>The tile the walker stands on or the current step starts from; -1 when idle.</summary>
    public int CurrentTile => IsMoving ? Tiles[StepIndex] : -1;

    /// <summary>The tile the current step leads to; -1 when idle or at the end of the path.</summary>
    public int NextTile => IsMoving && StepIndex + 1 < Tiles.Count ? Tiles[StepIndex + 1] : -1;

    private IReadOnlyList<int> Tiles => Path ?? [];

    /// <summary>Progress units a step of <paramref name="stepTicks"/> walking ticks costs: ×1000 straight, ×1400 diagonal.</summary>
    public static int StepCost(int stepTicks, bool diagonal) =>
        diagonal ? Pathfinding.PathCosts.Diagonal(stepTicks * ProgressPerTick) : stepTicks * ProgressPerTick;

    /// <summary>
    /// The same walker told to stop: mid-step it finishes the current step (the path shrinks to that step and the
    /// goal becomes its end), otherwise it becomes idle at once.
    /// </summary>
    public Mover Halt() => StepProgress > 0 && NextTile >= 0
        ? this with { Goal = NextTile, GoalRange = 0, Path = [CurrentTile, NextTile], StepIndex = 0 }
        : Idle;

    /// <summary>Writes goal, range, path (count then tiles), step index, progress and the sprint flag.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Goal);
        writer.WriteInt32(GoalRange);
        writer.WriteInt32(Tiles.Count);
        foreach (int tile in Tiles)
        {
            writer.WriteInt32(tile);
        }

        writer.WriteInt32(StepIndex);
        writer.WriteInt32(StepProgress);
        writer.WriteBool(Sprint);
    }

    /// <inheritdoc />
    public bool Equals(Mover other) =>
        Goal == other.Goal && GoalRange == other.GoalRange && StepIndex == other.StepIndex && StepProgress == other.StepProgress
        && Sprint == other.Sprint && Tiles.SequenceEqual(other.Tiles);

    /// <inheritdoc />
    public override int GetHashCode() => (((Goal * 31) + StepIndex) * 31) + StepProgress + (Tiles.Count * 7);
}
