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
    private EntityLayer? _entities;
    private PlayerInput? _playerInput;
    private ResidentLayer? _residents;
    private ResidentPicker? _residentPicker;
    private ResidentMessages? _residentMessages;

    /// <summary>Flags shared by the world input handlers (hero control, build mode, inspector).</summary>
    public GameInputState InputState { get; } = new();

    /// <summary>The building selected on the map.</summary>
    public BuildingSelection Selection { get; } = new();

    /// <summary>The resident selected on the map.</summary>
    public ResidentSelection ResidentSelection { get; } = new();

    /// <summary>The residents drawn on the map, or null when the world view is missing.</summary>
    public ResidentLayer? ResidentLayer => _residents;

    /// <summary>The click handler that selects residents, or null when the world view is missing.</summary>
    public ResidentPicker? ResidentPicker => _residentPicker;

    /// <summary>Build mode: <see cref="BuildMode.Begin"/> / <see cref="BuildMode.BeginMove"/>, <see cref="BuildMode.ToggleRequested"/>.</summary>
    public BuildMode BuildMode { get; } = new() { Name = "BuildMode" };

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

        if (_worldView is not null)
        {
            _entities = new EntityLayer { Name = "Entities" };
            _worldView.ObjectsRoot.AddChild(_entities);
            _playerInput = new PlayerInput { Name = "PlayerInput" };
            AddChild(_playerInput);
            _residents = new ResidentLayer { Name = "Residents" };
            _worldView.ObjectsRoot.AddChild(_residents);
            // Added after the hero input: unhandled input reaches the later node first, so a resident is picked before the hero walks.
            _residentPicker = new ResidentPicker { Name = "ResidentPicker" };
            AddChild(_residentPicker);
            var buildings = new BuildingLayer { Name = "BuildingLayer" };
            var overlay = new FootprintOverlay();
            AddChild(buildings);
            _worldView.AddChild(overlay);
            buildings.Attach(_worldView);
            overlay.Attach(_worldView, Selection);
            AddChild(BuildMode);
            BuildMode.Attach(_worldView, InputState, Selection, overlay, command => _host?.Send(command));
        }

        _residentMessages = new ResidentMessages { Name = "ResidentMessages" };
        GetNode<Node>("Ui").AddChild(_residentMessages);
        _errorPanel = GetNode<Control>("Ui/ErrorPanel");
        _errorPanel.Visible = _worldView is null || _minimapView is null;
        GetNode<PauseMenu>("Ui/PauseMenu").ExitToMenuRequested += () => ExitToMenuRequested?.Invoke();
    }

    public override void _Process(double delta)
    {
        // A menu (pause, console) blocks the host's speed keys; the world input handlers follow the same flag.
        InputState.WorldInputBlocked = _host?.InputBlocked ?? false;
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
        if (_worldView is not null)
        {
            _playerInput?.Bind(host, _worldView, InputState);
            if (_entities is not null)
            {
                _entities.TickSeconds = () => 1.0 / (host.Scheduler.TicksPerSecond * (int)host.Scheduler.Speed);
            }

            if (_residents is not null && _residentPicker is not null)
            {
                _residents.TickSeconds = () => 1.0 / (host.Scheduler.TicksPerSecond * (int)host.Scheduler.Speed);
                _residentPicker.Bind(host, _worldView, _residents, ResidentSelection, InputState);
            }
        }

        GetNode<Hud>("Ui/Hud").Bind(host);
        GetNode<PauseMenu>("Ui/PauseMenu").Bind(host);
        GetNode<ResourceBar>("Ui/ResourceBar").Bind(host);
        GetNode<BuildMenu>("Ui/BuildMenu").Bind(host);
        GetNode<InspectorPanel>("Ui/InspectorPanel").Bind(host);
        GetNode<BuildingMessages>("Ui/BuildingMessages").Bind(host);
        _residentMessages?.Bind(host);
        WireBuildingUi();
        DevConsolePanel console = GetNode<DevConsolePanel>("Ui/DevConsolePanel");
        console.Bind(host);
        console.CameraRequested += (x, y) => CenterCamera(x, y);
        OnSimulationChanged();
    }

    // Connects the build menu, the inspector and the build mode through their events (only the Game layer knows all three).
    private void WireBuildingUi()
    {
        var menu = GetNode<BuildMenu>("Ui/BuildMenu");
        var inspector = GetNode<InspectorPanel>("Ui/InspectorPanel");
        menu.BuildingChosen += id => BuildMode.Begin(id);
        inspector.MoveRequested += entity => BuildMode.BeginMove(entity);
        inspector.CloseRequested += Selection.Clear;
        BuildMode.ToggleRequested += menu.ToggleVisible;
        BuildMode.ActiveChanged += () =>
        {
            if (BuildMode.IsActive)
            {
                Selection.Clear();
            }
        };
        // A building and a resident are never selected together.
        ResidentSelection.ResidentSelected += resident =>
        {
            if (resident != Core.Entities.EntityId.None)
            {
                Selection.Clear();
            }
        };
        Selection.BuildingSelected += entity =>
        {
            if (entity != Core.Entities.EntityId.None)
            {
                ResidentSelection.Clear();
            }

            if (entity == Core.Entities.EntityId.None)
            {
                inspector.Hide();
            }
            else
            {
                inspector.Show(entity);
            }
        };
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

        BindHero();
        BindResidents();
        Core.WorldGen.StartPoint? start = _host.Simulation.World?.Map.Start;
        if (start is { } point)
        {
            CenterCamera(point.X, point.Y);
        }

        if (_entities is { HasHero: true } entities && _worldView is not null)
        {
            // Follow the hero (F returns to it); WASD steers the hero instead of panning.
            _worldView.Camera.FollowProvider = () => entities.HeroFollowPoint;
            _worldView.Camera.KeyPanEnabled = false;
            _worldView.Camera.Recenter();
        }
    }

    // Shows the hero of the bound simulation, or nothing (and restores key panning) when the game has none.
    private void BindHero()
    {
        if (_entities is null || _worldView is null || _host is null)
        {
            return;
        }

        _entities.Unbind();
        _worldView.Camera.FollowProvider = null;
        _worldView.Camera.KeyPanEnabled = true;
        if (WorldArt.TryGetShared(out WorldArt art, out _))
        {
            _entities.Bind(_host.Simulation, art);
        }
    }

    // Shows the residents of the bound simulation (the layer is emptied first, so nothing of the old game stays).
    private void BindResidents()
    {
        if (_residents is null || _host is null)
        {
            return;
        }

        _residents.Unbind();
        ResidentSelection.Clear();
        if (WorldArt.TryGetShared(out WorldArt art, out _))
        {
            _residents.Bind(_host.Simulation, art);
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
