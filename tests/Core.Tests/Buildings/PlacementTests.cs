using Core.Buildings;
using Core.Entities;
using Core.Fog;
using Core.Sim;
using Core.Tests.Heroes;
using Core.Tests.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Buildings;

/// <summary>The one placement check (FR-BUILD-01, FR-BUILD-06): every refusal reason and the valid case.</summary>
[Collection(BuildingTestWorld.Collection)]
public sealed class PlacementTests
{
    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    public void WithoutAWorldNothingCanBePlaced()
    {
        var core = new Simulation(1, SimTestData.Real);

        Assert.Equal(PlacementResult.Refuse(BuildResult.NoWorld), Construction.CheckPlacement(core, "campfire", 0, 0, 0));
    }

    [Theory]
    [Trait("Req", "FR-BUILD-01")]
    [InlineData("castle")]
    [InlineData("wood")]
    [InlineData("")]
    public void UnknownTypesAreRefused(string id)
    {
        Simulation sim = NewGame();
        (int x, int y) = HeroTestWorld.HeroTile(sim);

        Assert.Equal(BuildResult.UnknownBuilding, Construction.CheckPlacement(sim, id, x, y, 0).Reason);
    }

    [Theory]
    [Trait("Req", "FR-BUILD-01")]
    [InlineData("campfire", 1)]
    [InlineData("house", 4)]
    [InlineData("house", -1)]
    public void RotationMustBeAQuarterTurnTheTypeAllows(string id, int rotation)
    {
        Simulation sim = NewGame();
        (int x, int y) = HeroTestWorld.HeroTile(sim);

        Assert.Equal(BuildResult.InvalidRotation, Construction.CheckPlacement(sim, id, x, y, rotation).Reason);
    }

