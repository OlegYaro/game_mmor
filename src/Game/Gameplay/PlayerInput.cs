using Core.Entities;
using Core.Heroes;
using Core.Pathfinding;
using Core.Sim;
using Core.WorldGen;
using Game.Sim;
using Game.World;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Gameplay;

/// <summary>
/// Turns the player's keyboard and mouse into hero commands (FR-HERO-01, FR-HERO-02): WASD/arrows steer the hero along
/// the screen directions of the isometric view (physical keys, so the Russian layout works), Shift sprints, a left click
/// on the ground walks there and a click on a tree, stone or berry bush gathers it. It sends commands through
/// <see cref="SimulationHost.Send"/> only and shows why an order was refused or why the hero stopped as a toast.
/// </summary>
public partial class PlayerInput : Node
{
    /// <summary>Tiles ahead the keyboard steering aims at (the farthest walkable one up to this is used).</summary>
    public const int SteerLookahead = 3;

    /// <summary>Seconds between repeated steering orders while a key is held.</summary>
    public const double SteerInterval = 0.1;

    private SimulationHost? _host;
    private WorldView? _view;
    private GameInputState _state = new();
    private bool _steering;
    private Vector2I _lastDirection;
    private bool _lastSprint;
    private Vector2I _lastTarget = new(-1, -1);
    private double _sinceOrder;
    private long _clickTick = -1;
    private long _seenTick = -1;
    private HeroStopReason _seenStop = HeroStopReason.None;

    /// <summary>Connects the input to the game.</summary>
    public void Bind(SimulationHost host, WorldView view, GameInputState state)
    {
        _host = host;
        _view = view;
        _state = state;
    }

    /// <summary>
    /// Tile direction for the pressed screen directions: up is (−1,−1), right (1,−1), down (1,1), left (−1,1) in tile
    /// coordinates (the isometric projection turns the tile axes 45° on screen). Opposite keys cancel; each axis is clamped to −1..1.
    /// </summary>
    public static Vector2I TileDirection(bool up, bool down, bool left, bool right)
    {
        int dx = 0;
        int dy = 0;
        if (up)
        {
            dx--;
            dy--;
        }

        if (down)
        {
            dx++;
            dy++;
        }

        if (left)
        {
            dx--;
            dy++;
        }

        if (right)
        {
            dx++;
            dy--;
        }

        return new Vector2I(Math.Clamp(dx, -1, 1), Math.Clamp(dy, -1, 1));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click || !CanAct(out CoreSimulation? sim, out WorldView? view))
        {
            return;
        }

        if (!WorldPicker.TryPickTile(view, click.Position, out int x, out int y))
        {
            return;
        }

        // A tree, rock or bush sprite rises above its tile, so a click on its crown lands on a tile further back:
        // prefer a resource in front whose sprite covers the clicked point.
        (x, y) = ResourceUnderPoint(view, sim, click.Position, x, y);
        ResourceKind resource = sim.World!.Tiles.GetEffectiveResource(x, y);
        bool sprint = Input.IsPhysicalKeyPressed(Key.Shift);
        if (resource is ResourceKind.Tree or ResourceKind.Stone or ResourceKind.BerryBush)
        {
            _host!.Send(new GatherCommand(x, y));
        }
        else
        {
            _host!.Send(new MoveHeroCommand(x, y, sprint));
        }

