using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Content;
using Core.Save;
using Core.Sim;

namespace Core.Replay;

/// <summary>A command of a replay.</summary>
/// <param name="Tick">Tick the command executes in (it is enqueued while the simulation is at <c>Tick - 1</c>).</param>
/// <param name="Command">The command.</param>
public readonly record struct ReplayCommand(long Tick, ISimCommand Command);

/// <summary>
/// A replay (ADR 0005, NFR-04): seed, content hash, optionally the new-game parameters, and the commands with their
/// ticks. Starting a new simulation with the seed (a game via <see cref="Simulation.CreateNewGame"/> when
/// <see cref="MapSize"/> is set) and the same content, and feeding it the commands, reproduces the run exactly.
/// </summary>
/// <remarks>
/// <para>JSON layout (canonical: the same replay always gives the same text, LF line ends):</para>
/// <code>
/// {
///   "formatVersion": 1,
///   "seed": 42,
///   "contentHash": "0123456789abcdef",
///   "endTick": 300,
///   "mapSize": 64,
///   "difficulty": "hard",
///   "commands": [
///     {"tick":1,"command":{"kind":"spawn_marker","x":0,"y":0}}
///   ]
/// }
/// </code>
/// <para><c>mapSize</c> and <c>difficulty</c> are written only for games (<see cref="MapSize"/> above 0); replays of
/// core simulations omit them. One command per line keeps golden files diffable. Commands use the save DTOs (<see cref="CommandDto"/>).
/// Replays are debugging and test artifacts, so there is no migration chain: a format change regenerates them.</para>
/// </remarks>
public sealed class ReplayFile
{
    /// <summary>Format version written and accepted by this build.</summary>
    public const int CurrentFormatVersion = 1;

    private readonly ReplayCommand[] _commands;

    /// <summary>Creates a replay.</summary>
    /// <param name="seed">World seed.</param>
    /// <param name="contentHash"><see cref="ContentCatalog.ContentHash"/> of the content the replay was recorded with.</param>
    /// <param name="endTick">Last tick of the recording; players run up to it.</param>
    /// <param name="commands">Commands in non-decreasing tick order (same-tick commands keep their order).</param>
    /// <param name="mapSize">Map size of a game, or 0 for a core simulation without a world.</param>
    /// <param name="difficulty">Difficulty of a game; must be <see cref="Difficulty.Normal"/> without a world.</param>
    /// <exception cref="ArgumentException">
    /// A tick is below 1, above <paramref name="endTick"/>, or out of order; or the game parameters are invalid.
    /// </exception>
    public ReplayFile(ulong seed, ulong contentHash, long endTick, IEnumerable<ReplayCommand> commands, int mapSize = 0,
        Difficulty difficulty = Difficulty.Normal)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentOutOfRangeException.ThrowIfNegative(endTick);
        if (mapSize != 0)
        {
            new NewGameOptions(seed, mapSize, difficulty).Validate();
        }
        else if (difficulty != Difficulty.Normal)
        {
            throw new ArgumentException("A replay without a world has Normal difficulty.", nameof(difficulty));
        }

        _commands = commands.ToArray();
        long previous = 1;
        foreach (ReplayCommand command in _commands)
        {
            ArgumentNullException.ThrowIfNull(command.Command, nameof(commands));
            if (command.Tick < previous || command.Tick > endTick)
            {
                throw new ArgumentException(
                    $"Command ticks must be in non-decreasing order within 1..{endTick}; got {command.Tick} after {previous}.", nameof(commands));
            }

            previous = command.Tick;
        }

