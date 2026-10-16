using Core.Buildings;
using Core.Entities;
using Core.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Buildings;

/// <summary>The read-only answers the game's inspector uses: they must match what the orders then do (FR-BUILD-09, FR-BUILD-12).</summary>
[Collection(BuildingTestWorld.Collection)]
public sealed class OrderChecksTests
{
    [Fact]
    [Trait("Req", "FR-BUILD-09")]
    public void CheckUpgradeAnswersLikeTheOrder()
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId house = Build(sim, "house");

        Assert.Equal(BuildResult.NotEnoughMaterials, Construction.CheckUpgrade(sim, house));
        Assert.Equal(BuildResult.NotABuilding, Construction.CheckUpgrade(sim, sim.World!.HeroEntity));
        GiveCost(sim, "house", 2);
        Assert.Equal(BuildResult.Ok, Construction.CheckUpgrade(sim, house));
        Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(house)).Result);
        Assert.Equal(BuildResult.Busy, Construction.CheckUpgrade(sim, house));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-12")]
    public void CheckDemolishRefusesTheCenterAndTheLastStorage()
    {
        Simulation sim = NewGame();
        EntityId fire = Build(sim, "campfire");
        EntityId first = Build(sim, "storage");

        Assert.Equal(BuildResult.CenterNotRemovable, Construction.CheckDemolish(sim, fire));
        Assert.Equal(BuildResult.LastStorage, Construction.CheckDemolish(sim, first));
        EntityId second = Build(sim, "storage");
        Assert.Equal(BuildResult.Ok, Construction.CheckDemolish(sim, first));
        Assert.Equal(BuildResult.Ok, Construction.CheckDemolish(sim, second));
        Assert.Equal(BuildResult.Ok, Order(sim, new DemolishBuildingCommand(first)).Result);
        Assert.Equal(BuildResult.NotABuilding, Construction.CheckDemolish(sim, first));
    }
}
