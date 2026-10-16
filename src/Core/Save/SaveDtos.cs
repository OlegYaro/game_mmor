namespace Core.Save;

// Save format v4 DTOs (ADR 0007). They describe ONLY the current format version: older files are brought up to
// date by ISaveMigration on the JSON tree before they are bound to these types. Property order is the declaration
// order (constructor parameter order) and is part of the format; every property is always written. There are no
// dictionaries: collections are arrays in a canonical order, so output is byte-stable.

/// <summary>Save file header: the first line of the file, uncompressed JSON (ADR 0007).</summary>
/// <param name="FormatVersion">Format version of the body; see <see cref="SaveFormat.CurrentVersion"/>.</param>
/// <param name="GameVersion">Version of the build that wrote the file (informational).</param>
/// <param name="WorldGenVersion">World generator version the seed belongs to; see <see cref="SaveFormat.WorldGenVersion"/>.</param>
/// <param name="Seed">World seed.</param>
/// <param name="Codec">Body encoding: <see cref="SaveFormat.CodecBrotliJson"/> or <see cref="SaveFormat.CodecJson"/>.</param>
public sealed record SaveHeader(int FormatVersion, string GameVersion, int WorldGenVersion, ulong Seed, string Codec);

/// <summary>
/// Save body, format v4 (v1 plus <c>game</c>, <c>fog</c> and the <c>revealer</c> store in v2; plus <c>tiles</c> and the
/// <c>hero</c>, <c>inventory</c>, <c>mover</c> and <c>building</c> stores in v3; plus <c>settlement</c>, <c>jobs</c> and
/// the <c>resident</c>, <c>job</c>, <c>workOutput</c> and <c>storageFilter</c> stores in v4).
/// </summary>
/// <param name="Tick">Last executed tick.</param>
/// <param name="Rng">One entry per RNG stream, in stream id order.</param>
/// <param name="Entities">Entity registry slots.</param>
/// <param name="Components">Component stores.</param>
/// <param name="Commands">Command queue.</param>
/// <param name="Game">New-game parameters and map identity.</param>
/// <param name="Fog">Explored fog layer.</param>
/// <param name="Tiles">Tile changes against the generated map.</param>
/// <param name="Settlement">State of the player's settlement.</param>
/// <param name="Jobs">Reservations and banned targets of the job board.</param>
public sealed record SaveBodyDto(
    long Tick,
    IReadOnlyList<RngStreamDto> Rng,
    EntitiesDto Entities,
    ComponentsDto Components,
    CommandQueueDto Commands,
    GameDto Game,
    FogDto Fog,
    TilesDto Tiles,
    SettlementDto Settlement,
    JobsDto Jobs);

/// <summary>State of the player's settlement that is not in a component (FR-BUILD-13, FR-ECO-10).</summary>
/// <param name="ReachedEra">
/// Id of the highest era reached (the era never falls back when residents leave); empty without a world.
/// </param>
/// <param name="DaysWithoutFood">Days in a row the settlement could not eat its fill; 0 without a shortage.</param>
/// <param name="LastLeaveDay">Day the last hungry resident left in the current shortage; -1 when none did.</param>
public sealed record SettlementDto(string ReachedEra, int DaysWithoutFood, long LastLeaveDay);

/// <summary>The job board's reservations and bans (FR-NPC-07, ADR 0009), both in key order.</summary>
/// <param name="Reservations">Reservations by (layer, target index, target generation, holder index, holder generation).</param>
/// <param name="Bans">Bans by (holder index, holder generation, layer, target index, target generation).</param>
public sealed record JobsDto(IReadOnlyList<ReservationDto> Reservations, IReadOnlyList<JobBanDto> Bans);

