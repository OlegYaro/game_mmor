using Core.Pathfinding;
using Core.Tests.WorldGen;
using Core.WorldGen;

namespace Core.Tests.Pathfinding;

public sealed class PassabilityGridTests
{
    private const int E = 0;
    private const int S = 1;
    private const int W = 2;
    private const int N = 3;
    private const int SE = 4;
    private const int SW = 5;
    private const int NW = 6;
    private const int NE = 7;

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void WaterOceanLakeAndRiverAreImpassable()
    {
        TestMap map = PathTestMaps.Parse(
            ".~or",
            "....",
            "....",
            "....");

        Assert.True(map.Grid.IsPassable(0, 0));
        Assert.False(map.Grid.IsPassable(1, 0));
        Assert.False(map.Grid.IsPassable(2, 0));
        Assert.False(map.Grid.IsPassable(3, 0));
        Assert.False(map.Grid.CanStep(0, 0, E));
        Assert.False(map.Grid.CanStep(1, 1, N));
        Assert.Equal(0, map.Grid.StepMask(2, 0));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void HeightStepOfOneIsAllowedAndTwoIsACliffBothWays()
    {
        TestMap map = PathTestMaps.Parse(
            "0120",
            "0000",
            "3000",
            "0000");

        Assert.True(map.Grid.CanStep(0, 0, E));
        Assert.True(map.Grid.CanStep(1, 0, E));
        Assert.True(map.Grid.CanStep(2, 0, W));
        Assert.False(map.Grid.CanStep(2, 0, E)); // 2 -> 0
        Assert.False(map.Grid.CanStep(3, 0, W)); // 0 -> 2
        Assert.False(map.Grid.CanStep(0, 1, S)); // 0 -> 3
        Assert.False(map.Grid.CanStep(0, 2, N)); // 3 -> 0
        Assert.Equal(0, map.Grid.StepMask(0, 2)); // a lone peak
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void DiagonalCannotCutTheCornerOfABlockedTile()
    {
        TestMap map = PathTestMaps.Parse(
            ".#.",
            "...",
            "..~");

        Assert.False(map.Grid.CanStep(0, 0, SE)); // side tile blocked
        Assert.False(map.Grid.CanStep(2, 0, SW));
        Assert.False(map.Grid.CanStep(1, 1, NE));
        Assert.False(map.Grid.CanStep(1, 1, NW));
        Assert.True(map.Grid.CanStep(1, 1, SW));
        Assert.False(map.Grid.CanStep(1, 1, SE)); // the target is water
        Assert.False(map.Grid.CanStep(2, 1, SW)); // a side tile is water
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void DiagonalCannotCutTheCornerOfACliff()
    {
        // Side tile (1, 0) is a cliff for the start: no diagonal past it.
        TestMap sideCliff = PathTestMaps.Parse(
            "02",
            "11");
        Assert.False(sideCliff.Grid.CanStep(0, 0, SE));
        Assert.True(sideCliff.Grid.CanStep(0, 0, S));

        // Every side step is one level, but the diagonal itself climbs two.
        TestMap diagonalCliff = PathTestMaps.Parse(
            "01",
            "12");
        Assert.True(diagonalCliff.Grid.CanStep(0, 0, E));
        Assert.True(diagonalCliff.Grid.CanStep(1, 0, S));
        Assert.False(diagonalCliff.Grid.CanStep(0, 0, SE));
        Assert.False(diagonalCliff.Grid.CanStep(1, 1, NW));

        // Side tiles at another level than both ends are fine within one step.
        TestMap ridge = PathTestMaps.Parse(
            "12",
            "21");
        Assert.True(ridge.Grid.CanStep(0, 0, SE));
        Assert.True(ridge.Grid.CanStep(1, 0, SW));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void MapEdgesAreImpassable()
    {
        TestMap map = PathTestMaps.Open(3);

        Assert.Equal((1 << E) | (1 << S) | (1 << SE), map.Grid.StepMask(0, 0));
        Assert.Equal((1 << W) | (1 << N) | (1 << NW), map.Grid.StepMask(2, 2));
        Assert.Equal(0xFF, map.Grid.StepMask(1, 1));
        Assert.Equal(0b1111, map.Grid.StraightStepMask(1, 1));
        Assert.False(map.Grid.IsPassable(-1, 0));
        Assert.False(map.Grid.IsPassable(0, 3));
        Assert.Equal(0, map.Grid.StepMask(5, 5));
        Assert.Equal(0, map.Grid.StraightStepMask(-1, 1));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void BlockerRemovesPassableTiles()
    {
        TestMap map = PathTestMaps.Open(4);
        Assert.True(map.Grid.IsPassable(2, 2));

        map.Blocker.Set(2, 2, true);

        Assert.False(map.Grid.IsPassable(2, 2));
        Assert.False(map.Grid.IsPassable(map.Index(2, 2)));
        Assert.False(map.Grid.CanStep(1, 2, E));
        Assert.False(map.Grid.CanStep(1, 1, SE));
        Assert.False(map.Grid.CanStep(2, 1, SE)); // the blocked tile is a side tile
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void IndexHelpersAndArgumentChecks()
    {
        TestMap map = PathTestMaps.Open(5);
        PassabilityGrid grid = map.Grid;

        Assert.Equal(5, grid.Size);
        Assert.Equal(25, grid.TileCount);
        Assert.Equal(13, grid.Index(3, 2));
        Assert.Equal(3, grid.XOf(13));
        Assert.Equal(2, grid.YOf(13));
        Assert.True(grid.ContainsIndex(24));
        Assert.False(grid.ContainsIndex(25));
        Assert.False(grid.ContainsIndex(-1));
        Assert.False(grid.IsPassable(25));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Index(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.CanStep(1, 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.CanStep(1, 1, -1));
        Assert.True(PassabilityGrid.IsDiagonal(NE));
        Assert.False(PassabilityGrid.IsDiagonal(N));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void DirectionsMatchTheTerrainOrder()
    {
        for (int d = 0; d < PassabilityGrid.DirectionCount; d++)
        {
            Assert.Equal(TerrainMap.Directions[d], (PassabilityGrid.DirectionX(d), PassabilityGrid.DirectionY(d)));
        }
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void CostsComeFromTheCostLayerAndAreChecked()
    {
        var cost = new TestCost(3, 5);
        TestMap map = PathTestMaps.Parse(cost, "...", "...", "...");
        cost.Set(1, 1, 5);
        cost.Set(2, 2, 30);

        Assert.Equal(5, map.Grid.MinStraightCost);
        Assert.Equal(7, map.Grid.MinDiagonalCost);
        Assert.Equal(5, map.Grid.StepCost(1, 1, diagonal: false));
        Assert.Equal(7, map.Grid.StepCost(1, 1, diagonal: true));
        Assert.Equal(42, map.Grid.StepCost(2, 2, diagonal: true));
        Assert.Equal(10, map.Grid.TileCost(0, 0));
        Assert.Equal((2 * 7) + 5, map.Grid.Heuristic(0, 0, 3, 2));

        cost.Set(0, 0, 4);
        Assert.Throws<InvalidOperationException>(() => map.Grid.TileCost(0, 0));
        cost.Set(0, 0, PathCosts.MaxTileCost + 1);
        Assert.Throws<InvalidOperationException>(() => map.Grid.TileCost(0, 0));

        TerrainMap terrain = map.Terrain;
        Assert.Throws<ArgumentOutOfRangeException>(() => new PassabilityGrid(terrain, null, new TestCost(3, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PassabilityGrid(terrain, null, new TestCost(3, PathCosts.MaxTileCost + 1)));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void DefaultLayersAreUniform()
    {
        TestMap map = PathTestMaps.Open(2);
        var grid = new PassabilityGrid(map.Terrain);

        Assert.Equal(PathCosts.Straight, grid.TileCost(1, 1));
        Assert.Equal(PathCosts.DiagonalBase, grid.StepCost(1, 1, diagonal: true));
        Assert.False(NoTileBlocker.Instance.IsBlocked(0, 0));
        Assert.Equal(PathCosts.Straight, UniformTileCost.Instance.GetCost(0, 0));
        Assert.Equal(PathCosts.Straight, UniformTileCost.Instance.MinCost);
        Assert.Equal(14, PathCosts.Diagonal(10));
        Assert.Equal(1, PathCosts.Diagonal(1));
        Assert.Equal((3 * 14) + (2 * 10), PathCosts.Octile(-5, 3, 10, 14));
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void NoAllowedStepOnAGeneratedMapCrossesWaterOrACliff()
    {
        TerrainMap terrain = WorldGenTestData.Golden(0);
        var grid = new PassabilityGrid(terrain);
        int allowed = 0;
        for (int y = 0; y < terrain.Size; y++)
        {
            for (int x = 0; x < terrain.Size; x++)
            {
                int mask = grid.StepMask(x, y);
                for (int d = 0; d < PassabilityGrid.DirectionCount; d++)
                {
                    if ((mask & (1 << d)) == 0)
                    {
                        continue;
                    }

                    allowed++;
                    int nx = x + PassabilityGrid.DirectionX(d);
                    int ny = y + PassabilityGrid.DirectionY(d);
                    Assert.False(terrain.IsWater(nx, ny) || terrain.IsRiver(nx, ny) || terrain.IsWater(x, y) || terrain.IsRiver(x, y));
                    Assert.InRange(Math.Abs(terrain.GetHeight(nx, ny) - terrain.GetHeight(x, y)), 0, PassabilityGrid.MaxClimb);
                }
            }
        }

        Assert.True(allowed > terrain.TileCount, "A generated map should be mostly walkable.");
    }
}
