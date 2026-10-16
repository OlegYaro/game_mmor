using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Buildings;

/// <summary>Territory and era (FR-BUILD-06, A1) and the refund function (FR-BUILD-12, E5).</summary>
[Collection(BuildingTestWorld.Collection)]
public sealed class TerritoryAndRefundTests
{
    [Theory]
    [Trait("Req", "FR-BUILD-06")]
    [InlineData(0, 0, true)]
    [InlineData(48, 0, true)]
    [InlineData(0, -48, true)]
    [InlineData(49, 0, false)]
    [InlineData(33, 34, true)]   // 1089 + 1156 = 2245 ≤ 2304
    [InlineData(34, 34, false)]  // 2312 > 2304
    [InlineData(27, -39, true)]  // 729 + 1521 = 2250
    [InlineData(-28, 39, false)] // 784 + 1521 = 2305
    public void TerritoryIsAnIntegerDisc(int dx, int dy, bool inside)
    {
        var territory = new Territory(100, 100, 48);

        Assert.Equal(inside, territory.Contains(100 + dx, 100 + dy));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void AnAreaIsInsideWhenItsCornersAre()
    {
        var territory = new Territory(10, 10, 3);

        Assert.True(territory.ContainsArea(9, 9, 3, 3));
        Assert.True(territory.ContainsArea(10, 10, 3, 3));  // far corner 12,12: 8 ≤ 9
        Assert.False(territory.ContainsArea(10, 10, 4, 4)); // far corner 13,13: 18 > 9
        Assert.True(territory.ContainsArea(7, 10, 1, 1));   // on the edge: 9 ≤ 9
        Assert.False(territory.ContainsArea(6, 10, 1, 1));
        Assert.True(new Territory(0, 0, 0).ContainsArea(0, 0, 1, 1));
        Assert.False(new Territory(0, 0, 0).ContainsArea(0, 0, 2, 1));
    }

    [Theory]
    [Trait("Req", "FR-BUILD-06")]
    [InlineData(0, "camp")]
    [InlineData(1, "camp")]
    [InlineData(3, "camp")]
    [InlineData(4, "village")]
    [InlineData(15, "village")]
    [InlineData(16, "town")]
    [InlineData(39, "town")]
    [InlineData(40, "fortress")]
    [InlineData(100000, "fortress")]
    public void TheEraFollowsThePopulation(int population, string era)
    {
        Assert.Equal(era, Construction.EraFor(SimTestData.Real, population).Id);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void InM2TheSettlementIsACampWithTheCampRadius()
    {
        Simulation sim = NewGame();

        Assert.Equal(0, Construction.SettlementPopulation(sim));
        Assert.Equal("camp", Construction.CurrentEra(sim).Id);
        Assert.Equal(EntityId.None, Construction.FindCenter(sim));
        Assert.Null(Construction.GetTerritory(sim));
        Assert.False(Construction.IsInTerritory(sim, 1, 1, 1, 1));

        EntityId campfire = Build(sim, "campfire");
        Building center = BuildingOf(sim, campfire);
        Assert.Equal(campfire, Construction.FindCenter(sim));
        Assert.Equal(new Territory(center.X, center.Y, SimTestData.Real.Eras[0].TerritoryRadius), Construction.GetTerritory(sim));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void TheCenterTileIsTheMiddleOfTheFootprint()
    {
        Simulation sim = NewGame();
        Assert.True(sim.Content.TryGetId("storage", out DefId storage));

        Assert.Equal(new Territory(11, 20, 48), Construction.TerritoryAt(sim, storage, 10, 20, 0)); // 3x2: middle column 11
        Assert.Equal(new Territory(10, 21, 48), Construction.TerritoryAt(sim, storage, 10, 20, 1)); // 2x3: middle row 21
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void DemolishRefundIsSeventyFivePercentOfAllPaidLevelsRoundedDownPerItem()
    {
        Assert.True(SimTestData.Real.TryGet("house", out BuildingDef house));
        int permille = SimTestData.Real.Construction.DemolishRefundPermille;
        Assert.Equal(750, permille);

        Assert.Equal([new ItemAmountDef("wood", 15)], Construction.Refund(house, 1, 1, permille));               // 20 → 15
        Assert.Equal([new ItemAmountDef("wood", 37), new ItemAmountDef("stone", 7)], Construction.Refund(house, 1, 2, permille)); // 50 → 37.5, 10 → 7.5
        Assert.Equal([new ItemAmountDef("wood", 67), new ItemAmountDef("stone", 22)], Construction.Refund(house, 1, 3, permille)); // 90 → 67.5, 30 → 22.5
        Assert.Equal([new ItemAmountDef("wood", 30), new ItemAmountDef("stone", 10)], Construction.Refund(house, 2, 2, 1000));
        Assert.Empty(Construction.Refund(house, 1, 3, 0));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void RefundRoundsTheSumNotEachLevelAndDropsZeroes()
    {
        var def = new BuildingDef("test", "housing", new BuildingFootprint(1, 1), false, "flat", false, true,
        [
            new BuildingLevelDef([new ItemAmountDef("wood", 1), new ItemAmountDef("stone", 1)], 10, 5, []),
            new BuildingLevelDef([new ItemAmountDef("wood", 1)], 10, 5, []),
            new BuildingLevelDef([new ItemAmountDef("berries", 3), new ItemAmountDef("wood", 1)], 10, 5, []),
        ]);

        // Per level 750 ‰ of 1 wood would be 0 each time; of the sum (3) it is 2.
        Assert.Equal([new ItemAmountDef("wood", 2), new ItemAmountDef("berries", 2)], Construction.Refund(def, 1, 3, 750));
        Assert.Equal([new ItemAmountDef("wood", 1)], Construction.Refund(def, 1, 2, 999));  // 2 × 0.999 = 1.998
        Assert.Equal([new ItemAmountDef("wood", 2), new ItemAmountDef("stone", 1)], Construction.Refund(def, 1, 2, 1000));
        Assert.Equal([new ItemAmountDef("berries", 3), new ItemAmountDef("wood", 1)], Construction.Refund(def, 3, 3, 1000));
    }

    [Theory]
    [Trait("Req", "FR-BUILD-12")]
    [InlineData(0, 1, 750)]
    [InlineData(1, 4, 750)]
    [InlineData(2, 1, 750)]
    [InlineData(1, 1, -1)]
    [InlineData(1, 1, 1001)]
    public void RefundRejectsInvalidArguments(int from, int to, int permille)
    {
        Assert.True(SimTestData.Real.TryGet("house", out BuildingDef house));

        Assert.Throws<ArgumentOutOfRangeException>(() => Construction.Refund(house, from, to, permille));
    }

    [Theory]
    [Trait("Req", "FR-BUILD-02")]
    [InlineData(BuildingState.Constructing, 1, 100, 0)]
    [InlineData(BuildingState.Constructing, 1, 76, 240)]
    [InlineData(BuildingState.Constructing, 1, 75, 250)]
    [InlineData(BuildingState.Constructing, 1, 51, 490)]
    [InlineData(BuildingState.Constructing, 1, 50, 500)]
    [InlineData(BuildingState.Constructing, 1, 21, 790)]
    [InlineData(BuildingState.Constructing, 1, 20, 800)]
    [InlineData(BuildingState.Constructing, 1, 1, 990)]
    [InlineData(BuildingState.Upgrading, 3, 60, 500)]   // level 3 takes 120 ticks
    [InlineData(BuildingState.Moving, 1, 22, 333)]      // 33 move ticks: 11 elapsed
    [InlineData(BuildingState.Moving, 3, 1, 975)]       // 40 move ticks
    [InlineData(BuildingState.Active, 2, 0, 1000)]
    public void ProgressIsPermilleOfTheElapsedTicksRoundedDown(BuildingState state, int level, int left, int permille)
    {
        Assert.True(SimTestData.Real.TryGetId("house", out DefId house));
        var building = new Building(house, level, state, left, 0, 0, 0, -1, -1, -1, Building.PlayerSettlement);

        Assert.Equal(permille, Construction.ProgressPermille(SimTestData.Real, building));
    }
}