/// <summary>A reservation: a resident holds an amount of a target.</summary>
/// <param name="Layer">Layer name: <c>tile</c>, <c>work_slot</c>, <c>output</c> or <c>storage</c>.</param>
/// <param name="Index">Tile index (<c>tile</c>) or the building's slot index.</param>
/// <param name="Generation">The building's generation; 0 for a tile.</param>
/// <param name="HolderIndex">The resident's slot index.</param>
/// <param name="HolderGeneration">The resident's generation.</param>
/// <param name="Item">Item id of an output or storage reservation; empty otherwise.</param>
/// <param name="Amount">Items, or 1 for a tile or a work slot.</param>
public sealed record ReservationDto(string Layer, int Index, int Generation, int HolderIndex, int HolderGeneration, string Item, int Amount);

/// <summary>A target a resident failed to reach and leaves alone until a tick.</summary>
/// <param name="HolderIndex">The resident's slot index.</param>
/// <param name="HolderGeneration">The resident's generation.</param>
/// <param name="Layer">Layer name of the target (see <see cref="ReservationDto.Layer"/>).</param>
/// <param name="Index">Tile index or the building's slot index.</param>
/// <param name="Generation">The building's generation; 0 for a tile.</param>
/// <param name="UntilTick">First tick at which the target is allowed again.</param>
public sealed record JobBanDto(int HolderIndex, int HolderGeneration, string Layer, int Index, int Generation, long UntilTick);

/// <summary>
/// New-game parameters and the identity of the map (FR-SAVE-05). The map itself is not saved: it is regenerated from
/// the header's seed and <c>worldGenVersion</c> and must hash to <paramref name="MapHash"/>.
/// </summary>
/// <param name="Difficulty">Difficulty name: <c>peaceful</c>, <c>normal</c> or <c>hard</c>.</param>
/// <param name="MapSize">Side of the map in tiles; 0 for a simulation without a world (all v1 saves).</param>
/// <param name="MapHash">Map hash (XXH3-64 of the canonical world bytes); 0 without a world.</param>
public sealed record GameDto(string Difficulty, int MapSize, ulong MapHash);

/// <summary>Fog of war (FR-WORLD-06). Only the explored layer is saved; the visible layer is recomputed on load.</summary>
/// <param name="ExploredRuns">
/// Run lengths over the tiles in index order (<c>y * mapSize + x</c>), alternating unknown and explored, starting with
/// unknown (the first run may be 0, no other). Empty without a world.
/// </param>
public sealed record FogDto(IReadOnlyList<int> ExploredRuns);

/// <summary>
/// Tile changes against the generated map (FR-WORLD-05, ADR 0007): the changed 32×32 chunks of each layer in ascending
/// chunk index (<c>y * chunksPerSide + x</c>). Empty without a world.
/// </summary>
/// <param name="Resources">Chunks with changed resource stocks.</param>
/// <param name="Occupancy">Chunks with occupied tiles.</param>
public sealed record TilesDto(IReadOnlyList<ResourceChunkDto> Resources, IReadOnlyList<OccupancyChunkDto> Occupancy);

/// <summary>Changed resource stocks of one chunk.</summary>
/// <param name="X">Chunk column.</param>
/// <param name="Y">Chunk row.</param>
/// <param name="Mask">16 words of the changed-tile mask: bit <c>ly * 32 + lx</c> of the chunk, least significant first.</param>
/// <param name="Amounts">Remaining stock of each changed tile in mask order; 0 means the node is gone.</param>
public sealed record ResourceChunkDto(int X, int Y, IReadOnlyList<ulong> Mask, IReadOnlyList<int> Amounts);

/// <summary>Occupied tiles of one chunk.</summary>
/// <param name="X">Chunk column.</param>
/// <param name="Y">Chunk row.</param>
/// <param name="Mask">16 words of the occupied-tile mask (same layout as <see cref="ResourceChunkDto.Mask"/>).</param>
/// <param name="OwnerIndices">Owner slot index of each occupied tile in mask order.</param>
/// <param name="OwnerGenerations">Owner generation of each occupied tile in mask order.</param>
public sealed record OccupancyChunkDto(int X, int Y, IReadOnlyList<ulong> Mask, IReadOnlyList<int> OwnerIndices, IReadOnlyList<int> OwnerGenerations);

