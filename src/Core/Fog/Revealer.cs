using Core.Content;
using Core.Determinism;
using Core.Entities;

namespace Core.Fog;

/// <summary>
/// What kind of sight a <see cref="Revealer"/> has; selects the radii in <see cref="FogBalanceDef"/> (B3). Values are
/// part of the state hash and names of the save format: never renumber, only append.
/// </summary>
public enum RevealerKind : byte
{
    /// <summary>Not assigned; sees nothing.</summary>
    None = 0,

    /// <summary>The hero (12 tiles, 6 at night). Until the hero exists (M2), the start marker uses this.</summary>
    Hero = 1,

    /// <summary>A villager (8 tiles, 4 at night).</summary>
    Villager = 2,

    /// <summary>A watch tower (16 tiles).</summary>
    Tower = 3,
}

/// <summary>
/// Component of entities that reveal the fog of war around their <see cref="Position"/> (FR-WORLD-06). The source
/// tile is the tile of the position; the radius comes from data (<see cref="FogBalanceDef"/>), by kind and night.
/// </summary>
/// <param name="Kind">Sight kind.</param>
public readonly record struct Revealer(RevealerKind Kind) : IComponent
{
    /// <summary>Writes the kind as one byte.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte((byte)Kind);
    }

    /// <summary>Sight radius in tiles of <paramref name="kind"/> by day or night; 0 for <see cref="RevealerKind.None"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown kind.</exception>
    public static int RadiusOf(FogBalanceDef balance, RevealerKind kind, bool night)
    {
        ArgumentNullException.ThrowIfNull(balance);
        return kind switch
        {
            RevealerKind.None => 0,
            RevealerKind.Hero => night ? balance.HeroNight : balance.HeroDay,
            RevealerKind.Villager => night ? balance.VillagerNight : balance.VillagerDay,
            RevealerKind.Tower => night ? balance.TowerNight : balance.TowerDay,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown revealer kind."),
        };
    }
}
