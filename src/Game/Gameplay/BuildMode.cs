using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Geometry;
using Core.Sim;
using Game.World;
using Godot;

namespace Game.Gameplay;

/// <summary>
/// The build mode and the building selection on the map (FR-BUILD-01, FR-BUILD-13). While a building is chosen the cursor shows
/// its footprint (green when <see cref="Construction.CheckPlacement"/> accepts the tile, red with a reason otherwise); the left
/// button sends <see cref="PlaceBuildingCommand"/> (or <see cref="MoveBuildingCommand"/> when moving), the right button and Esc
/// leave. Outside the mode a left click on a building selects it (<see cref="BuildingSelection"/>), a click on empty ground
/// clears the selection and still reaches the hero.
/// </summary>
/// <remarks>
/// <para>Anchor: the tile under the cursor is the footprint's top-left tile (smallest x and y, the top of the diamond); a quarter
/// turn (R) keeps that tile and swaps width and height.</para>
/// <para>Input: everything is read in <c>_Input</c> and a consumed event is marked handled, so <c>_UnhandledInput</c> handlers
/// (the hero control) never see it. Mouse events over a GUI control are left alone. B raises <see cref="ToggleRequested"/>; keys
/// 1-3, Space and F2 are not used. Placement validity is never computed here: it is Core's answer.</para>
/// </remarks>
public partial class BuildMode : Node
{
    private const int TooltipLayer = 10;
    private static readonly Vector2 _tooltipOffset = new(18, 22);

    private readonly HashSet<MouseButton> _swallowRelease = [];
    private WorldView? _view;
    private GameInputState? _input;
    private BuildingSelection? _selection;
    private FootprintOverlay? _overlay;
    private Action<ISimCommand>? _send;
    private Simulation? _sim;
    private Label? _tooltip;
    private BuildingDef? _def;
    private bool _hasAnchor;
    private int _anchorX;
    private int _anchorY;
    private (int X, int Y, int Rotation, long Tick) _lastPreview = (-1, -1, -1, -1);

    /// <summary>
    /// Raised when the player presses B. The build menu listens and opens or closes itself. When nobody listens, B starts the
    /// mode for <see cref="LastBuildingId"/> instead. While the mode is active, B leaves it before the event is raised.
    /// </summary>
    public event Action? ToggleRequested;

    /// <summary>Raised after the mode started or ended (<see cref="IsActive"/> tells which).</summary>
    public event Action? ActiveChanged;

    /// <summary>True while a building is being placed or moved.</summary>
    public bool IsActive => _def is not null;

    /// <summary>Id of the building being placed or moved; null when inactive.</summary>
    public string? BuildingId => _def?.Id;

    /// <summary>The building being moved, or <see cref="EntityId.None"/> when placing a new one.</summary>
    public EntityId MovingBuilding { get; private set; }

    /// <summary>Quarter turns of the footprint, 0 to 3.</summary>
    public int Rotation { get; private set; }

    /// <summary>The building type chosen last (also after the mode ended); null before the first choice.</summary>
    public string? LastBuildingId { get; private set; }

    /// <summary>Core's verdict for the footprint at the last previewed tile.</summary>
    public PlacementResult Placement { get; private set; }

    /// <summary>True when a tile under the cursor was checked since the mode started (or the last rotation).</summary>
    public bool HasPreview => _hasAnchor;

    /// <summary>Asks whether a resident is drawn in front of the building at the viewport position (set by the owner; may be null).</summary>
    public Func<Vector2, float, bool>? ResidentInFront { get; set; }

