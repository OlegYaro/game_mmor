using Core.DevConsole;
using Core.Geometry;
using Godot;

namespace Game.World;

/// <summary>
/// World camera (FR-UI-05): WASD/arrows, the screen edge and middle-drag pan; the wheel and +/- step through
/// <see cref="ZoomLevels"/> with smooth zoom towards the cursor; <c>F</c> returns to the follow target or the home
/// tile; the centre stays inside the map diamond. Keys are read as physical keys, so the Russian layout works the same.
/// Input is ignored while a text field has focus.
/// </summary>
public partial class WorldCamera : Camera2D
{
    /// <summary>Zoom steps, widest first (ADR 0010: integer ratios of 32×16 pixel art plus one overview step).</summary>
    public static readonly float[] ZoomLevels = [0.5f, 1f, 2f, 4f];

    private const int DefaultZoomIndex = 2;
    private const float KeyPanSpeed = 1100f;   // screen pixels per second
    private const float EdgePanSpeed = 900f;   // screen pixels per second
    private const float EdgeMargin = 10f;      // screen pixels
    private const float ZoomSharpness = 14f;   // 1/s, exponential approach
    private const float FollowSharpness = 10f; // 1/s

    private float _zoom = ZoomLevels[DefaultZoomIndex];
    private int _zoomIndex = DefaultZoomIndex;
    private Vector2? _zoomAnchorScreen;
    private Vector2 _zoomAnchorWorld;
    private bool _following;
    private int _mapSize;
    private Node2D? _followNode;
    private Func<Vector2>? _followProvider;

    /// <summary>When false the camera ignores the player (benchmarks, cut-scenes).</summary>
    public bool InputEnabled { get; set; } = true;

    /// <summary>When false the mouse at the window edge does not pan.</summary>
    public bool EdgePanEnabled { get; set; } = true;

    /// <summary>Tile <c>F</c> returns to when there is no follow target (the start tile).</summary>
    public Vector2I HomeTile { get; set; }

    /// <summary>Node the camera follows after <c>F</c> or <see cref="FollowTarget"/> is set; null for none.</summary>
    public Node2D? FollowTarget
    {
        get => _followNode;
        set
        {
            _followNode = value;
            _following = value is not null || FollowProvider is not null;
        }
    }

    /// <summary>Alternative to <see cref="FollowTarget"/>: a function returning the world point to follow.</summary>
    public Func<Vector2>? FollowProvider
    {
        get => _followProvider;
        set
        {
            _followProvider = value;
            _following = value is not null || _followNode is not null;
        }
    }

    /// <summary>True while the camera tracks the follow target (manual panning stops it, <c>F</c> resumes).</summary>
    public bool IsFollowing => _following && (FollowTarget is not null || FollowProvider is not null);

    /// <summary>Index into <see cref="ZoomLevels"/> the camera is heading to.</summary>
    public int ZoomIndex => _zoomIndex;

    /// <summary>Side of the bound map in tiles (0 while unbound: no clamping).</summary>
    public int MapSize => _mapSize;

    /// <summary>World rectangle visible on screen.</summary>
    public Rect2 VisibleWorldRect
    {
        get
        {
            Vector2 size = GetViewportRect().Size / _zoom;
            return new Rect2(Position - (size / 2), size);
        }
    }

