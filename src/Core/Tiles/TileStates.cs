namespace Core.Tiles;

/// <summary>
/// Vegetation of a tile on top of the generated map (FR-WORLD-04, gdd-issues B4). Values are hashed; save names are
/// <see cref="TileStateNames.Flora"/>. Append only.
/// </summary>
public enum FloraState : byte
{
    /// <summary>Nothing beyond the map: the tile shows its generated node (if any, and if its stock is left).</summary>
    None = 0,

    /// <summary>A felled tree: no node; it grows back into a tree (<see cref="TileChanges"/>, regrowth).</summary>
    Stump = 1,

    /// <summary>A planted sapling: no node yet; it becomes a tree after the sapling days.</summary>
    Sapling = 2,

    /// <summary>A tree that grew on a tile without a generated tree (planted, or regrown from such a tree's stump).</summary>
    Tree = 3,
}

/// <summary>
/// Class of a walked tile (FR-WORLD-15, docs/research/roads-from-paths.md). Values are hashed; save names are
/// <see cref="TileStateNames.RoadClasses"/>. Append only.
/// </summary>
public enum RoadClass : byte
{
    /// <summary>Plain ground.</summary>
    None = 0,

    /// <summary>A footpath: cheaper and faster to walk.</summary>
    Trail = 1,

    /// <summary>A road: the cheapest and fastest ground.</summary>
    Road = 2,
}

/// <summary>Stable save names of the tile state enums.</summary>
public static class TileStateNames
{
    /// <summary>Save names of <see cref="FloraState"/>, indexed by value. Append only.</summary>
    public static IReadOnlyList<string> Flora { get; } = ["none", "stump", "sapling", "tree"];

    /// <summary>Save names of <see cref="RoadClass"/>, indexed by value. Append only.</summary>
    public static IReadOnlyList<string> RoadClasses { get; } = ["none", "trail", "road"];
}

/// <summary>
/// Saved form of one 32×32 chunk of the flora layer: the mask of the tiles with flora and, in mask order, each one's
/// state, the tick it took that state (felled, planted, grown) and the stock of a grown tree (0 otherwise).
/// </summary>
/// <param name="ChunkX">Chunk column.</param>
/// <param name="ChunkY">Chunk row.</param>
/// <param name="Mask">16 words of the flora mask, least significant bit first.</param>
/// <param name="States">State of each tile in mask order (never <see cref="FloraState.None"/>).</param>
/// <param name="Ticks">Tick each tile took its state, in mask order.</param>
/// <param name="Stocks">Wood left in each <see cref="FloraState.Tree"/>, 0 for a stump or a sapling, in mask order.</param>
public sealed record FloraChunkState(int ChunkX, int ChunkY, IReadOnlyList<ulong> Mask, IReadOnlyList<FloraState> States,
    IReadOnlyList<long> Ticks, IReadOnlyList<int> Stocks);

/// <summary>
/// Saved form of one 32×32 chunk of the walk layer: the mask of the tiles whose walk counter is above 0 now and, in mask
/// order, each one's counter right after its last step, the tick of that step and the tile's class now.
/// </summary>
/// <param name="ChunkX">Chunk column.</param>
/// <param name="ChunkY">Chunk row.</param>
/// <param name="Mask">16 words of the walked-tile mask, least significant bit first.</param>
/// <param name="Counters">Counter right after the last step (it fades from there), in mask order.</param>
/// <param name="Ticks">Tick of the last step, in mask order.</param>
/// <param name="Classes">Class of the tile at the saved tick, in mask order.</param>
public sealed record WalkChunkState(int ChunkX, int ChunkY, IReadOnlyList<ulong> Mask, IReadOnlyList<int> Counters, IReadOnlyList<long> Ticks,
    IReadOnlyList<RoadClass> Classes);
