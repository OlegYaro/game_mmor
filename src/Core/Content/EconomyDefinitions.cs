using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Core.Content;

/// <summary>
/// Economy and resident numbers of M3a (FR-ECO-10, FR-ECO-06, FR-NPC-01, FR-NPC-06), <c>data/balance/economy.json</c>.
/// Every number is provisional ([estimate] from docs/research/economy-six-chains.md) and is tuned by simulation.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="FoodPerResidentPerDay">Food units one resident eats per game day.</param>
/// <param name="ShortageSlowdownPermille">Work speed while the settlement is out of food, in thousandths.</param>
/// <param name="DaysWithoutFoodUntilFirstLeaves">Days without food after which the first resident leaves.</param>
/// <param name="LeaveIntervalDays">Days between two leaving residents while food is still missing.</param>
/// <param name="WarnDaysBeforeEmpty">Days of food left at which the player is warned.</param>
/// <param name="TaxPerResidentPerDay">Gold per resident per day; multiplied by the settlement center level in code.</param>
/// <param name="MaxResidents">Upper limit of residents (NFR-01).</param>
/// <param name="ResidentStepTicks">Ticks a resident needs to cross one tile walking.</param>
/// <param name="ArrivalHour">Hour of the day (0..23) when a newcomer may arrive.</param>
/// <param name="ArrivalFreeSlotPoints">Chance points (percent) per free house place.</param>
/// <param name="ArrivalMaxSlots">Free places counted at most.</param>
/// <param name="ArrivalFoodPoints">Chance points when food lasts <paramref name="ArrivalFoodDays"/> days for everyone including the newcomer.</param>
/// <param name="ArrivalFoodDays">Days of food needed for the food points.</param>
/// <param name="ArrivalNoRaidPoints">Chance points when there was no raid for <paramref name="ArrivalNoRaidDays"/> days.</param>
/// <param name="ArrivalNoRaidDays">Raid-free days needed for the no-raid points.</param>
/// <param name="ArrivalFamePointsDivisor">Fame is divided by this to get chance points.</param>
/// <param name="ArrivalEraPoints">Chance points per era, in era order; one entry per era.</param>
/// <param name="ArrivalChanceCapPermille">Largest arrival chance, in thousandths.</param>
/// <param name="HaulCapacity">Items a carrier moves in one trip.</param>
/// <param name="StorageStacksPerItem">Stacks of one item a storage holds at most, so that one good cannot fill it and leave no room for food and the tax.</param>
/// <param name="JobRetryCooldownTicks">Ticks a failed job stays banned for the resident.</param>
/// <param name="ThinkIntervalTicks">Ticks between two decisions of an idle resident.</param>
/// <param name="IdleThinkMaxIntervalTicks">Longest wait between two looks of a resident that keeps finding no work: the wait doubles from <paramref name="ThinkIntervalTicks"/> after every look that found nothing, up to this.</param>
/// <param name="SkillSpeedPermillePerPoint">Work speed gain per skill point, in thousandths.</param>
/// <param name="InitialSkillMax">A newcomer starts with each skill between 0 and this value.</param>
/// <param name="JobSearchMarginTiles">Tiles beyond the territory border where residents still look for resource nodes to work.</param>
/// <param name="AutoAssignPattern">Profession ids handed to newcomers in turn, wrapping around; ordinary professions only.</param>
public sealed record EconomyBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, 100)] [property: Description("Food units one resident eats per game day (provisional).")] int FoodPerResidentPerDay,
    [property: Range(1, 1000)] [property: Description("Work speed while the settlement is out of food, in thousandths (provisional).")] int ShortageSlowdownPermille,
    [property: Range(0, 100)] [property: Description("Days without food after which the first resident leaves (provisional).")] int DaysWithoutFoodUntilFirstLeaves,
    [property: Range(1, 100)] [property: Description("Days between two leaving residents while food is still missing (provisional).")] int LeaveIntervalDays,
    [property: Range(0, 100)] [property: Description("Days of food left at which the player is warned (provisional).")] int WarnDaysBeforeEmpty,
    [property: Range(0, 1000)] [property: Description("Gold per resident per day; multiplied by the settlement center level in code (provisional).")] int TaxPerResidentPerDay,
    [property: Range(1, 1000)] [property: Description("Upper limit of residents (NFR-01).")] int MaxResidents,
    [property: Range(1, 100)] [property: Description("Ticks a resident needs to cross one tile walking (provisional).")] int ResidentStepTicks,
    [property: Range(0, 23)] [property: Description("Hour of the day when a newcomer may arrive (provisional).")] int ArrivalHour,
    [property: Range(0, 100)] [property: Description("Arrival chance points per free house place (provisional).")] int ArrivalFreeSlotPoints,
    [property: Range(1, 100)] [property: Description("Free places counted at most (provisional).")] int ArrivalMaxSlots,
    [property: Range(0, 100)] [property: Description("Arrival chance points when food lasts arrivalFoodDays for everyone including the newcomer (provisional).")] int ArrivalFoodPoints,
    [property: Range(1, 100)] [property: Description("Days of food needed for the food points (provisional).")] int ArrivalFoodDays,
    [property: Range(0, 100)] [property: Description("Arrival chance points when there was no raid for arrivalNoRaidDays (provisional).")] int ArrivalNoRaidPoints,
    [property: Range(1, 100)] [property: Description("Raid-free days needed for the no-raid points (provisional).")] int ArrivalNoRaidDays,
    [property: Range(1, 1000)] [property: Description("Fame is divided by this to get arrival chance points (provisional).")] int ArrivalFamePointsDivisor,
    [property: Description("Arrival chance points per era, in era order; one entry per era, each 0..100 (provisional).")] IReadOnlyList<int> ArrivalEraPoints,
    [property: Range(0, 1000)] [property: Description("Largest arrival chance, in thousandths (provisional).")] int ArrivalChanceCapPermille,
    [property: Range(1, 1000)] [property: Description("Items a carrier moves in one trip (provisional).")] int HaulCapacity,
    [property: Range(1, 1000)] [property: Description("Stacks of one item a storage holds at most, so that one good cannot fill it (provisional).")] int StorageStacksPerItem,
    [property: Range(1, 1000000)] [property: Description("Ticks a failed job stays banned for the resident, at least 1 so a ban outlives the tick it was given in (provisional).")] int JobRetryCooldownTicks,
    [property: Range(1, 1000)] [property: Description("Ticks between two decisions of an idle resident (provisional).")] int ThinkIntervalTicks,
    [property: Range(1, 1000000)] [property: Description("Longest wait between two looks of a resident that keeps finding no work; the wait doubles from thinkIntervalTicks after every look that found nothing (provisional).")] int IdleThinkMaxIntervalTicks,
    [property: Range(0, 100)] [property: Description("Work speed gain per skill point, in thousandths (provisional).")] int SkillSpeedPermillePerPoint,
    [property: Range(0, 20)] [property: Description("A newcomer starts with each skill between 0 and this value (provisional).")] int InitialSkillMax,
    [property: Range(0, 64)] [property: Description("Tiles beyond the territory border where residents still look for resource nodes to work (provisional).")] int JobSearchMarginTiles,
    [property: Description("Profession ids handed to newcomers in turn, wrapping around. Ordinary professions only; at least one.")] IReadOnlyList<string> AutoAssignPattern) : IContentDef
{
    /// <summary>Checks the cross-field rules that attributes cannot express.</summary>
    /// <returns>(JSON path below the definition, message) per problem.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        if (ArrivalEraPoints.Count == 0)
        {
            yield return ("arrivalEraPoints", "Needs one entry per era.");
        }

        for (int i = 0; i < ArrivalEraPoints.Count; i++)
        {
            if (ArrivalEraPoints[i] is < 0 or > 100)
            {
                yield return ($"arrivalEraPoints[{i}]", "Era arrival points must be 0..100.");
            }
        }

        if (AutoAssignPattern.Count == 0)
        {
            yield return ("autoAssignPattern", "Needs at least one profession.");
        }

        if (IdleThinkMaxIntervalTicks < ThinkIntervalTicks)
        {
            yield return ("idleThinkMaxIntervalTicks", "idleThinkMaxIntervalTicks must not be below thinkIntervalTicks.");
        }
    }
}

