using Core.Determinism;

namespace Core.Entities;

/// <summary>A component: plain data in a struct that writes itself canonically (same bytes for hash and saves).</summary>
public interface IComponent : IStateHashable
{
}

/// <summary>Type-independent view of a <see cref="ComponentStore{T}"/>, used by <see cref="EntityWorld"/>.</summary>
public interface IComponentStore : IStateHashable
{
    /// <summary>Stable name of the store; part of the state hash and the save format.</summary>
    string Name { get; }

    /// <summary>Type of the stored component.</summary>
    Type ComponentType { get; }

    /// <summary>Number of entities that have the component.</summary>
    int Count { get; }

    /// <summary>True when the entity has the component.</summary>
    bool Has(EntityId entity);

    /// <summary>Removes the component; returns false when the entity did not have it.</summary>
    bool Remove(EntityId entity);
}

/// <summary>One stored component with its owner, used for snapshots.</summary>
/// <param name="Entity">Owner.</param>
/// <param name="Value">Component value.</param>
public readonly record struct ComponentEntry<T>(EntityId Entity, T Value)
    where T : struct, IComponent;

/// <summary>
/// Dense storage of one component type, kept sorted by entity index so iteration is always in ascending index
/// order (ADR 0004). Lookups are a binary search. Adds and removes shift the tail, which is cheap at the
/// hundreds-of-entities scale and keeps the order canonical without a separate sort.
/// </summary>
/// <remarks>Do not add or remove while iterating <see cref="Entities"/> or <see cref="Values"/>; systems use <see cref="EntityCommandBuffer"/>.</remarks>
public sealed class ComponentStore<T> : IComponentStore
    where T : struct, IComponent
{
    private const int InitialCapacity = 16;

    private readonly EntityRegistry _registry;
    private EntityId[] _entities = new EntityId[InitialCapacity];
    private T[] _values = new T[InitialCapacity];
    private int _count;

    internal ComponentStore(string name, EntityRegistry registry)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        _registry = registry;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Type ComponentType => typeof(T);

    /// <inheritdoc />
    public int Count => _count;

    /// <summary>Owners in ascending index order; parallel to <see cref="Values"/>.</summary>
    public ReadOnlySpan<EntityId> Entities => _entities.AsSpan(0, _count);

    /// <summary>Component values, writable in place; parallel to <see cref="Entities"/>.</summary>
    public Span<T> Values => _values.AsSpan(0, _count);

    /// <inheritdoc />
    public bool Has(EntityId entity) => Find(entity) >= 0;

    /// <summary>Adds the component to a live entity.</summary>
    /// <exception cref="InvalidOperationException">The entity is not alive or already has the component.</exception>
    public void Add(EntityId entity, T value)
    {
        int slot = SearchIndex(entity.Index);
        if (slot >= 0)
        {
            throw new InvalidOperationException($"{entity} already has component '{Name}'.");
        }

        Insert(~slot, entity, value);
    }

    /// <summary>Adds the component, or replaces it when the entity already has one.</summary>
    /// <exception cref="InvalidOperationException">The entity is not alive.</exception>
    public void Set(EntityId entity, T value)
    {
        int slot = SearchIndex(entity.Index);
        if (slot >= 0 && _entities[slot] == entity)
        {
            _values[slot] = value;
            return;
        }

        if (slot >= 0)
        {
            // A different generation at the same index can only be left over from a bypassed destroy.
            throw new InvalidOperationException($"Slot {entity.Index} of '{Name}' belongs to {_entities[slot]}, not {entity}.");
        }

        Insert(~slot, entity, value);
    }

    /// <inheritdoc />
    public bool Remove(EntityId entity)
    {
        int slot = Find(entity);
        if (slot < 0)
        {
            return false;
        }

        int tail = _count - slot - 1;
        Array.Copy(_entities, slot + 1, _entities, slot, tail);
        Array.Copy(_values, slot + 1, _values, slot, tail);
        _count--;
        _entities[_count] = default;
        _values[_count] = default;
        return true;
    }

    /// <summary>Reference to the component of <paramref name="entity"/>.</summary>
    /// <exception cref="KeyNotFoundException">The entity does not have the component.</exception>
    public ref T Get(EntityId entity)
    {
        int slot = Find(entity);
        if (slot < 0)
        {
            throw new KeyNotFoundException($"{entity} has no component '{Name}'.");
        }

        return ref _values[slot];
    }

    /// <summary>Copies the component of <paramref name="entity"/> if it has one.</summary>
    public bool TryGet(EntityId entity, out T value)
    {
        int slot = Find(entity);
        value = slot >= 0 ? _values[slot] : default;
        return slot >= 0;
    }

    /// <summary>Snapshot of all entries in ascending index order, for saves.</summary>
    public ComponentEntry<T>[] GetEntries()
    {
        var entries = new ComponentEntry<T>[_count];
        for (int i = 0; i < _count; i++)
        {
            entries[i] = new ComponentEntry<T>(_entities[i], _values[i]);
        }

        return entries;
    }

    /// <summary>Writes the count as Int32, then each entity id followed by its component, in ascending index order.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(_count);
        for (int i = 0; i < _count; i++)
        {
            _entities[i].WriteState(writer);
            _values[i].WriteState(writer);
        }
    }

    private void Insert(int slot, EntityId entity, T value)
    {
        if (!_registry.IsAlive(entity))
        {
            throw new InvalidOperationException($"Cannot add component '{Name}' to {entity}: entity is not alive.");
        }

        if (_count == _entities.Length)
        {
            Array.Resize(ref _entities, _count * 2);
            Array.Resize(ref _values, _count * 2);
        }

        int tail = _count - slot;
        Array.Copy(_entities, slot, _entities, slot + 1, tail);
        Array.Copy(_values, slot, _values, slot + 1, tail);
        _entities[slot] = entity;
        _values[slot] = value;
        _count++;
    }

    private int Find(EntityId entity)
    {
        int slot = SearchIndex(entity.Index);
        return slot >= 0 && _entities[slot] == entity ? slot : -1;
    }

    // Binary search by index; returns the slot, or the bitwise complement of the insertion point.
    private int SearchIndex(int index)
    {
        int low = 0;
        int high = _count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int midIndex = _entities[mid].Index;
            if (midIndex == index)
            {
                return mid;
            }

            if (midIndex < index)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }
}
