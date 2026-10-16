using Core.Determinism;
using Core.Entities;
using Core.Pathfinding;
using Core.Tests.Sim;
using Core.Tests.WorldGen;
using Core.Tiles;
using Core.WorldGen;

namespace Core.Tests.Tiles;

/// <summary>The sparse tile change layer (FR-WORLD-05, ADR 0007): resource stocks, occupancy, state and hash.</summary>
public sealed class TileChangesTests
{
    private static readonly Lazy<WorldMap> _map = new(() => WorldGenerator.Generate(new WorldGenRequest(0x711E5, 64, SimTestData.Real.WorldGen)));

    private static WorldMap Map => _map.Value;

    private static (int X, int Y) FindResource(WorldMap map, Func<ResourceKind, bool> match, int skip = 0)
    {
        for (int y = 0; y < map.Size; y++)
        {
            for (int x = 0; x < map.Size; x++)
            {
                if (match(map.GetResource(x, y)) && skip-- == 0)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such resource on the test map.");
    }

    private static (int X, int Y) FindLand(WorldMap map, int skip = 0)
    {
        for (int y = 0; y < map.Size; y++)
        {
            for (int x = 0; x < map.Size; x++)
            {
                if (!map.Terrain.IsWater(x, y) && !map.Terrain.IsRiver(x, y) && skip-- == 0)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No land on the test map.");
    }

    private static ulong Hash(TileChanges tiles) => new StateHasher().Hash(tiles);

    [Fact]
    [Trait("Req", "FR-WORLD-05")]
    public void UnchangedLayerReadsTheMap()
    {
        var tiles = new TileChanges(Map, SimTestData.Real.Roads);
        (int x, int y) = FindResource(Map, k => k == ResourceKind.Tree);

        Assert.Equal(ResourceKind.Tree, tiles.GetEffectiveResource(x, y));
        Assert.Equal(Map.GetResourceAmount(x, y), tiles.GetEffectiveAmount(x, y));
        Assert.False(tiles.IsResourceChanged(x, y));
        Assert.Equal(0, tiles.ChangedResourceTiles);
        Assert.Equal(TileChangesState.Empty.Resources, tiles.CaptureState().Resources);
        Assert.Empty(tiles.CaptureState().Occupancy);
        Assert.Equal(2, tiles.ChunksPerSide);
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.GetEffectiveAmount(64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.IsOccupied(-1, 0));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-05")]
    public void TakingLowersTheStockAndAnEmptyNodeDisappears()
    {
        var tiles = new TileChanges(Map, SimTestData.Real.Roads);
        (int x, int y) = FindResource(Map, k => k == ResourceKind.Tree);
        int stock = Map.GetResourceAmount(x, y);
        long version = tiles.Version;

        Assert.Equal(1, tiles.TakeResource(x, y, 1));
        Assert.Equal(stock - 1, tiles.GetEffectiveAmount(x, y));
        Assert.True(tiles.IsResourceChanged(x, y));
        Assert.Equal(1, tiles.ChangedResourceTiles);
        Assert.True(tiles.Version > version);
        Assert.Equal(tiles.Version, tiles.GetChunkVersion(x / 32, y / 32));

        Assert.Equal(stock - 1, tiles.TakeResource(x, y, stock + 100));
        Assert.Equal(0, tiles.GetEffectiveAmount(x, y));
        Assert.Equal(ResourceKind.None, tiles.GetEffectiveResource(x, y));
        Assert.Equal(0, tiles.TakeResource(x, y, 5));
        Assert.Equal(1, tiles.ChangedResourceTiles);

        // The map itself never changes.
        Assert.Equal(ResourceKind.Tree, Map.GetResource(x, y));
        Assert.Equal(stock, Map.GetResourceAmount(x, y));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-05")]
    public void TakingFromEmptyTilesOrZeroChangesNothing()
    {
        var tiles = new TileChanges(Map, SimTestData.Real.Roads);
        (int x, int y) = FindResource(Map, k => k == ResourceKind.None);
        (int tx, int ty) = FindResource(Map, k => k != ResourceKind.None);

        Assert.Equal(0, tiles.TakeResource(x, y, 10));
        Assert.Equal(0, tiles.TakeResource(tx, ty, 0));
        Assert.Equal(0, tiles.ChangedResourceTiles);
        Assert.Equal(0, tiles.Version);
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.TakeResource(tx, ty, -1));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-05")]
    [Trait("Req", "FR-WORLD-14")]
    public void VeinTilesLowerOnlyTheirOwnStockAndTheGeneratedSumStays()
    {
        WorldMap map = WorldGenTestData.LargeWorld(1);
        Assert.NotEmpty(map.Veins);
        Vein vein = map.Veins[0];
        var tiles = new TileChanges(map, SimTestData.Real.Roads);
        (int x, int y) = FindResource(map, k => k == vein.Kind);
        int before = map.GetResourceAmount(x, y);

        Assert.Equal(3, tiles.TakeResource(x, y, 3));

        Assert.Equal(before - 3, tiles.GetEffectiveAmount(x, y));
        Assert.Equal(vein.Stock, map.Veins[0].Stock);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-05")]
    [Trait("Req", "FR-BUILD-02")]
    public void OccupancyBlocksTilesAndTellsThePathfinder()
    {
        var tiles = new TileChanges(Map, SimTestData.Real.Roads);
        var pathfinder = new Pathfinder(new PassabilityGrid(Map.Terrain, tiles));
        tiles.AttachPathfinder(pathfinder);
        (int x, int y) = FindLand(Map, skip: 100);
        var owner = new EntityId(4, 2);
        Assert.True(pathfinder.Grid.IsPassable(x, y));
        Assert.NotEqual(ConnectivityMap.NoComponent, pathfinder.Connectivity.GetComponent(x, y));

        Assert.True(tiles.TryOccupy(x, y, owner));
        Assert.True(tiles.IsOccupied(x, y));
        Assert.Equal(owner, tiles.GetOccupant(x, y));
        Assert.True(tiles.IsBlocked(x, y));
        Assert.False(pathfinder.Grid.IsPassable(x, y));
        Assert.True(pathfinder.Connectivity.IsDirty);
        Assert.Equal(ConnectivityMap.NoComponent, pathfinder.Connectivity.GetComponent(x, y));
        Assert.False(tiles.TryOccupy(x, y, new EntityId(5, 1)));
        Assert.Equal(1, tiles.OccupiedTiles);

        Assert.False(tiles.Release(x, y, new EntityId(4, 3)));
        Assert.False(tiles.Release(x + 1, y, owner));
        Assert.True(tiles.Release(x, y, owner));
        Assert.False(tiles.IsOccupied(x, y));
        Assert.Equal(0, tiles.OccupiedTiles);
        Assert.True(pathfinder.Grid.IsPassable(x, y));
        Assert.NotEqual(ConnectivityMap.NoComponent, pathfinder.Connectivity.GetComponent(x, y));
        Assert.Empty(tiles.CaptureState().Occupancy);
        Assert.Throws<ArgumentException>(() => tiles.TryOccupy(x, y, EntityId.None));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-05")]
    [Trait("Req", "FR-SAVE-04")]
    public void StateRoundTripsAndHashesOnlyTheChanges()
    {
        var tiles = new TileChanges(Map, SimTestData.Real.Roads);
        ulong empty = Hash(tiles);
        (int ax, int ay) = FindResource(Map, k => k == ResourceKind.Tree);
        (int bx, int by) = FindResource(Map, k => k == ResourceKind.Tree, skip: 3);
        (int ox, int oy) = FindLand(Map, skip: 2000);
        tiles.TakeResource(ax, ay, 2);
        tiles.TakeResource(bx, by, Map.GetResourceAmount(bx, by));
        var registry = new EntityRegistry();
        EntityId owner = registry.Create();
        Assert.True(tiles.TryOccupy(ox, oy, owner));
        Assert.True(tiles.TryOccupy(ox + 1, oy, owner));

        TileChangesState state = tiles.CaptureState();
        var restored = new TileChanges(Map, SimTestData.Real.Roads);
        restored.Load(state, registry);

        Assert.NotEqual(empty, Hash(tiles));
        Assert.Equal(Hash(tiles), Hash(restored));
        Assert.Equal(Map.GetResourceAmount(ax, ay) - 2, restored.GetEffectiveAmount(ax, ay));
        Assert.Equal(ResourceKind.None, restored.GetEffectiveResource(bx, by));
        Assert.Equal(owner, restored.GetOccupant(ox + 1, oy));
        Assert.Equal(2, restored.OccupiedTiles);
        Assert.Equal(tiles.ChangedResourceTiles, restored.ChangedResourceTiles);
        int amounts = state.Resources.Sum(c => c.Amounts.Count);
        Assert.Equal(tiles.ChangedResourceTiles, amounts);
        Assert.All(state.Resources, c => Assert.Equal(TileChanges.MaskWords, c.Mask.Count));
        Assert.Equal([owner, owner], state.Occupancy.SelectMany(c => c.Owners));

        // Same changes in another order give the same hash: the layer is canonical.
        var other = new TileChanges(Map, SimTestData.Real.Roads);
        other.TryOccupy(ox + 1, oy, owner);
        other.TakeResource(bx, by, 1000);
        other.TryOccupy(ox, oy, owner);
        other.TakeResource(ax, ay, 1);
        other.TakeResource(ax, ay, 1);
        Assert.Equal(Hash(tiles), Hash(other));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-05")]
    [Trait("Req", "FR-SAVE-04")]
    [InlineData("outside")]
    [InlineData("order")]
    [InlineData("mask-length")]
    [InlineData("count")]
    [InlineData("empty")]
    [InlineData("not-lowered")]
    [InlineData("no-node")]
    [InlineData("dead-owner")]
    public void InvalidStateIsRejectedAndLeavesTheLayerEmpty(string damage)
    {
        (int tx, int ty) = FindResource(Map, k => k == ResourceKind.Tree);
        (int nx, int ny) = FindResource(Map, k => k == ResourceKind.None);
        var registry = new EntityRegistry();
        EntityId owner = registry.Create();
        ulong[] mask = new ulong[TileChanges.MaskWords];
        int local = ((ty % 32) * 32) + (tx % 32);
        mask[local >> 6] = 1UL << (local & 63);
        var good = new ResourceChunkState(tx / 32, ty / 32, mask, [1]);
        ulong[] noNodeMask = new ulong[TileChanges.MaskWords];
        int noNode = ((ny % 32) * 32) + (nx % 32);
        noNodeMask[noNode >> 6] = 1UL << (noNode & 63);

        TileChangesState state = damage switch
        {
            "outside" => new([good with { ChunkX = 2 }], [], [], []),
            "order" => new([good, good], [], [], []),
            "mask-length" => new([good with { Mask = [1] }], [], [], []),
            "count" => new([good with { Amounts = [1, 2] }], [], [], []),
            "empty" => new([good with { Mask = new ulong[TileChanges.MaskWords], Amounts = [] }], [], [], []),
            "not-lowered" => new([good with { Amounts = [Map.GetResourceAmount(tx, ty)] }], [], [], []),
            "no-node" => new([new ResourceChunkState(nx / 32, ny / 32, noNodeMask, [0])], [], [], []),
            "dead-owner" => new([good], [new OccupancyChunkState(0, 0, mask, [new EntityId(owner.Index, 9)])], [], []),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        var tiles = new TileChanges(Map, SimTestData.Real.Roads);

        Assert.Throws<ArgumentException>(() => tiles.Load(state, registry));
        Assert.Equal(0, tiles.ChangedResourceTiles);
        Assert.Equal(0, tiles.OccupiedTiles);
        Assert.Equal(Hash(new TileChanges(Map, SimTestData.Real.Roads)), Hash(tiles));

        tiles.Load(new TileChangesState([good], [], [], []), registry);
        Assert.Throws<InvalidOperationException>(() => tiles.Load(TileChangesState.Empty, registry));
    }
}