/// <summary>
/// Footpath and road numbers (FR-WORLD-15, docs/research/roads-from-paths.md), <c>data/balance/roads.json</c>. A tile has a
/// walk counter that grows with every step and fades lazily; the counter picks the tile class. All numbers are provisional.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="StepIncrement">Counter growth per step on a tile.</param>
/// <param name="DecayEveryTicks">The counter falls by 1 per this many ticks, computed lazily on access.</param>
/// <param name="MaxCounter">Largest counter value.</param>
/// <param name="TrailThreshold">Counter at which a tile becomes a trail.</param>
/// <param name="RoadThreshold">Counter at which a tile becomes a road.</param>
/// <param name="TrailKeepThreshold">A trail stays a trail while the counter is at least this (hysteresis).</param>
/// <param name="RoadKeepThreshold">A road stays a road while the counter is at least this (hysteresis).</param>
/// <param name="TrailCostPermille">Path step cost on a trail relative to base, in thousandths (lower is faster).</param>
/// <param name="RoadCostPermille">Path step cost on a road relative to base, in thousandths (lower is faster).</param>
public sealed record RoadsBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, 255)] [property: Description("Counter growth per step on a tile (provisional).")] int StepIncrement,
    [property: Range(1, 1000000)] [property: Description("The counter falls by 1 per this many ticks, computed lazily on access (provisional).")] int DecayEveryTicks,
    [property: Range(1, 255)] [property: Description("Largest counter value (provisional).")] int MaxCounter,
    [property: Range(1, 255)] [property: Description("Counter at which a tile becomes a trail (provisional).")] int TrailThreshold,
    [property: Range(1, 255)] [property: Description("Counter at which a tile becomes a road (provisional).")] int RoadThreshold,
    [property: Range(1, 255)] [property: Description("A trail stays a trail while the counter is at least this (provisional).")] int TrailKeepThreshold,
    [property: Range(1, 255)] [property: Description("A road stays a road while the counter is at least this (provisional).")] int RoadKeepThreshold,
    [property: Range(1, 1000)] [property: Description("Path step cost on a trail relative to base, in thousandths; lower is faster (provisional).")] int TrailCostPermille,
    [property: Range(1, 1000)] [property: Description("Path step cost on a road relative to base, in thousandths; lower is faster (provisional).")] int RoadCostPermille) : IContentDef
{
    /// <summary>Checks the cross-field rules that attributes cannot express.</summary>
    /// <returns>(JSON path below the definition, message) per problem.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        if (TrailThreshold >= RoadThreshold)
        {
            yield return ("roadThreshold", "roadThreshold must be above trailThreshold.");
        }

        if (RoadThreshold > MaxCounter)
        {
            yield return ("maxCounter", "maxCounter must reach roadThreshold.");
        }

        if (TrailKeepThreshold > TrailThreshold)
        {
            yield return ("trailKeepThreshold", "trailKeepThreshold must not exceed trailThreshold.");
        }

        if (RoadKeepThreshold > RoadThreshold)
        {
            yield return ("roadKeepThreshold", "roadKeepThreshold must not exceed roadThreshold.");
        }

        if (TrailKeepThreshold >= RoadKeepThreshold)
        {
            yield return ("roadKeepThreshold", "roadKeepThreshold must be above trailKeepThreshold.");
        }

        if (RoadCostPermille >= TrailCostPermille)
        {
            yield return ("roadCostPermille", "A road must be cheaper to walk than a trail.");
        }
    }
}