/// <summary>State of one RNG stream.</summary>
/// <param name="Stream">Stable stream name (see <see cref="SaveMapper.RngStreamNames"/>).</param>
/// <param name="S0">xoshiro256** word 0.</param>
/// <param name="S1">xoshiro256** word 1.</param>
/// <param name="S2">xoshiro256** word 2.</param>
/// <param name="S3">xoshiro256** word 3.</param>
public sealed record RngStreamDto(string Stream, ulong S0, ulong S1, ulong S2, ulong S3);

/// <summary>Entity registry slots as two parallel arrays indexed by slot.</summary>
/// <param name="Generations">Generation per slot.</param>
/// <param name="Alive">Alive flag per slot.</param>
public sealed record EntitiesDto(IReadOnlyList<int> Generations, IReadOnlyList<bool> Alive);

/// <summary>All component stores; one property per store, in store registration order.</summary>
/// <param name="Position">Position components in ascending entity index order.</param>
/// <param name="Revealer">Revealer components in ascending entity index order (empty without a world).</param>
/// <param name="Hero">Hero components in ascending entity index order (empty without a world).</param>
/// <param name="Inventory">Inventory components in ascending entity index order (empty without a world).</param>
/// <param name="Mover">Mover components in ascending entity index order (empty without a world).</param>
/// <param name="Building">Building components in ascending entity index order (empty without a world).</param>
/// <param name="Resident">Resident components in ascending entity index order (empty without a world).</param>
/// <param name="Job">Resident job components in ascending entity index order (empty without a world).</param>
/// <param name="WorkOutput">Work output components of buildings in ascending entity index order (empty without a world).</param>
/// <param name="StorageFilter">Storage filter components in ascending entity index order (empty without a world).</param>
public sealed record ComponentsDto(
    IReadOnlyList<PositionEntryDto> Position,
    IReadOnlyList<RevealerEntryDto> Revealer,
    IReadOnlyList<HeroEntryDto> Hero,
    IReadOnlyList<InventoryEntryDto> Inventory,
    IReadOnlyList<MoverEntryDto> Mover,
    IReadOnlyList<BuildingEntryDto> Building,
    IReadOnlyList<ResidentEntryDto> Resident,
    IReadOnlyList<JobEntryDto> Job,
    IReadOnlyList<WorkOutputEntryDto> WorkOutput,
    IReadOnlyList<StorageFilterEntryDto> StorageFilter);

/// <summary>One position component with its owner.</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="X">X in 1/256 tile.</param>
/// <param name="Y">Y in 1/256 tile.</param>
public sealed record PositionEntryDto(int Index, int Generation, int X, int Y);

/// <summary>One revealer component with its owner.</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Kind">Sight kind name: <c>hero</c>, <c>villager</c> or <c>tower</c>.</param>
public sealed record RevealerEntryDto(int Index, int Generation, string Kind);

/// <summary>One hero component with its owner (FR-HERO-01).</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Attack">Attack.</param>
/// <param name="Defense">Defense.</param>
/// <param name="SpellPower">Spell Power.</param>
/// <param name="Knowledge">Knowledge.</param>
/// <param name="Health">Current health.</param>
/// <param name="Stamina">Current stamina.</param>
/// <param name="Activity">Activity name: <c>idle</c>, <c>walking</c> or <c>gathering</c>.</param>
/// <param name="TargetX">Column of the node being gathered, -1 otherwise.</param>
/// <param name="TargetY">Row of the node being gathered, -1 otherwise.</param>
/// <param name="ActionTicks">Ticks spent on the current gathering action.</param>
/// <param name="LastStop">Why the last activity ended (see <see cref="SaveMapper.HeroStopReasonNames"/>).</param>
/// <param name="LastOrder">Outcome of the latest order (see <see cref="SaveMapper.HeroOrderResultNames"/>).</param>
public sealed record HeroEntryDto(
    int Index,
    int Generation,
    int Attack,
    int Defense,
    int SpellPower,
    int Knowledge,
    int Health,
    int Stamina,
    string Activity,
    int TargetX,
    int TargetY,
    int ActionTicks,
    string LastStop,
    string LastOrder);

