using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Content;
using Core.Logging;
using Core.Sim;

namespace Core.Save;

/// <summary>
/// Writes and reads save files (ADR 0007, FR-SAVE-02, FR-SAVE-04). Writing is canonical: the same state always
/// gives the same bytes. Reading never throws for bad files; it returns a <see cref="SaveLoadResult"/> with an error.
/// </summary>
/// <remarks>Slots and autosave rotation (FR-SAVE-01) are built on top of this in M1.</remarks>
public static class SaveGame
{
    /// <summary>Upper bound for a decoded body; anything larger is treated as corrupt (decompression bomb guard).</summary>
    internal const int MaxBodyBytes = 512 * 1024 * 1024;

    private const string CodecProperty = "codec";

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>Encodes the current state of <paramref name="simulation"/> as a save file.</summary>
    /// <exception cref="InvalidOperationException">Called during a step.</exception>
    public static byte[] ToBytes(Simulation simulation, SaveWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        options ??= SaveWriteOptions.Default;
        SimulationState state = simulation.CaptureState();

        string codec = options.Indented ? SaveFormat.CodecJson : SaveFormat.CodecBrotliJson;
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(SaveMapper.ToHeader(state, codec), SaveJsonContext.Default.SaveHeader);
        SaveJsonContext context = options.Indented ? SaveJsonContext.CreateIndented() : SaveJsonContext.Default;
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(SaveMapper.ToBody(state), context.SaveBodyDto);

        using var output = new MemoryStream();
        output.Write(header);
        output.WriteByte((byte)'\n');
        if (options.Indented)
        {
            output.Write(body);
        }
        else
        {
            output.Write(Compress(body));
        }

        return output.ToArray();
    }

    /// <summary>Writes a save of <paramref name="simulation"/> to <paramref name="output"/>.</summary>
    public static void Write(Simulation simulation, Stream output, SaveWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(ToBytes(simulation, options));
    }

    /// <summary>Writes a save of <paramref name="simulation"/> to <paramref name="path"/> atomically (<see cref="AtomicFile"/>).</summary>
    /// <exception cref="IOException">The file could not be written; an existing file at the path is unchanged.</exception>
    public static void WriteFile(Simulation simulation, string path, SaveWriteOptions? options = null) =>
        AtomicFile.WriteAllBytes(path, ToBytes(simulation, options));

    /// <summary>Reads a save from <paramref name="input"/> (to its end) and restores the simulation.</summary>
    /// <param name="input">Stream positioned at the start of the save.</param>
    /// <param name="content">Content to run the restored simulation with.</param>
    /// <param name="log">Log for the restored simulation; a default one when null.</param>
    public static SaveLoadResult Read(Stream input, ContentCatalog content, SimLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] data;
        try
        {
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            data = buffer.ToArray();
        }
        catch (IOException ex)
        {
            return Fail(SaveErrorKind.Io, $"Cannot read save: {ex.Message}");
        }

