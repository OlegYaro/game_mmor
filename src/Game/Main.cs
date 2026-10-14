using Core;
using Game.Sim;
using Game.Ui;
using Godot;

namespace Game;

/// <summary>
/// Entry scene. Routes to the smoke test when launched with <c>-- --smoke</c>; otherwise starts the simulation
/// and connects the HUD and the developer console to it.
/// </summary>
public partial class Main : Node2D
{
    private const string SmokeArg = "--smoke";
    private const string SmokeScenePath = "res://Smoke/SmokeTest.tscn";
    private const string DefaultLocale = "ru";

    public override void _Ready()
    {
        GD.Print($"{CoreInfo.Name} {CoreInfo.Version}");

        if (OS.GetCmdlineUserArgs().Contains(SmokeArg))
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, SmokeScenePath);
            return;
        }

        TranslationServer.SetLocale(DefaultLocale);
        var host = GetNode<SimulationHost>("SimulationHost");
        if (!host.Initialize())
        {
            return;
        }

        GetNode<Hud>("Ui/Hud").Bind(host);
        GetNode<DevConsolePanel>("Ui/DevConsolePanel").Bind(host);
    }
}
