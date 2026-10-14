namespace Core.Determinism;

/// <summary>
/// Something whose simulation state can be written canonically. The same bytes feed the per-tick
/// state hash and the save code (ADR 0004, ADR 0005, ADR 0007).
/// </summary>
/// <remarks>
/// Implementations must write every field that affects future ticks, in a fixed order, iterating
/// collections by ascending ID. Never iterate a <c>Dictionary</c> or <c>HashSet</c> directly.
/// </remarks>
public interface IStateHashable
{
    /// <summary>Writes the canonical state to <paramref name="writer"/>.</summary>
    void WriteState(StateWriter writer);
}
