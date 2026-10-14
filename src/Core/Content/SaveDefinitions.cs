using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Core.Content;

/// <summary>
/// Save slot numbers (FR-SAVE-01), <c>data/balance/saves.json</c>: how many manual slots the player has and how many
/// autosaves are kept before the oldest is dropped.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="ManualSlots">Number of manual slots (<c>slot_1</c> to <c>slot_N</c>).</param>
/// <param name="AutosavesKept">Number of rotating autosaves kept (<c>autosave_1</c> is the newest).</param>
public sealed record SaveBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, SaveBalanceDef.MaxSlots)] int ManualSlots,
    [property: Range(1, SaveBalanceDef.MaxSlots)] int AutosavesKept) : IContentDef
{
    /// <summary>Largest allowed slot count; keeps the slot list usable.</summary>
    public const int MaxSlots = 99;
}

/// <summary>File envelope for <c>data/balance/saves.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record SaveBalanceFile(
    IReadOnlyList<SaveBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
