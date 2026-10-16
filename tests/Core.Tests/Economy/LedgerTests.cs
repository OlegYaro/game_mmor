using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Economy;

/// <summary>
/// The item ledger (FR-ECO-01, FR-ECO-10): every way items enter or leave the economy is counted, so the items held
/// anywhere equal the inflow minus the outflow at every tick (the conservation check of the bot simulation).
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class LedgerTests
{
    private static void AssertBalanced(Simulation sim)
    {
        long[] held = BotSimulation.Census(sim);
        EconomyLedger ledger = sim.World!.Ledger;
        for (int value = 1; value < held.Length; value++)
        {
            var item = new DefId(value);
            Assert.True(held[value] == ledger.Inflow(item) - ledger.Outflow(item),
                $"Tick {sim.Tick}: {sim.Content.Get(item).Id} held {held[value]}, ledger in {ledger.Inflow(item)} out {ledger.Outflow(item)}.");
        }
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    [Trait("Req", "FR-ECO-10")]
    public void EveryItemHeldIsAccountedForAtEveryTick()
    {
        // Built with given materials (given, spent), a field and residents at work (gathered, harvested), meals and the tax:
        // everything through orders, as in a game.
        Simulation sim = NewGame(Quiet);
        BotSimulation.Setup(sim, new BotScenario(Heroes.HeroTestWorld.Seed, 64, 0, 1, 1, [("wood", 160), ("berries", 40)], []), 0);
        EntityId house = M3aScenarioFind(sim, "house");
        foreach (string profession in new[] { "lumberjack", "farmer", "carrier", "stonemason" })
        {
            Spawn(sim, profession);
        }

        AssertBalanced(sim);
        EconomyLedger ledger = sim.World!.Ledger;
        DefId wood = Item(sim, "wood");
        Assert.True(ledger.Get(LedgerFlow.Given, wood) > 0);
        Assert.True(ledger.Get(LedgerFlow.Spent, wood) > 0);

        long end = sim.Calendar.FirstTickOf(2, 0) + 10;
        while (sim.Tick < end)
        {
            sim.Step();
            AssertBalanced(sim);
        }

        Assert.True(ledger.Get(LedgerFlow.Gathered, wood) > 0);
        Assert.True(ledger.Get(LedgerFlow.Harvested, Item(sim, "vegetable")) > 0);
        Assert.True(ledger.Get(LedgerFlow.Eaten, Item(sim, "berries")) + ledger.Get(LedgerFlow.Eaten, Item(sim, "vegetable")) >= 8);
        Assert.True(ledger.Get(LedgerFlow.Taxed, Item(sim, FoodSupply.TaxItem)) > 0);

        // A demolition refunds part of the cost; a refund with no room anywhere is lost.
        long refunded = ledger.Get(LedgerFlow.Refunded, wood);
        Order(sim, new DemolishBuildingCommand(house));
        Assert.True(ledger.Get(LedgerFlow.Refunded, wood) > refunded);
        AssertBalanced(sim);
        Assert.Equal(0, new EconomyLedger(3).Get(LedgerFlow.Gathered, new DefId(9)));
    }

    private static EntityId M3aScenarioFind(Simulation sim, string id) => Replay.M3aScenario.Find(sim, id);

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void TheLoadOfAResidentWhoLeavesWithoutRoomIsLost()
    {
        (Simulation sim, EntityId storage) = NewSettlement(Quiet);
        EntityId lumberjack = Spawn(sim, "lumberjack");
        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == Core.Jobs.JobPhase.ToStorage);
        Fill(sim, storage, "stone", 50 * 20);
        int load = JobOf(sim, lumberjack).CarryCount;

        Core.Residents.ResidentRules.Leave(sim, lumberjack);

        Assert.Equal(load, sim.World!.Ledger.Get(LedgerFlow.Lost, Item(sim, "wood")));
    }
}
