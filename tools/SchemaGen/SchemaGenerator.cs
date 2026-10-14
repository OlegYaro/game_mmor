using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Core.Content;

namespace SchemaGen;

/// <summary>Builds the JSON schemas of all content categories from the C# types, deterministically.</summary>
public static class SchemaGenerator
{
    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Returns schema file name to file text (LF endings, trailing newline).</summary>
    public static IReadOnlyDictionary<string, string> Generate()
    {
        SortedDictionary<string, string> result = new(StringComparer.Ordinal);
        JsonSchemaExporterOptions options = new() { TransformSchemaNode = Transform };
        foreach (ContentCategory category in ContentCategories.All)
        {
            JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(category.TypeInfo, options);
            JsonObject root = (JsonObject)schema;
            JsonObject ordered = new()
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["title"] = category.TypeInfo.Type.Name,
            };
            foreach (KeyValuePair<string, JsonNode?> pair in root.ToList())
            {
                root.Remove(pair.Key);
                ordered[pair.Key] = pair.Value;
            }

            result[category.SchemaFileName] = ordered.ToJsonString(Output) + "\n";
        }

        return result;
    }

    // Translates DataAnnotations on record properties into schema keywords.
    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is JsonObject obj && context.PropertyInfo?.AttributeProvider is { } provider)
        {
            foreach (object attribute in provider.GetCustomAttributes(inherit: true))
            {
                switch (attribute)
                {
                    case RangeAttribute range:
                        obj["minimum"] = Convert.ToInt32(range.Minimum, System.Globalization.CultureInfo.InvariantCulture);
                        obj["maximum"] = Convert.ToInt32(range.Maximum, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case RegularExpressionAttribute regex:
                        obj["pattern"] = regex.Pattern;
                        break;
                }
            }
        }

        return schema;
    }

    /// <summary>Finds the repository root by walking up to Game.sln.</summary>
    public static string FindRepoRoot(string start)
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Game.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Game.sln not found above " + start);
    }
}