/// <summary>One inventory component with its owner (FR-HERO-04). Only non-empty slots are listed.</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="BackpackSize">Number of backpack slots.</param>
/// <param name="Backpack">Non-empty backpack slots in ascending slot order.</param>
/// <param name="EquipmentSize">Number of equipment slots.</param>
/// <param name="Equipment">Non-empty equipment slots in ascending slot order.</param>
public sealed record InventoryEntryDto(int Index, int Generation, int BackpackSize, IReadOnlyList<ItemSlotDto> Backpack, int EquipmentSize, IReadOnlyList<ItemSlotDto> Equipment);

/// <summary>A non-empty inventory slot.</summary>
/// <param name="Slot">Slot number (0-based).</param>
/// <param name="Item">Item definition id (the string id, so content changes do not shift it).</param>
/// <param name="Count">Number of items.</param>
public sealed record ItemSlotDto(int Slot, string Item, int Count);

/// <summary>One mover component with its owner: goal, planned path and step progress (FR-HERO-01).</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Goal">Goal tile index (<c>y * mapSize + x</c>), -1 when idle.</param>
/// <param name="GoalRange">0 to reach the goal tile, otherwise the Chebyshev distance to stop at.</param>
/// <param name="Path">Planned tile indices, the walker's tile first; empty when idle.</param>
/// <param name="StepIndex">Index in the path of the tile the current step starts from.</param>
/// <param name="StepProgress">Progress of the current step in thousandths of a walking tick.</param>
/// <param name="Sprint">Sprint while stamina lasts.</param>
public sealed record MoverEntryDto(int Index, int Generation, int Goal, int GoalRange, IReadOnlyList<int> Path, int StepIndex, int StepProgress, bool Sprint);

/// <summary>
/// One building component with its owner (FR-BUILD-02, 09, 13, ADR 0012). A storage's stock is its inventory entry
/// (same owner) in <see cref="ComponentsDto.Inventory"/>.
/// </summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Building">Building type id (the string id, so content changes do not shift it).</param>
/// <param name="Level">Level, from 1 (the new level while upgrading).</param>
/// <param name="State">State name: <c>constructing</c>, <c>active</c>, <c>upgrading</c> or <c>moving</c>.</param>
/// <param name="TicksLeft">Ticks until the construction, upgrade or move ends; 0 when active.</param>
/// <param name="X">Column of the footprint's top-left tile.</param>
/// <param name="Y">Row of the footprint's top-left tile.</param>
/// <param name="Rotation">Quarter turns, 0 to 3.</param>
/// <param name="TargetX">Column of the new top-left tile while moving, -1 otherwise.</param>
/// <param name="TargetY">Row of the new top-left tile while moving, -1 otherwise.</param>
/// <param name="TargetRotation">Rotation at the new place while moving, -1 otherwise.</param>
/// <param name="Settlement">Owning settlement (1: the player's).</param>
public sealed record BuildingEntryDto(
    int Index,
    int Generation,
    string Building,
    int Level,
    string State,
    int TicksLeft,
    int X,
    int Y,
    int Rotation,
    int TargetX,
    int TargetY,
    int TargetRotation,
    int Settlement);

