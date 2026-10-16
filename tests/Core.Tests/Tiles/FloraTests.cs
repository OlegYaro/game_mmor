using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Tests.Sim;
using Core.Tiles;
using Core.WorldGen;

namespace Core.Tests.Tiles;

/// <summary>
/// Stumps, saplings and regrowth in the tile layer (FR-WORLD-04, gdd-issues B4): a felled tree leaves a stump that grows
/// back after the near or the far days, trees that grew back count for their neighbours, saplings grow into trees with a
/// stock from the world's range, building clears flora, and the layer saves, loads and hashes canonically.
/// </summary>
public sealed class FloraTests
{
    private const int TicksPerDay = 9000;

    private static readonly Lazy<WorldMap> _map = new(() => WorldGenerator.Generate(new WorldGenRequest(0xF10_4A, 128, SimTestData.Real.WorldGen)));

    private static WorldMap Map => _map.Value;

    private static RegrowthBalanceDef Regrowth => SimTestData.Real.Regrowth;

    private static TreeParams Trees => SimTestData.Real.WorldGen.Resources.Trees;

    private static TileChanges NewTiles() => new(Map, SimTestData.Real.Roads);

    // A map tree whose neighbourhood of the near radius holds `trees` other trees (0: isolated; -1: at least one).
    private static (int X, int Y) FindTree(int trees, int skip = 0)
    {
        int radius = Regrowth.NearTreeRadius;
        for (int y = radius; y < Map.Size - radius; y++)
        {
            for (int x = radius; x < Map.Size - radius; x++)
            {
                if (Map.GetResource(x, y) != ResourceKind.Tree)
                {
                    continue;
                }

                int around = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        around += (dx != 0 || dy != 0) && Map.GetResource(x + dx, y + dy) == ResourceKind.Tree ? 1 : 0;
                    }
                }

                if ((trees < 0 ? around > 0 : around == trees) && skip-- == 0)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such tree on the test map.");
    }

