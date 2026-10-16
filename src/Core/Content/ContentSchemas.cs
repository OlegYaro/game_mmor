using System.Text.Json.Serialization.Metadata;

namespace Core.Content;

/// <summary>One content category: a data folder (optionally one file in it), its file type and the schema file generated for it.</summary>
/// <param name="Folder">Folder under <c>data/</c>.</param>
/// <param name="SchemaFileName">File name under <c>data/schema/</c>.</param>
/// <param name="TypeInfo">Source-generated metadata of the file envelope.</param>
/// <param name="FileName">
/// When set, the category covers only this file name in <paramref name="Folder"/> (e.g. <c>balance/fog.json</c>);
/// otherwise it covers every other <c>.json</c> file of the folder.
/// </param>
public sealed record ContentCategory(string Folder, string SchemaFileName, JsonTypeInfo TypeInfo, string? FileName = null);

/// <summary>Registry of content categories shared by the loader, SchemaGen and tests.</summary>
public static class ContentCategories
{
    /// <summary>
    /// Folders under <c>data/</c> that hold data files which are not content definitions (the art atlas table, ADR 0010).
    /// The loader skips them (they still count toward the content hash); each has its own schema next to the data.
    /// </summary>
    public static IReadOnlyList<string> NonDefinitionFolders { get; } = ["art"];

    /// <summary>All categories, ordered by folder (a file-specific category before the folder's general one).</summary>
    public static IReadOnlyList<ContentCategory> All { get; } =
    [
        new("balance", "fog.schema.json", ContentJsonContext.Default.FogBalanceFile, "fog.json"),
        new("balance", "saves.schema.json", ContentJsonContext.Default.SaveBalanceFile, "saves.json"),
        new("balance", "hero.schema.json", ContentJsonContext.Default.HeroBalanceFile, "hero.json"),
        new("balance", "construction.schema.json", ContentJsonContext.Default.ConstructionBalanceFile, "construction.json"),
        new("balance", "relations.schema.json", ContentJsonContext.Default.RelationModifierFile, "relations.json"),
        new("balance", "economy.schema.json", ContentJsonContext.Default.EconomyBalanceFile, "economy.json"),
        new("balance", "roads.schema.json", ContentJsonContext.Default.RoadsBalanceFile, "roads.json"),
        new("balance", "regrowth.schema.json", ContentJsonContext.Default.RegrowthBalanceFile, "regrowth.json"),
        new("balance", "balance.schema.json", ContentJsonContext.Default.TimeBalanceFile),
        new("buildings", "buildings.schema.json", ContentJsonContext.Default.BuildingFile),
        new("eras", "eras.schema.json", ContentJsonContext.Default.EraFile),
        new("items", "items.schema.json", ContentJsonContext.Default.ItemFile),
        new("names", "names.schema.json", ContentJsonContext.Default.NameListFile),
        new("professions", "professions.schema.json", ContentJsonContext.Default.ProfessionFile),
        new("races", "races.schema.json", ContentJsonContext.Default.RaceFile),
        new("worldgen", "worldgen.schema.json", ContentJsonContext.Default.WorldGenFile),
    ];

    /// <summary>
    /// The category of a data file path relative to <c>data/</c> (forward slashes): the file-specific category of its
    /// folder when the file name matches, otherwise the folder's general category; null when there is none.
    /// </summary>
    public static ContentCategory? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return null;
        }

        string folder = path[..slash];
        string fileName = path[(slash + 1)..];
        return All.FirstOrDefault(c => c.Folder == folder && c.FileName == fileName)
            ?? All.FirstOrDefault(c => c.Folder == folder && c.FileName is null);
    }
}
