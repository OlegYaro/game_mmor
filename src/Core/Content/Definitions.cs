using System.ComponentModel;
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

/// <summary>
/// Fog of war sight radii in tiles (FR-WORLD-06, B3), <c>data/balance/fog.json</c>. A tile is seen when its squared
/// distance to the source tile is at most the squared radius. Night radii apply while the calendar says it is night.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="HeroDay">Hero by day.</param>
/// <param name="HeroNight">Hero at night.</param>
/// <param name="VillagerDay">Villager by day.</param>
/// <param name="VillagerNight">Villager at night.</param>
/// <param name="TowerDay">Watch tower by day.</param>
/// <param name="TowerNight">Watch tower at night.</param>
public sealed record FogBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, FogBalanceDef.MaxRadius)] int HeroDay,
    [property: Range(1, FogBalanceDef.MaxRadius)] int HeroNight,
    [property: Range(1, FogBalanceDef.MaxRadius)] int VillagerDay,
    [property: Range(1, FogBalanceDef.MaxRadius)] int VillagerNight,
    [property: Range(1, FogBalanceDef.MaxRadius)] int TowerDay,
    [property: Range(1, FogBalanceDef.MaxRadius)] int TowerNight) : IContentDef
{
    /// <summary>Largest allowed radius; keeps one fog update cheap (a circle is at most 129 rows).</summary>
    public const int MaxRadius = 64;
}

/// <summary>An item type.</summary>
/// <param name="Id">Definition id.</param>
/// <param name="StackSize">Maximum items in one stack.</param>
/// <param name="Weight">Weight of one item in grams.</param>
/// <param name="NameKey">Localization key of the name; derived from the id when omitted.</param>
/// <param name="SpoilsInto">Optional id of another item this one turns into when it spoils.</param>
/// <param name="Category">Optional item category: resource, food, material or currency (M3a).</param>
/// <param name="FoodValue">Food units one item gives when eaten (provisional); 0 means not edible. Only food items may have more than 0.</param>
public sealed record ItemDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, 100000)] int StackSize,
    [property: Range(0, 1000000)] int Weight,
    string? NameKey = null,
    [property: RegularExpression(ContentId.Pattern)] string? SpoilsInto = null,
    [property: RegularExpression(ItemCategories.Pattern)]
    [property: Description("resource, food, material or currency. Optional.")]
    string? Category = null,
    [property: Range(0, 1000)]
    [property: Description("Food units one item gives when eaten (provisional). A resident needs foodPerResidentPerDay units per day (data/balance/economy.json). 0 means not edible; only food items may be above 0.")]
    int FoodValue = 0) : IContentDef
{
    /// <summary>The localization key of the name: the explicit one or <c>item.{id}.name</c>.</summary>
    [JsonIgnore]
    public string EffectiveNameKey => NameKey ?? ContentKeys.ItemName(Id);

    /// <summary>Checks that only food items are edible.</summary>
    /// <returns>(JSON path below the definition, message) per problem.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        if (FoodValue > 0 && Category != ItemCategories.Food)
        {
            yield return ("foodValue", $"Item '{Id}' has a foodValue but is not in the food category.");
        }
    }
}

/// <summary>Item categories (M3a); the pattern is also emitted into the JSON schema.</summary>
public static class ItemCategories
{
    /// <summary>Pattern of an item category.</summary>
    public const string Pattern = "^(resource|food|material|currency)$";

    /// <summary>Category of edible items (<see cref="ItemDef.FoodValue"/> above 0).</summary>
    public const string Food = "food";

    /// <summary>Category of money (the tax is paid in it).</summary>
    public const string Currency = "currency";

    /// <summary>
    /// The categories in <see cref="Pattern"/> order. Category <c>i</c> is bit <c>1 &lt;&lt; i</c> of a storage filter
    /// mask (FR-ECO-01), so the order is append only.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = ["resource", "food", "material", "currency"];

    /// <summary>Mask with every category's bit set.</summary>
    public static int AllMask => (1 << All.Count) - 1;

    /// <summary>
    /// The filter bit of <paramref name="category"/>, or 0 for an item without a category (such items pass every
    /// filter: a filter can only exclude what it can name).
    /// </summary>
    public static int BitOf(string? category)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], category, StringComparison.Ordinal))
            {
                return 1 << i;
            }
        }

        return 0;
    }
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

/// <summary>File envelope for <c>data/balance/fog.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record FogBalanceFile(
    IReadOnlyList<FogBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/items/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record ItemFile(
    IReadOnlyList<ItemDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
