namespace Core.Save;

// Save format v2 DTOs (ADR 0007). They describe ONLY the current format version: older files are brought up to
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

/// <summary>Save body, format v2 (v1 plus <c>game</c>, <c>fog</c> and the <c>revealer</c> store).</summary>
/// <param name="Tick">Last executed tick.</param>
/// <param name="Rng">One entry per RNG stream, in stream id order.</param>
/// <param name="Entities">Entity registry slots.</param>
/// <param name="Components">Component stores.</param>
/// <param name="Commands">Command queue.</param>
/// <param name="Game">New-game parameters and map identity.</param>
/// <param name="Fog">Explored fog layer.</param>
public sealed record SaveBodyDto(
    long Tick,
    IReadOnlyList<RngStreamDto> Rng,
    EntitiesDto Entities,
    ComponentsDto Components,
    CommandQueueDto Commands,
    GameDto Game,
    FogDto Fog);

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
public sealed record ComponentsDto(IReadOnlyList<PositionEntryDto> Position, IReadOnlyList<RevealerEntryDto> Revealer);

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

/// <summary>The command queue.</summary>
/// <param name="NextSequence">Sequence number of the next enqueued command.</param>
/// <param name="Pending">Waiting commands in execution order.</param>
public sealed record CommandQueueDto(long NextSequence, IReadOnlyList<PendingCommandDto> Pending);

/// <summary>A waiting command.</summary>
/// <param name="ExecuteTick">Tick the command runs in.</param>
/// <param name="Sequence">Enqueue counter.</param>
/// <param name="Command">The command; its <c>kind</c> property selects the type.</param>
public sealed record PendingCommandDto(long ExecuteTick, long Sequence, CommandDto Command);
