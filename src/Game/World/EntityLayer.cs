using Core.Entities;
using Core.Geometry;
using Core.Heroes;
using Core.Sim;
using Core.WorldGen;
using Godot;

namespace Game.World;

/// <summary>
/// Creatures standing on the map (M2: the hero), drawn under <see cref="WorldView.ObjectsRoot"/> so they are y-sorted with the
/// chunk object layers. Reads Core state only: the hero's <see cref="Position"/> is sampled once per Core tick and shown
/// interpolated over the tick duration, lifted by the height of the tile it stands on. Also draws a marker on the hero's
/// destination tile.
/// </summary>
public partial class EntityLayer : Node2D
{
    /// <summary>Art id of the hero sprite in the objects atlas.</summary>
    public const string HeroArtId = "hero";

    private Simulation? _sim;
    private WorldArt? _art;
    private Node2D? _heroRoot;
    private Sprite2D? _sprite;
    private TargetMarker? _marker;
    private long _sampledTick = -1;
    private Vector2 _prevGround;
    private Vector2 _curGround;
    private float _prevLift;
    private float _curLift;
    private double _sinceTick;

    /// <summary>Seconds one Core tick lasts at the current game speed (the host knows the speed); null means 1/10 s.</summary>
    public Func<double>? TickSeconds { get; set; }

    /// <summary>The hero sprite, or null when nothing is bound or the art lacks it.</summary>
    public Sprite2D? HeroSprite => _sprite;

    /// <summary>The y-sorted node of the hero (its position is the ground point under the feet), or null.</summary>
    public Node2D? HeroNode => _heroRoot;

    /// <summary>True while a hero is shown.</summary>
    public bool HasHero => _heroRoot is { Visible: true };

    /// <summary>World point of the hero's feet on the ground plane (without the height lift), interpolated.</summary>
    public Vector2 HeroGroundPoint => _heroRoot?.Position ?? Vector2.Zero;

    /// <summary>World point the camera should follow: the feet lifted by the height of the tile.</summary>
    public Vector2 HeroFollowPoint => _heroRoot is null ? Vector2.Zero : _heroRoot.Position + (_sprite?.Position ?? Vector2.Zero);

    public override void _Ready() => YSortEnabled = true;

    /// <summary>Starts showing the hero of <paramref name="simulation"/>; replaces any earlier binding.</summary>
    public void Bind(Simulation simulation, WorldArt art)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(art);
        Unbind();
        _sim = simulation;
        _art = art;
        if (!EntityMotion.TryMakeObjectTexture(art, HeroArtId, out AtlasTexture texture, out Vector2 offset))
        {
            return;
        }

