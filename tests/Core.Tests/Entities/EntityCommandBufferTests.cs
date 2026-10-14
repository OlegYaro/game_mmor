using Core.Entities;

namespace Core.Tests.Entities;

public sealed class EntityCommandBufferTests
{
    [Fact]
    [Trait("Req", "NFR-04")]
    public void NothingChangesUntilPlayback()
    {
        var world = new EntityWorld();
        ComponentStore<Health> store = world.AddStore<Health>("health");
        EntityId existing = world.Create();
        var buffer = new EntityCommandBuffer();

        DeferredEntity created = buffer.Create();
        buffer.Add(created, new Health(3));
        buffer.Add(existing, new Health(9));

        Assert.Equal(3, buffer.Count);
        Assert.Equal(1, world.Registry.AliveCount);
        Assert.Equal(0, store.Count);

        Assert.Equal(0, buffer.Playback(world));

        Assert.Equal(0, buffer.Count);
        Assert.Equal(2, world.Registry.AliveCount);
        Assert.Equal(9, store.Get(existing).Value);
        Assert.Equal(3, store.Get(new EntityId(1, 1)).Value);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void OperationsApplyInRecordingOrder()
    {
        var world = new EntityWorld();
        ComponentStore<Health> store = world.AddStore<Health>("health");
        EntityId a = world.Create();
        var buffer = new EntityCommandBuffer();

        buffer.Add(a, new Health(1));
        buffer.Add(a, new Health(2));
        buffer.Remove<Health>(a);
        buffer.Add(a, new Health(4));
        buffer.Playback(world);

        Assert.Equal(4, store.Get(a).Value);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DestroyedSlotIsReusedByALaterCreateInTheSameBuffer()
    {
        var world = new EntityWorld();
        world.AddStore<Health>("health");
        EntityId a = world.Create();
        world.Create();
        var buffer = new EntityCommandBuffer();

        buffer.Destroy(a);
        DeferredEntity created = buffer.Create();
        buffer.Add(created, new Health(5));
        buffer.Playback(world);

        Assert.True(world.IsAlive(new EntityId(0, 2)));
        Assert.Equal(5, world.Store<Health>().Get(new EntityId(0, 2)).Value);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void OperationsOnDeadEntitiesAreSkippedAndCounted()
    {
        var world = new EntityWorld();
        world.AddStore<Health>("health");
        EntityId a = world.Create();
        var buffer = new EntityCommandBuffer();

        buffer.Destroy(a);
        buffer.Destroy(a);
        buffer.Add(a, new Health(1));
        buffer.Remove<Health>(a);

        Assert.Equal(3, buffer.Playback(world));
        Assert.False(world.IsAlive(a));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DeferredHandlesExpireAfterPlaybackOrClear()
    {
        var world = new EntityWorld();
        world.AddStore<Health>("health");
        var buffer = new EntityCommandBuffer();
        DeferredEntity first = buffer.Create();
        buffer.Playback(world);

        Assert.Throws<ArgumentException>(() => buffer.Add(first, new Health(1)));

        DeferredEntity second = buffer.Create();
        buffer.Clear();
        Assert.Equal(0, buffer.Count);
        Assert.Throws<ArgumentException>(() => buffer.Add(second, new Health(1)));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void MissingStoreFailsAtPlaybackAndClearsTheBuffer()
    {
        var world = new EntityWorld();
        EntityId a = world.Create();
        var buffer = new EntityCommandBuffer();
        buffer.Add(a, new Health(1));

        Assert.Throws<InvalidOperationException>(() => buffer.Playback(world));
        Assert.Equal(0, buffer.Count);
    }
}