/// <summary>
/// Tree regrowth and planting (FR-WORLD-04), <c>data/balance/regrowth.json</c>. Timers are measured in game days and
/// run lazily. All numbers are provisional.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="StumpRegrowDaysNearTree">Days for a stump to regrow when a living tree is within <paramref name="NearTreeRadius"/>.</param>
/// <param name="StumpRegrowDaysFar">Days for a stump to regrow otherwise.</param>
/// <param name="NearTreeRadius">Radius in tiles that counts as near a tree.</param>
/// <param name="SaplingGrowDays">Days for a planted sapling to become a tree.</param>
/// <param name="PlantActionTicks">Ticks a resident needs to plant a sapling.</param>
public sealed record RegrowthBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, 1000)] [property: Description("Days for a stump to regrow when a living tree is within nearTreeRadius (provisional).")] int StumpRegrowDaysNearTree,
    [property: Range(1, 1000)] [property: Description("Days for a stump to regrow otherwise; not below the near value (provisional).")] int StumpRegrowDaysFar,
    [property: Range(1, 16)] [property: Description("Radius in tiles that counts as near a living tree (provisional).")] int NearTreeRadius,
    [property: Range(1, 1000)] [property: Description("Days for a planted sapling to become a tree (provisional).")] int SaplingGrowDays,
    [property: Range(1, 100000)] [property: Description("Ticks a resident needs to plant a sapling (provisional).")] int PlantActionTicks) : IContentDef
{
    /// <summary>Checks that a far stump is not faster than a near one.</summary>
    /// <returns>(JSON path below the definition, message) per problem.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        if (StumpRegrowDaysFar < StumpRegrowDaysNearTree)
        {
            yield return ("stumpRegrowDaysFar", "stumpRegrowDaysFar must not be below stumpRegrowDaysNearTree.");
        }
    }
}

