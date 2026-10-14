using Core.Determinism;

namespace Core.Entities;

/// <summary>
/// Handle of an entity: a slot index plus the generation of that slot (ADR 0004). When an entity is destroyed its
/// index is reused later with a higher generation, so stale handles never match the new entity.
/// </summary>
/// <remarks>Generations start at 1, so <c>default(EntityId)</c> (<see cref="None"/>) never refers to a live entity.</remarks>
/// <param name="Index">Slot index, 0 or greater.</param>
/// <param name="Generation">Generation of the slot, 1 or greater for issued ids.</param>
public readonly record struct EntityId(int Index, int Generation) : IStateHashable
{
    /// <summary>The invalid id (index 0, generation 0).</summary>
    public static EntityId None => default;

    /// <summary>True for ids a registry could have issued (generation 1 or greater, index not negative).</summary>
    public bool IsValid => Generation > 0 && Index >= 0;

    /// <summary>Writes the index, then the generation, both as Int32.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Index);
        writer.WriteInt32(Generation);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{Index}v{Generation}");
}
