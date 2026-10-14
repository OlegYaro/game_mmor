using Game.Menu;
using Game.Sim;
using Game.Ui;
using Game.World;
using Godot;

namespace Game.Gameplay;

/// <summary>
/// The running game: the world view and the minimap (from <c>res://World</c>), the HUD, the developer
/// console and the pause menu. It rebinds the views whenever the host swaps the simulation (load, console <c>load</c>).
/// </summary>
public partial class GameRoot : Node
{
    /// <summary>Scene of the world view (owned by the rendering code).</summary>
    public const string WorldViewScene = "res://World/WorldView.tscn";

    /// <summary>Scene of the minimap (owned by the rendering code).</summary>
    public const string MinimapScene = "res://World/MinimapView.tscn";

    private SimulationHost? _host;
    private WorldView? _worldView;
    private MinimapView? _minimapView;
    private Control _errorPanel = null!;

    /// <summary>The player asked to leave for the main menu.</summary>
    public event Action? ExitToMenuRequested;

    public override void _Ready()
    {
        _worldView = Instance(WorldViewScene, GetNode<Node>("World")) as WorldView;
        _minimapView = Instance(MinimapScene, GetNode<Control>("Ui/MinimapSlot")) as MinimapView;
        if (_minimapView is Control minimap)
        {
            minimap.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        }

        _errorPanel = GetNode<Control>("Ui/ErrorPanel");
        _errorPanel.Visible = _worldView is null || _minimapView is null;
        GetNode<PauseMenu>("Ui/PauseMenu").ExitToMenuRequested += () => ExitToMenuRequested?.Invoke();
    }

    public override void _ExitTree()
    {
        if (_host is not null)
        {
            _host.SimulationChanged -= OnSimulationChanged;
        }
    }

    /// <summary>Connects every part to the running host and shows the current simulation.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        host.SimulationChanged += OnSimulationChanged;
        GetNode<Hud>("Ui/Hud").Bind(host);
        GetNode<PauseMenu>("Ui/PauseMenu").Bind(host);
        DevConsolePanel console = GetNode<DevConsolePanel>("Ui/DevConsolePanel");
        console.Bind(host);
        console.CameraRequested += (x, y) => CenterCamera(x, y);
        OnSimulationChanged();
    }

    private void OnSimulationChanged()
    {
        if (_host is not { IsRunning: true })
        {
            return;
        }

        if (_worldView is not null)
        {
            _worldView.Unbind();
            _worldView.Bind(_host.Simulation);
        }

        if (_minimapView is not null)
        {
            _minimapView.Unbind();
            _minimapView.Bind(_host.Simulation, _worldView?.Camera);
        }

        Core.WorldGen.StartPoint? start = _host.Simulation.World?.Map.Start;
        if (start is { } point)
        {
            CenterCamera(point.X, point.Y);
        }
    }

    private void CenterCamera(int tileX, int tileY)
    {
        if (_worldView is not null)
        {
            _worldView.Camera.CenterOnTile(tileX, tileY);
        }
    }

    // Loads and adds a scene when it exists. A missing scene is not fatal: the error panel tells the player and the log.
    private static Node? Instance(string path, Node parent)
    {
        if (!ResourceLoader.Exists(path))
        {
            GD.Print($"Scene {path} does not exist yet; the game runs without it.");
            return null;
        }

        if (ResourceLoader.Load(path) is not PackedScene scene)
        {
            GD.PushWarning($"Scene {path} could not be loaded.");
            return null;
        }

        Node node = scene.Instantiate();
        parent.AddChild(node);
        return node;
    }
}