        return Read(data, content, log, SaveMigrations.All);
    }

    /// <summary>Reads the save file at <paramref name="path"/> and restores the simulation.</summary>
    public static SaveLoadResult ReadFile(string path, ContentCatalog content, SimLog? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(SaveErrorKind.Io, $"Cannot read save '{path}': {ex.Message}");
        }

        return Read(data, content, log, SaveMigrations.All);
    }

    /// <summary>Reads a save held in memory and restores the simulation.</summary>
    public static SaveLoadResult Read(ReadOnlySpan<byte> data, ContentCatalog content, SimLog? log = null) =>
        Read(data, content, log, SaveMigrations.All);

    // Test hook: an explicit migration chain and target version.
    internal static SaveLoadResult Read(ReadOnlySpan<byte> data, ContentCatalog content, SimLog? log,
        IReadOnlyList<ISaveMigration> migrations, int currentVersion = SaveFormat.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(migrations);

        // 1. Header line.
        int newline = data[..Math.Min(data.Length, SaveFormat.MaxHeaderBytes + 1)].IndexOf((byte)'\n');
        if (newline < 0)
        {
            return Fail(SaveErrorKind.InvalidHeader, "No header line found: not a save file, or the file is truncated.");
        }

        JsonObject? header;
        try
        {
            header = JsonNode.Parse(data[..newline], documentOptions: _documentOptions) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Fail(SaveErrorKind.InvalidHeader, $"Header is not valid JSON: {ex.Message}");
        }

        if (header is null || !TryGetInt(header, SaveMigrator.VersionProperty, out int version))
        {
            return Fail(SaveErrorKind.InvalidHeader, "Header has no integer 'formatVersion'.");
        }

        if (version > currentVersion)
        {
            return Fail(SaveErrorKind.UnsupportedVersion,
                $"Save format v{version} is newer than this build supports (v{currentVersion}). Update the game to load it.", version);
        }

        if (header[CodecProperty] is not JsonValue codecValue || !codecValue.TryGetValue(out string? codec))
        {
            return Fail(SaveErrorKind.InvalidHeader, "Header has no string 'codec'.", version);
        }

        // 2. Body bytes to a JSON tree.
        ReadOnlySpan<byte> encoded = data[(newline + 1)..];
        byte[] bodyJson;
        switch (codec)
        {
            case SaveFormat.CodecBrotliJson:
                try
                {
                    bodyJson = Decompress(encoded);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
                {
                    // BrotliStream reports bad data as InvalidOperationException ("Decoder ran into invalid data").
                    return Fail(SaveErrorKind.CorruptBody, $"Body cannot be decompressed: {ex.Message}", version);
                }

                break;
            case SaveFormat.CodecJson:
                bodyJson = encoded.ToArray();
                break;
            default:
                return Fail(SaveErrorKind.UnknownCodec, $"Unknown body codec '{codec}'.", version);
        }

        JsonObject? body;
        try
        {
            body = JsonNode.Parse(bodyJson, documentOptions: _documentOptions) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Fail(SaveErrorKind.CorruptBody, $"Body is not valid JSON: {ex.Message}", version);
        }

        if (body is null)
        {
            return Fail(SaveErrorKind.CorruptBody, "Body is not a JSON object.", version);
        }

        // 3. Migrations on the tree, then bind to the current-version DTOs.
        SaveHeader headerDto;
        SaveBodyDto bodyDto;
        try
        {
            SaveMigrator.Run(header, body, version, currentVersion, migrations);
            headerDto = header.Deserialize(SaveJsonContext.Default.SaveHeader)
                ?? throw new JsonException("Header is null.");
            bodyDto = body.Deserialize(SaveJsonContext.Default.SaveBodyDto)
                ?? throw new JsonException("Body is null.");
        }
        catch (MissingMigrationException ex)
        {
            return Fail(SaveErrorKind.MissingMigration, ex.Message, version);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // NotSupportedException: missing command 'kind'; InvalidOperationException: JsonNode shape mismatch.
            return Fail(SaveErrorKind.CorruptBody, $"Save does not match format v{currentVersion}: {ex.Message}", version);
        }

        // 4. The world is rebuilt from the seed, so the generator version must be one this build can run.
        bool hasWorld = bodyDto.Game is { MapSize: not 0 };
        if (!SaveFormat.IsLoadableWorldGenVersion(headerDto.WorldGenVersion)
            || (hasWorld && !WorldGen.WorldGenerator.IsSupportedVersion(headerDto.WorldGenVersion)))
        {
            return new SaveLoadResult(null, headerDto, version, new SaveError(SaveErrorKind.UnsupportedWorldGenVersion,
                $"Save was made with world generator v{headerDto.WorldGenVersion}; this build has v{SaveFormat.WorldGenVersion} and cannot rebuild that world."));
        }

        // 5. Restore; a game's map is regenerated and must hash to the saved map hash.
        try
        {
            SimulationState state = SaveMapper.ToState(headerDto, bodyDto);
            Simulation simulation = Simulation.Restore(content, state, log);
            return new SaveLoadResult(simulation, headerDto, version, null);
        }
        catch (WorldGenMismatchException ex)
        {
            return new SaveLoadResult(null, headerDto, version, new SaveError(SaveErrorKind.WorldGenMismatch, ex.Message));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new SaveLoadResult(null, headerDto, version, new SaveError(SaveErrorKind.InvalidState, $"Save describes an invalid state: {ex.Message}"));
        }
    }

    private static bool TryGetInt(JsonObject obj, string name, out int value)
    {
        value = 0;
        return obj[name] is JsonValue node && node.GetValueKind() == JsonValueKind.Number && node.TryGetValue(out value);
    }

    private static byte[] Compress(byte[] data)
    {
        var destination = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, destination, out int written, SaveFormat.BrotliQuality, SaveFormat.BrotliWindow))
        {
            throw new InvalidOperationException("Brotli compression failed.");
        }

        return destination.AsSpan(0, written).ToArray();
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
            if (output.Length + read > MaxBodyBytes)
            {
                throw new InvalidDataException($"Decoded body exceeds {MaxBodyBytes} bytes.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static SaveLoadResult Fail(SaveErrorKind kind, string message, int version = 0) =>
        new(null, null, version, new SaveError(kind, message));
}
