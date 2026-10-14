using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Content;
using Core.Sim;

namespace Core.Save;

/// <summary>Kind of a save slot.</summary>
public enum SaveSlotKind
{
    /// <summary>Written by the player: <c>slot_1</c> to <c>slot_N</c>.</summary>
    Manual,

    /// <summary>Written by the game once per day: <c>autosave_1</c> (newest) to <c>autosave_M</c>.</summary>
    Auto,
}

/// <summary>Identifies a slot. <paramref name="Index"/> is 1-based.</summary>
/// <param name="Kind">Manual or automatic.</param>
/// <param name="Index">1-based index.</param>
public readonly record struct SaveSlotId(SaveSlotKind Kind, int Index)
{
    /// <summary>File name stem of manual slots.</summary>
    public const string ManualPrefix = "slot_";

    /// <summary>File name stem of autosaves.</summary>
    public const string AutoPrefix = "autosave_";

    /// <summary>Extension of save files.</summary>
    public const string Extension = ".sav";

    /// <summary>Slot name such as <c>slot_2</c> or <c>autosave_1</c>.</summary>
    public string Name => (Kind == SaveSlotKind.Manual ? ManualPrefix : AutoPrefix) + Index.ToString(CultureInfo.InvariantCulture);

    /// <summary>File name such as <c>slot_2.sav</c>.</summary>
    public string FileName => Name + Extension;

    /// <summary>Parses <c>slot_N</c> or <c>autosave_N</c>; false for anything else.</summary>
    public static bool TryParse(string? name, out SaveSlotId id)
    {
        id = default;
        if (name is null)
        {
            return false;
        }

        SaveSlotKind kind;
        string digits;
        if (name.StartsWith(ManualPrefix, StringComparison.Ordinal))
        {
            kind = SaveSlotKind.Manual;
            digits = name[ManualPrefix.Length..];
        }
        else if (name.StartsWith(AutoPrefix, StringComparison.Ordinal))
        {
            kind = SaveSlotKind.Auto;
            digits = name[AutoPrefix.Length..];
        }
        else
        {
            return false;
        }

        if (digits.Length is 0 or > 4 || !digits.All(char.IsAsciiDigit) || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 1)
        {
            return false;
        }

        id = new SaveSlotId(kind, index);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>What a save says about itself, read without rebuilding the world (cheap enough for a slot list).</summary>
/// <param name="Seed">World seed.</param>
/// <param name="Tick">Tick the save was made at.</param>
/// <param name="Day">0-based game day of <paramref name="Tick"/>.</param>
/// <param name="MapSize">Map side in tiles; 0 for a save without a world.</param>
/// <param name="Difficulty">Difficulty (Normal for a save without a world).</param>
/// <param name="FormatVersion">Format version found in the file.</param>
public sealed record SaveInfo(ulong Seed, long Tick, long Day, int MapSize, Difficulty Difficulty, int FormatVersion);

/// <summary>One existing slot file in a listing.</summary>
/// <param name="Id">Slot.</param>
/// <param name="Path">Full path of the file.</param>
/// <param name="FileTime">Last write time of the file as reported by the file system (not the wall clock).</param>
/// <param name="Info">Header data, or null when the file is unreadable.</param>
/// <param name="Error">Why the file is unreadable, or null.</param>
public sealed record SaveSlotEntry(SaveSlotId Id, string Path, DateTimeOffset FileTime, SaveInfo? Info, string? Error)
{
    /// <summary>True when <see cref="Info"/> could be read.</summary>
    public bool IsReadable => Info is not null;
}

/// <summary>Outcome of a slot operation. Failures are values, never exceptions.</summary>
/// <param name="Succeeded">True when the operation worked.</param>
/// <param name="Error">English detail for logs when it did not.</param>
public sealed record SaveSlotResult(bool Succeeded, string? Error)
{
    /// <summary>A success.</summary>
    public static SaveSlotResult Ok { get; } = new(true, null);

    /// <summary>A failure.</summary>
    public static SaveSlotResult Fail(string error) => new(false, error);
}

/// <summary>
/// Save slots in one directory (FR-SAVE-01): manual slots <c>slot_1..slot_N</c>, rotating autosaves
/// <c>autosave_1..autosave_M</c> (<c>autosave_1</c> is the newest), listing with header info, deletion. The numbers N
/// and M come from <see cref="SaveBalanceDef"/>. The directory is given by the host; Core never picks locations and
/// never reads the wall clock: rotation is a rename chain, ordering by time uses the file times the file system reports.
/// A damaged file shows up in the listing as unreadable and never throws. Writes are atomic (<see cref="AtomicFile"/>).
/// </summary>
public sealed class SaveSlots
{
    private readonly string _directory;
    private readonly int _ticksPerDay;

    /// <summary>Creates the manager.</summary>
    /// <param name="directory">Folder that holds the save files; created on the first write.</param>
    /// <param name="balance">Slot counts.</param>
    /// <param name="time">Time balance, to turn ticks into game days.</param>
    public SaveSlots(string directory, SaveBalanceDef balance, TimeBalanceDef time)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(time);
        _directory = directory;
        Balance = balance;
        _ticksPerDay = new Sim.Calendar(time).TicksPerDay;
    }

    /// <summary>Slot counts in use.</summary>
    public SaveBalanceDef Balance { get; }

    /// <summary>The folder with the save files.</summary>
    public string DirectoryPath => _directory;

    /// <summary>Full path of the file of <paramref name="id"/>.</summary>
    public string PathOf(SaveSlotId id) => System.IO.Path.Combine(_directory, id.FileName);

    /// <summary>True when <paramref name="id"/> is inside the configured slot counts.</summary>
    public bool IsValid(SaveSlotId id) =>
        id.Index >= 1 && id.Index <= (id.Kind == SaveSlotKind.Manual ? Balance.ManualSlots : Balance.AutosavesKept);

    /// <summary>
    /// Lists the existing slot files: autosaves first (newest first), then manual slots by number. Files outside the
    /// configured counts and files with other names are ignored. Unreadable files are listed with <see cref="SaveSlotEntry.Error"/>.
    /// </summary>
    public IReadOnlyList<SaveSlotEntry> List()
    {
        var entries = new List<SaveSlotEntry>();
        foreach (SaveSlotKind kind in new[] { SaveSlotKind.Auto, SaveSlotKind.Manual })
        {
            int count = kind == SaveSlotKind.Manual ? Balance.ManualSlots : Balance.AutosavesKept;
            for (int i = 1; i <= count; i++)
            {
                var id = new SaveSlotId(kind, i);
                string path = PathOf(id);
                if (!File.Exists(path))
                {
                    continue;
                }

                entries.Add(Inspect(id, path));
            }
        }

        return entries;
    }

    /// <summary>
    /// The save "Continue" should load: the readable one with the latest file time (ties: the later tick, then the
    /// lower autosave index). Null when there is none.
    /// </summary>
    public static SaveSlotEntry? FindNewest(IEnumerable<SaveSlotEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(e => e.IsReadable)
            .OrderByDescending(e => e.FileTime)
            .ThenByDescending(e => e.Info!.Tick)
            .ThenBy(e => e.Id.Kind == SaveSlotKind.Auto ? 0 : 1)
            .ThenBy(e => e.Id.Index)
            .FirstOrDefault();
    }

    /// <summary>Writes <paramref name="simulation"/> to manual slot <paramref name="id"/>, replacing its file atomically.</summary>
    public SaveSlotResult SaveManual(Simulation simulation, SaveSlotId id)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (id.Kind != SaveSlotKind.Manual || !IsValid(id))
        {
            return SaveSlotResult.Fail($"'{id}' is not a manual slot.");
        }

        return Guard(() => SaveGame.WriteFile(simulation, PathOf(id)));
    }

    /// <summary>
    /// Writes an autosave: shifts <c>autosave_1..M-1</c> up by one (the oldest is dropped) and writes the new state to
    /// <c>autosave_1</c>. The state is encoded before any file is touched, so an encoding failure changes nothing.
    /// </summary>
    public SaveSlotResult Autosave(Simulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        byte[] data;
        try
        {
            data = SaveGame.ToBytes(simulation);
        }
        catch (InvalidOperationException ex)
        {
            return SaveSlotResult.Fail($"Cannot encode the save: {ex.Message}");
        }

        return Guard(() =>
        {
            int kept = Balance.AutosavesKept;
            System.IO.Directory.CreateDirectory(_directory);
            File.Delete(PathOf(new SaveSlotId(SaveSlotKind.Auto, kept)));
            for (int i = kept - 1; i >= 1; i--)
            {
                string from = PathOf(new SaveSlotId(SaveSlotKind.Auto, i));
                if (File.Exists(from))
                {
                    File.Move(from, PathOf(new SaveSlotId(SaveSlotKind.Auto, i + 1)), overwrite: true);
                }
            }

            AtomicFile.WriteAllBytes(PathOf(new SaveSlotId(SaveSlotKind.Auto, 1)), data);
        });
    }

    /// <summary>Deletes the file of <paramref name="id"/>. A missing file counts as success.</summary>
    public SaveSlotResult Delete(SaveSlotId id)
    {
        if (!IsValid(id))
        {
            return SaveSlotResult.Fail($"'{id}' is not a slot.");
        }

        return Guard(() => File.Delete(PathOf(id)));
    }

    /// <summary>Loads the save of <paramref name="id"/>.</summary>
    public SaveLoadResult Load(SaveSlotId id, ContentCatalog content, Logging.SimLog? log = null) =>
        SaveGame.ReadFile(PathOf(id), content, log);

    /// <summary>
    /// Reads what a save says about itself without rebuilding the world: header, tick, difficulty, map size. Never throws
    /// for bad data; <paramref name="info"/> is null and <paramref name="error"/> says why.
    /// </summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, int ticksPerDay, out SaveInfo? info, out string? error)
    {
        info = null;
        int newline = data[..Math.Min(data.Length, SaveFormat.MaxHeaderBytes + 1)].IndexOf((byte)'\n');
        if (newline < 0)
        {
            error = "No header line: not a save file, or truncated.";
            return false;
        }

        try
        {
            if (JsonNode.Parse(data[..newline]) is not JsonObject header
                || !TryGetInt(header, "formatVersion", out int version)
                || !TryGetString(header, "codec", out string? codec)
                || header["seed"] is not JsonValue seedNode || !seedNode.TryGetValue(out ulong seed))
            {
                error = "Header is missing formatVersion, codec or seed.";
                return false;
            }

            if (version > SaveFormat.CurrentVersion)
            {
                error = $"Save format v{version} is newer than this build supports.";
                return false;
            }

            ReadOnlySpan<byte> encoded = data[(newline + 1)..];
            byte[] bodyJson = codec switch
            {
                SaveFormat.CodecBrotliJson => Decompress(encoded),
                SaveFormat.CodecJson => encoded.ToArray(),
                _ => throw new InvalidDataException($"Unknown codec '{codec}'."),
            };

            if (JsonNode.Parse(bodyJson) is not JsonObject body || body["tick"] is not JsonValue tickNode || !tickNode.TryGetValue(out long tick) || tick < 0)
            {
                error = "Body has no valid tick.";
                return false;
            }

            int mapSize = 0;
            var difficulty = Difficulty.Normal;
            if (body["game"] is JsonObject game)
            {
                if (TryGetInt(game, "mapSize", out int size))
                {
                    mapSize = size;
                }

                if (TryGetString(game, "difficulty", out string? name) && DifficultyNames.TryParse(name, out Difficulty parsed))
                {
                    difficulty = parsed;
                }
            }

            info = new SaveInfo(seed, tick, tick / ticksPerDay, mapSize, difficulty, version);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or IOException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    private SaveSlotEntry Inspect(SaveSlotId id, string path)
    {
        DateTimeOffset time = DateTimeOffset.MinValue;
        try
        {
            time = File.GetLastWriteTimeUtc(path);
            byte[] data = File.ReadAllBytes(path);
            return TryReadInfo(data, _ticksPerDay, out SaveInfo? info, out string? error)
                ? new SaveSlotEntry(id, path, time, info, null)
                : new SaveSlotEntry(id, path, time, null, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveSlotEntry(id, path, time, null, ex.Message);
        }
    }

    private static SaveSlotResult Guard(Action action)
    {
        try
        {
            action();
            return SaveSlotResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveSlotResult.Fail(ex.Message);
        }
    }

    private static bool TryGetInt(JsonObject obj, string name, out int value)
    {
        value = 0;
        return obj[name] is JsonValue node && node.GetValueKind() == JsonValueKind.Number && node.TryGetValue(out value);
    }

    private static bool TryGetString(JsonObject obj, string name, out string? value)
    {
        value = null;
        return obj[name] is JsonValue node && node.GetValueKind() == JsonValueKind.String && node.TryGetValue(out value);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = brotli.Read(buffer)) > 0)
        {
            if (output.Length + read > SaveGame.MaxBodyBytes)
            {
                throw new InvalidDataException("Decoded body is too large.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}

/// <summary>
/// Decides when the daily autosave is due (FR-SAVE-01), as pure logic over the game day: the first call on a day later
/// than the last one returns true, once. Failures to write must not retry every frame, so the day is recorded when
/// <see cref="Poll"/> says yes, not when the write succeeds.
/// </summary>
public sealed class AutosavePolicy
{
    /// <summary>Creates a policy that treats <paramref name="lastAutosaveDay"/> as already covered (the day a game started or was loaded).</summary>
    public AutosavePolicy(long lastAutosaveDay)
    {
        LastAutosaveDay = lastAutosaveDay;
    }

    /// <summary>The latest day for which an autosave was requested or that was loaded.</summary>
    public long LastAutosaveDay { get; private set; }

    /// <summary>True (once) when <paramref name="day"/> is later than the last autosaved day.</summary>
    public bool Poll(long day)
    {
        if (day <= LastAutosaveDay)
        {
            return false;
        }

        LastAutosaveDay = day;
        return true;
    }

    /// <summary>Starts over from <paramref name="day"/> (after a load or a new game); no autosave is due until a later day.</summary>
    public void Reset(long day) => LastAutosaveDay = day;
}
