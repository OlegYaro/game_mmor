namespace Core.Save;

/// <summary>
/// Constants of the save file container (ADR 0007).
/// </summary>
/// <remarks>
/// <para>Layout: line 1 is the header, a compact UTF-8 JSON object (<see cref="SaveHeader"/>) followed by
/// <c>'\n'</c>. Compact JSON never contains a raw newline, so the first <c>'\n'</c> always ends the header. The rest
/// of the file is the body (<see cref="SaveBodyDto"/>) encoded as the header's <c>codec</c> says.</para>
/// <para>Changing anything the body or header contain means bumping <see cref="CurrentVersion"/> and adding an
/// <see cref="ISaveMigration"/> from the previous version, plus a sample file of the old version in the tests.</para>
/// </remarks>
public static class SaveFormat
{
    /// <summary>Format version written by this build. Older versions are migrated on load; newer ones are rejected.</summary>
    /// <remarks>
    /// v1 (M0): simulation core only. v2 (M1): adds <c>game</c> (difficulty, map size, map hash), <c>fog</c> (explored
    /// runs) and the <c>revealer</c> component store; <see cref="SaveMigrations"/> brings v1 files up to v2 as games
    /// without a world. v3 (M2): adds <c>tiles</c> (tile changes) and the <c>hero</c>, <c>inventory</c> and
    /// <c>mover</c> stores; v2 files get empty ones, and their hero revealer becomes the hero on load.
    /// </remarks>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Version of the world generator the save's seed must be fed to (ADR 0006), written into every new header. It is
    /// <see cref="WorldGen.WorldGenerator.Version"/>: loading regenerates the map from the seed with that generator.
    /// </summary>
    public const int WorldGenVersion = WorldGen.WorldGenerator.Version;

    /// <summary>
    /// <c>worldGenVersion</c> of saves written before the world generator existed (M0, e.g. the committed
    /// <c>save_v1.sav</c> sample): they hold no world, only the simulation core, and keep loading as such.
    /// Any other value that this build's generator cannot reproduce is rejected with
    /// <see cref="SaveErrorKind.UnsupportedWorldGenVersion"/>.
    /// </summary>
    public const int NoWorldGenVersion = 0;

    /// <summary>True when a save with header <c>worldGenVersion</c> = <paramref name="worldGenVersion"/> can be loaded.</summary>
    public static bool IsLoadableWorldGenVersion(int worldGenVersion) =>
        worldGenVersion == NoWorldGenVersion || WorldGen.WorldGenerator.IsSupportedVersion(worldGenVersion);

    /// <summary>Codec of normal saves: canonical compact JSON, Brotli-compressed.</summary>
    public const string CodecBrotliJson = "json-brotli";

    /// <summary>Codec of developer saves (<see cref="SaveWriteOptions.Indented"/>): indented JSON, uncompressed.</summary>
    public const string CodecJson = "json";

    /// <summary>Recommended file extension.</summary>
    public const string FileExtension = ".sav";

    /// <summary>Longest header line accepted; anything longer is not a save file.</summary>
    public const int MaxHeaderBytes = 4096;

    /// <summary>Brotli quality (0–11). 5 balances speed and size (docs/research/save-format.md).</summary>
    internal const int BrotliQuality = 5;

    /// <summary>Brotli window (log2 of bytes). Fixed so output does not depend on encoder defaults.</summary>
    internal const int BrotliWindow = 22;
}
