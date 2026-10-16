using Core.Buildings;
using Core.Entities;
using Core.Residents;
using Core.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Residents;

/// <summary>
/// Housing (FR-NPC-01, FR-BUILD-09, FR-BUILD-12, FR-BUILD-13): the nearest house with a free place, residents staying
/// through an upgrade and a move, eviction on demolition into other houses or homelessness, and homeless residents
/// moving in as soon as a place frees up.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class HousingTests
{
    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void ResidentsMoveIntoTheNearestHouseWithAFreePlaceThenTheNext()
    {
        Simulation sim = NewResidentGame();
        EntityId[] houses = Settle(sim, houses: 2);
        (int cx, int cy) = CenterTile(sim);
        EntityId nearest = Housing.FindHome(sim, cx, cy);
        EntityId other = nearest == houses[0] ? houses[1] : houses[0];
        Assert.Contains(nearest, houses);

        EntityId[] residents = [Spawn(sim), Spawn(sim), Spawn(sim), Spawn(sim), Spawn(sim)];

        Assert.Equal([nearest, nearest, other, other, EntityId.None], residents.Select(r => ResidentOf(sim, r).Home));
        Assert.Equal(2, Housing.CountOccupants(sim.World!, nearest));
        Assert.Equal(0, Housing.FreePlaces(sim, nearest));
        Assert.Equal(EntityId.None, Housing.FindHome(sim, cx, cy));
        Assert.Equal(0, Housing.FreePlaces(sim, Construction.FindCenter(sim))); // not a house
        Assert.Equal(0, Housing.FreePlaces(sim, new EntityId(60, 1)));
        Assert.Equal(EntityId.None, Housing.FindHome(new Simulation(1, Core.Tests.Sim.SimTestData.Real), 0, 0));
        Assert.Equal(0, Housing.TotalFreePlaces(new Simulation(1, Core.Tests.Sim.SimTestData.Real)));
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-BUILD-09")]
    public void ResidentsStayThroughAnUpgradeAndTheNewLevelAddsPlaces()
    {
        Simulation sim = NewResidentGame();
        EntityId house = Settle(sim, houses: 1)[0];
        EntityId a = Spawn(sim);
        EntityId b = Spawn(sim);
        Assert.Equal(0, Housing.FreePlaces(sim, house));

        GiveCost(sim, "house", 2);
        Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(house)).Result);
        Assert.Equal(BuildingState.Upgrading, BuildingOf(sim, house).State);
        Assert.Equal((house, house), (ResidentOf(sim, a).Home, ResidentOf(sim, b).Home));
        Assert.Equal(0, Housing.FreePlaces(sim, house)); // a house under work offers no places
        EntityId waiting = Spawn(sim);
        Assert.Equal(EntityId.None, ResidentOf(sim, waiting).Home);

        StepUntilActive(sim, house);
        sim.Step();
        Assert.Equal(3, Housing.CapacityOf(sim.Content, BuildingOf(sim, house)));
        Assert.Equal(house, ResidentOf(sim, waiting).Home); // the homeless resident took the new place
        Assert.Equal(0, Housing.FreePlaces(sim, house));
        Assert.Equal(ResidentEventKind.Housed, sim.World!.ResidentEvents[^1].Kind);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-BUILD-13")]
    public void ResidentsKeepTheirHomeWhenItIsMoved()
    {
        Simulation sim = NewResidentGame();
        EntityId house = Settle(sim, houses: 1)[0];
        EntityId resident = Spawn(sim);
        Building placed = BuildingOf(sim, house);

        (int x, int y) = FindSpot(sim, "house", rotation: 1, minDistance: 4, near: (placed.X, placed.Y), moving: house);
        Assert.Equal(BuildResult.Ok, Order(sim, new MoveBuildingCommand(house, x, y, 1)).Result);
        Assert.Equal(0, Housing.FreePlaces(sim, house));
        StepUntilActive(sim, house);

        Assert.Equal((x, y), (BuildingOf(sim, house).X, BuildingOf(sim, house).Y));
        Assert.Equal(house, ResidentOf(sim, resident).Home);
        Assert.Equal(1, Housing.FreePlaces(sim, house));
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "FR-BUILD-12")]
    public void DemolishingAHouseMovesItsResidentsIntoOtherHousesOrLeavesThemHomeless()
    {
        Simulation sim = NewResidentGame();
        EntityId[] houses = Settle(sim, houses: 2);
        EntityId[] residents = [Spawn(sim), Spawn(sim), Spawn(sim)];
        EntityId full = ResidentOf(sim, residents[0]).Home;
        EntityId half = full == houses[0] ? houses[1] : houses[0];
        Assert.Equal(half, ResidentOf(sim, residents[2]).Home);

        Assert.Equal(BuildResult.Ok, Order(sim, new DemolishBuildingCommand(full)).Result);

        // One place was left in the other house: the first evicted resident takes it, the second stays homeless.
        Assert.Equal(half, ResidentOf(sim, residents[0]).Home);
        Assert.Equal(EntityId.None, ResidentOf(sim, residents[1]).Home);
        Assert.Equal(1, Housing.CountHomeless(sim.World!));
        ResidentEventKind[] kinds = [.. sim.World!.ResidentEvents.TakeLast(3).Select(e => e.Kind)];
        Assert.Equal([ResidentEventKind.Evicted, ResidentEventKind.Evicted, ResidentEventKind.Housed], kinds);
        Assert.Equal(0, Arrivals.ComputeOdds(sim).ChancePermille);

        // Demolishing the last house leaves everyone homeless; nobody can arrive.
        Assert.Equal(BuildResult.Ok, Order(sim, new DemolishBuildingCommand(half)).Result);
        Assert.Equal(3, Housing.CountHomeless(sim.World));
        sim.Step(5);
        Assert.Equal(3, Housing.CountHomeless(sim.World));
        Assert.Equal(0, Arrivals.ComputeOdds(sim).FreePlaces);
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void ResidentEventsKeepOnlyTheLatest()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 0);
        for (int i = 0; i < GameWorld.ResidentEventCapacity + 2; i++)
        {
            sim.Enqueue(new SpawnResidentCommand());
        }

        sim.Step();

        Assert.Equal(GameWorld.ResidentEventCapacity, sim.World!.ResidentEvents.Count);
        Assert.Equal(GameWorld.ResidentEventCapacity + 2, sim.World.LastResidentEventSequence);
        Assert.Equal(3, sim.World.ResidentEvents[0].Sequence);
    }
}
