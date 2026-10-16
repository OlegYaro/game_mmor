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

    /// <summary>
    /// Events: newcomers, later caravans (ADR 0005, renamed from "storyteller" on 15.10.2026; the save name stays
    /// <c>storyteller</c>). Faction decisions such as raids use <see cref="Factions"/>, so they do not depend on how
    /// many newcomers came.
    /// </summary>
    Events = 4,

    /// <summary>Faction behavior.</summary>
    Factions = 5,
}
