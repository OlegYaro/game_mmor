using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Logging;
using Core.Pathfinding;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Residents;

/// <summary>Refusals and rare outcomes of the resident rules (FR-NPC-01, FR-NPC-02): they change nothing but the log.</summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class ResidentRulesEdgeTests
{
    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void SpawnOrdersWithoutAWorldOrWithAnUnknownProfessionAreRefused()
    {
        var core = new Simulation(1, SimTestData.Real);
        core.Enqueue(new SpawnResidentCommand());
        core.Step();
        Assert.Contains(core.Log.GetEvents(), e => e.Level == LogLevel.Warning && e.Category == ResidentRules.LogCategory);
        Assert.Empty(core.Entities.Registry.AliveEntities());

        Simulation sim = NewResidentGame();
        Settle(sim, houses: 0);
        sim.Enqueue(new SpawnResidentCommand("astronaut"));
        sim.Enqueue(new SpawnResidentCommand("wood"));
        sim.Step();
        Assert.Equal(0, sim.World!.Residents.Count);
        Assert.Equal(2, sim.Log.GetEvents().Count(e => e.Level == LogLevel.Warning && e.Category == ResidentRules.LogCategory));
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void AFailedRollDrawsOneNumberAndBringsNobody()
    {
        ContentCatalog content = WithEconomy(json => json.Replace("\"arrivalChanceCapPermille\": 800", "\"arrivalChanceCapPermille\": 1", StringComparison.Ordinal));
        Simulation sim = NewResidentGame(content);
        Settle(sim, houses: 1);
        StepTo(sim, ArrivalTick(sim, 0) - 1);
        Assert.Equal(1, Arrivals.ComputeOdds(sim).ChancePermille);
        var expected = new Xoshiro256StarStar(sim.Rng[RngStreamId.Events].GetState());
        Assert.False(expected.ChancePermille(1)); // this seed's first roll misses the 1 ‰

        sim.Step();

        Assert.Equal(0, sim.World!.Residents.Count);
        Assert.Equal(expected.GetState(), sim.Rng[RngStreamId.Events].GetState());
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void ACenterWithoutAFreeTileAroundItGetsNobody()
    {
        Simulation sim = NewResidentGame(Certain);
        Settle(sim, houses: 1);
        EntityId center = Core.Buildings.Construction.FindCenter(sim);
        foreach (int tile in Arrivals.MeetingTiles(sim))
        {
            PassabilityGrid grid = sim.World!.Pathfinder.Grid;
            Assert.True(sim.World.Tiles.TryOccupy(grid.XOf(tile), grid.YOf(tile), center));
        }

        Assert.Empty(Arrivals.MeetingTiles(sim));
        StepTo(sim, ArrivalTick(sim, 0));

        Assert.Equal(0, sim.World!.Residents.Count);
        Assert.Contains(sim.Log.GetEvents(), e => e.Level == LogLevel.Warning && e.Category == ResidentRules.LogCategory);
        sim.Enqueue(new SpawnResidentCommand());
        sim.Step();
        Assert.Equal(0, sim.World.Residents.Count); // no tile next to the center for the console spawn either
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void RestoreRejectsAReachedEraThatIsNotAnEra()
    {
        Simulation sim = NewResidentGame();
        SimulationState state = sim.CaptureState();
        Assert.True(sim.Content.TryGetId("wood", out DefId wood));

        Assert.Throws<ArgumentException>(() => Simulation.Restore(sim.Content, state with { World = state.World! with { ReachedEra = wood } }));
        Assert.Throws<ArgumentException>(() => Simulation.Restore(sim.Content, state with { World = state.World! with { ReachedEra = default } }));
        Assert.Throws<ArgumentException>(() => Simulation.Restore(sim.Content, state with { World = state.World! with { ReachedEra = new DefId(sim.Content.Count + 1) } }));
    }
}