/// <summary>One resident component with its owner (FR-NPC-01, FR-NPC-02). Its position and mover are in their stores.</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Name">First name from the <c>human_names</c> list (the name, so reordering the list does not change it).</param>
/// <param name="Profession">Profession id.</param>
/// <param name="Skills">The twelve skill levels (0..20) in the order construction, mining, farming, hunting, cooking, crafting, smithing, medicine, trading, knowledge, melee, shooting.</param>
/// <param name="HomeIndex">Home building slot index; 0 with generation 0 when homeless.</param>
/// <param name="HomeGeneration">Home building generation; 0 when homeless.</param>
/// <param name="State">State name: <c>arriving</c>, <c>idle</c>, <c>walking</c>, <c>working</c> or <c>leaving</c>.</param>
/// <param name="Priorities">The job types with a priority above 0, in job type order (haul, chop, plant, quarry, forage, farm).</param>
public sealed record ResidentEntryDto(
    int Index,
    int Generation,
    string Name,
    string Profession,
    IReadOnlyList<int> Skills,
    int HomeIndex,
    int HomeGeneration,
    string State,
    IReadOnlyList<WorkPriorityDto> Priorities);

/// <summary>A resident's priority for one job type.</summary>
/// <param name="Job">Job type name.</param>
/// <param name="Priority">1 (highest) to 4.</param>
public sealed record WorkPriorityDto(string Job, int Priority);

/// <summary>One resident's job and load (FR-NPC-06, ADR 0009). Its reservations are in <see cref="JobsDto"/>.</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Kind">Job type name, or <c>none</c>.</param>
/// <param name="Phase">Phase name: <c>none</c>, <c>to_target</c>, <c>working</c> or <c>to_storage</c>.</param>
/// <param name="BuildingIndex">Slot index of the field or the hauled building; 0 with generation 0 for none.</param>
/// <param name="BuildingGeneration">Generation of that building; 0 for none.</param>
/// <param name="TargetTile">Node tile index of a gathering job; -1 otherwise.</param>
/// <param name="StorageIndex">Slot index of the storage the load goes to; 0 with generation 0 for none.</param>
/// <param name="StorageGeneration">Generation of that storage; 0 for none.</param>
/// <param name="GoalTile">Tile the resident walks to or works next to; -1 without one.</param>
/// <param name="WorkLeft">Work left in the current action or cycle, in thousandths of a tick at base speed.</param>
/// <param name="CarryItem">Item id of the load; empty when carrying nothing.</param>
/// <param name="CarryCount">Items carried.</param>
/// <param name="NextThinkTick">First tick at which an idle resident looks for work again.</param>
public sealed record JobEntryDto(
    int Index,
    int Generation,
    string Kind,
    string Phase,
    int BuildingIndex,
    int BuildingGeneration,
    int TargetTile,
    int StorageIndex,
    int StorageGeneration,
    int GoalTile,
    int WorkLeft,
    string CarryItem,
    int CarryCount,
    long NextThinkTick);

/// <summary>Items waiting in the output of a building where residents work (a field).</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Count">Items of the level's work item waiting.</param>
public sealed record WorkOutputEntryDto(int Index, int Generation, int Count);

/// <summary>The item categories a storage takes in (FR-ECO-01); a storage without an entry takes every category.</summary>
/// <param name="Index">Owner slot index.</param>
/// <param name="Generation">Owner generation.</param>
/// <param name="Categories">Allowed category names in category order (resource, food, material, currency); may be empty.</param>
public sealed record StorageFilterEntryDto(int Index, int Generation, IReadOnlyList<string> Categories);

/// <summary>The command queue.</summary>
/// <param name="NextSequence">Sequence number of the next enqueued command.</param>
/// <param name="Pending">Waiting commands in execution order.</param>
public sealed record CommandQueueDto(long NextSequence, IReadOnlyList<PendingCommandDto> Pending);

/// <summary>A waiting command.</summary>
/// <param name="ExecuteTick">Tick the command runs in.</param>
/// <param name="Sequence">Enqueue counter.</param>
/// <param name="Command">The command; its <c>kind</c> property selects the type.</param>
public sealed record PendingCommandDto(long ExecuteTick, long Sequence, CommandDto Command);