    [Theory]
    [Trait("Req", "FR-BUILD-01")]
    [InlineData(63, 10)]
    [InlineData(10, 63)]
    [InlineData(-1, 10)]
    [InlineData(10, int.MaxValue)]
    public void TheFootprintMustBeOnTheMap(int x, int y)
    {
        Simulation sim = NewGame();

        PlacementResult result = Construction.CheckPlacement(sim, "house", x, y, 0);

        Assert.Equal(new PlacementResult(BuildResult.OutOfMap, x, y), result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    [Trait("Req", "FR-WORLD-06")]
    public void EveryTileMustBeExplored()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        // A 2x2 area whose bottom-right tile is the first unknown one in its row after explored tiles.
        (int x, int y) = FindTile(sim, (tx, ty) => tx > 0 && ty > 0 && world.Fog.GetState(tx, ty) == FogState.Unknown
            && world.Fog.GetState(tx - 1, ty - 1) != FogState.Unknown);

        PlacementResult result = Construction.CheckPlacement(sim, "campfire", x - 1, y - 1, 0);
        Assert.True(result.IsOk || result.Reason != BuildResult.NotExplored); // the explored corner alone is fine
        Assert.Equal(new PlacementResult(BuildResult.NotExplored, x, y), Construction.CheckPlacement(sim, "campfire", x, y, 0));
        Assert.Equal(BuildResult.NotExplored, Construction.CheckPlacement(sim, "house", x - 1, y - 1, 0).Reason);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void OnlyACenterCanBePlacedBeforeTheCenterAndOnlyOneCenter()
    {
        Simulation sim = NewGame();
        (int hx, int hy) = HeroTestWorld.HeroTile(sim);
        GiveCost(sim, "house", 1);

        Assert.Equal(PlacementResult.Refuse(BuildResult.NoCenter), Construction.CheckPlacement(sim, "house", hx, hy, 0));
        Assert.Null(Construction.GetTerritory(sim));

        (int cx, int cy) = FindSpot(sim, "campfire");
        Place(sim, "campfire", cx, cy);
        GiveCost(sim, "campfire", 1);

        Assert.Equal(PlacementResult.Refuse(BuildResult.CenterExists), Construction.CheckPlacement(sim, "campfire", hx, hy, 0));
        Assert.Equal(new Territory(cx, cy, 48), Construction.GetTerritory(sim));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void OtherBuildingsMustStandInsideTheTerritory()
    {
        Simulation sim = RevealedWithCenterAt((3, 3), out (int X, int Y) center);
        Territory territory = Construction.GetTerritory(sim)!.Value;
        GiveCost(sim, "house", 1);
        // A house straddling the territory edge on the diagonal from the center: its top-left tile is inside, its far corner is not.
        int k = 0;
        while (territory.Contains(center.X + k + 1, center.Y + k + 1))
        {
            k++;
        }

        PlacementResult straddling = Construction.CheckPlacement(sim, "house", center.X + k, center.Y + k, 0);
        PlacementResult far = Construction.CheckPlacement(sim, "house", 60, 60, 0);

        Assert.True(territory.Contains(center.X + k, center.Y + k));
        Assert.Equal(BuildResult.OutsideTerritory, straddling.Reason);
        Assert.False(territory.Contains(straddling.TileX, straddling.TileY));
        Assert.Equal(new PlacementResult(BuildResult.OutsideTerritory, 60, 60), far);
        Assert.False(Construction.IsInTerritory(sim, 60, 60, 1, 1));
        Assert.True(Construction.IsInTerritory(sim, center.X, center.Y, 1, 1));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    public void WaterRiversSlopesNodesBuildingsAndTheHeroBlockAPlace()
    {
        Simulation sim = RevealedWithCenterAt((32, 32), out (int X, int Y) center, Extended);
        GameWorld world = sim.World!;
        TerrainMap terrain = world.Map.Terrain;
        Give(sim, "wood", 10);
        bool Land(int x, int y) => !terrain.IsWater(x, y) && !terrain.IsRiver(x, y);
        bool InTerritory(int x, int y) => Construction.IsInTerritory(sim, x, y, 2, 2);

        (int wx, int wy) = FindTile(sim, (x, y) => !Land(x, y) && InTerritory(x, y));
        Assert.Equal(new PlacementResult(BuildResult.Water, wx, wy), Construction.CheckPlacement(sim, "test_depot", wx, wy, 0));

        (int sx, int sy) = FindTile(sim, (x, y) => x + 1 < world.Size && y + 1 < world.Size && InTerritory(x, y)
            && FootprintTiles(x, y, 2, 2).All(t => Land(t.X, t.Y)) && FootprintTiles(x, y, 2, 2).Select(t => terrain.GetHeight(t.X, t.Y)).Distinct().Count() > 1);
        Assert.Equal(BuildResult.NotFlat, Construction.CheckPlacement(sim, "house", sx, sy, 0).Reason);

        (int tx, int ty) = FindTile(sim, (x, y) => world.Tiles.GetEffectiveResource(x, y) == ResourceKind.Tree && Land(x, y) && InTerritory(x, y));
        Assert.Equal(new PlacementResult(BuildResult.ResourceInTheWay, tx, ty), Construction.CheckPlacement(sim, "test_depot", tx, ty, 0));

        Assert.Equal(new PlacementResult(BuildResult.Occupied, center.X, center.Y), Construction.CheckPlacement(sim, "test_depot", center.X, center.Y, 0));

        (int hx, int hy) = HeroTestWorld.HeroTile(sim);
        Assert.Equal(new PlacementResult(BuildResult.UnitInTheWay, hx, hy), Construction.CheckPlacement(sim, "test_depot", hx, hy, 0));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    public void TheTileTheHeroStepsOntoIsBlockedToo()
    {
        Simulation sim = RevealedWithCenterAt((32, 32), out _, Extended);
        (int gx, int gy) = HeroTestWorld.StraightTarget(sim, 4);
        sim.Enqueue(new MoveHeroCommand(gx, gy));
        sim.Step();
        Core.Heroes.Mover mover = HeroTestWorld.MoverOf(sim);
        Assert.True(mover.StepProgress > 0);
        int next = mover.NextTile;
        Give(sim, "wood", 3);

        PlacementResult result = Construction.CheckPlacement(sim, "test_depot", next % 64, next / 64, 0);

        Assert.Equal(new PlacementResult(BuildResult.UnitInTheWay, next % 64, next / 64), result);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    public void RequirementsOfLevelOneAreChecked()
    {
        Simulation sim = RevealedWithCenterAt((32, 32), out _, Extended);
        Give(sim, "wood", 10);
        (int x, int y) = FindSpot(sim, "test_depot");

        Assert.Equal(PlacementResult.Refuse(BuildResult.RequirementNotMet), Construction.CheckPlacement(sim, "test_shrine", x, y, 0)); // village era
        Assert.Equal(PlacementResult.Refuse(BuildResult.RequirementNotMet), Construction.CheckPlacement(sim, "test_forge", x, y, 0)); // blacksmith
        Assert.Equal(PlacementResult.Valid, Construction.CheckPlacement(sim, "test_depot", x, y, 0));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    public void MaterialsOfLevelOneMustBeAvailable()
    {
        Simulation sim = RevealedWithCenterAt((32, 32), out _);
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");
        Assert.True(Construction.CheckPlacement(sim, "house", x, y, 0).IsOk);

        Assert.True(sim.Content.TryGetId("wood", out Core.Content.DefId wood));
        ref Core.Heroes.Inventory backpack = ref sim.World!.Inventories.Get(sim.World.HeroEntity);
        backpack = backpack.Remove(wood, 1, out _);

        Assert.Equal(PlacementResult.Refuse(BuildResult.NotEnoughMaterials), Construction.CheckPlacement(sim, "house", x, y, 0));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    public void RotationSwapsTheFootprint()
    {
        Simulation sim = RevealedWithCenterAt((32, 32), out _);
        Assert.True(sim.Content.TryGet("storage", out Core.Content.BuildingDef storage));

        Assert.Equal((3, 2), Construction.FootprintOf(storage, 0));
        Assert.Equal((2, 3), Construction.FootprintOf(storage, 1));
        Assert.Equal((3, 2), Construction.FootprintOf(storage, 2));
        Assert.Equal((2, 3), Construction.FootprintOf(storage, 3));
        Assert.Equal(new PlacementResult(BuildResult.OutOfMap, 62, 10), Construction.CheckPlacement(sim, "storage", 62, 10, 0));
        Assert.NotEqual(BuildResult.OutOfMap, Construction.CheckPlacement(sim, "storage", 62, 10, 1).Reason);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void MoveChecksTheBuildingItsStateAndThePlace()
    {
        Simulation sim = RevealedWithCenterAt((32, 32), out (int X, int Y) center, Extended);
        GiveCost(sim, "test_depot", 1);
        (int x, int y) = FindSpot(sim, "test_depot");
        EntityId depot = Place(sim, "test_depot", x, y, give: false);

        Assert.Equal(PlacementResult.Refuse(BuildResult.NotABuilding), Construction.CheckPlacement(sim, "test_depot", 1, 1, 0, sim.World!.HeroEntity));
        Assert.Equal(PlacementResult.Refuse(BuildResult.NotABuilding), Construction.CheckPlacement(sim, "campfire", 1, 1, 0, depot));
        Assert.Equal(PlacementResult.Refuse(BuildResult.Busy), Construction.CheckPlacement(sim, "test_depot", center.X, center.Y, 0, depot));

        StepUntilActive(sim, depot);
        Assert.Equal(PlacementResult.Refuse(BuildResult.SamePlace), Construction.CheckPlacement(sim, "test_depot", x, y, 0, depot));
        Assert.Equal(new PlacementResult(BuildResult.Occupied, center.X, center.Y), Construction.CheckPlacement(sim, "test_depot", center.X, center.Y, 0, depot));

        // A move needs neither materials nor requirements: the backpack is empty now.
        Assert.Equal(0, Count(sim, "wood"));
        (int nx, int ny) = FindSpot(sim, "test_depot", moving: depot, near: (x, y), minDistance: 1);
        Assert.Equal(PlacementResult.Valid, Construction.CheckPlacement(sim, "test_depot", nx, ny, 0, depot));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void AMovingBuildingsOwnTilesAreFreeForItself()
    {
        Simulation sim = RevealedWithCenterAt((32, 32), out _);
        GiveCost(sim, "house", 1);
        GameWorld world = sim.World!;
        // A flat, clear 3x2 strip so the house can shift by one tile onto its own tiles.
        (int x, int y) = FindTile(sim, (tx, ty) => tx + 3 <= world.Size && ty + 2 <= world.Size && Construction.CheckPlacement(sim, "house", tx, ty, 0).IsOk
            && Construction.CheckPlacement(sim, "house", tx + 1, ty, 0).IsOk);
        EntityId house = Place(sim, "house", x, y, give: false);
        StepUntilActive(sim, house);

        Assert.Equal(PlacementResult.Valid, Construction.CheckPlacement(sim, "house", x + 1, y, 0, house));
        Assert.Equal(new PlacementResult(BuildResult.Occupied, x + 1, y), Construction.CheckPlacement(sim, "house", x + 1, y, 0));
    }

    /// <summary>A new game with the whole map explored and a campfire built as near to <paramref name="near"/> as possible.</summary>
    internal static Simulation RevealedWithCenterAt((int X, int Y) near, out (int X, int Y) center, Core.Content.ContentCatalog? content = null)
    {
        Simulation sim = NewGame(content);
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        GiveCost(sim, "campfire", 1);
        center = FindSpot(sim, "campfire", minDistance: 0, near: near);
        EntityId campfire = Place(sim, "campfire", center.X, center.Y, give: false);
        StepUntilActive(sim, campfire);
        return sim;
    }
}
