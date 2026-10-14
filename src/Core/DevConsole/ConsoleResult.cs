namespace Core.DevConsole;

/// <summary>
/// A request from a console command to the host's camera (console <c>goto</c>). The camera lives in Game, so Core
/// only describes the wish; the host checks <see cref="ConsoleResult.CameraRequest"/> after each command.
/// </summary>
/// <param name="TileX">Tile column to centre on.</param>
/// <param name="TileY">Tile row to centre on.</param>
public sealed record CameraRequest(int TileX, int TileY);

/// <summary>Outcome of one console command: output lines, whether the command worked, and an optional host request.</summary>
public sealed record ConsoleResult
{
    private ConsoleResult(bool success, IReadOnlyList<string> lines, CameraRequest? cameraRequest = null)
    {
        Success = success;
        Lines = lines;
        CameraRequest = cameraRequest;
    }

    /// <summary>True when the command did what was asked.</summary>
    public bool Success { get; }

    /// <summary>Output lines, in order. For a failure these hold the error message.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Where the host should move the camera, or null. Only successful results carry one.</summary>
    public CameraRequest? CameraRequest { get; }

    /// <summary>A successful result with the given lines.</summary>
    public static ConsoleResult Ok(params IEnumerable<string> lines) => new(true, [.. lines]);

    /// <summary>A successful result that also asks the host to move the camera.</summary>
    public static ConsoleResult OkWithCamera(CameraRequest request, params IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(true, [.. lines], request);
    }

    /// <summary>A failed result with the given error lines.</summary>
    public static ConsoleResult Fail(params IEnumerable<string> lines) => new(false, [.. lines]);

    /// <summary>All lines joined with <c>\n</c>.</summary>
    public string Text => string.Join('\n', Lines);
}