        _steering = false;
        _clickTick = sim.Tick;
        GetViewport().SetInputAsHandled();
    }

    // Object sprites are 32x32 with the origin at (16, 24) above the tile centre (tools/ArtGen/ObjectArt).
    private const int SpriteHalfWidth = 12;
    private const int SpriteAbove = 24;
    private const int SpriteBelow = 4;

    private static (int X, int Y) ResourceUnderPoint(WorldView view, CoreSimulation sim, Vector2 screen, int pickedX, int pickedY)
    {
        WorldArt? art = view.Chunks.Art;
        if (art is null || sim.World is not { } world)
        {
            return (pickedX, pickedY);
        }

        Vector2 point = view.Camera.ScreenToWorld(screen);
        // Tiles nearer to the camera than the picked one, nearest first; their sprites reach over the picked tile.
        ReadOnlySpan<(int Dx, int Dy)> candidates = [(1, 1), (1, 0), (0, 1)];
        foreach ((int dx, int dy) in candidates)
        {
            int cx = pickedX + dx;
            int cy = pickedY + dy;
            if (!world.Map.Terrain.Contains(cx, cy) || world.Tiles.GetEffectiveResource(cx, cy) == ResourceKind.None)
            {
                continue;
            }

            Core.WorldGen.TerrainMap terrain = world.Map.Terrain;
            int height = art.VisualHeight(terrain.GetBiome(cx, cy), terrain.GetFeatures(cx, cy), terrain.GetHeight(cx, cy));
            (int px, int py) = Core.Geometry.IsoProjection.TileCenter(cx, cy, height);
            if (Math.Abs(point.X - px) <= SpriteHalfWidth && point.Y >= py - SpriteAbove && point.Y <= py + SpriteBelow)
            {
                return (cx, cy);
            }
        }

        return (pickedX, pickedY);
    }

    public override void _Process(double delta)
    {
        if (_host is not { IsRunning: true })
        {
            return;
        }

        CoreSimulation sim = _host.Simulation;
        ReportHero(sim);
        if (!CanAct(out _, out _) || GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)
        {
            ReleaseSteering();
            return;
        }

        Vector2I direction = TileDirection(
            Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up),
            Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down),
            Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left),
            Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right));
        if (direction == Vector2I.Zero)
        {
            ReleaseSteering();
            return;
        }

        bool sprint = Input.IsPhysicalKeyPressed(Key.Shift);
        _sinceOrder += delta;
        bool changed = direction != _lastDirection || sprint != _lastSprint || !_steering;
        if (!changed && _sinceOrder < SteerInterval)
        {
            return;
        }

        _lastDirection = direction;
        _lastSprint = sprint;
        _sinceOrder = 0;
        if (!TryFindSteerTarget(sim, direction, out Vector2I target))
        {
            return;
        }

        bool idle = sim.World!.Heroes.TryGet(sim.World.HeroEntity, out Hero hero) && hero.Activity == HeroActivity.Idle;
        if (changed || target != _lastTarget || idle)
        {
            _host.Send(new MoveHeroCommand(target.X, target.Y, sprint));
            _lastTarget = target;
        }

        _steering = true;
    }

    // The player's orders reach the world only while a game runs, no menu or modal panel is open and build mode is off.
    private bool CanAct(out CoreSimulation sim, out WorldView view)
    {
        sim = null!;
        view = null!;
        if (_host is not { IsRunning: true, InputBlocked: false } || _view is null || _state.BuildModeActive || _state.WorldInputBlocked)
        {
            return false;
        }

        sim = _host.Simulation;
        view = _view;
        return sim.World is not null && view.IsBound && sim.World.HeroEntity != EntityId.None;
    }

    private void ReleaseSteering()
    {
        if (_steering && _host is { IsRunning: true })
        {
            _host.Send(new StopHeroCommand());
        }

        _steering = false;
        _lastDirection = Vector2I.Zero;
        _lastTarget = new Vector2I(-1, -1);
    }

    // Farthest walkable explored tile in the steering direction (up to the lookahead); a diagonal that is blocked
    // slides along one axis.
    private static bool TryFindSteerTarget(CoreSimulation sim, Vector2I direction, out Vector2I target)
    {
        target = default;
        GameWorld world = sim.World!;
        if (!sim.Positions.TryGet(world.HeroEntity, out Position position))
        {
            return false;
        }

        PassabilityGrid grid = world.Pathfinder.Grid;
        Span<Vector2I> candidates = stackalloc Vector2I[3];
        int count = 0;
        candidates[count++] = direction;
        if (direction.X != 0 && direction.Y != 0)
        {
            candidates[count++] = new Vector2I(direction.X, 0);
            candidates[count++] = new Vector2I(0, direction.Y);
        }

        for (int c = 0; c < count; c++)
        {
            for (int k = SteerLookahead; k >= 1; k--)
            {
                int x = position.TileX + (candidates[c].X * k);
                int y = position.TileY + (candidates[c].Y * k);
                if (grid.IsPassable(x, y) && world.Fog.GetState(x, y) != Core.Fog.FogState.Unknown)
                {
                    target = new Vector2I(x, y);
                    return true;
                }
            }
        }

        return false;
    }

    // Toasts for refused clicks and for stops the player should know about; looks at the hero once per tick.
    private void ReportHero(CoreSimulation sim)
    {
        if (sim.Tick == _seenTick || sim.World is not { } world || !world.Heroes.TryGet(world.HeroEntity, out Hero hero))
        {
            return;
        }

        if (_seenTick < 0 || sim.Tick < _seenTick)
        {
            _seenStop = hero.LastStop;
            _clickTick = -1;
        }

        _seenTick = sim.Tick;
        if (_clickTick >= 0 && sim.Tick > _clickTick)
        {
            _clickTick = -1;
            string? key = hero.LastOrder switch
            {
                HeroOrderResult.OutsideMap => "hero.order.outside_map",
                HeroOrderResult.Unexplored => "hero.order.unexplored",
                HeroOrderResult.Impassable => "hero.order.impassable",
                HeroOrderResult.Unreachable => "hero.order.unreachable",
                HeroOrderResult.NoResource => "hero.order.no_resource",
                _ => null,
            };
            if (key is not null)
            {
                _host!.ShowToast(key);
            }
        }

        if (hero.LastStop != _seenStop)
        {
            _seenStop = hero.LastStop;
            string? key = hero.LastStop switch
            {
                HeroStopReason.PathBlocked => "hero.stop.path_blocked",
                HeroStopReason.BackpackFull => "hero.stop.backpack_full",
                HeroStopReason.NoResource => "hero.stop.no_resource",
                _ => null,
            };
            if (key is not null)
            {
                _host!.ShowToast(key);
            }
        }
    }
}
