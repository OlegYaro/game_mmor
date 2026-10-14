using Core.WorldGen;

namespace Core.Sim;

/// <summary>
/// Game difficulty (FR-SAVE-05, G1), chosen at new game and kept for the whole game. The storyteller (M5) reads it:
/// Peaceful has no raids, Normal scales the threat budget ×1.0, Hard ×1.5. Values are part of the state hash: never
/// renumber, only append.
/// </summary>
public enum Difficulty : byte
{
    /// <summary>No raids.</summary>
    Peaceful = 0,

    /// <summary>Threat ×1.0.</summary>
    Normal = 1,

    /// <summary>Threat ×1.5.</summary>
    Hard = 2,
}

/// <summary>Stable data names of <see cref="Difficulty"/> (saves, replays, console), independent of enum member names.</summary>
public static class DifficultyNames
{
    private static readonly string[] _names = ["peaceful", "normal", "hard"];

    /// <summary>All difficulties in numeric order.</summary>
    public static IReadOnlyList<Difficulty> All { get; } = [Difficulty.Peaceful, Difficulty.Normal, Difficulty.Hard];

    /// <summary>Data name, e.g. <c>hard</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown value.</exception>
    public static string ToName(Difficulty difficulty) =>
        (uint)difficulty < (uint)_names.Length
            ? _names[(int)difficulty]
            : throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "Unknown difficulty.");

    /// <summary>Parses a data name (exact, lower case); false for anything else.</summary>
    public static bool TryParse(string? name, out Difficulty difficulty)
    {
        int index = Array.IndexOf(_names, name);
        difficulty = index < 0 ? Difficulty.Normal : (Difficulty)index;
        return index >= 0;
    }
}

/// <summary>Parameters of a new game (FR-SAVE-05, FR-WORLD-01/02): everything the world and the run derive from.</summary>
/// <param name="Seed">World seed.</param>
/// <param name="MapSize">Side of the map in tiles (see <see cref="WorldGenRequest"/> for the allowed values).</param>
/// <param name="Difficulty">Difficulty.</param>
public sealed record NewGameOptions(ulong Seed, int MapSize = NewGameOptions.DefaultMapSize, Difficulty Difficulty = Difficulty.Normal)
{
    /// <summary>
    /// Default map size (FR-WORLD-02): 2048² on the 32×16 tile grid (P-07), four times the area of the former 512² MVP
    /// map on the 64×32 grid.
    /// </summary>
    public const int DefaultMapSize = 2048;

    /// <summary>Throws when the options cannot start a game.</summary>
    /// <exception cref="ArgumentException">Bad map size or difficulty.</exception>
    public void Validate()
    {
        if (MapSize < WorldGenRequest.MinSize || MapSize > WorldGenRequest.MaxSize || MapSize % ChunkCoord.Size != 0)
        {
            throw new ArgumentException(
                $"Map size {MapSize} must be a multiple of {ChunkCoord.Size} between {WorldGenRequest.MinSize} and {WorldGenRequest.MaxSize}.", nameof(MapSize));
        }

        if (!Enum.IsDefined(Difficulty))
        {
            throw new ArgumentException($"Unknown difficulty {(int)Difficulty}.", nameof(Difficulty));
        }
    }
}

/// <summary>
/// The regenerated map of a save does not match the map the save was made on (ADR 0006/0007): the generator code or
/// <c>data/worldgen</c> changed without a <see cref="WorldGenerator.Version"/> bump, so the saved state would sit on a
/// different world.
/// </summary>
public sealed class WorldGenMismatchException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public WorldGenMismatchException()
        : base("The regenerated world does not match the saved world.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public WorldGenMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public WorldGenMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a hash mismatch.</summary>
    public WorldGenMismatchException(ulong expectedHash, ulong actualHash)
        : base($"The world regenerated from the seed has map hash {Content.ContentHash.Format(actualHash)}, but the save was made on {Content.ContentHash.Format(expectedHash)}. " +
            "The world generator or data/worldgen changed without a version bump.")
    {
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }

    /// <summary>Map hash stored with the state.</summary>
    public ulong ExpectedHash { get; }

    /// <summary>Map hash of the regenerated world.</summary>
    public ulong ActualHash { get; }
}