        _heroRoot = new Node2D { Name = "Hero", Visible = false };
        _sprite = new Sprite2D
        {
            Name = "HeroSprite",
            Texture = texture,
            Centered = false,
            Offset = offset,
        };
        _heroRoot.AddChild(_sprite);
        _marker = new TargetMarker { Name = "TargetMarker", Visible = false };
        AddChild(_marker);
        AddChild(_heroRoot);
        Sync();
    }

    /// <summary>Removes the sprite and forgets the simulation.</summary>
    public void Unbind()
    {
        foreach (Node? node in new Node?[] { _heroRoot, _marker })
        {
            if (node is not null)
            {
                RemoveChild(node);
                node.QueueFree();
            }
        }

        _heroRoot = null;
        _sprite = null;
        _marker = null;
        _sim = null;
        _art = null;
        _sampledTick = -1;
    }

    /// <summary>Samples the hero now and shows it exactly there, without interpolation (after binding, tests).</summary>
    public void Sync()
    {
        if (!Sample())
        {
            return;
        }

        _prevGround = _curGround;
        _prevLift = _curLift;
        _sinceTick = double.MaxValue;
        Apply(1f);
    }

    public override void _Process(double delta)
    {
        if (_sim is null || _heroRoot is null)
        {
            return;
        }

        if (_sim.Tick != _sampledTick)
        {
            Vector2 previous = _curGround;
            float previousLift = _curLift;
            bool had = _heroRoot.Visible;
            if (Sample())
            {
                bool snap = !had || previous.DistanceTo(_curGround) > EntityMotion.SnapDistancePixels;
                _prevGround = snap ? _curGround : previous;
                _prevLift = snap ? _curLift : previousLift;
                _sinceTick = 0;
            }
        }
        else
        {
            _sinceTick += delta;
        }

        double tickSeconds = Math.Max(TickSeconds?.Invoke() ?? EntityMotion.FallbackTickSeconds, 0.001);
        Apply((float)Math.Clamp(_sinceTick / tickSeconds, 0.0, 1.0));
    }

    // Reads the hero's position and height at the current tick into the "current" values; false without a hero.
    private bool Sample()
    {
        GameWorld? world = _sim?.World;
        if (_sim is null || _art is null || world is null || _heroRoot is null)
        {
            return false;
        }

        _sampledTick = _sim.Tick;
        EntityId hero = world.HeroEntity;
        if (hero == EntityId.None || !_sim.Positions.TryGet(hero, out Core.Entities.Position position))
        {
            _heroRoot.Visible = false;
            return false;
        }

        _curGround = EntityMotion.GroundPoint(position);
        _curLift = EntityMotion.Lift(world, _art, position.TileX, position.TileY);
        UpdateMarker(world, hero);
        return true;
    }

    private int HeightAt(GameWorld world, int x, int y) => EntityMotion.HeightAt(world, _art!, x, y);

    private void Apply(float alpha)
    {
        if (_heroRoot is null || _sprite is null)
        {
            return;
        }

        _heroRoot.Position = _prevGround.Lerp(_curGround, alpha);
        _sprite.Position = new Vector2(0, -Mathf.Lerp(_prevLift, _curLift, alpha));
        _heroRoot.Visible = true;
    }

    private void UpdateMarker(GameWorld world, EntityId hero)
    {
        if (_marker is null || !world.Heroes.TryGet(hero, out Hero state))
        {
            return;
        }

        int tx = -1;
        int ty = -1;
        if (state.Activity == HeroActivity.Gathering)
        {
            tx = state.TargetX;
            ty = state.TargetY;
        }
        else if (state.Activity == HeroActivity.Walking && world.Movers.TryGet(hero, out Mover mover) && mover.Goal >= 0)
        {
            tx = mover.Goal % world.Size;
            ty = mover.Goal / world.Size;
        }

        if (tx < 0 || !world.Map.Terrain.Contains(tx, ty))
        {
            _marker.Visible = false;
            return;
        }

        _marker.Mark(tx, ty, HeightAt(world, tx, ty), state.Activity == HeroActivity.Gathering);
    }

    /// <summary>Diamond outline on the destination tile.</summary>
    private sealed partial class TargetMarker : Node2D
    {
        private int _x = -1;
        private int _y = -1;
        private int _height;
        private bool _gather;

        public void Mark(int x, int y, int height, bool gather)
        {
            if (_x != x || _y != y || _height != height || _gather != gather || !Visible)
            {
                _x = x;
                _y = y;
                _height = height;
                _gather = gather;
                Visible = true;
                QueueRedraw();
            }
        }

        public override void _Draw()
        {
            (int cx, int cy) = IsoProjection.TileCenter(_x, _y, _height);
            var c = new Vector2(cx, cy);
            Vector2[] ring =
            [
                c + new Vector2(0, -IsoProjection.HalfHeight),
                c + new Vector2(IsoProjection.HalfWidth, 0),
                c + new Vector2(0, IsoProjection.HalfHeight),
                c + new Vector2(-IsoProjection.HalfWidth, 0),
                c + new Vector2(0, -IsoProjection.HalfHeight),
            ];
            DrawPolyline(ring, _gather ? new Color(1f, 0.85f, 0.3f, 0.9f) : new Color(1f, 1f, 1f, 0.8f), 1f);
        }
    }
}