        Seed = seed;
        ContentHash = contentHash;
        EndTick = endTick;
        MapSize = mapSize;
        Difficulty = difficulty;
    }

    /// <summary>World seed.</summary>
    public ulong Seed { get; }

    /// <summary>Hash of the content the replay was recorded with.</summary>
    public ulong ContentHash { get; }

    /// <summary>Last tick of the recording.</summary>
    public long EndTick { get; }

    /// <summary>Map size of a game replay, or 0 for a core simulation.</summary>
    public int MapSize { get; }

    /// <summary>Difficulty of a game replay (<see cref="Difficulty.Normal"/> for core simulations).</summary>
    public Difficulty Difficulty { get; }

    /// <summary>The new-game parameters of a game replay, or null for a core simulation.</summary>
    public NewGameOptions? NewGame => MapSize == 0 ? null : new NewGameOptions(Seed, MapSize, Difficulty);

    /// <summary>Commands in execution order.</summary>
    public IReadOnlyList<ReplayCommand> Commands => _commands;

    /// <summary>Canonical JSON text of this replay.</summary>
    public string ToJson()
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"formatVersion\": {CurrentFormatVersion},\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"seed\": {Seed},\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"contentHash\": \"{Content.ContentHash.Format(ContentHash)}\",\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"endTick\": {EndTick},\n");
        if (MapSize != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"  \"mapSize\": {MapSize},\n");
            text.Append(CultureInfo.InvariantCulture, $"  \"difficulty\": \"{DifficultyNames.ToName(Difficulty)}\",\n");
        }

        text.Append("  \"commands\": [");
        for (int i = 0; i < _commands.Length; i++)
        {
            var dto = new ReplayCommandDto(_commands[i].Tick, CommandDto.FromCommand(_commands[i].Command));
            text.Append(i == 0 ? "\n    " : ",\n    ");
            text.Append(JsonSerializer.Serialize(dto, ReplayJsonContext.Default.ReplayCommandDto));
        }

        text.Append(_commands.Length == 0 ? "]\n" : "\n  ]\n");
        text.Append("}\n");
        return text.ToString();
    }

    /// <summary>Parses replay JSON (any whitespace layout).</summary>
    /// <exception cref="InvalidDataException">The text is not a valid replay of <see cref="CurrentFormatVersion"/>.</exception>
    public static ReplayFile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ReplayFileDto dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, ReplayJsonContext.Default.ReplayFileDto)
                ?? throw new InvalidDataException("Replay is null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException($"Replay is not valid: {ex.Message}", ex);
        }

        if (dto.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Replay format v{dto.FormatVersion} is not supported (this build reads v{CurrentFormatVersion}).");
        }

        if (!Content.ContentHash.TryParse(dto.ContentHash, out ulong contentHash))
        {
            throw new InvalidDataException($"Replay content hash '{dto.ContentHash}' is not 16 lowercase hex digits.");
        }

        Difficulty difficulty = Difficulty.Normal;
        if (dto.Difficulty is not null && !DifficultyNames.TryParse(dto.Difficulty, out difficulty))
        {
            throw new InvalidDataException($"Replay difficulty '{dto.Difficulty}' is unknown.");
        }

        if ((dto.MapSize == 0) != (dto.Difficulty is null))
        {
            throw new InvalidDataException("Replay 'mapSize' and 'difficulty' go together: both for a game, neither for a core simulation.");
        }

        try
        {
            return new ReplayFile(dto.Seed, contentHash, dto.EndTick, dto.Commands.Select(c =>
                new ReplayCommand(c?.Tick ?? throw new InvalidDataException("A replay command is null."), c.Command.ToCommand())),
                dto.MapSize, difficulty);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Replay is not valid: {ex.Message}", ex);
        }
    }
}

/// <summary>JSON shape of a replay file.</summary>
/// <param name="FormatVersion">Replay format version.</param>
/// <param name="Seed">World seed.</param>
/// <param name="ContentHash">Content hash, 16 lowercase hex digits.</param>
/// <param name="EndTick">Last tick of the recording.</param>
/// <param name="Commands">Commands in execution order.</param>
/// <param name="MapSize">Map size of a game; absent (0) for a core simulation.</param>
/// <param name="Difficulty">Difficulty name of a game; absent (null) for a core simulation.</param>
public sealed record ReplayFileDto(int FormatVersion, ulong Seed, string ContentHash, long EndTick, IReadOnlyList<ReplayCommandDto> Commands,
    int MapSize = 0, string? Difficulty = null);

/// <summary>JSON shape of one replay command.</summary>
/// <param name="Tick">Execution tick.</param>
/// <param name="Command">The command.</param>
public sealed record ReplayCommandDto(long Tick, CommandDto Command);

/// <summary>Source-generated strict JSON metadata for replays (same rules as <see cref="SaveJsonContext"/>).</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Strict,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(ReplayFileDto))]
[JsonSerializable(typeof(ReplayCommandDto))]
public sealed partial class ReplayJsonContext : JsonSerializerContext
{
}
