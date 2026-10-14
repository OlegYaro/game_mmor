using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Content;

/// <summary>Source-generated strict JSON metadata for content files: unknown members and missing required members are errors.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Strict,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow)]
[JsonSerializable(typeof(TimeBalanceFile))]
[JsonSerializable(typeof(FogBalanceFile))]
[JsonSerializable(typeof(SaveBalanceFile))]
[JsonSerializable(typeof(ItemFile))]
[JsonSerializable(typeof(WorldGenFile))]
public sealed partial class ContentJsonContext : JsonSerializerContext
{
}
