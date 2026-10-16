using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Residents;

/// <summary>
/// Population and era (FR-BUILD-13, P-14): the population counts every resident, newcomers on their way included; the
/// era and the territory grow with it and never fall back, also across a save.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class EraTests
{
    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    [Trait("Req", "FR-NPC-01")]
    public void FourResidentsMakeAVillageWhoseTerritoryNeverShrinks()
    {
        Simulation sim = NewLargeGame();
        Settle(sim, houses: 1);
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        Assert.Equal(("camp", 48), (Construction.CurrentEra(sim).Id, Construction.GetTerritory(sim)!.Value.Radius));

        // A spot inside the village territory but outside the camp one.
        GiveCost(sim, "house", 1);
        (int x, int y) = FindTile(sim, (tx, ty) => Construction.CheckPlacement(sim, "house", tx, ty, 0).Reason == BuildResult.OutsideTerritory
            && new Territory(Construction.GetTerritory(sim)!.Value.CenterX, Construction.GetTerritory(sim)!.Value.CenterY, 80).ContainsArea(tx, ty, 2, 2)
            && WouldFitInVillage(sim, tx, ty));

        EntityId[] residents = [Spawn(sim), Spawn(sim), Spawn(sim)];
        Assert.Equal("camp", Construction.CurrentEra(sim).Id);
        residents = [.. residents, Spawn(sim)];

        Assert.Equal(4, Construction.SettlementPopulation(sim));
        Assert.Equal(("village", 80), (Construction.CurrentEra(sim).Id, Construction.GetTerritory(sim)!.Value.Radius));
        Assert.Equal("village", sim.Content.Get(sim.World!.ReachedEra).Id);
        EntityId outer = Place(sim, "house", x, y, give: false);

        // Every resident is gone: the population is 0, the era and the territory stay, the outer house stays inside.
        foreach (EntityId resident in residents)
        {
            sim.Enqueue(new DestroyEntityCommand(resident));
        }

        sim.Step();
        Assert.Equal(0, Construction.SettlementPopulation(sim));
        Assert.Equal(("village", 80), (Construction.CurrentEra(sim).Id, Construction.GetTerritory(sim)!.Value.Radius));
        Building building = BuildingOf(sim, outer);
        Assert.True(Construction.GetTerritory(sim)!.Value.ContainsArea(building.X, building.Y, 2, 2));

        // The reached era survives a save; a fresh settlement starts in the first era.
        SaveLoadResult loaded = SaveGame.Read(SaveGame.ToBytes(sim), SimTestData.Real);
        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Assert.Equal("village", Construction.CurrentEra(loaded.Simulation!).Id);
        Assert.Equal("camp", Construction.CurrentEra(NewResidentGame()).Id);
    }

    // The tiles of a 2×2 house at (x, y) are free land of one height (everything but the territory is fine).
    private static bool WouldFitInVillage(Simulation sim, int x, int y)
    {
        GameWorld world = sim.World!;
        int height = world.Map.Terrain.GetHeight(x, y);
        for (int ty = y; ty < y + 2; ty++)
        {
            for (int tx = x; tx < x + 2; tx++)
            {
                if (!world.Map.Terrain.Contains(tx, ty) || world.Map.Terrain.IsWater(tx, ty) || world.Map.Terrain.IsRiver(tx, ty)
                    || world.Map.Terrain.GetHeight(tx, ty) != height || world.Tiles.GetEffectiveResource(tx, ty) != Core.WorldGen.ResourceKind.None
                    || world.Tiles.IsOccupied(tx, ty))
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void TheReachedEraIsTheHigherOfTheSavedOneAndThePopulationsOne()
    {
        Simulation sim = NewResidentGame();
        Settle(sim, houses: 0);
        Assert.True(sim.Content.TryGetId("town", out DefId town));
        sim.World!.ReachedEra = town;
        Assert.Equal("town", Construction.CurrentEra(sim).Id);

        Assert.True(sim.Content.TryGetId("camp", out DefId camp));
        sim.World.ReachedEra = camp;
        for (int i = 0; i < 4; i++)
        {
            sim.Enqueue(new SpawnResidentCommand());
        }

        sim.Step();
        Assert.Equal("village", Construction.CurrentEra(sim).Id);
        Assert.Equal("village", sim.Content.Get(sim.World.ReachedEra).Id);
        Assert.Equal(0, Construction.SettlementPopulation(new Simulation(1, SimTestData.Real)));
        Assert.Equal("camp", Construction.CurrentEra(new Simulation(1, SimTestData.Real)).Id);
    }
}