    /// <summary>Bounding rectangle, in tiles, of the ground tiles under the screen, clipped to the map (empty when unbound).</summary>
    public Rect2I VisibleTileRect
    {
        get
        {
            if (_mapSize <= 0)
            {
                return default;
            }

            Rect2 view = VisibleWorldRect;
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;
            foreach (Vector2 c in new[] { view.Position, new Vector2(view.End.X, view.Position.Y), view.End, new Vector2(view.Position.X, view.End.Y) })
            {
                (int x, int y) = IsoProjection.TileAtPixel((int)Math.Floor(c.X), (int)Math.Floor(c.Y));
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            minX = Math.Clamp(minX, 0, _mapSize - 1);
            minY = Math.Clamp(minY, 0, _mapSize - 1);
            maxX = Math.Clamp(maxX, 0, _mapSize - 1);
            maxY = Math.Clamp(maxY, 0, _mapSize - 1);
            return new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
    }

    /// <summary>Sets the map the camera is clamped to and its home tile.</summary>
    public void SetMap(int mapSize, Vector2I homeTile)
    {
        _mapSize = mapSize;
        HomeTile = homeTile;
        Position = ClampToMap(Position);
    }

    /// <summary>Centres the camera on tile (<paramref name="x"/>, <paramref name="y"/>) immediately and stops following.</summary>
    public void CenterOnTile(int x, int y)
    {
        (int px, int py) = IsoProjection.TileCenter(x, y);
        CenterOnWorld(new Vector2(px, py));
    }

    /// <summary>Carries out a console camera request (<c>goto x y</c>); does nothing for null.</summary>
    /// <returns>True when the camera moved.</returns>
    public bool Apply(CameraRequest? request)
    {
        if (request is null)
        {
            return false;
        }

        CenterOnTile(request.TileX, request.TileY);
        return true;
    }

    /// <summary>Centres the camera on a world point immediately and stops following.</summary>
    public void CenterOnWorld(Vector2 world)
    {
        _following = false;
        _zoomAnchorScreen = null;
        Position = ClampToMap(world);
        ResetSmoothing();
    }

    /// <summary>Jumps to a zoom step (index into <see cref="ZoomLevels"/>) without smoothing.</summary>
    public void SetZoomIndex(int index, bool smooth = false)
    {
        _zoomIndex = Math.Clamp(index, 0, ZoomLevels.Length - 1);
        _zoomAnchorScreen = null;
        if (!smooth)
        {
            _zoom = ZoomLevels[_zoomIndex];
            Zoom = new Vector2(_zoom, _zoom);
        }
    }

    /// <summary>Steps the zoom in (+1) or out (−1), keeping the world point under <paramref name="screenAnchor"/> in place.</summary>
    public void StepZoom(int direction, Vector2? screenAnchor = null)
    {
        int next = Math.Clamp(_zoomIndex + direction, 0, ZoomLevels.Length - 1);
        if (next == _zoomIndex)
        {
            return;
        }

        _zoomIndex = next;
        Vector2 anchor = screenAnchor ?? (GetViewportRect().Size / 2);
        _zoomAnchorScreen = anchor;
        _zoomAnchorWorld = ScreenToWorld(anchor);
    }

    /// <summary>Returns to the follow target if there is one, otherwise centres on <see cref="HomeTile"/>.</summary>
    public void Recenter()
    {
        if (FollowTarget is not null || FollowProvider is not null)
        {
            _following = true;
            Position = ClampToMap(FollowPoint());
            return;
        }

        CenterOnTile(HomeTile.X, HomeTile.Y);
    }

    /// <summary>World point under a viewport (screen) position.</summary>
    public Vector2 ScreenToWorld(Vector2 screen) => Position + ((screen - (GetViewportRect().Size / 2)) / _zoom);

    public override void _Ready()
    {
        Zoom = new Vector2(_zoom, _zoom);
        PositionSmoothingEnabled = false;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        Vector2 pan = InputEnabled ? ReadPan() : Vector2.Zero;
        if (pan != Vector2.Zero)
        {
            _following = false;
            Position += pan * dt / _zoom;
        }
        else if (IsFollowing)
        {
            Vector2 target = FollowPoint();
            Position = Position.Lerp(target, 1f - MathF.Exp(-FollowSharpness * dt));
        }

        float targetZoom = ZoomLevels[_zoomIndex];
        if (!Mathf.IsEqualApprox(_zoom, targetZoom))
        {
            _zoom = Mathf.Lerp(_zoom, targetZoom, 1f - MathF.Exp(-ZoomSharpness * dt));
            if (Mathf.Abs(_zoom - targetZoom) < 0.001f)
            {
                _zoom = targetZoom;
            }

            Zoom = new Vector2(_zoom, _zoom);
            if (_zoomAnchorScreen is Vector2 anchor && !IsFollowing)
            {
                // Keep the anchored world point under the same screen point while the zoom moves.
                Position = _zoomAnchorWorld - ((anchor - (GetViewportRect().Size / 2)) / _zoom);
            }
        }
        else
        {
            _zoomAnchorScreen = null;
        }

        Position = ClampToMap(Position);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!InputEnabled)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wheelUp:
                StepZoom(+1, wheelUp.Position);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wheelDown:
                StepZoom(-1, wheelDown.Position);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Middle) != 0:
                _following = false;
                _zoomAnchorScreen = null;
                Position = ClampToMap(Position - (motion.Relative / _zoom));
                GetViewport().SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.PhysicalKeycode)
        {
            case Key.F:
                Recenter();
                break;
            case Key.Equal or Key.KpAdd:
                StepZoom(+1);
                break;
            case Key.Minus or Key.KpSubtract:
                StepZoom(-1);
                break;
            default:
                return;
        }

        GetViewport().SetInputAsHandled();
    }

    // Screen-space pan velocity from keys and the window edge.
    private Vector2 ReadPan()
    {
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)
        {
            return Vector2.Zero;
        }

        Vector2 dir = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left))
        {
            dir.X -= 1;
        }

        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right))
        {
            dir.X += 1;
        }

        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up))
        {
            dir.Y -= 1;
        }

        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down))
        {
            dir.Y += 1;
        }

        if (dir != Vector2.Zero)
        {
            return dir.Normalized() * KeyPanSpeed;
        }

        if (!EdgePanEnabled || !DisplayServer.WindowIsFocused())
        {
            return Vector2.Zero;
        }

        Vector2 size = GetViewportRect().Size;
        Vector2 mouse = GetViewport().GetMousePosition();
        if (mouse.X < 0 || mouse.Y < 0 || mouse.X > size.X || mouse.Y > size.Y)
        {
            return Vector2.Zero;
        }

        Vector2 edge = Vector2.Zero;
        edge.X = mouse.X <= EdgeMargin ? -1 : mouse.X >= size.X - EdgeMargin ? 1 : 0;
        edge.Y = mouse.Y <= EdgeMargin ? -1 : mouse.Y >= size.Y - EdgeMargin ? 1 : 0;
        return edge == Vector2.Zero ? Vector2.Zero : edge.Normalized() * EdgePanSpeed;
    }

    private Vector2 FollowPoint() => FollowProvider?.Invoke() ?? FollowTarget?.GlobalPosition ?? Position;

    // Keeps the centre inside the map diamond: convert to continuous tile coordinates, clamp to [0, size], convert back.
    private Vector2 ClampToMap(Vector2 world)
    {
        if (_mapSize <= 0)
        {
            return world;
        }

        float a = (world.X - IsoProjection.HalfWidth) / IsoProjection.TileWidth;
        float b = world.Y / IsoProjection.TileHeight;
        float tx = Math.Clamp(b + a, 0, _mapSize);
        float ty = Math.Clamp(b - a, 0, _mapSize);
        return new Vector2((IsoProjection.HalfWidth * (tx - ty)) + IsoProjection.HalfWidth, IsoProjection.HalfHeight * (tx + ty));
    }
}
