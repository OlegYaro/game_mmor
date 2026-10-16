using Core.Determinism;

namespace Core.Sim;

/// <summary>
/// Creates a resident next to the settlement center (console <c>spawnresident</c>, NFR-07; specialists in M3b): name and
/// skills like a newcomer's, waiting for work, in the nearest house with a free place or homeless. Refused (logged)
/// without a world or a center, at <see cref="Content.EconomyBalanceDef.MaxResidents"/>, or for an unknown profession.
/// </summary>
/// <param name="Profession">Profession id (ordinary or specialist); empty for the next one of <see cref="Content.EconomyBalanceDef.AutoAssignPattern"/>.</param>
public sealed record SpawnResidentCommand(string Profession = "") : ISimCommand
{
    /// <summary>Value of <see cref="Kind"/>.</summary>
    public const string KindName = "spawn_resident";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public void Execute(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        Residents.ResidentRules.OrderSpawn(sim, Profession);
    }

    /// <summary>Writes the profession id.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Profession);
    }
}
