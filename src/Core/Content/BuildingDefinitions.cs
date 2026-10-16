using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Core.Content;

/// <summary>
/// A building type, <c>data/buildings/*.json</c> (ADR 0012, FR-BUILD-13). One definition per building; everything that
/// changes with the level lives in <paramref name="Levels"/>. An entity keeps the definition and its level.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="Category">Build menu category.</param>
/// <param name="Footprint">Size on the tile grid, the same for every level.</param>
/// <param name="OutsideTerritory">True when the building may be placed outside the settlement territory (the first settlement center, later outposts).</param>
/// <param name="Placement">Ground rule: <c>flat</c> needs level ground.</param>
/// <param name="Rotatable">True when the player may rotate the building before placing it (swaps width and height).</param>
/// <param name="Provisional">True while the numbers are placeholders to be tuned in the M2 plan.</param>
/// <param name="Levels">Level 1 first; at least one.</param>
public sealed record BuildingDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: RegularExpression("^(housing|storage|settlement_center|production)$")] string Category,
    BuildingFootprint Footprint,
    bool OutsideTerritory,
    [property: RegularExpression("^(flat)$")] string Placement,
    bool Rotatable,
    [property: Description("True while the costs, times and capacities are placeholders; balance is tuned in the M2 plan (docs/MVP.md).")]
    bool Provisional,
    IReadOnlyList<BuildingLevelDef> Levels) : IContentDef;

/// <summary>Size of a building in tiles.</summary>
/// <param name="Width">Width in tiles.</param>
/// <param name="Height">Height in tiles.</param>
public sealed record BuildingFootprint(
    [property: Range(1, 32)] int Width,
    [property: Range(1, 32)] int Height) : IContentSection;

/// <summary>One level of a building. The cost is the extra payment over the previous level (ADR 0012).</summary>
/// <param name="Cost">Items paid for this level, in a fixed order.</param>
/// <param name="BuildTicks">Ticks to build or upgrade to this level (100 ticks are 10 seconds at 10 Hz).</param>
/// <param name="MoveTicks">Ticks to move a finished building of this level to another spot (a third of <paramref name="BuildTicks"/>).</param>
/// <param name="Requirements">Conditions to build or upgrade to this level; all must hold.</param>
/// <param name="Capacity">Resident places (housing); 0 when the building has none.</param>
/// <param name="Storage">Item stacks the building stores (storage); 0 when it has none.</param>
/// <param name="Work">Resident work done at this level (production buildings); null when residents do not work here.</param>
public sealed record BuildingLevelDef(
    IReadOnlyList<ItemAmountDef> Cost,
    [property: Range(1, 100000)] int BuildTicks,
    [property: Range(1, 100000)] int MoveTicks,
    IReadOnlyList<RequirementDef> Requirements,
    [property: Range(0, 1000)] int Capacity = 0,
    [property: Range(0, 100000)] int Storage = 0,
    WorkDef? Work = null) : IContentSection;

/// <summary>
/// Resident work at a building level (M3a): <paramref name="Slots"/> residents each repeat a cycle that yields items.
/// All numbers are provisional.
/// </summary>
/// <param name="Job">Job type, one of the values of <see cref="JobTypes"/>.</param>
/// <param name="Slots">Residents that can work here at once.</param>
/// <param name="CycleTicks">Ticks of one work cycle at base speed.</param>
/// <param name="Item">Item produced per cycle (item definition id).</param>
/// <param name="Yield">Items produced per cycle.</param>
public sealed record WorkDef(
    [property: RegularExpression(JobTypes.Pattern)] string Job,
    [property: Range(1, 100)] int Slots,
    [property: Range(1, 1000000)] int CycleTicks,
    [property: RegularExpression(ContentId.Pattern)] string Item,
    [property: Range(1, 100000)] int Yield) : IContentSection;

/// <summary>An item and how many of it.</summary>
/// <param name="Item">Item definition id.</param>
/// <param name="Count">Number of items.</param>
public sealed record ItemAmountDef(
    [property: RegularExpression(ContentId.Pattern)] string Item,
    [property: Range(1, 100000)] int Count) : IContentSection;

/// <summary>A condition on a building level.</summary>
/// <param name="Kind"><c>era</c> (value is an era id; the settlement must have reached it) or <c>specialist</c> (value is a specialist profession id).</param>
/// <param name="Value">Id the kind refers to.</param>
public sealed record RequirementDef(
    [property: RegularExpression("^(era|specialist)$")] string Kind,
    [property: RegularExpression(ContentId.Pattern)] string Value) : IContentSection;

/// <summary>File envelope for <c>data/buildings/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record BuildingFile(
    IReadOnlyList<BuildingDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
