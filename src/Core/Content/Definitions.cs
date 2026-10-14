using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Core.Content;

/// <summary>Common shape of every content definition.</summary>
public interface IContentDef
{
    /// <summary>String id from the data file.</summary>
    string Id { get; }
}

/// <summary>Time constants of the simulation. All balance numbers are integers (ADR 0005).</summary>
/// <param name="Id">Definition id.</param>
/// <param name="TicksPerSecond">Simulation ticks per real second at speed x1.</param>
/// <param name="RealMinutesPerGameDay">Real minutes (at speed x1) in one game day.</param>
/// <param name="NightPermille">Share of the day that is night, in thousandths.</param>
/// <param name="DaysPerWeek">Days in a week.</param>
/// <param name="DaysPerSeason">Days in a season.</param>
/// <param name="SeasonsPerYear">Seasons in a year.</param>
public sealed record TimeBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, 1000)] int TicksPerSecond,
    [property: Range(1, 1440)] int RealMinutesPerGameDay,
    [property: Range(0, 1000)] int NightPermille,
    [property: Range(1, 100)] int DaysPerWeek,
    [property: Range(1, 1000)] int DaysPerSeason,
    [property: Range(1, 100)] int SeasonsPerYear) : IContentDef;

/// <summary>An item type.</summary>
/// <param name="Id">Definition id.</param>
/// <param name="StackSize">Maximum items in one stack.</param>
/// <param name="Weight">Weight of one item in grams.</param>
/// <param name="NameKey">Localization key of the name; derived from the id when omitted.</param>
/// <param name="SpoilsInto">Optional id of another item this one turns into when it spoils.</param>
public sealed record ItemDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, 100000)] int StackSize,
    [property: Range(0, 1000000)] int Weight,
    string? NameKey = null,
    [property: RegularExpression(ContentId.Pattern)] string? SpoilsInto = null) : IContentDef
{
    /// <summary>The localization key of the name: the explicit one or <c>item.{id}.name</c>.</summary>
    [JsonIgnore]
    public string EffectiveNameKey => NameKey ?? ContentKeys.ItemName(Id);
}

/// <summary>Builds localization keys from content ids (FR-UI-09).</summary>
public static class ContentKeys
{
    /// <summary>Name key of an item, e.g. <c>item.wood.name</c>; a mod prefix separator becomes a dot.</summary>
    public static string ItemName(string id) => "item." + id.Replace(':', '.') + ".name";
}

/// <summary>File envelope for <c>data/balance/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record TimeBalanceFile(
    IReadOnlyList<TimeBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/items/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record ItemFile(
    IReadOnlyList<ItemDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
