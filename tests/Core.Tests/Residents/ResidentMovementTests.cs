using Core.Entities;
using Core.Heroes;
using Core.Logging;
using Core.Pathfinding;
using Core.Residents;
using Core.Sim;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Residents;

/// <summary>
/// Residents walk with the shared agent movement (FR-NPC-02, ADR 0003): at the resident pace from the economy data, and
/// a newcomer whose way in gets blocked stops and waits where it is.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class ResidentMovementTests
{
    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void ANewcomerWalksInAtTheResidentPace()
    {
        Simulation sim = NewResidentGame(Certain);
        Settle(sim, houses: 1);
        StepTo(sim, ArrivalTick(sim, 0));
        EntityId newcomer = Newest(sim);
        Mover mover = sim.World!.Movers.Get(newcomer);
        PassabilityGrid grid = sim.World.Pathfinder.Grid;
        Assert.Equal(mover.Goal, mover.Path[^1]); // the whole way was planned at once (a short walk on a small map)

        // The newcomer already took its first tick of walking in the tick it appeared.
        long total = 0;
        for (int i = 0; i + 1 < mover.Path.Count; i++)
        {
            bool diagonal = grid.XOf(mover.Path[i]) != grid.XOf(mover.Path[i + 1]) && grid.YOf(mover.Path[i]) != grid.YOf(mover.Path[i + 1]);
            total += Mover.StepCost(sim.Content.Economy.ResidentStepTicks, diagonal);
        }

        long ticks = (total + Mover.ProgressPerTick - 1) / Mover.ProgressPerTick;
        long appeared = sim.Tick;
        Assert.Equal(Mover.ProgressPerTick, mover.StepProgress);

        Heroes.HeroTestWorld.StepUntil(sim, () => ResidentOf(sim, newcomer).State == ResidentState.Idle, limit: 2000);

        Assert.Equal(appeared + ticks - 1, sim.Tick);
        Assert.Equal(Simulation.TileCenter(grid.XOf(mover.Goal), grid.YOf(mover.Goal)), sim.Positions.Get(newcomer));
    }

    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void ANewcomerWhoseWayIsBlockedStopsAndWaits()
    {
        Simulation sim = NewResidentGame(Certain);
        Settle(sim, houses: 1);
        StepTo(sim, ArrivalTick(sim, 0));
        EntityId newcomer = Newest(sim);
        Mover mover = sim.World!.Movers.Get(newcomer);
        PassabilityGrid grid = sim.World.Pathfinder.Grid;

        // Something takes the goal tile: the newcomer walks up to it, cannot step in and finds no other way.
        Assert.True(sim.World.Tiles.TryOccupy(grid.XOf(mover.Goal), grid.YOf(mover.Goal), Core.Buildings.Construction.FindCenter(sim)));
        Heroes.HeroTestWorld.StepUntil(sim, () => ResidentOf(sim, newcomer).State == ResidentState.Idle, limit: 2000);

        Assert.False(sim.World.Movers.Get(newcomer).IsMoving);
        Assert.NotEqual((grid.XOf(mover.Goal), grid.YOf(mover.Goal)), TileOf(sim, newcomer));
        Assert.Contains(sim.Log.GetEvents(), e => e.Level == LogLevel.Warning && e.Category == ResidentRules.LogCategory
            && e.Fields.Any(f => f.ValueText.Contains("blocked", StringComparison.Ordinal)));
    }
}
