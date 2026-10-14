using Core.Determinism;
using Core.Entities;

namespace Core.Tests.Entities;

public sealed class EntityRegistryTests
{
    [Fact]
    [Trait("Req", "NFR-04")]
    public void FirstIdsAreSequentialWithGenerationOne()
    {
        var registry = new EntityRegistry();

        Assert.Equal(new EntityId(0, 1), registry.Create());
        Assert.Equal(new EntityId(1, 1), registry.Create());
        Assert.Equal(new EntityId(2, 1), registry.Create());
        Assert.Equal(3, registry.AliveCount);
        Assert.Equal(3, registry.Capacity);
    }

    // ADR 0004 mandatory test: re-creating an entity gives the same index with a new generation.
    [Fact]
    [Trait("Req", "NFR-04")]
    public void RecreatingAnEntityReusesTheIndexWithANewGeneration()
    {
        var registry = new EntityRegistry();
        EntityId first = registry.Create();

        Assert.True(registry.Destroy(first));
        EntityId second = registry.Create();

        Assert.Equal(first.Index, second.Index);
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.False(registry.IsAlive(first));
        Assert.True(registry.IsAlive(second));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void FreeIndicesAreReusedSmallestFirstRegardlessOfDestroyOrder()
    {
        var registry = new EntityRegistry();
        EntityId[] ids = [.. Enumerable.Range(0, 6).Select(_ => registry.Create())];

        registry.Destroy(ids[4]);
        registry.Destroy(ids[1]);
        registry.Destroy(ids[3]);

        Assert.Equal(1, registry.Create().Index);
        Assert.Equal(3, registry.Create().Index);
        Assert.Equal(4, registry.Create().Index);
        Assert.Equal(6, registry.Create().Index);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void StaleAndInvalidIdsAreNotAliveAndCannotBeDestroyed()
    {
        var registry = new EntityRegistry();
        EntityId id = registry.Create();
        registry.Destroy(id);

        Assert.False(registry.Destroy(id));
        Assert.False(registry.Destroy(EntityId.None));
        Assert.False(registry.IsAlive(new EntityId(-1, 1)));
        Assert.False(registry.IsAlive(new EntityId(99, 1)));
        Assert.False(EntityId.None.IsValid);
        Assert.True(id.IsValid);
        Assert.Equal(0, registry.AliveCount);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void AliveEntitiesAreInAscendingIndexOrder()
    {
        var registry = new EntityRegistry();
        EntityId[] ids = [.. Enumerable.Range(0, 5).Select(_ => registry.Create())];
        registry.Destroy(ids[2]);
        registry.Destroy(ids[0]);
        EntityId reused = registry.Create();

        Assert.Equal([reused, ids[1], ids[3], ids[4]], registry.AliveEntities());
    }

    // ADR 0004 mandatory test: the next id after save-state-restore equals the id of an uninterrupted run.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    public void IdsAfterRestoreMatchAnUninterruptedRun()
    {
        var uninterrupted = new EntityRegistry();
        var beforeSave = new EntityRegistry();
        RunScript(uninterrupted, 0, 40);
        RunScript(beforeSave, 0, 40);

        EntityRegistry restored = EntityRegistry.FromState(beforeSave.GetState());

        Assert.Equal(Bytes(uninterrupted), Bytes(restored));
        Assert.Equal(RunScript(uninterrupted, 40, 80), RunScript(restored, 40, 80));
        Assert.Equal(Bytes(uninterrupted), Bytes(restored));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CanonicalBytesListSlotsInIndexOrder()
    {
        var registry = new EntityRegistry();
        registry.Create();
        registry.Destroy(registry.Create());

        var expected = new StateWriter();
        expected.WriteInt32(2);
        expected.WriteInt32(1);
        expected.WriteBool(true);
        expected.WriteInt32(2);
        expected.WriteBool(false);

        Assert.Equal(expected.ToArray(), Bytes(registry));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void FromStateRejectsInconsistentState()
    {
        Assert.Throws<ArgumentException>(() => EntityRegistry.FromState(new EntityRegistryState([1, 1], [true])));
        Assert.Throws<ArgumentException>(() => EntityRegistry.FromState(new EntityRegistryState([0], [false])));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void EntityIdWritesIndexThenGeneration()
    {
        var writer = new StateWriter();
        new EntityId(7, 3).WriteState(writer);

        Assert.Equal(new byte[] { 7, 0, 0, 0, 3, 0, 0, 0 }, writer.ToArray());
        Assert.Equal("#7v3", new EntityId(7, 3).ToString());
    }

    private static byte[] Bytes(EntityRegistry registry)
    {
        var writer = new StateWriter();
        registry.WriteState(writer);
        return writer.ToArray();
    }

    // A fixed create/destroy pattern; returns every id it created.
    private static List<EntityId> RunScript(EntityRegistry registry, int from, int to)
    {
        List<EntityId> created = [];
        for (int step = from; step < to; step++)
        {
            if (step % 3 == 2)
            {
                EntityId[] alive = [.. registry.AliveEntities()];
                registry.Destroy(alive[(step * 7) % alive.Length]);
            }
            else
            {
                created.Add(registry.Create());
            }
        }

        return created;
    }
}
