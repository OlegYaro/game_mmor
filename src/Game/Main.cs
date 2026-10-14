using Core;
using Godot;

namespace Game;

/// <summary>
/// Entry scene. Routes to the smoke test when launched with <c>-- --smoke</c>.
/// </summary>
public partial class Main : Node2D
{
    private const string SmokeArg = "--smoke";
    private const string SmokeScenePath = "res://Smoke/SmokeTest.tscn";

    public override void _Ready()
    {
        GD.Print($"{CoreInfo.Name} {CoreInfo.Version}");

        if (OS.GetCmdlineUserArgs().Contains(SmokeArg))
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, SmokeScenePath);
        }
    }
}
