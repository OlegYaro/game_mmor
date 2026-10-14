namespace Core.Save;

/// <summary>
/// Crash-safe file replacement (ADR 0007): the data goes to a temporary file in the same directory, is flushed to
/// disk, and the temporary file is then renamed over the target. A crash leaves either the old or the new file,
/// never a half-written one. Core gets the path from the host; it never picks locations itself.
/// </summary>
public static class AtomicFile
{
    /// <summary>Suffix of the temporary file next to the target.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> atomically, creating the directory if needed.</summary>
    /// <exception cref="IOException">The write or the rename failed; the target is unchanged.</exception>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = fullPath + TempSuffix;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is overwritten by the next save.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
