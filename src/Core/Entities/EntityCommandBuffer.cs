namespace Core.Entities;

/// <summary>
/// Placeholder for an entity that an <see cref="EntityCommandBuffer"/> will create at playback.
/// Valid only until that buffer's next <see cref="EntityCommandBuffer.Playback"/> or <see cref="EntityCommandBuffer.Clear"/>.
/// </summary>
/// <param name="Slot">Order of the create among the buffer's creates.</param>
/// <param name="Batch">Batch number of the buffer when it was recorded.</param>
public readonly record struct DeferredEntity(int Slot, int Batch);

/// <summary>
/// Records structural changes (create, destroy, add, remove) during a system step and applies them in recording
/// order afterwards (ADR 0004), so stores are never changed while a system iterates them.
/// </summary>
/// <remarks>
/// At playback, operations aimed at an entity that is no longer alive (for example destroyed earlier in the same
/// buffer) are skipped and counted, not thrown: two systems may legitimately race to remove the same entity.
/// Adding a component that already exists replaces it.
/// </remarks>
public sealed class EntityCommandBuffer
{
    private readonly List<Op> _ops = [];
    private readonly List<EntityId> _created = [];
    private int _creates;
    private int _batch;

    /// <summary>Number of recorded operations.</summary>
    public int Count => _ops.Count;

    /// <summary>Records the creation of an entity with no components.</summary>
    public DeferredEntity Create()
    {
        _ops.Add(new CreateOp());
        return new DeferredEntity(_creates++, _batch);
    }

    /// <summary>Records destroying an entity together with all its components.</summary>
    public void Destroy(EntityId entity) => _ops.Add(new DestroyOp(entity));

    /// <summary>Records adding (or replacing) a component of an existing entity.</summary>
    public void Add<T>(EntityId entity, T value)
        where T : struct, IComponent => _ops.Add(new AddOp<T>(Target.Of(entity), value));

    /// <summary>Records adding a component to an entity created by this buffer.</summary>
    /// <exception cref="ArgumentException">The placeholder belongs to an earlier batch.</exception>
    public void Add<T>(DeferredEntity entity, T value)
        where T : struct, IComponent => _ops.Add(new AddOp<T>(Target.Of(CheckDeferred(entity)), value));

    /// <summary>Records removing a component.</summary>
    public void Remove<T>(EntityId entity)
        where T : struct, IComponent => _ops.Add(new RemoveOp<T>(entity));

    /// <summary>Forgets every recorded operation and invalidates outstanding placeholders.</summary>
    public void Clear()
    {
        _ops.Clear();
        _created.Clear();
        _creates = 0;
        _batch++;
    }

    /// <summary>
    /// Applies all operations in recording order, then clears the buffer.
    /// Returns the number of operations skipped because their entity was not alive.
    /// </summary>
    /// <exception cref="InvalidOperationException">A component type has no registered store.</exception>
    public int Playback(EntityWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int skipped = 0;
        try
        {
            foreach (Op op in _ops)
            {
                if (!op.Apply(world, _created))
                {
                    skipped++;
                }
            }
        }
        finally
        {
            Clear();
        }

        return skipped;
    }

    private DeferredEntity CheckDeferred(DeferredEntity entity)
    {
        if (entity.Batch != _batch || entity.Slot < 0 || entity.Slot >= _creates)
        {
            throw new ArgumentException($"Deferred entity {entity} does not belong to this buffer's current batch.", nameof(entity));
        }

        return entity;
    }

    private readonly record struct Target(EntityId Entity, int DeferredSlot)
    {
        public static Target Of(EntityId entity) => new(entity, -1);

        public static Target Of(DeferredEntity entity) => new(EntityId.None, entity.Slot);

        public EntityId Resolve(List<EntityId> created) => DeferredSlot < 0 ? Entity : created[DeferredSlot];
    }

    private abstract class Op
    {
        // Returns false when the operation was skipped because its entity is not alive.
        public abstract bool Apply(EntityWorld world, List<EntityId> created);
    }

    private sealed class CreateOp : Op
    {
        public override bool Apply(EntityWorld world, List<EntityId> created)
        {
            created.Add(world.Create());
            return true;
        }
    }

    private sealed class DestroyOp(EntityId entity) : Op
    {
        public override bool Apply(EntityWorld world, List<EntityId> created) => world.Destroy(entity);
    }

    private sealed class AddOp<T>(Target target, T value) : Op
        where T : struct, IComponent
    {
        public override bool Apply(EntityWorld world, List<EntityId> created)
        {
            ComponentStore<T> store = world.Store<T>();
            EntityId entity = target.Resolve(created);
            if (!world.IsAlive(entity))
            {
                return false;
            }

            store.Set(entity, value);
            return true;
        }
    }

    private sealed class RemoveOp<T>(EntityId entity) : Op
        where T : struct, IComponent
    {
        public override bool Apply(EntityWorld world, List<EntityId> created)
        {
            ComponentStore<T> store = world.Store<T>();
            if (!world.IsAlive(entity))
            {
                return false;
            }

            store.Remove(entity);
            return true;
        }
    }
}
