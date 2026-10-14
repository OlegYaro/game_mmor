using Core;
using Core.Save;
using Core.Sim;
using Game.Gameplay;
using Game.Menu;
using Game.Sim;
using Game.Ui;
using Godot;

namespace Game;

/// <summary>
/// Entry scene. Routes to the smoke test when launched with <c>-- --smoke</c>; otherwise loads the settings and content,
/// then runs the flow main menu to game and back. <c>-- --autostart</c> (with optional <c>--seed</c>, <c>--size</c>,
/// <c>--difficulty</c>) skips the menu.
/// </summary>
public partial class Main : Node2D
{
    private const string SmokeArg = "--smoke";
    private const string SmokeScenePath = "res://Smoke/SmokeTest.tscn";
    private const string MenuScenePath = "res://Menu/MainMenu.tscn";
    private const string GameScenePath = "res://Gameplay/GameRoot.tscn";

    private SimulationHost _host = null!;
    private CanvasLayer _screens = null!;
    private MainMenu? _menu;
    private GameRoot? _game;

    public override void _Ready()
    {
        GD.Print($"{CoreInfo.Name} {CoreInfo.Version}");

        if (OS.GetCmdlineUserArgs().Contains(SmokeArg))
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, SmokeScenePath);
            return;
        }

        UserSettings.LoadAndApply();
        _host = GetNode<SimulationHost>("SimulationHost");
        _screens = GetNode<CanvasLayer>("Screens");
        if (!_host.Initialize())
        {
            return;
        }

        var toasts = GetNode<ToastHost>("Overlay/Toasts");
        _host.Toast += toasts.Show;

        if (GameSettings.AutoStart)
        {
            string? error = _host.StartNew(GameSettings.ResolveNewGameOptions());
            if (error is null)
            {
                ShowGame();
                return;
            }

            toasts.Show(error);
        }

        ShowMenu();
    }

    private void ShowMenu()
    {
        _host.Stop();
        _game?.QueueFree();
        _game = null;
        _menu = LoadScene<MainMenu>(MenuScenePath, _screens);
        _menu.Bind(_host);
        _menu.NewGameRequested += options => _ = StartNewAsync(options);
        _menu.LoadRequested += OnLoadRequested;
        _menu.QuitRequested += () => GetTree().Quit();
    }

    private void ShowGame()
    {
        _menu?.QueueFree();
        _menu = null;
        _game = LoadScene<GameRoot>(GameScenePath, this);
        _game.Bind(_host);
        _game.ExitToMenuRequested += ShowMenu;
    }

    // World generation blocks the main thread; one frame is given to the menu to show "generating" first.
    private async Task StartNewAsync(NewGameOptions options)
    {
        MainMenu? menu = _menu;
        menu?.SetBusy(true);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
        string? error = _host.StartNew(options);
        if (error is null)
        {
            ShowGame();
            return;
        }

        menu?.SetBusy(false);
        _host.ShowToast(error);
    }

    private void OnLoadRequested(SaveSlotId id)
    {
        string? error = _host.LoadSlot(id);
        if (error is null)
        {
            ShowGame();
            return;
        }

        _host.ShowToast(error);
    }

    private static T LoadScene<T>(string path, Node parent)
        where T : Node
    {
        var node = (T)GD.Load<PackedScene>(path).Instantiate();
        parent.AddChild(node);
        return node;
    }
}
