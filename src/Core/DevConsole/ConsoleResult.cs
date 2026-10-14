namespace Core.DevConsole;

/// <summary>Outcome of one console command: output lines and whether the command worked.</summary>
public sealed record ConsoleResult
{
    private ConsoleResult(bool success, IReadOnlyList<string> lines)
    {
        Success = success;
        Lines = lines;
    }

    /// <summary>True when the command did what was asked.</summary>
    public bool Success { get; }

    /// <summary>Output lines, in order. For a failure these hold the error message.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>A successful result with the given lines.</summary>
    public static ConsoleResult Ok(params IEnumerable<string> lines) => new(true, [.. lines]);

    /// <summary>A failed result with the given error lines.</summary>
    public static ConsoleResult Fail(params IEnumerable<string> lines) => new(false, [.. lines]);

    /// <summary>All lines joined with <c>\n</c>.</summary>
    public string Text => string.Join('\n', Lines);
}
