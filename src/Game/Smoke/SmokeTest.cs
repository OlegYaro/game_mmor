using Core;
using Godot;

namespace Game.Smoke;

/// <summary>
/// Headless smoke test (ADR 0011). Launched via <c>Main</c> with <c>-- --smoke</c>: runs a few frames,
/// calls into Core, prints <see cref="OkMarker"/> and quits with exit code 0.
/// With <c>--smoke-throw</c> it throws inside <c>_Process</c> to prove that <see cref="CrashGuard"/>
/// turns an exception into <see cref="CrashGuard.Marker"/> and exit code 1.
/// </summary>
public partial class SmokeTest : Node
{
    /// <summary>Marker the smoke script looks for in the log.</summary>
    public const string OkMarker = "SMOKE_OK";

    private const string ThrowArg = "--smoke-throw";
    private const int FramesToRun = 10;
    private const int ThrowAtFrame = 3;

    private int _frames;
    private bool _throwOnPurpose;

    public override void _Ready()
    {
        _throwOnPurpose = OS.GetCmdlineUserArgs().Contains(ThrowArg);
        GD.Print($"SMOKE_START core={CoreInfo.Name} {CoreInfo.Version}");
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_throwOnPurpose && _frames == ThrowAtFrame)
        {
            throw new InvalidOperationException($"Deliberate smoke-test exception ({ThrowArg}).");
        }

        if (_frames < FramesToRun || CrashGuard.HasFailed)
        {
            return;
        }

        GD.Print(OkMarker);
        GetTree().Quit(0);
        SetProcess(false);
    }
}
