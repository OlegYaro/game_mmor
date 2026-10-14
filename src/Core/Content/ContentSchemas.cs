using System.Text.Json.Serialization.Metadata;

namespace Core.Content;

/// <summary>One content category: a data folder, its file type and the schema file generated for it.</summary>
/// <param name="Folder">Folder under <c>data/</c>.</param>
/// <param name="SchemaFileName">File name under <c>data/schema/</c>.</param>
/// <param name="TypeInfo">Source-generated metadata of the file envelope.</param>
public sealed record ContentCategory(string Folder, string SchemaFileName, JsonTypeInfo TypeInfo);

/// <summary>Registry of content categories shared by the loader, SchemaGen and tests.</summary>
public static class ContentCategories
{
    /// <summary>All categories, ordered by folder.</summary>
    public static IReadOnlyList<ContentCategory> All { get; } =
    [
        new("balance", "balance.schema.json", ContentJsonContext.Default.TimeBalanceFile),
        new("items", "items.schema.json", ContentJsonContext.Default.ItemFile),
    ];
}
