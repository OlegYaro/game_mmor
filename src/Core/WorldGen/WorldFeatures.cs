namespace Core.WorldGen;

/// <summary>
/// Kind of the resource node on a tile (B5: trees, stone, ore and bushes are a tile layer with a stock, not entities).
/// Stored as one byte per tile; values are part of the map hash and of future tile diffs: never renumber or reuse
/// them, only append.
/// </summary>
public enum ResourceKind : byte
{
    /// <summary>No resource node.</summary>
    None = 0,

    /// <summary>A tree (wood). Grows back after felling (B4, M3).</summary>
    Tree = 1,

    /// <summary>A stone outcrop.</summary>
    Stone = 2,

    /// <summary>An ore vein; never regrows (FR-WORLD-04).</summary>
    Ore = 3,

    /// <summary>A berry bush.</summary>
    BerryBush = 4,
}

/// <summary>Kind of a point of interest (FR-WORLD-07, B7: the three MVP kinds from GDD §12). Values are part of the map hash.</summary>
public enum PoiKind : byte
{
    /// <summary>Not assigned. Never present on a generated map.</summary>
    None = 0,

    /// <summary>Treasure chest (сундук с сокровищами).</summary>
    Chest = 1,

    /// <summary>Ruins (руины).</summary>
    Ruins = 2,

    /// <summary>Monster lair (логово монстров).</summary>
    Lair = 3,
}

/// <summary>Faction owning a settlement site (FR-WORLD-09). MVP: humans and orcs. Values are part of the map hash.</summary>
public enum Faction : byte
{
    /// <summary>Not assigned. Never present on a generated map.</summary>
    None = 0,

    /// <summary>Humans (люди): a good faction.</summary>
    Human = 1,

    /// <summary>Orcs (орки): an evil faction, kept at least 120 tiles from the start.</summary>
    Orc = 2,
}

/// <summary>
/// How far the start search had to relax the B1 checks. Ordered from strict to lenient; part of the map hash.
/// </summary>
public enum StartRelaxation : byte
{
    /// <summary>Every check passed: forest, stone and water in the zone, flat building area, room for evil sites.</summary>
    None = 0,

    /// <summary>The water requirement was dropped.</summary>
    Water = 1,

    /// <summary>Water and the room for all evil settlement sites were dropped (fewer evil sites may be placed).</summary>
    EvilDistance = 2,

    /// <summary>
    /// No candidate passed even then (beyond B1): the start is the first allowed flat tile, or the land tile nearest
    /// to the map centre.
    /// </summary>
    Fallback = 3,
}

/// <summary>A point of interest on a tile.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="X">Tile column.</param>
/// <param name="Y">Tile row.</param>
public readonly record struct PointOfInterest(PoiKind Kind, int X, int Y);

/// <summary>The start tile of the hero (FR-WORLD-08).</summary>
/// <param name="X">Tile column.</param>
/// <param name="Y">Tile row.</param>
public readonly record struct StartPoint(int X, int Y);

/// <summary>A reserved settlement site: the centre tile of a square footprint (<see cref="Content.SettlementParams.SiteSize"/>).</summary>
/// <param name="Faction">Faction that will settle there (M5).</param>
/// <param name="X">Centre tile column.</param>
/// <param name="Y">Centre tile row.</param>
public readonly record struct SettlementSite(Faction Faction, int X, int Y);

/// <summary>
/// Data names (snake_case, used in <c>data/worldgen</c>) and art ids (<c>data/art/atlas.json</c>) of the world's
/// placed things.
/// </summary>
public static class WorldNames
{
    /// <summary>Pattern of point of interest kind names (also emitted into the JSON schema).</summary>
    public const string PoiKindPattern = "^(chest|ruins|lair)$";

    /// <summary>Pattern of faction names (also emitted into the JSON schema).</summary>
    public const string FactionPattern = "^(human|orc)$";

    /// <summary>Art id of a reserved settlement site.</summary>
    public const string SettlementSiteArtId = "settlement_site";

    /// <summary>Art id of the start marker.</summary>
    public const string StartMarkerArtId = "start_marker";

    private static readonly string[] _resourceNames = ["none", "tree", "stone", "ore", "berry_bush"];
    private static readonly string[] _resourceArtIds = ["", "tree", "rock", "ore", "berry_bush"];
    private static readonly string[] _poiNames = ["none", "chest", "ruins", "lair"];
    private static readonly string[] _factionNames = ["none", "human", "orc"];

    /// <summary>Resource kinds that can appear on a generated map, in numeric order.</summary>
    public static IReadOnlyList<ResourceKind> Resources { get; } = [ResourceKind.Tree, ResourceKind.Stone, ResourceKind.Ore, ResourceKind.BerryBush];

    /// <summary>Point of interest kinds that can appear on a generated map, in numeric order.</summary>
    public static IReadOnlyList<PoiKind> PoiKinds { get; } = [PoiKind.Chest, PoiKind.Ruins, PoiKind.Lair];

    /// <summary>Factions that can own sites, in numeric order.</summary>
    public static IReadOnlyList<Faction> Factions { get; } = [Faction.Human, Faction.Orc];

    /// <summary>Data name of a resource kind, e.g. <c>berry_bush</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown kind.</exception>
    public static string ToName(ResourceKind kind) => Lookup(_resourceNames, (int)kind, kind);

    /// <summary>Data name of a point of interest kind, e.g. <c>ruins</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown kind.</exception>
    public static string ToName(PoiKind kind) => Lookup(_poiNames, (int)kind, kind);

    /// <summary>Data name of a faction, e.g. <c>orc</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown faction.</exception>
    public static string ToName(Faction faction) => Lookup(_factionNames, (int)faction, faction);

    /// <summary>Art id (atlas object cell) of a resource node, e.g. <c>rock</c> for stone.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceKind.None"/> or an unknown kind.</exception>
    public static string ArtId(ResourceKind kind) =>
        kind == ResourceKind.None ? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No art for an empty tile.") : Lookup(_resourceArtIds, (int)kind, kind);

    /// <summary>Art id (atlas object cell) of a point of interest, e.g. <c>poi_chest</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PoiKind.None"/> or an unknown kind.</exception>
    public static string ArtId(PoiKind kind) =>
        kind == PoiKind.None ? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No art for an unassigned point.") : "poi_" + ToName(kind);

    /// <summary>True for factions hostile to the player (kept away from the start, FR-WORLD-09).</summary>
    public static bool IsEvil(Faction faction) => faction == Faction.Orc;

    /// <summary>Parses a point of interest kind name; false for unknown names.</summary>
    public static bool TryParse(string? name, out PoiKind kind)
    {
        int index = Array.IndexOf(_poiNames, name);
        kind = index <= 0 ? PoiKind.None : (PoiKind)index;
        return index > 0;
    }

    /// <summary>Parses a faction name; false for unknown names.</summary>
    public static bool TryParse(string? name, out Faction faction)
    {
        int index = Array.IndexOf(_factionNames, name);
        faction = index <= 0 ? Faction.None : (Faction)index;
        return index > 0;
    }

    private static string Lookup<T>(string[] names, int index, T value) =>
        (uint)index < (uint)names.Length ? names[index] : throw new ArgumentOutOfRangeException(nameof(value), value, $"Unknown {typeof(T).Name}.");
}
