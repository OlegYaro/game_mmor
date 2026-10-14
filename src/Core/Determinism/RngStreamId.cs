namespace Core.Determinism;

/// <summary>
/// Subsystems that own an independent random stream (ADR 0005). The numeric values take part in seed
/// derivation and in saves: never renumber or reuse them, only append.
/// </summary>
public enum RngStreamId
{
    /// <summary>World generation.</summary>
    WorldGen = 0,

    /// <summary>Combat resolution.</summary>
    Combat = 1,

    /// <summary>Gathering, mining, harvesting.</summary>
    Gathering = 2,

    /// <summary>Colonist and creature AI.</summary>
    Ai = 3,

    /// <summary>Storyteller events.</summary>
    Storyteller = 4,

    /// <summary>Faction behavior.</summary>
    Factions = 5,
}
