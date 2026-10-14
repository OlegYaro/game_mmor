namespace Core.WorldGen;

/// <summary>
/// Biome of a tile (GDD §4, FR-WORLD-03). Stored as one byte per tile; the numeric values are part of the terrain
/// hash, saves and art tables: never renumber or reuse them, only append.
/// </summary>
public enum Biome : byte
{
    /// <summary>Not assigned. Never present on a generated map.</summary>
    None = 0,

    /// <summary>Standing water: ocean or lake (see <see cref="TileFeatures.Ocean"/> and <see cref="TileFeatures.Lake"/>).</summary>
    Water = 1,

    /// <summary>Meadow (луга): the start zone biome.</summary>
    Meadow = 2,

    /// <summary>Forest (лес).</summary>
    Forest = 3,

    /// <summary>Mountains and hills (горы и холмы).</summary>
    Mountain = 4,

    /// <summary>Wasteland (пустоши).</summary>
    Wasteland = 5,
}

/// <summary>Per-tile features of the terrain (bit flags). Stored as one byte per tile; values are part of the terrain hash.</summary>
[Flags]
public enum TileFeatures : byte
{
    /// <summary>Plain land.</summary>
    None = 0,

    /// <summary>Standing water (ocean or lake). Always together with exactly one of <see cref="Ocean"/>, <see cref="Lake"/>.</summary>
    Water = 1,

    /// <summary>Water connected to the map edge.</summary>
    Ocean = 2,

    /// <summary>Inland water not connected to the map edge.</summary>
    Lake = 4,

    /// <summary>A river flows over this land tile. Never set on <see cref="Water"/> tiles.</summary>
    River = 8,
}

/// <summary>Data names of biomes (snake_case, used in <c>data/worldgen</c> and art tables).</summary>
public static class BiomeNames
{
    /// <summary>Pattern of the land biome names allowed in the biome table (also emitted into the JSON schema).</summary>
    public const string LandPattern = "^(meadow|forest|mountain|wasteland)$";

    private static readonly string[] _names = ["none", "water", "meadow", "forest", "mountain", "wasteland"];

    /// <summary>All biomes that can appear on a generated map, in numeric order.</summary>
    public static IReadOnlyList<Biome> Generated { get; } = [Biome.Water, Biome.Meadow, Biome.Forest, Biome.Mountain, Biome.Wasteland];

    /// <summary>Land biomes (everything generated except water), in numeric order.</summary>
    public static IReadOnlyList<Biome> Land { get; } = [Biome.Meadow, Biome.Forest, Biome.Mountain, Biome.Wasteland];

    /// <summary>Data name of a biome, e.g. <c>meadow</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a known biome.</exception>
    public static string ToName(Biome biome) =>
        (int)biome < _names.Length ? _names[(int)biome] : throw new ArgumentOutOfRangeException(nameof(biome), biome, "Unknown biome.");

    /// <summary>Parses a data name; false for unknown names.</summary>
    public static bool TryParse(string? name, out Biome biome)
    {
        int index = Array.IndexOf(_names, name);
        biome = index < 0 ? Biome.None : (Biome)index;
        return index >= 0;
    }
}
