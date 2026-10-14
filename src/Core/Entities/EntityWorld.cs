using Core.Determinism;

namespace Core.Entities;

/// <summary>
/// The entity registry together with all component stores. Stores are kept in registration order, which is
/// their order in the state hash and in saves; the type lookup table is only used for lookups, never iterated.
/// </summary>
public sealed class EntityWorld
{
    private readonly List<IComponentStore> _stores = [];
    private readonly Dictionary<Type, IComponentStore> _storesByType = [];

    /// <summary>Creates a world with an empty registry.</summary>
    public EntityWorld()
        : this(new EntityRegistry())
    {
    }

    /// <summary>Creates a world around an existing (for example restored) registry.</summary>
    public EntityWorld(EntityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
    }

    /// <summary>Issues and retires entity ids.</summary>
    public EntityRegistry Registry { get; }

    /// <summary>Component stores in registration order.</summary>
    public IReadOnlyList<IComponentStore> Stores => _stores;

    /// <summary>Registers the store for <typeparamref name="T"/>. Registration order is part of the state format.</summary>
    /// <exception cref="InvalidOperationException">A store for the type or with the name already exists.</exception>
    public ComponentStore<T> AddStore<T>(string name)
        where T : struct, IComponent
    {
        if (_storesByType.ContainsKey(typeof(T)) || _stores.Exists(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Component store '{name}' ({typeof(T).Name}) is already registered.");
        }

        var store = new ComponentStore<T>(name, Registry);
        _stores.Add(store);
        _storesByType.Add(typeof(T), store);
        return store;
    }

    /// <summary>The store for <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">No store is registered for the type.</exception>
    public ComponentStore<T> Store<T>()
        where T : struct, IComponent =>
        _storesByType.TryGetValue(typeof(T), out IComponentStore? store)
            ? (ComponentStore<T>)store
            : throw new InvalidOperationException($"No component store registered for {typeof(T).Name}.");

    /// <summary>Creates an entity with no components.</summary>
    public EntityId Create() => Registry.Create();

    /// <summary>True when the entity is alive.</summary>
    public bool IsAlive(EntityId entity) => Registry.IsAlive(entity);

    /// <summary>Removes every component of a live entity, then frees its id. Returns false for stale ids.</summary>
    public bool Destroy(EntityId entity)
    {
        if (!Registry.IsAlive(entity))
        {
            return false;
        }

        foreach (IComponentStore store in _stores)
        {
            store.Remove(entity);
        }

        return Registry.Destroy(entity);
    }

    /// <summary>Writes the store count, then each store's name and state in registration order.</summary>
    public void WriteComponentState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(_stores.Count);
        foreach (IComponentStore store in _stores)
        {
            writer.WriteString(store.Name);
            store.WriteState(writer);
        }
    }
}
