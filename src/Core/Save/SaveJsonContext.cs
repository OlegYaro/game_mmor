using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Save;

/// <summary>
/// Source-generated, canonical JSON metadata for saves (ADR 0007): compact, camelCase, every property always written
/// in declaration order, strict on read (unknown, duplicate, missing or null-for-non-nullable members are errors).
/// Numbers are integers only, so NaN and Infinity cannot occur; named float literals stay disallowed.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Strict,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    WriteIndented = false,
    IndentSize = 2,
    NewLine = "\n")]
[JsonSerializable(typeof(SaveHeader))]
[JsonSerializable(typeof(SaveBodyDto))]
public sealed partial class SaveJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Same contract, indented with two spaces and LF line ends (developer saves). Created per call: it is a rare
    /// developer path, and a static field would depend on the undefined initializer order of the partial class.
    /// </summary>
    internal static SaveJsonContext CreateIndented() =>
        new(new JsonSerializerOptions(Default.Options) { WriteIndented = true });
}
