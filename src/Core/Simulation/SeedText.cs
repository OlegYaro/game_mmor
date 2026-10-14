using System.Globalization;
using System.Text;

namespace Core.Sim;

/// <summary>
/// Turns the text a player types as a world seed into the numeric seed (FR-SAVE-05). A whole number up to
/// <see cref="ulong.MaxValue"/> is used as is; any other text maps to a fixed 64-bit FNV-1a hash of its UTF-8 bytes, so
/// the same words always give the same world on every platform.
/// </summary>
public static class SeedText
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Converts <paramref name="text"/> (trimmed) to a seed; false for empty or whitespace text.</summary>
    public static bool TryParse(string? text, out ulong seed)
    {
        seed = 0;
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out seed))
        {
            return true;
        }

        ulong hash = FnvOffset;
        foreach (byte b in Encoding.UTF8.GetBytes(trimmed))
        {
            hash = (hash ^ b) * FnvPrime;
        }

        seed = hash;
        return true;
    }
}
