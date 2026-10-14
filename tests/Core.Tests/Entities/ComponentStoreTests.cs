using Core.Determinism;
using Core.Entities;

namespace Core.Tests.Entities;

/// <summary>Second component type for tests.</summary>
internal readonly record struct Health(int Value) : IComponent
{
    public void WriteState(StateWriter writer) => writer.WriteInt32(Value);
}

public sealed class ComponentStoreTests
{
    [Fact]
    [Trait("Req", "NFR-04")]
    public void IterationIsInAscendingIndexOrderWhateverTheAddOrder()
    {
        var world = new EntityWorld();
        ComponentStore<Position> store = world.AddStore<Position>("position");
        EntityId[] ids = [.. Enumerable.Range(0, 20).Select(_ => world.Create())];

        foreach (int i in new[] { 13, 2, 19, 0, 7, 8, 1, 15 })
        {
            store.Add(ids[i], new Position(i, -i));
        }

        Assert.Equal([0, 1, 2, 7, 8, 13, 15, 19], store.Entities.ToArray().Select(e => e.Index));
        Assert.Equal([0, 1, 2, 7, 8, 13, 15, 19], store.Values.ToArray().Select(p => p.X));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void AddGetHasRemove()
    {
        var world = new EntityWorld();
        ComponentStore<Health> store = world.AddStore<Health>("health");
        EntityId a = world.Create();
        EntityId b = world.Create();

        store.Add(a, new Health(10));
        Assert.True(store.Has(a));
        Assert.False(store.Has(b));
        Assert.Equal(10, store.Get(a).Value);

        store.Get(a) = new Health(5);
        Assert.True(store.TryGet(a, out Health health));
        Assert.Equal(5, health.Value);
        Assert.False(store.TryGet(b, out _));
        Assert.Throws<KeyNotFoundException>(() => store.Get(b));
        Assert.Throws<InvalidOperationException>(() => store.Add(a, new Health(1)));

        store.Set(a, new Health(7));
        store.Set(b, new Health(8));
        Assert.Equal(7, store.Get(a).Value);
        Assert.Equal(2, store.Count);

        Assert.True(store.Remove(a));
        Assert.False(store.Remove(a));
        Assert.False(store.Has(a));
        Assert.Equal(1, store.Count);
        Assert.Equal("health", store.Name);
        Assert.Equal(typeof(Health), store.ComponentType);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void StaleIdsDoNotSeeTheNewEntitysComponents()
    {
        var world = new EntityWorld();
        ComponentStore<Health> store = world.AddStore<Health>("health");
        EntityId old = world.Create();
        world.Destroy(old);
        EntityId reused = world.Create();
        store.Add(reused, new Health(1));

        Assert.Equal(old.Index, reused.Index);
        Assert.False(store.Has(old));
        Assert.Throws<InvalidOperationException>(() => store.Add(old, new Health(2)));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ComponentsCannotBeAddedToDeadEntities()
    {
        var world = new EntityWorld();
        ComponentStore<Health> store = world.AddStore<Health>("health");
        EntityId id = world.Create();
        world.Destroy(id);

        Assert.Throws<InvalidOperationException>(() => store.Add(id, new Health(1)));
        Assert.Throws<InvalidOperationException>(() => store.Set(new EntityId(5, 1), new Health(1)));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DestroyRemovesTheEntityFromEveryStore()
    {
        var world = new EntityWorld();
        ComponentStore<Health> health = world.AddStore<Health>("health");
        ComponentStore<Position> positions = world.AddStore<Position>("position");
        EntityId id = world.Create();
        health.Add(id, new Health(1));
        positions.Add(id, new Position(1, 2));

        Assert.True(world.Destroy(id));
        Assert.False(world.Destroy(id));
        Assert.Equal(0, health.Count);
        Assert.Equal(0, positions.Count);
        Assert.Equal(["health", "position"], world.Stores.Select(s => s.Name));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CanonicalBytesDoNotDependOnInsertionOrder()
    {
        Assert.Equal(Build([3, 1, 2, 0]), Build([0, 1, 2, 3]));

        static byte[] Build(int[] order)
        {
            var world = new EntityWorld();
            ComponentStore<Position> store = world.AddStore<Position>("position");
            EntityId[] ids = [.. Enumerable.Range(0, 4).Select(_ => world.Create())];
            foreach (int i in order)
            {
                store.Add(ids[i], new Position(i * 256, 3));
            }

            var writer = new StateWriter();
            world.WriteComponentState(writer);
            return writer.ToArray();
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void StoresAreRegisteredOncePerTypeAndName()
    {
        var world = new EntityWorld();
        world.AddStore<Health>("health");

        Assert.Throws<InvalidOperationException>(() => world.AddStore<Health>("other"));
        Assert.Throws<InvalidOperationException>(() => world.AddStore<Position>("health"));
        Assert.Throws<InvalidOperationException>(() => world.Store<Position>());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void GrowsBeyondInitialCapacityAndKeepsEntries()
    {
        var world = new EntityWorld();
        ComponentStore<Health> store = world.AddStore<Health>("health");
        EntityId[] ids = [.. Enumerable.Range(0, 100).Select(_ => world.Create())];
        for (int i = ids.Length - 1; i >= 0; i--)
        {
            store.Add(ids[i], new Health(i));
        }

        Assert.Equal(100, store.Count);
        Assert.Equal(Enumerable.Range(0, 100), store.GetEntries().Select(e => e.Value.Value));
        Assert.Equal(ids, store.GetEntries().Select(e => e.Entity));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 0)]
    [InlineData(256, 1)]
    [InlineData(-1, -1)]
    [InlineData(-256, -1)]
    [InlineData(-257, -2)]
    [Trait("Req", "NFR-04")]
    public void PositionTileIsFloorDivision(int units, int tile)
    {
        var position = new Position(units, units);

        Assert.Equal(tile, position.TileX);
        Assert.Equal(tile, position.TileY);
        Assert.Equal(new Position(512, -256), Position.FromTile(2, -1));
    }
}
