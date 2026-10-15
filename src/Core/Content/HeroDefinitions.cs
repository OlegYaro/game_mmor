using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Core.Content;

/// <summary>
/// Hero balance (FR-HERO-01), <c>data/balance/hero.json</c>: start stats, movement, stamina, gathering, backpack and
/// equipment. Exactly one definition. All numbers are integers (ADR 0005) and provisional until the M2 balance pass.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="Provisional">True while the numbers are placeholders.</param>
/// <param name="Attack">Starting Attack.</param>
/// <param name="Defense">Starting Defense.</param>
/// <param name="SpellPower">Starting Spell Power.</param>
/// <param name="Knowledge">Starting Knowledge.</param>
/// <param name="Health">Starting and maximum health.</param>
/// <param name="Stamina">Starting and maximum stamina.</param>
/// <param name="StepTicks">Ticks the hero needs to cross one tile at walking speed.</param>
/// <param name="SprintPermille">Sprint speed as a share of walking speed, in thousandths (1500 is x1.5).</param>
/// <param name="SprintStaminaDrain">Stamina spent per tick while sprinting.</param>
/// <param name="StaminaRegen">Stamina regained per tick while not sprinting.</param>
/// <param name="InteractionRange">Distance in tiles within which the hero can interact with a resource or building.</param>
/// <param name="Gathering">One entry per gatherable source.</param>
/// <param name="BackpackStacks">Number of backpack stacks.</param>
/// <param name="EquipmentSlots">Names of the equipment slots, in display order.</param>
/// <param name="StartItems">Items in the backpack at the start of a game.</param>
public sealed record HeroBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: Description("True while the numbers are placeholders; balance is tuned in the M2 plan (docs/MVP.md).")]
    bool Provisional,
    [property: Range(0, 1000)] int Attack,
    [property: Range(0, 1000)] int Defense,
    [property: Range(0, 1000)] int SpellPower,
    [property: Range(0, 1000)] int Knowledge,
    [property: Range(1, 100000)] int Health,
    [property: Range(1, 100000)] int Stamina,
    [property: Description("Ticks per tile at walking speed. 3 ticks at 10 Hz is about 3.3 tiles per second, a comfortable pace on the small 32x16 tile grid.")]
    [property: Range(1, 100)] int StepTicks,
    [property: Range(1000, 10000)] int SprintPermille,
    [property: Range(0, 1000)] int SprintStaminaDrain,
    [property: Range(0, 1000)] int StaminaRegen,
    [property: Range(1, 64)] int InteractionRange,
    IReadOnlyList<GatherDef> Gathering,
    [property: Range(1, 1000)] int BackpackStacks,
    [property: Description("Slot names; each of head, body, hands, legs, weapon, shield, ring1, ring2 and amulet at most once.")]
    IReadOnlyList<string> EquipmentSlots,
    IReadOnlyList<ItemAmountDef> StartItems) : IContentDef
{
    /// <summary>The allowed equipment slot names.</summary>
    public static IReadOnlyList<string> AllSlots { get; } = ["head", "body", "hands", "legs", "weapon", "shield", "ring1", "ring2", "amulet"];

    /// <summary>Consistency problems as (json path, message); empty when fine.</summary>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        for (int i = 0; i < EquipmentSlots.Count; i++)
        {
            if (!AllSlots.Contains(EquipmentSlots[i]))
            {
                yield return ($"equipmentSlots[{i}]", $"Unknown equipment slot '{EquipmentSlots[i]}'.");
            }
            else if (EquipmentSlots.Take(i).Contains(EquipmentSlots[i]))
            {
                yield return ($"equipmentSlots[{i}]", $"Duplicate equipment slot '{EquipmentSlots[i]}'.");
            }
        }

        for (int i = 0; i < Gathering.Count; i++)
        {
            if (Gathering.Take(i).Any(g => g.Source == Gathering[i].Source))
            {
                yield return ($"gathering[{i}].source", $"Duplicate gathering source '{Gathering[i].Source}'.");
            }
        }
    }
}

/// <summary>How the hero gathers from one kind of resource node.</summary>
/// <param name="Source">Kind of node: <c>tree</c>, <c>rock</c> or <c>berry_bush</c>.</param>
/// <param name="Item">Item definition id gathered.</param>
/// <param name="TicksPerAction">Ticks one gathering action takes.</param>
/// <param name="YieldPerAction">Items gained per action.</param>
public sealed record GatherDef(
    [property: RegularExpression("^(tree|rock|berry_bush)$")] string Source,
    [property: RegularExpression(ContentId.Pattern)] string Item,
    [property: Range(1, 100000)] int TicksPerAction,
    [property: Range(1, 100000)] int YieldPerAction) : IContentSection;

/// <summary>
/// Construction rules shared by all buildings (FR-BUILD-01), <c>data/balance/construction.json</c>: how much of the
/// paid cost comes back. Shares are thousandths of the sum of all paid levels, rounded down per resource.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="Provisional">True while the numbers are placeholders.</param>
/// <param name="DemolishRefundPermille">Share returned when a finished building is demolished.</param>
/// <param name="CancelRefundPermille">Share returned when a placement that has not started is cancelled.</param>
public sealed record ConstructionBalanceDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    bool Provisional,
    [property: Range(0, 1000)] int DemolishRefundPermille,
    [property: Range(0, 1000)] int CancelRefundPermille) : IContentDef;

/// <summary>File envelope for <c>data/balance/hero.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record HeroBalanceFile(
    IReadOnlyList<HeroBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);

/// <summary>File envelope for <c>data/balance/construction.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record ConstructionBalanceFile(
    IReadOnlyList<ConstructionBalanceDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
