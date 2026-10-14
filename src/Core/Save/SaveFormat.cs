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
    public const int CurrentVersion = 1;

    /// <summary>
    /// Version of the world generator the save's seed must be fed to (ADR 0006). There is no generator yet (M2), so it
    /// is 0; a save whose value differs from the running generator will need a full tile snapshot instead of diffs.
    /// </summary>
    public const int WorldGenVersion = 0;

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
