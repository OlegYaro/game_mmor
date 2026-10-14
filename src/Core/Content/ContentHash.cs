using System.Globalization;
using Core.Determinism;

namespace Core.Content;

/// <summary>
/// Canonical XXH3-64 hash of a set of content files (replays and saves record it to detect changed content, ADR 0005).
/// </summary>
/// <remarks>
/// Canonical form: files sorted by ordinal path; per file the path and the text, each length-prefixed UTF-8
/// (<see cref="StateWriter.WriteString"/>). The text is normalized first: a leading BOM is dropped and CRLF/CR
/// become LF, so a Windows checkout with <c>core.autocrlf</c> hashes the same as macOS.
/// </remarks>
public static class ContentHash
{
    /// <summary>Hash of <paramref name="files"/> in canonical form.</summary>
    /// <exception cref="ArgumentException">Two files have the same path.</exception>
    public static ulong Compute(IEnumerable<ContentFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        ContentFile[] sorted = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var writer = new StateWriter();
        writer.WriteInt32(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i > 0 && string.Equals(sorted[i].Path, sorted[i - 1].Path, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Content file '{sorted[i].Path}' appears twice.", nameof(files));
            }

            writer.WriteString(sorted[i].Path);
            writer.WriteString(Normalize(sorted[i].Text));
        }

        return StateHasher.HashBytes(writer.WrittenSpan);
    }

    /// <summary>Formats a hash as 16 lowercase hex digits (the form used in replay files and logs).</summary>
    public static string Format(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Parses the output of <see cref="Format"/>; false for anything else.</summary>
    public static bool TryParse(string? text, out ulong hash)
    {
        hash = 0;
        return text is { Length: 16 }
            && !text.Any(char.IsUpper)
            && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash);
    }

    private static string Normalize(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        return text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;
    }
}