    // A land tile without a node such that the tiles from it to `width` tiles east, and the near radius around them, hold
    // no node, water or river.
    private static (int X, int Y) FindOpenLand(int width = 0)
    {
        int radius = Regrowth.NearTreeRadius;
        for (int y = radius; y < Map.Size - radius; y++)
        {
            for (int x = radius; x < Map.Size - radius - width; x++)
            {
                bool open = true;
                for (int dy = -radius; dy <= radius && open; dy++)
                {
                    for (int dx = -radius; dx <= width + radius && open; dx++)
                    {
                        open = Map.GetResource(x + dx, y + dy) == ResourceKind.None && !Map.Terrain.IsWater(x + dx, y + dy) && !Map.Terrain.IsRiver(x + dx, y + dy);
                    }
                }

                if (open)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No open land on the test map.");
    }

    private static void Fell(TileChanges tiles, int x, int y) => Assert.True(tiles.TakeResource(x, y, int.MaxValue) > 0);

    // Runs the daily passes from the start of `fromDay` to the start of `toDay` (both included) and returns the day each
    // watched tile first held a tree, or -1.
    private static long[] RunDays(TileChanges tiles, long fromDay, long toDay, params (int X, int Y)[] watched)
    {
        long[] grownOn = [.. watched.Select(_ => -1L)];
        for (long day = fromDay; day <= toDay; day++)
        {
            tiles.AdvanceTo(day * TicksPerDay);
            tiles.GrowFlora(Regrowth, Trees, TicksPerDay);
            for (int i = 0; i < watched.Length; i++)
            {
                if (grownOn[i] < 0 && tiles.GetEffectiveResource(watched[i].X, watched[i].Y) == ResourceKind.Tree)
                {
                    grownOn[i] = day;
                }
            }
        }

        return grownOn;
    }

    private static ulong Hash(TileChanges tiles) => new StateHasher().Hash(tiles);

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void AFelledTreeLeavesAStumpThatGrowsBackNearOtherTrees()
    {
        TileChanges tiles = NewTiles();
        (int x, int y) = FindTree(-1);
        int stock = Map.GetResourceAmount(x, y);
        tiles.AdvanceTo((2 * TicksPerDay) + 4321); // felled in the middle of day 2

        Assert.Equal(stock - 1, tiles.TakeResource(x, y, stock - 1));
        Assert.Equal(FloraState.None, tiles.GetFlora(x, y)); // a lowered tree is still a tree
        long version = tiles.Version;
        Fell(tiles, x, y);

        Assert.Equal(FloraState.Stump, tiles.GetFlora(x, y));
        Assert.Equal(tiles.Tick, tiles.GetFloraTick(x, y));
        Assert.Equal(ResourceKind.None, tiles.GetEffectiveResource(x, y));
        Assert.Equal(0, tiles.GetEffectiveAmount(x, y));
        Assert.Equal(0, tiles.TakeResource(x, y, 5));
        Assert.Equal(1, tiles.FloraTiles);
        Assert.True(tiles.Version > version);

        // Ten full days later, at the first day start after them: the map's own tree is back and the tile is unchanged again.
        long[] grown = RunDays(tiles, 3, 14, (x, y));
        Assert.Equal(2 + Regrowth.StumpRegrowDaysNearTree + 1, grown[0]);
        Assert.Equal(FloraState.None, tiles.GetFlora(x, y));
        Assert.Equal(-1, tiles.GetFloraTick(x, y));
        Assert.Equal(stock, tiles.GetEffectiveAmount(x, y));
        Assert.False(tiles.IsResourceChanged(x, y));
        Assert.Equal(0, tiles.ChangedResourceTiles);
        Assert.Equal(0, tiles.FloraTiles);
        Assert.Equal(Hash(NewTiles()), Hash(tiles));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void AnIsolatedStumpTakesTheFarDays()
    {
        // Generated trees grow in groves, so the lone tree is a grown one in a clearing.
        TileChanges tiles = NewTiles();
        (int x, int y) = FindOpenLand();
        tiles.AdvanceTo(0);
        Assert.True(tiles.Plant(x, y));
        RunDays(tiles, 1, Regrowth.SaplingGrowDays);
        long day = Regrowth.SaplingGrowDays;
        Fell(tiles, x, y); // right at the start of the day

        long[] grown = RunDays(tiles, day + 1, day + Regrowth.StumpRegrowDaysFar, (x, y));

        Assert.Equal(day + Regrowth.StumpRegrowDaysFar, grown[0]);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void TreesThatGrewBackCountForTheirNeighbours()
    {
        // Two saplings in a clearing, the near radius apart; the second is planted a tick later and grows a day later.
        TileChanges tiles = NewTiles();
        int radius = Regrowth.NearTreeRadius;
        (int lx, int ly) = FindOpenLand(radius);
        tiles.AdvanceTo(0);
        Assert.True(tiles.Plant(lx, ly));
        tiles.AdvanceTo(1);
        Assert.True(tiles.Plant(lx + radius, ly));
        long[] grown = RunDays(tiles, 1, Regrowth.SaplingGrowDays + 1, (lx, ly), (lx + radius, ly));
        Assert.Equal([Regrowth.SaplingGrowDays, Regrowth.SaplingGrowDays + 1L], grown);

        // Felling the second leaves a stump whose only living neighbour is the first grown tree: that is near enough.
        long felledDay = Regrowth.SaplingGrowDays + 1L;
        tiles.AdvanceTo((felledDay * TicksPerDay) + 10);
        Fell(tiles, lx + radius, ly);
        Assert.Equal(FloraState.Stump, tiles.GetFlora(lx + radius, ly));
        long[] regrown = RunDays(tiles, felledDay + 1, felledDay + Regrowth.StumpRegrowDaysNearTree + 1, (lx + radius, ly));
        Assert.Equal(felledDay + Regrowth.StumpRegrowDaysNearTree + 1, regrown[0]);
        Assert.Equal(FloraState.Tree, tiles.GetFlora(lx + radius, ly)); // not on a map tree: a grown tree again
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void AFelledGroveGrowsBackFromItsEdgeOneRingADay()
    {
        // Two stumps in a row away from a living tree: the first is within the radius of the tree, the second only of the
        // first. The pass decides against the state before it, so the second grows a day after the first.
        TileChanges tiles = NewTiles();
        int radius = Regrowth.NearTreeRadius;
        (int x, int y) = FindOpenLand(2 * radius);
        tiles.AdvanceTo(0);
        Assert.True(tiles.Plant(x, y));
        Assert.True(tiles.Plant(x + radius, y));
        Assert.True(tiles.Plant(x + (2 * radius), y));
        RunDays(tiles, 1, Regrowth.SaplingGrowDays);
        Assert.Equal(3, Enumerable.Range(0, 3).Count(i => tiles.GetFlora(x + (i * radius), y) == FloraState.Tree));

        long day = Regrowth.SaplingGrowDays + 1L;
        tiles.AdvanceTo((day * TicksPerDay) + 1);
        Fell(tiles, x + (2 * radius), y);
        Fell(tiles, x + radius, y);

        long[] grown = RunDays(tiles, day + 1, day + Regrowth.StumpRegrowDaysNearTree + 2, (x + radius, y), (x + (2 * radius), y));

        Assert.Equal([day + Regrowth.StumpRegrowDaysNearTree + 1, day + Regrowth.StumpRegrowDaysNearTree + 2], grown);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void ASaplingGrowsIntoATreeWithAStockFromTheWorldRange()
    {
        TileChanges tiles = NewTiles();
        (int x, int y) = FindOpenLand();
        tiles.AdvanceTo(123);
        long version = tiles.Version;

        Assert.True(tiles.CanPlant(x, y));
        Assert.True(tiles.Plant(x, y));
        Assert.True(tiles.Version > version);
        Assert.Equal(FloraState.Sapling, tiles.GetFlora(x, y));
        Assert.Equal(123, tiles.GetFloraTick(x, y));
        Assert.Equal(ResourceKind.None, tiles.GetEffectiveResource(x, y));
        Assert.False(tiles.CanPlant(x, y));
        Assert.False(tiles.Plant(x, y));

        // Not yet at day 10 (planted at tick 123 of day 0), a tree at day 11.
        long[] grown = RunDays(tiles, 1, Regrowth.SaplingGrowDays + 1, (x, y));
        Assert.Equal(Regrowth.SaplingGrowDays + 1L, grown[0]);
        Assert.Equal(FloraState.Tree, tiles.GetFlora(x, y));
        Assert.Equal(ResourceKind.Tree, tiles.GetEffectiveResource(x, y));
        int stock = tiles.GetEffectiveAmount(x, y);
        Assert.InRange(stock, Trees.StockMin, Trees.StockMax);

        // The stock depends only on the seed and the tile.
        TileChanges other = NewTiles();
        other.AdvanceTo(5 * TicksPerDay);
        Assert.True(other.Plant(x, y));
        RunDays(other, 6, 6 + Regrowth.SaplingGrowDays);
        Assert.Equal(stock, other.GetEffectiveAmount(x, y));

        // A grown tree gives wood like any other and leaves a stump.
        Assert.Equal(3, tiles.TakeResource(x, y, 3));
        Assert.Equal(stock - 3, tiles.GetEffectiveAmount(x, y));
        Assert.Equal(stock - 3, tiles.TakeResource(x, y, 1000));
        Assert.Equal(FloraState.Stump, tiles.GetFlora(x, y));
        Assert.Equal(tiles.Tick, tiles.GetFloraTick(x, y));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void SaplingsGoOnlyOnFreeLandAndBuildingsClearFlora()
    {
        TileChanges tiles = NewTiles();
        (int tx, int ty) = FindTree(-1);
        (int lx, int ly) = FindOpenLand();
        var owner = new EntityId(3, 1);
        int water = Enumerable.Range(0, Map.Size * Map.Size).First(i => Map.Terrain.IsWater(i % Map.Size, i / Map.Size));

        Assert.False(tiles.CanPlant(tx, ty)); // a node
        Assert.False(tiles.CanPlant(water % Map.Size, water / Map.Size));
        Assert.True(tiles.TryOccupy(lx, ly, owner));
        Assert.False(tiles.CanPlant(lx, ly)); // a building
        Assert.True(tiles.Release(lx, ly, owner));
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.CanPlant(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.GetFlora(0, Map.Size));

        // A felled tree's tile takes no sapling (its stump is there) until a building clears the stump.
        Fell(tiles, tx, ty);
        Assert.False(tiles.CanPlant(tx, ty));
        Assert.True(tiles.TryOccupy(tx, ty, owner));
        Assert.Equal(FloraState.None, tiles.GetFlora(tx, ty));
        Assert.True(tiles.Plant(lx, ly));
        Assert.True(tiles.TryOccupy(lx, ly, owner));
        Assert.Equal(FloraState.None, tiles.GetFlora(lx, ly));
        Assert.Equal(0, tiles.FloraTiles);

        // Once the building is gone, the felled map tree's tile is free land: a sapling there grows into the map's tree.
        Assert.True(tiles.Release(tx, ty, owner));
        Assert.True(tiles.CanPlant(tx, ty));
        Assert.True(tiles.Plant(tx, ty));
        RunDays(tiles, 1, Regrowth.SaplingGrowDays + 1);
        Assert.Equal(FloraState.None, tiles.GetFlora(tx, ty));
        Assert.Equal(Map.GetResourceAmount(tx, ty), tiles.GetEffectiveAmount(tx, ty));
        Assert.False(tiles.IsResourceChanged(tx, ty));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-SAVE-04")]
    public void FloraSavesLoadsAndContinuesLikeTheOriginal()
    {
        TileChanges tiles = NewTiles();
        int radius = Regrowth.NearTreeRadius;
        (int ax, int ay) = FindTree(-1);
        (int bx, int by) = FindOpenLand((2 * radius) + 1);
        (int sx, int sy) = (bx + (2 * radius) + 1, by);
        tiles.AdvanceTo(0);
        Assert.True(tiles.Plant(bx, by));
        RunDays(tiles, 1, Regrowth.SaplingGrowDays);

        // A stump near a grove, a lone stump and a sapling, all from the same tick.
        long day = Regrowth.SaplingGrowDays;
        tiles.AdvanceTo((day * TicksPerDay) + 77);
        Fell(tiles, ax, ay);
        Fell(tiles, bx, by);
        Assert.True(tiles.Plant(sx, sy));
        RunDays(tiles, day + 1, day + 4);

        // Save in the middle of every timer, load, and run both to the end.
        TileChangesState state = tiles.CaptureState();
        Assert.Equal(3, state.Flora.Sum(c => c.States.Count));
        Assert.All(state.Flora, c => Assert.Equal(TileChanges.MaskWords, c.Mask.Count));
        TileChanges restored = NewTiles();
        restored.AdvanceTo(tiles.Tick);
        restored.Load(state, new EntityRegistry());
        Assert.Equal(Hash(tiles), Hash(restored));
        Assert.Equal(FloraState.Stump, restored.GetFlora(bx, by));
        Assert.Equal(tiles.GetFloraTick(sx, sy), restored.GetFloraTick(sx, sy));

        long last = day + Regrowth.StumpRegrowDaysFar + 2;
        long[] original = RunDays(tiles, day + 5, last, (ax, ay), (bx, by), (sx, sy));
        long[] loaded = RunDays(restored, day + 5, last, (ax, ay), (bx, by), (sx, sy));
        Assert.Equal(original, loaded);
        Assert.Equal([day + Regrowth.StumpRegrowDaysNearTree + 1, day + Regrowth.StumpRegrowDaysFar + 1, day + Regrowth.SaplingGrowDays + 1], original);
        Assert.Equal(Hash(tiles), Hash(restored));

        // The same changes in another order give the same hash: the layer is canonical.
        TileChanges first = NewTiles();
        TileChanges second = NewTiles();
        first.AdvanceTo(50);
        second.AdvanceTo(50);
        Fell(first, ax, ay);
        Assert.True(first.Plant(sx, sy));
        Assert.True(second.Plant(sx, sy));
        Fell(second, ax, ay);
        Assert.Equal(Hash(first), Hash(second));
        Assert.NotEqual(Hash(NewTiles()), Hash(first));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-SAVE-04")]
    [InlineData("state")]
    [InlineData("future")]
    [InlineData("tree-on-map-tree")]
    [InlineData("stump-with-stock")]
    [InlineData("tree-without-stock")]
    [InlineData("on-a-node")]
    [InlineData("occupied")]
    [InlineData("water")]
    [InlineData("counts")]
    public void InvalidFloraIsRejected(string damage)
    {
        (int tx, int ty) = FindTree(-1);
        (int lx, int ly) = FindOpenLand();
        int water = Enumerable.Range(0, Map.Size * Map.Size).First(i => Map.Terrain.IsWater(i % Map.Size, i / Map.Size));
        var registry = new EntityRegistry();
        EntityId owner = registry.Create();
        FloraChunkState sapling = OneTile(lx, ly, FloraState.Sapling, 50, 0);
        var felledTree = new ResourceChunkState(tx / 32, ty / 32, OneTile(tx, ty, FloraState.Stump, 0, 0).Mask, [0]);
        TileChangesState state = damage switch
        {
            "state" => new([], [], [sapling with { States = [FloraState.None] }], []),
            "future" => new([], [], [sapling with { Ticks = [101] }], []),
            "tree-on-map-tree" => new([felledTree], [], [OneTile(tx, ty, FloraState.Tree, 50, 5)], []),
            "stump-with-stock" => new([], [], [sapling with { States = [FloraState.Stump], Stocks = [3] }], []),
            "tree-without-stock" => new([], [], [sapling with { States = [FloraState.Tree] }], []),
            "on-a-node" => new([], [], [OneTile(tx, ty, FloraState.Stump, 50, 0)], []),
            "occupied" => new([], [new OccupancyChunkState(sapling.ChunkX, sapling.ChunkY, sapling.Mask, [owner])], [sapling], []),
            "water" => new([], [], [OneTile(water % Map.Size, water / Map.Size, FloraState.Sapling, 50, 0)], []),
            "counts" => new([], [], [sapling with { Ticks = [] }], []),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        TileChanges target = NewTiles();
        target.AdvanceTo(100);
        TileChanges empty = NewTiles();
        empty.AdvanceTo(100);

        Assert.Throws<ArgumentException>(() => target.Load(state, registry));
        Assert.Equal(0, target.FloraTiles);
        Assert.Equal(0, target.OccupiedTiles);
        Assert.Equal(Hash(empty), Hash(target));

        // The undamaged state loads.
        target.Load(new([], [], [sapling], []), registry);
        Assert.Equal(FloraState.Sapling, target.GetFlora(lx, ly));
        Assert.Equal(50, target.GetFloraTick(lx, ly));
    }

    // A flora chunk holding one tile.
    private static FloraChunkState OneTile(int x, int y, FloraState state, long tick, int stock)
    {
        ulong[] mask = new ulong[TileChanges.MaskWords];
        int local = ((y % 32) * 32) + (x % 32);
        mask[local >> 6] = 1UL << (local & 63);
        return new FloraChunkState(x / 32, y / 32, mask, [state], [tick], [stock]);
    }
}
