using Core.Determinism;

namespace Core.Entities;

/// <summary>
/// Canonical, restorable state of an <see cref="EntityRegistry"/>: one entry per slot ever allocated.
/// The free list is not stored: it is exactly the set of slots that are not alive, so it is rebuilt on restore.
/// </summary>
/// <param name="Generations">Per slot: the generation of the live entity, or for a free slot the generation its next entity gets.</param>
/// <param name="Alive">Per slot: whether an entity currently lives in it.</param>
public sealed record EntityRegistryState(IReadOnlyList<int> Generations, IReadOnlyList<bool> Alive);

/// <summary>
/// Issues and retires <see cref="EntityId"/>s (ADR 0004). Free slots are reused smallest index first (min-heap),
/// so the id sequence depends only on the create/destroy history and survives save and restore.
/// </summary>
public sealed class EntityRegistry : IStateHashable
{
    private readonly List<int> _generations = [];
    private readonly List<bool> _alive = [];

    // Unique indices as both element and priority, so equal-priority ordering never comes into play.
    private readonly PriorityQueue<int, int> _free = new();

    /// <summary>Creates an empty registry.</summary>
    public EntityRegistry()
    {
    }

    /// <summary>Number of slots ever allocated (highest index + 1).</summary>
    public int Capacity => _generations.Count;

    /// <summary>Number of live entities.</summary>
    public int AliveCount { get; private set; }

    /// <summary>Restores a registry from <see cref="GetState"/> output.</summary>
    /// <exception cref="ArgumentException">Lists differ in length or a generation is below 1.</exception>
    public static EntityRegistry FromState(EntityRegistryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Generations.Count != state.Alive.Count)
        {
            throw new ArgumentException("Generations and Alive must have the same length.", nameof(state));
        }

        var registry = new EntityRegistry();
        for (int i = 0; i < state.Generations.Count; i++)
        {
            int generation = state.Generations[i];
            if (generation < 1)
            {
                throw new ArgumentException($"Slot {i} has generation {generation}; generations start at 1.", nameof(state));
            }

            bool alive = state.Alive[i];
            registry._generations.Add(generation);
            registry._alive.Add(alive);
            if (alive)
            {
                registry.AliveCount++;
            }
            else
            {
                registry._free.Enqueue(i, i);
            }
        }

        return registry;
    }

    /// <summary>Creates an entity in the smallest free slot, or in a new slot when none is free.</summary>
    public EntityId Create()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free.Dequeue();
            _alive[index] = true;
        }
        else
        {
            index = _generations.Count;
            _generations.Add(1);
            _alive.Add(true);
        }

        AliveCount++;
        return new EntityId(index, _generations[index]);
    }

    /// <summary>Destroys a live entity and frees its slot with the next generation. Returns false for stale or unknown ids.</summary>
    public bool Destroy(EntityId id)
    {
        if (!IsAlive(id))
        {
            return false;
        }

        int index = id.Index;
        _alive[index] = false;

        // Wrapping after 2^31 - 2 reuses of one slot is acceptable; it cannot happen in a real session.
        _generations[index] = id.Generation == int.MaxValue ? 1 : id.Generation + 1;
        _free.Enqueue(index, index);
        AliveCount--;
        return true;
    }

    /// <summary>True when <paramref name="id"/> is the current live entity of its slot.</summary>
    public bool IsAlive(EntityId id) =>
        id.Index >= 0 && id.Index < _generations.Count && _alive[id.Index] && _generations[id.Index] == id.Generation;

    /// <summary>Live entities in ascending index order.</summary>
    public IEnumerable<EntityId> AliveEntities()
    {
        for (int i = 0; i < _generations.Count; i++)
        {
            if (_alive[i])
            {
                yield return new EntityId(i, _generations[i]);
            }
        }
    }

    /// <summary>Snapshot for saves; restore with <see cref="FromState"/>.</summary>
    public EntityRegistryState GetState() => new(_generations.ToArray(), _alive.ToArray());

    /// <summary>Writes the slot count as Int32, then per slot in index order the generation (Int32) and alive flag (bool).</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(_generations.Count);
        for (int i = 0; i < _generations.Count; i++)
        {
            writer.WriteInt32(_generations[i]);
            writer.WriteBool(_alive[i]);
        }
    }
}