    /// <summary>Connects the mode to the world, the shared flags, the selection, the overlay and the command sink (<c>SimulationHost.Send</c>).</summary>
    public void Attach(WorldView view, GameInputState input, BuildingSelection selection, FootprintOverlay overlay, Action<ISimCommand> send)
    {
        _view = view;
        _input = input;
        _selection = selection;
        _overlay = overlay;
        _send = send;
        if (_tooltip is null)
        {
            var layer = new CanvasLayer { Layer = TooltipLayer };
            _tooltip = new Label { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            var box = new StyleBoxFlat { BgColor = new Color(0.08f, 0.08f, 0.1f, 0.85f) };
            box.SetContentMarginAll(6);
            _tooltip.AddThemeStyleboxOverride("normal", box);
            layer.AddChild(_tooltip);
            AddChild(layer);
        }
    }

    /// <summary>Starts placing a new building of type <paramref name="buildingId"/>. False for an unknown id or when no game runs.</summary>
    public bool Begin(string buildingId)
    {
        if (_view?.Simulation is not { World: not null } sim || !sim.Content.TryGet(buildingId, out BuildingDef def))
        {
            return false;
        }

        Start(sim, def, EntityId.None, 0);
        return true;
    }

    /// <summary>Starts moving an existing building (FR-BUILD-13): the footprint follows the cursor, a click sends the move. False when it is not a building.</summary>
    public bool BeginMove(EntityId building)
    {
        if (_view?.Simulation is not { World: { } world } sim || !world.Buildings.TryGet(building, out Building value))
        {
            return false;
        }

        Start(sim, Construction.DefOf(sim.Content, value), building, value.Rotation);
        return true;
    }

    /// <summary>Leaves the mode without sending anything.</summary>
    public void Cancel()
    {
        if (_def is null)
        {
            return;
        }

        _def = null;
        MovingBuilding = EntityId.None;
        Rotation = 0;
        _hasAnchor = false;
        _lastPreview = (-1, -1, -1, -1);
        Placement = default;
        _overlay?.HidePreview();
        if (_tooltip is not null)
        {
            _tooltip.Visible = false;
        }

        if (_input is not null)
        {
            _input.BuildModeActive = false;
        }

        ActiveChanged?.Invoke();
    }

    /// <summary>Turns the footprint a quarter turn when the type is rotatable; the anchor tile stays.</summary>
    public void RotateStep()
    {
        if (_def is not { Rotatable: true })
        {
            return;
        }

        Rotation = (Rotation + 1) & Construction.MaxRotation;
        if (_hasAnchor)
        {
            Preview(_anchorX, _anchorY);
        }
    }

    /// <summary>
    /// Checks the footprint with its top-left tile at (x, y) through Core and shows it (green or red). The mode must be active.
    /// </summary>
    /// <returns>True when the placement is valid.</returns>
    public bool Preview(int x, int y)
    {
        if (_def is null || _view?.Simulation is not { } sim)
        {
            return false;
        }

        _hasAnchor = true;
        _anchorX = x;
        _anchorY = y;
        _lastPreview = (x, y, Rotation, sim.Tick);
        Placement = Construction.CheckPlacement(sim, _def.Id, x, y, Rotation, MovingBuilding);
        (int width, int height) = Construction.FootprintOf(_def, Rotation);
        _overlay?.ShowPreview(x, y, width, height, Placement);
        ShowTooltip(true);
        return Placement.IsOk;
    }

    /// <summary>
    /// Sends the placement (or move) at the previewed tile when Core accepts it, and leaves the mode (a held Shift keeps it for
    /// another building of the same type, except for the single campfire and for moves).
    /// </summary>
    /// <returns>True when a command was sent.</returns>
    public bool Confirm()
    {
        if (_def is null || !_hasAnchor || !Placement.IsOk || _send is null)
        {
            return false;
        }

        ISimCommand command = MovingBuilding != EntityId.None
            ? new MoveBuildingCommand(MovingBuilding, _anchorX, _anchorY, Rotation)
            : new PlaceBuildingCommand(_def.Id, _anchorX, _anchorY, Rotation);
        _send(command);
        bool keep = Input.IsKeyPressed(Key.Shift) && MovingBuilding == EntityId.None && !Construction.IsCenter(_def);
        if (keep)
        {
            // The next check runs on the tick after the command, when the new building stands.
            _lastPreview = (-1, -1, -1, -1);
        }
        else
        {
            Cancel();
        }

        return true;
    }

    public override void _ExitTree()
    {
        Cancel();
    }

    public override void _Process(double delta)
    {
        Simulation? sim = _view?.Simulation;
        if (!ReferenceEquals(sim, _sim))
        {
            _sim = sim;
            Cancel();
            _selection?.Clear();
        }

        if (sim?.World is not { } world)
        {
            return;
        }

        if (_selection is { Selected.IsValid: true } selection && !world.Buildings.Has(selection.Selected))
        {
            selection.Clear();
        }

        if (_def is null || _view is null)
        {
            return;
        }

        if (_input?.WorldInputBlocked == true || IsOverGui() || !WorldPicker.TryPickTile(_view, GetViewport().GetMousePosition(), out int x, out int y))
        {
            _overlay?.HidePreview();
            ShowTooltip(false);
            _lastPreview = (-1, -1, -1, -1);
            return;
        }

        if (_lastPreview != (x, y, Rotation, sim.Tick))
        {
            Preview(x, y);
        }
    }

    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                OnKey(key);
                break;
            case InputEventMouseButton button:
                OnMouse(button);
                break;
        }
    }

    private void Start(Simulation sim, BuildingDef def, EntityId moving, int rotation)
    {
        bool wasActive = IsActive;
        _def = def;
        MovingBuilding = moving;
        Rotation = def.Rotatable ? rotation : 0;
        LastBuildingId = def.Id;
        _hasAnchor = false;
        _lastPreview = (-1, -1, -1, -1);
        _sim = sim;
        if (_input is not null)
        {
            _input.BuildModeActive = true;
        }

        if (!wasActive)
        {
            ActiveChanged?.Invoke();
        }
    }

    private void OnKey(InputEventKey key)
    {
        if (_input?.WorldInputBlocked == true || key.CtrlPressed || key.AltPressed || key.MetaPressed || IsTyping())
        {
            return;
        }

        if (key.PhysicalKeycode == Key.B)
        {
            if (IsActive)
            {
                Cancel();
                ToggleRequested?.Invoke();
            }
            else if (ToggleRequested is not null)
            {
                ToggleRequested.Invoke();
            }
            else if (LastBuildingId is not null)
            {
                Begin(LastBuildingId);
            }

            Consume();
        }
        else if (IsActive && key.Keycode == Key.Escape)
        {
            Cancel();
            Consume();
        }
        else if (IsActive && key.PhysicalKeycode == Key.R)
        {
            RotateStep();
            Consume();
        }
    }

    private void OnMouse(InputEventMouseButton button)
    {
        if (!button.Pressed)
        {
            if (_swallowRelease.Remove(button.ButtonIndex))
            {
                Consume();
            }

            return;
        }

        if (button.ButtonIndex is not (MouseButton.Left or MouseButton.Right)
            || _view is null || _input?.WorldInputBlocked == true || IsOverGui())
        {
            return;
        }

        if (IsActive)
        {
            if (button.ButtonIndex == MouseButton.Right)
            {
                Cancel();
            }
            else if (WorldPicker.TryPickTile(_view, button.Position, out int x, out int y))
            {
                Preview(x, y);
                Confirm();
            }

            _swallowRelease.Add(button.ButtonIndex);
            Consume();
            return;
        }

        if (button.ButtonIndex == MouseButton.Left)
        {
            SelectAt(button.Position);
        }
    }

    // Selects the building on the clicked tile; a click anywhere else clears the selection and goes on to the hero.
    private void SelectAt(Vector2 screen)
    {
        if (_selection is null || _view?.Simulation?.World is not { } world)
        {
            return;
        }

        if (WorldPicker.TryPickTile(_view, screen, out int x, out int y))
        {
            EntityId occupant = world.Tiles.GetOccupant(x, y);
            if (occupant != EntityId.None && world.Buildings.Has(occupant))
            {
                // A resident drawn in front of the building (y-sort) takes the click: it is not consumed, the resident picker gets it.
                if (ResidentInFront?.Invoke(screen, IsoProjection.TileCenter(x, y).Py) == true)
                {
                    return;
                }

                _selection.Select(occupant);
                _swallowRelease.Add(MouseButton.Left);
                Consume();
                return;
            }
        }

        _selection.Clear();
    }

    private void ShowTooltip(bool visible)
    {
        if (_tooltip is null)
        {
            return;
        }

        if (!visible || _def is null || !_hasAnchor)
        {
            _tooltip.Visible = false;
            return;
        }

        string hintKey = MovingBuilding != EntityId.None ? "build.hint.move" : _def.Rotatable ? "build.hint.place_rotate" : "build.hint.place";
        _tooltip.Text = Tr(Placement.IsOk ? hintKey : "build.reason." + Construction.NameOf(Placement.Reason));
        _tooltip.AddThemeColorOverride("font_color", Placement.IsOk ? new Color(0.85f, 1f, 0.85f) : new Color(1f, 0.6f, 0.55f));
        Vector2 mouse = GetViewport().GetMousePosition();
        Vector2 size = _tooltip.GetCombinedMinimumSize();
        Vector2 limit = GetViewport().GetVisibleRect().Size - size;
        _tooltip.Position = new Vector2(Math.Min(mouse.X + _tooltipOffset.X, limit.X), Math.Min(mouse.Y + _tooltipOffset.Y, limit.Y));
        _tooltip.Visible = true;
    }

    private bool IsOverGui() => GetViewport().GuiGetHoveredControl() is not null;

    private bool IsTyping() => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;

    private void Consume() => GetViewport().SetInputAsHandled();
}
