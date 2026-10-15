using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Core.WorldGen;

namespace Core.Content;

/// <summary>Names of the twelve skills (GDD section 7); the pattern is also emitted into the JSON schemas.</summary>
public static class SkillNames
{
    /// <summary>Pattern of a skill name.</summary>
    public const string Pattern =
        "^(construction|mining|farming|hunting|cooking|crafting|smithing|medicine|trading|knowledge|melee|shooting)$";
}

/// <summary>
/// An era of the settlement, <c>data/eras/*.json</c> (FR-BUILD-13, P-14). The era follows from the settlement
/// population; the eras together must cover every population from 1 up without gaps or overlaps.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="Order">Position of the era, starting at 1 with no gaps; requirements compare eras by it.</param>
/// <param name="MinPopulation">Smallest population in this era.</param>
/// <param name="MaxPopulation">Largest population in this era; null (no upper bound) only for the last era.</param>
/// <param name="TerritoryRadius">Radius of the settlement territory in tiles.</param>
public sealed record EraDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(1, 16)] int Order,
    [property: Range(1, 1000000)] int MinPopulation,
    [property: Range(1, 1000000)] int? MaxPopulation,
    [property: Range(1, 4096)] int TerritoryRadius) : IContentDef
{
    /// <summary>
    /// Checks that the eras are numbered 1..N and tile the population line: the first starts at 1, each next one
    /// starts right after the previous one ends, and only the last has no upper bound.
    /// </summary>
    /// <returns>(index in <paramref name="eras"/>, JSON path below the definition, message) per problem.</returns>
    public static IEnumerable<(int Index, string Path, string Message)> CheckCoverage(IReadOnlyList<EraDef> eras)
    {
        ArgumentNullException.ThrowIfNull(eras);
        List<(EraDef Era, int Index)> sorted = eras.Select((e, i) => (e, i)).OrderBy(x => x.e.Order).ThenBy(x => x.i).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            (EraDef era, int index) = sorted[i];
            if (era.Order != i + 1)
            {
                yield return (index, "order", $"Era '{era.Id}' has order {era.Order}; eras must be numbered 1..{sorted.Count} without gaps or repeats.");
            }

            if (era.MaxPopulation is { } max && max < era.MinPopulation)
            {
                yield return (index, "maxPopulation", $"Era '{era.Id}' has maxPopulation below minPopulation.");
            }

            if (i == 0 && era.MinPopulation != 1)
            {
                yield return (index, "minPopulation", $"First era '{era.Id}' must start at population 1.");
            }

            if (i == sorted.Count - 1)
            {
                if (era.MaxPopulation is not null)
                {
                    yield return (index, "maxPopulation", $"Last era '{era.Id}' must have no upper population bound (null).");
                }
            }
            else
            {
                EraDef next = sorted[i + 1].Era;
                if (era.MaxPopulation is null)
                {
                    yield return (index, "maxPopulation", $"Era '{era.Id}' is not the last, so it needs an upper population bound.");
                }
                else if (next.MinPopulation != era.MaxPopulation + 1)
                {
                    yield return (sorted[i + 1].Index, "minPopulation",
                        $"Era '{next.Id}' must start at population {era.MaxPopulation + 1}, right after '{era.Id}' ends (gap or overlap).");
                }
            }
        }
    }
}

/// <summary>
/// A playable or hostile race, <c>data/races/*.json</c> (ROADMAP "what to build into the MVP", item 1): a new race is
/// a new data file, not code.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="Side"><c>good</c> or <c>evil</c>.</param>
/// <param name="StartRelation">Starting relation of the player to this race faction, -100..100 (P-18).</param>
/// <param name="Biome">Land biome the race settles in.</param>
/// <param name="StartTechnologies">Technology tags the faction knows at the start. Hard-coded tags under the MVP; the v1 technology tree will replace them.</param>
/// <param name="SettlementBuildings">Ids of buildings the faction settlement starts with (checked against the building definitions).</param>
/// <param name="Army">Ids of the army composition. Unit definitions do not exist yet, so only the id format is checked.</param>
public sealed record RaceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: RegularExpression("^(good|evil)$")] string Side,
    [property: Range(-100, 100)] int StartRelation,
    [property: RegularExpression(BiomeNames.LandPattern)] string Biome,
    [property: Description("Technology tags the faction knows at the start. Hard-coded per faction under the MVP (human: none, orc: siege_ladders); the v1 technology tree replaces them.")]
    IReadOnlyList<string> StartTechnologies,
    [property: Description("Ids of buildings the faction settlement starts with; each must be a building definition.")]
    IReadOnlyList<string> SettlementBuildings,
    [property: Description("Ids of the army composition; unit definitions do not exist yet, so only the id format is checked.")]
    IReadOnlyList<string> Army) : IContentDef;

/// <summary>
/// A resident profession, <c>data/professions/*.json</c>. Ordinary professions can be taken by any resident;
/// specialists must be lured, hired or found (FR-NPC-05, P-15).
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="Kind"><c>ordinary</c> or <c>specialist</c>. The specialist list (blacksmith, healer, armorer) is proposal A7 and awaits the owner.</param>
/// <param name="Skill">The skill this profession works with.</param>
public sealed record ProfessionDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: RegularExpression("^(ordinary|specialist)$")]
    [property: Description("ordinary or specialist. The specialist list (blacksmith, healer, armorer) is proposal A7, not yet confirmed by the owner.")]
    string Kind,
    [property: RegularExpression(SkillNames.Pattern)] string Skill) : IContentDef;

/// <summary>
/// A reason that changes the relation between two factions, <c>data/balance/relations.json</c> (P-18). A relation is a
/// list of such modifiers, so the player can be told why. Values are whole points on the -100..100 scale.
/// </summary>
/// <param name="Id">Definition id of the reason.</param>
/// <param name="Delta">Change of the relation per occurrence.</param>
/// <param name="DecayKind"><c>none</c>, <c>expiring</c> (gone after <paramref name="DecayTicks"/>) or <c>fading</c> (shrinks to zero over <paramref name="DecayTicks"/>).</param>
/// <param name="DecayTicks">Duration in ticks; 0 exactly when the decay kind is <c>none</c>.</param>
public sealed record RelationModifierDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Range(-100, 100)] int Delta,
    [property: RegularExpression("^(none|expiring|fading)$")]
    [property: Description("none, expiring or fading. All reasons are none for now: whether penalties fade is the owner's open question A9.")]
    string DecayKind,
    [property: Range(0, 100000000)] int DecayTicks) : IContentDef
{
    /// <summary>Checks that the decay duration matches the decay kind.</summary>
    /// <returns>(JSON path below the definition, message) per problem.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        if (DecayKind == "none" && DecayTicks != 0)
        {
            yield return ("decayTicks", $"Reason '{Id}' has decayKind none, so decayTicks must be 0.");
        }
        else if (DecayKind != "none" && DecayTicks == 0)
        {
            yield return ("decayTicks", $"Reason '{Id}' has decayKind {DecayKind}, so decayTicks must be above 0.");
        }
    }
}

/// <summary>File envelope for <c>data/eras/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record EraFile(
    IReadOnlyList<EraDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/races/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record RaceFile(
    IReadOnlyList<RaceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/professions/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record ProfessionFile(
    IReadOnlyList<ProfessionDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/balance/relations.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record RelationModifierFile(
    IReadOnlyList<RelationModifierDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
