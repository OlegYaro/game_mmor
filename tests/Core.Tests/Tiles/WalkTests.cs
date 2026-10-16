using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Pathfinding;
using Core.Tests.Sim;
using Core.Tests.WorldGen;
using Core.Tiles;
using Core.WorldGen;

namespace Core.Tests.Tiles;

/// <summary>
/// Trails and roads in the tile layer (FR-WORLD-15, docs/research/roads-from-paths.md): counters grow with steps and fade
/// lazily, classes rise at the thresholds and fall with hysteresis, costs and step times follow the classes, the path
/// search prefers roads with an admissible heuristic, and the layer saves only live tiles and continues identically.
/// </summary>
public sealed class WalkTests
{
    private static readonly Lazy<WorldMap> _map = new(() => WorldGenerator.Generate(new WorldGenRequest(0xF10_4A, 128, SimTestData.Real.WorldGen)));

    private static WorldMap Map => _map.Value;

    private static RoadsBalanceDef Roads => SimTestData.Real.Roads;

    private static int Decay => Roads.DecayEveryTicks;

    private static TileChanges NewTiles(WorldMap? map = null) => new(map ?? Map, Roads);

    private static (int X, int Y) Land(int skip = 0)
    {
        for (int y = 0; y < Map.Size; y++)
        {
            for (int x = 0; x < Map.Size; x++)
            {
                if (!Map.Terrain.IsWater(x, y) && !Map.Terrain.IsRiver(x, y) && skip-- == 0)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No land on the test map.");
    }

    // Steps onto the tile until its counter reaches at least `counter`.
    private static void StepTo(TileChanges tiles, int x, int y, int counter)
    {
        while (tiles.GetWalkCounter(x, y) < counter)
        {
            tiles.Trample(x, y);
        }
    }

    private static ulong Hash(TileChanges tiles) => new StateHasher().Hash(tiles);

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    public void CountersGrowWithStepsAndFadeLazily()
    {
        TileChanges tiles = NewTiles();
        (int x, int y) = Land(100);
        tiles.AdvanceTo(1000);

        tiles.Trample(x, y);
        tiles.Trample(x, y);
        tiles.Trample(x, y);
        Assert.Equal(3 * Roads.StepIncrement, tiles.GetWalkCounter(x, y));
        Assert.Equal(1, tiles.WalkedTiles);
        Assert.Equal(RoadClass.None, tiles.GetRoadClass(x, y));

        tiles.AdvanceTo(1000 + Decay - 1);
        Assert.Equal(3 * Roads.StepIncrement, tiles.GetWalkCounter(x, y));
        tiles.AdvanceTo(1000 + Decay);
        Assert.Equal((3 * Roads.StepIncrement) - 1, tiles.GetWalkCounter(x, y));
        tiles.AdvanceTo(1000 + (3L * Roads.StepIncrement * Decay));
        Assert.Equal(0, tiles.GetWalkCounter(x, y));

        // A step on a faded tile starts from what is left; the counter never passes its cap.
        tiles.Trample(x, y);
        Assert.Equal(Roads.StepIncrement, tiles.GetWalkCounter(x, y));
        StepTo(tiles, x, y, Roads.MaxCounter);
        tiles.Trample(x, y);
        Assert.Equal(Roads.MaxCounter, tiles.GetWalkCounter(x, y));
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.GetWalkCounter(Map.Size, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.AdvanceTo(tiles.Tick - 1));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    public void ClassesRiseAtTheThresholdsAndFallWithHysteresis()
    {
        TileChanges tiles = NewTiles();
        (int x, int y) = Land(200);
        tiles.AdvanceTo(0);

        // A trail at the trail threshold; the chunk is marked for redrawing.
        StepTo(tiles, x, y, Roads.TrailThreshold - Roads.StepIncrement);
        Assert.Equal(RoadClass.None, tiles.GetRoadClass(x, y));
        long version = tiles.GetChunkVersion(x / 32, y / 32);
        tiles.Trample(x, y);
        int counter = tiles.GetWalkCounter(x, y);
        Assert.True(counter >= Roads.TrailThreshold);
        Assert.Equal(RoadClass.Trail, tiles.GetRoadClass(x, y));
        Assert.True(tiles.GetChunkVersion(x / 32, y / 32) > version);
        Assert.Equal(1, tiles.TrailTiles);

        // It stays a trail down to the keep threshold and falls on its own exactly when the counter goes below it.
        long fall = (long)(counter - Roads.TrailKeepThreshold + 1) * Decay;
        tiles.AdvanceTo(fall - 1);
        Assert.Equal(Roads.TrailKeepThreshold, tiles.GetWalkCounter(x, y));
        Assert.Equal(RoadClass.Trail, tiles.GetRoadClass(x, y));
        version = tiles.GetChunkVersion(x / 32, y / 32);
        tiles.AdvanceTo(fall);
        Assert.Equal(RoadClass.None, tiles.GetRoadClass(x, y));
        Assert.True(tiles.GetChunkVersion(x / 32, y / 32) > version);
        Assert.Equal(0, tiles.TrailTiles);

        // A road at the road threshold falls to a trail below the road keep threshold, then to plain ground.
        StepTo(tiles, x, y, Roads.RoadThreshold);
        long stepped = tiles.Tick;
        counter = tiles.GetWalkCounter(x, y);
        Assert.Equal(RoadClass.Road, tiles.GetRoadClass(x, y));
        Assert.Equal((0, 1), (tiles.TrailTiles, tiles.RoadTiles));
        tiles.AdvanceTo(stepped + ((long)(counter - Roads.RoadKeepThreshold) * Decay));
        Assert.Equal(RoadClass.Road, tiles.GetRoadClass(x, y));
        tiles.AdvanceTo(stepped + ((long)(counter - Roads.RoadKeepThreshold + 1) * Decay));
        Assert.Equal(RoadClass.Trail, tiles.GetRoadClass(x, y));
        Assert.Equal((1, 0), (tiles.TrailTiles, tiles.RoadTiles));

        // Hysteresis: with a counter between the road keep and the road threshold, a fresh tile is a trail and a faded road
        // is still a road, also after a step.
        (int fx, int fy) = Land(300);
        StepTo(tiles, fx, fy, Roads.RoadKeepThreshold);
        Assert.Equal(RoadClass.Trail, tiles.GetRoadClass(fx, fy));
        (int rx, int ry) = Land(400);
        StepTo(tiles, rx, ry, Roads.RoadThreshold);
        tiles.AdvanceTo(tiles.Tick + ((long)(tiles.GetWalkCounter(rx, ry) - Roads.RoadKeepThreshold) * Decay));
        Assert.Equal(Roads.RoadKeepThreshold, tiles.GetWalkCounter(rx, ry));
        Assert.Equal(RoadClass.Road, tiles.GetRoadClass(rx, ry));
        tiles.Trample(rx, ry); // a step on a road above its keep threshold keeps it a road
        Assert.Equal(RoadClass.Road, tiles.GetRoadClass(rx, ry));
        Assert.True(tiles.GetWalkCounter(rx, ry) < Roads.RoadThreshold);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    public void CostsAndStepTimesFollowTheClasses()
    {
        TileChanges tiles = NewTiles();
        (int px, int py) = Land(10);
        (int tx, int ty) = Land(20);
        (int rx, int ry) = Land(30);
        tiles.AdvanceTo(0);
        int trailCost = PathCosts.Straight * Roads.TrailCostPermille / 1000;
        int roadCost = PathCosts.Straight * Roads.RoadCostPermille / 1000;

        Assert.True(tiles.IsUniform);
        Assert.Equal(PathCosts.Straight, tiles.MinCost);
        Assert.Equal(PathCosts.Straight, tiles.GetCost(px, py));
        Assert.Equal(1000, tiles.GetStepPermille(px, py));

        StepTo(tiles, tx, ty, Roads.TrailThreshold);
        Assert.False(tiles.IsUniform);
        Assert.Equal(trailCost, tiles.MinCost);
        Assert.Equal(trailCost, tiles.GetCost(tx, ty));
        Assert.Equal(Roads.TrailCostPermille, tiles.GetStepPermille(tx, ty));
        Assert.Equal(PathCosts.Straight, tiles.GetCost(px, py));

        StepTo(tiles, rx, ry, Roads.RoadThreshold);
        Assert.Equal(roadCost, tiles.MinCost);
        Assert.Equal(roadCost, tiles.GetCost(rx, ry));
        Assert.Equal(Roads.RoadCostPermille, tiles.GetStepPermille(rx, ry));

        // Everything fades: uniform again.
        tiles.AdvanceTo((long)Roads.MaxCounter * Decay);
        Assert.True(tiles.IsUniform);
        Assert.Equal(PathCosts.Straight, tiles.MinCost);
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.GetStepPermille(-1, 0));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    [Trait("Req", "FR-HERO-01")]
    public void PathsFollowRoadsWithAnAdmissibleHeuristic()
    {
        // An open stretch of flat ground: from S to G ten tiles east; a road one row below from S + 1 to S + 9.
        WorldMap map = WorldGenTestData.GoldenWorld(0);
        TileChanges tiles = NewTiles(map);
        tiles.AdvanceTo(0);
        var pathfinder = new Pathfinder(new PassabilityGrid(map.Terrain, tiles, tiles));
        PassabilityGrid grid = pathfinder.Grid;
        (int x, int y) = OpenStretch(grid, 11, 2);
        int start = grid.Index(x, y);
        int goal = grid.Index(x + 10, y);
        var path = new List<int>();

        Assert.True(grid.HasUniformCost);
        PathResult plain = pathfinder.FindPath(start, goal, path, PathfinderConfig.MaxNodeLimit);
        Assert.Equal(10 * PathCosts.Straight, plain.Cost);

        for (int rx = x + 1; rx <= x + 9; rx++)
        {
            StepTo(tiles, rx, y + 1, Roads.RoadThreshold);
        }

        Assert.False(grid.HasUniformCost);
        int road = PathCosts.Straight * Roads.RoadCostPermille / 1000;
        Assert.Equal(road, grid.MinStraightCost);
        Assert.Equal(PathCosts.Diagonal(road), grid.MinDiagonalCost);
        PathResult viaRoad = pathfinder.FindPath(start, goal, path, PathfinderConfig.MaxNodeLimit);

        // In diagonally onto the road, eight straight road steps, out diagonally onto plain ground.
        Assert.Equal(PathCosts.Diagonal(road) + (8 * road) + PathCosts.DiagonalBase, viaRoad.Cost);
        Assert.Equal(Enumerable.Range(x + 1, 9).Select(rx => grid.Index(rx, y + 1)), path.Skip(1).Take(9));
        Assert.True(viaRoad.ExpandedNodes > 0);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    [Trait("Req", "FR-SAVE-04")]
    public void OnlyLiveTilesAreSavedAndALoadedLayerContinuesIdentically()
    {
        TileChanges tiles = NewTiles();
        (int ax, int ay) = Land(10);
        (int bx, int by) = Land(2000);
        (int cx, int cy) = Land(4000);
        tiles.AdvanceTo(500);
        StepTo(tiles, ax, ay, Roads.RoadThreshold);
        StepTo(tiles, bx, by, Roads.TrailThreshold);
        tiles.Trample(cx, cy); // fades to 0 before the save
        tiles.AdvanceTo(500 + ((long)Roads.StepIncrement * Decay) + (7 * Decay) + 13);
        Assert.Equal(0, tiles.GetWalkCounter(cx, cy));
        Assert.Equal(3, tiles.WalkedTiles);

        TileChangesState state = tiles.CaptureState();
        Assert.Equal(2, state.Walks.Sum(c => c.Counters.Count));
        Assert.All(state.Walks, c => Assert.Equal(TileChanges.MaskWords, c.Mask.Count));
        Assert.Contains(state.Walks.SelectMany(c => c.Classes), c => c == RoadClass.Road);
        TileChanges restored = NewTiles();
        restored.AdvanceTo(tiles.Tick);
        restored.Load(state, new EntityRegistry());
        Assert.Equal(Hash(tiles), Hash(restored));
        Assert.Equal(2, restored.WalkedTiles);
        Assert.Equal((tiles.TrailTiles, tiles.RoadTiles), (restored.TrailTiles, restored.RoadTiles));

        // Compacting drops the faded tile from memory and changes nothing else.
        ulong before = Hash(tiles);
        tiles.CompactWalks();
        Assert.Equal(2, tiles.WalkedTiles);
        Assert.Equal(before, Hash(tiles));

        // Both go on: more steps, time, falls.
        foreach (TileChanges layer in new[] { tiles, restored })
        {
            layer.Trample(cx, cy);
            layer.Trample(bx, by);
            layer.AdvanceTo(layer.Tick + (40L * Decay));
            layer.Trample(ax, ay);
            layer.AdvanceTo(layer.Tick + (90L * Decay));
        }

        Assert.Equal(Hash(tiles), Hash(restored));
        Assert.Equal(tiles.GetRoadClass(ax, ay), restored.GetRoadClass(ax, ay));
        Assert.Equal(tiles.GetWalkCounter(bx, by), restored.GetWalkCounter(bx, by));
        Assert.Equal(tiles.CaptureState().Walks.SelectMany(c => c.Ticks), restored.CaptureState().Walks.SelectMany(c => c.Ticks));

        // Steps in another order within a tick give the same layer.
        TileChanges first = NewTiles();
        TileChanges second = NewTiles();
        first.AdvanceTo(7);
        second.AdvanceTo(7);
        first.Trample(ax, ay);
        first.Trample(bx, by);
        first.Trample(ax, ay);
        second.Trample(bx, by);
        second.Trample(ax, ay);
        second.Trample(ax, ay);
        Assert.Equal(Hash(first), Hash(second));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-15")]
    [Trait("Req", "FR-SAVE-04")]
    [InlineData("zero")]
    [InlineData("above-max")]
    [InlineData("future")]
    [InlineData("faded")]
    [InlineData("road-too-low")]
    [InlineData("road-counter-as-trail")]
    [InlineData("trail-counter-as-plain")]
    [InlineData("class")]
    [InlineData("counts")]
    [InlineData("order")]
    public void InvalidWalksAreRejected(string damage)
    {
        (int x, int y) = Land(10);
        ulong[] mask = new ulong[TileChanges.MaskWords];
        int local = ((y % 32) * 32) + (x % 32);
        mask[local >> 6] = 1UL << (local & 63);
        const long Now = 100_000;
        var good = new WalkChunkState(x / 32, y / 32, mask, [Roads.RoadThreshold], [Now - 10], [RoadClass.Road]);
        WalkChunkState walk = damage switch
        {
            "zero" => good with { Counters = [0], Classes = [RoadClass.None] },
            "above-max" => good with { Counters = [Roads.MaxCounter + 1] },
            "future" => good with { Ticks = [Now + 1] },
            "faded" => good with { Counters = [10], Ticks = [Now - (11L * Decay)], Classes = [RoadClass.None] },
            "road-too-low" => good with { Counters = [Roads.RoadKeepThreshold - 1] },
            "road-counter-as-trail" => good with { Classes = [RoadClass.Trail] },
            "trail-counter-as-plain" => good with { Counters = [Roads.TrailThreshold], Classes = [RoadClass.None] },
            "class" => good with { Classes = [(RoadClass)7] },
            "counts" => good with { Ticks = [] },
            "order" => good,
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        TileChanges tiles = NewTiles();
        tiles.AdvanceTo(Now);
        IReadOnlyList<WalkChunkState> walks = damage == "order" ? [good, good] : [walk];

        Assert.Throws<ArgumentException>(() => tiles.Load(new TileChangesState([], [], [], walks), new EntityRegistry()));
        Assert.Equal(0, tiles.WalkedTiles);
        Assert.True(tiles.IsUniform);

        tiles.Load(new TileChangesState([], [], [], [good]), new EntityRegistry());
        Assert.Equal(RoadClass.Road, tiles.GetRoadClass(x, y));
        Assert.Equal(1, tiles.RoadTiles);
    }

    // The top-left corner of a flat open stretch `width` tiles wide whose row and the `rows` rows below allow every step.
    private static (int X, int Y) OpenStretch(PassabilityGrid grid, int width, int rows)
    {
        for (int y = 1; y < grid.Size - rows - 1; y++)
        {
            for (int x = 1; x < grid.Size - width - 1; x++)
            {
                bool open = true;
                for (int dy = -1; dy <= rows && open; dy++)
                {
                    for (int dx = -1; dx <= width && open; dx++)
                    {
                        open = grid.StepMask(x + dx, y + dy) == 0xFF;
                    }
                }

                if (open)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No open stretch on the test map.");
    }
}
