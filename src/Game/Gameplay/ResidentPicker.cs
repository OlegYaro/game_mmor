using Core.Entities;
using Game.Sim;
using Game.World;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Gameplay;

/// <summary>
/// Selecting a resident with the left mouse button (FR-NPC-01). It is added to <c>GameRoot</c> after the hero input, so its
/// <c>_UnhandledInput</c> runs first: build mode (which sees clicks in <c>_Input</c> and takes the buildings) has already had its
/// turn, a click on a resident selects it and is consumed (the hero does not walk there), a click anywhere else clears the
/// selection and goes on to the hero. The ring under the selected resident is drawn by <see cref="ResidentLayer"/>.
/// </summary>
public partial class ResidentPicker : Node
{
    private SimulationHost? _host;
    private WorldView? _view;
    private ResidentLayer? _layer;
    private ResidentSelection? _selection;
    private GameInputState _state = new();
    private CoreSimulation? _sim;

    /// <summary>Connects the picker to the game.</summary>
    public void Bind(SimulationHost host, WorldView view, ResidentLayer layer, ResidentSelection selection, GameInputState state)
    {
        _host = host;
        _view = view;
        _layer = layer;
        _selection = selection;
        _state = state;
    }

    public override void _Process(double delta)
    {
        if (_selection is null || _layer is null)
        {
            return;
        }

        CoreSimulation? sim = _host is { IsRunning: true } ? _host.Simulation : null;
        if (!ReferenceEquals(sim, _sim))
        {
            _sim = sim;
            _selection.Clear();
        }

        // The selected resident left (or was never real): drop the selection so the window closes.
        if (_selection.Selected.IsValid && sim?.World is { } world && !world.Residents.Has(_selection.Selected))
        {
            _selection.Clear();
        }

        _layer.Selected = _selection.Selected;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && ClickAt(click.Position))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Handles a left click at a viewport position: selects the resident drawn there (true, the click is consumed) or clears
    /// the selection (false, the click is the hero's). Does nothing while build mode or a menu owns the mouse.
    /// </summary>
    public bool ClickAt(Vector2 screen)
    {
        if (_host is not { IsRunning: true, InputBlocked: false } || _view is null || _selection is null || _state.BuildModeActive
            || _state.WorldInputBlocked || !_view.IsBound)
        {
            return false;
        }

        return SelectAtWorld(_view.Camera.ScreenToWorld(screen));
    }

    /// <summary>
    /// True when a resident is drawn in front of a building whose feet are at <paramref name="buildingFootY"/> (the y-sort
    /// rule: the larger y is in front) and covers the viewport position <paramref name="screen"/>. Build mode asks this
    /// before it selects a building, so the click goes on to <see cref="ClickAt"/>.
    /// </summary>
    public bool IsResidentInFront(Vector2 screen, float buildingFootY)
    {
        return _layer is not null && _view is { IsBound: true }
            && _layer.TryPick(_view.Camera.ScreenToWorld(screen), out _, out float footY) && footY >= buildingFootY;
    }

    /// <summary>Same as <see cref="ClickAt"/> for a point in world coordinates (the coordinates of the objects layer).</summary>
    public bool SelectAtWorld(Vector2 world)
    {
        if (_layer is null || _selection is null)
        {
            return false;
        }

        if (_layer.TryPick(world, out EntityId resident))
        {
            _selection.Select(resident);
            _layer.Selected = resident;
            return true;
        }

        _selection.Clear();
        _layer.Selected = EntityId.None;
        return false;
    }
}