/// <summary>
/// First names for residents, <c>data/names/*.json</c>. Names are invented proper names written in Latin letters, not
/// translated. One list per race.
/// </summary>
/// <param name="Id">Definition id: the race id plus <c>_names</c> (e.g. <c>human_names</c>), because ids share one namespace with the race itself.</param>
/// <param name="Names">Unique names: a capital letter followed by 2 to 11 lowercase letters.</param>
public sealed record NameListDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Description("Unique invented first names: a capital Latin letter followed by 2 to 11 lowercase Latin letters.")]
    IReadOnlyList<string> Names) : IContentDef
{
    /// <summary>Pattern of a single name.</summary>
    public const string NamePattern = "^[A-Z][a-z]{2,11}$";

    /// <summary>Checks that the list is not empty, every name matches the pattern and no name repeats.</summary>
    /// <returns>(JSON path below the definition, message) per problem.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        if (Names.Count == 0)
        {
            yield return ("names", $"Name list '{Id}' is empty.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < Names.Count; i++)
        {
            if (!Regex.IsMatch(Names[i], NamePattern))
            {
                yield return ($"names[{i}]", $"Name '{Names[i]}' must be a capital Latin letter followed by 2 to 11 lowercase Latin letters.");
            }
            else if (!seen.Add(Names[i]))
            {
                yield return ($"names[{i}]", $"Name '{Names[i]}' is listed twice.");
            }
        }
    }
}

/// <summary>File envelope for <c>data/balance/economy.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record EconomyBalanceFile(
    IReadOnlyList<EconomyBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/balance/roads.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record RoadsBalanceFile(
    IReadOnlyList<RoadsBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/balance/regrowth.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record RegrowthBalanceFile(
    IReadOnlyList<RegrowthBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/names/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record NameListFile(
    IReadOnlyList<NameListDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
