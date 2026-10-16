using Core.Content;
using Core.Entities;
using Core.Fog;
using Core.Geometry;
using Core.Residents;
using Core.Sim;
using Godot;

namespace Game.World;

/// <summary>
/// The residents of the settlement standing on the map (FR-NPC-01), drawn under <see cref="WorldView.ObjectsRoot"/> so they are
/// y-sorted with the chunk objects and the hero. Reads Core state only: every resident's <see cref="Position"/> is sampled once
/// per Core tick and shown interpolated over the tick duration, lifted by the height of its tile (the same maths as
/// <see cref="EntityLayer"/>, see <see cref="EntityMotion"/>). The sprite is chosen by the profession
/// (<c>resident_{profession}</c> in the objects atlas; specialists share <c>resident_specialist</c>, everything else
/// <c>resident_unemployed</c>). Residents on tiles of unknown fog are not shown, like objects there.
/// </summary>
/// <remarks>
/// Sprites are pooled: a slot is created the first time a resident is seen, hidden and kept for the next newcomer when the
/// resident leaves, and nothing is allocated per frame. The layer follows the one simulation it was bound to; the owner calls
/// <see cref="Unbind"/> and <see cref="Bind"/> when the simulation is replaced.
/// </remarks>
public partial class ResidentLayer : Node2D
{
    /// <summary>Prefix of the art id of a profession sprite.</summary>
    public const string ArtPrefix = "resident_";

    /// <summary>Art id shared by all specialist professions without a sprite of their own.</summary>
    public const string SpecialistArtId = "resident_specialist";

    /// <summary>Art id of residents without a profession (or one without a sprite).</summary>
    public const string UnemployedArtId = "resident_unemployed";

    private const int RingPoints = 16;
    private const float RingRadiusX = IsoProjection.HalfWidth * 0.55f;
    private const float RingRadiusY = IsoProjection.HalfHeight * 0.55f;

    private static readonly Vector2[] _ring = BuildRing();
    private static readonly Color _ringColor = new(1f, 0.9f, 0.35f, 0.95f);

    private readonly Dictionary<EntityId, Slot> _slots = [];
    private readonly Stack<Slot> _free = new();
    private readonly List<EntityId> _gone = [];
    private readonly Dictionary<int, Frame> _frames = [];
    private Frame? _unemployed;
    private Simulation? _sim;
    private WorldArt? _art;
    private long _sampledTick = -1;
    private long _stamp;
    private double _sinceTick;
    private EntityId _selected;

    /// <summary>Seconds one Core tick lasts at the current game speed (the host knows the speed); null means 1/10 s.</summary>
    public Func<double>? TickSeconds { get; set; }

    /// <summary>Number of residents shown.</summary>
    public int Count => _slots.Count;

    /// <summary>Number of pooled sprites, shown or waiting for a newcomer.</summary>
    public int PoolSize => _slots.Count + _free.Count;

    /// <summary>True while the layer follows a simulation.</summary>
    public bool IsBound => _sim is not null;

    /// <summary>The resident that gets a ring under its feet, or <see cref="EntityId.None"/>.</summary>
    public EntityId Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            foreach (Slot slot in _slots.Values)
            {
                slot.Ring.Visible = slot.Entity == _selected;
            }
        }
    }

    public override void _Ready() => YSortEnabled = true;

    /// <summary>Starts showing the residents of <paramref name="simulation"/>; replaces any earlier binding.</summary>
    public void Bind(Simulation simulation, WorldArt art)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(art);
        Unbind();
        if (!BuildFrames(simulation.Content, art))
        {
            return;
        }

        _sim = simulation;
        _art = art;
        Sync();
    }

    /// <summary>Removes every sprite and forgets the simulation.</summary>
    public void Unbind()
    {
        foreach (Slot slot in _slots.Values)
        {
            Discard(slot);
        }

        while (_free.Count > 0)
        {
            Discard(_free.Pop());
        }

        _slots.Clear();
        _frames.Clear();
        _unemployed = null;
        _sim = null;
        _art = null;
        _sampledTick = -1;
    }

    /// <summary>Samples the residents now and shows them exactly there, without interpolation (after binding, tests).</summary>
    public void Sync()
    {
        if (_sim is null)
        {
            return;
        }

        Sample();
        foreach (Slot slot in _slots.Values)
        {
            slot.Prev = slot.Cur;
            slot.PrevLift = slot.CurLift;
            Apply(slot, 1f);
        }

        _sinceTick = double.MaxValue;
    }

    public override void _Process(double delta)
    {
        if (_sim is null)
        {
            return;
        }

        if (_sim.Tick != _sampledTick)
        {
            Sample();
            _sinceTick = 0;
        }
        else
        {
            _sinceTick += delta;
        }

        double tickSeconds = Math.Max(TickSeconds?.Invoke() ?? EntityMotion.FallbackTickSeconds, 0.001);
        float alpha = (float)Math.Clamp(_sinceTick / tickSeconds, 0.0, 1.0);
        foreach (Slot slot in _slots.Values)
        {
            Apply(slot, alpha);
        }
    }

    /// <summary>The sprite of a shown resident, or null.</summary>
    public Sprite2D? SpriteOf(EntityId resident) => _slots.TryGetValue(resident, out Slot? slot) ? slot.Sprite : null;

    /// <summary>The y-sorted node of a shown resident (its position is the ground point under the feet), or null.</summary>
    public Node2D? NodeOf(EntityId resident) => _slots.TryGetValue(resident, out Slot? slot) ? slot.Root : null;

    /// <summary>The art id of the sprite a shown resident has, or null when it is not shown.</summary>
    public string? ArtIdOf(EntityId resident) => _slots.TryGetValue(resident, out Slot? slot) ? slot.Frame.ArtId : null;

    /// <summary>The visible selection ring under a shown resident, or null.</summary>
    public Node2D? RingOf(EntityId resident) => _slots.TryGetValue(resident, out Slot? slot) ? slot.Ring : null;

    /// <summary>
    /// The shown resident whose sprite covers the world point (the one standing in front when several overlap). False when
    /// there is none. <paramref name="world"/> is in the coordinates of <see cref="WorldView.ObjectsRoot"/>.
    /// </summary>
    public bool TryPick(Vector2 world, out EntityId resident)
    {
        resident = EntityId.None;
        float bestY = float.MinValue;
        foreach (Slot slot in _slots.Values)
        {
            Vector2 size = slot.Frame.Texture.Region.Size;
            Vector2 topLeft = slot.Root.Position + slot.Sprite.Position + slot.Sprite.Offset;
            if (new Rect2(topLeft, size).HasPoint(world) && slot.Root.Position.Y > bestY)
            {
                bestY = slot.Root.Position.Y;
                resident = slot.Entity;
            }
        }

        return resident != EntityId.None;
    }

    // One sprite frame per art id; professions map onto them. False when the unemployed sprite (the fallback) cannot be built.
    private bool BuildFrames(ContentCatalog content, WorldArt art)
    {
        var byArt = new Dictionary<string, Frame>();
        if (!TryFrame(art, UnemployedArtId, byArt, out Frame? fallback))
        {
            return false;
        }

        _unemployed = fallback;
        foreach (ProfessionDef profession in content.Professions)
        {
            string artId = ArtPrefix + profession.Id;
            if (!art.Table.TryGet(artId, out _))
            {
                artId = profession.Kind == "specialist" ? SpecialistArtId : UnemployedArtId;
            }

            if (content.TryGetId(profession.Id, out DefId id) && TryFrame(art, artId, byArt, out Frame? frame))
            {
                _frames[id.Value] = frame;
            }
        }

        return true;
    }

    private static bool TryFrame(WorldArt art, string artId, Dictionary<string, Frame> cache, out Frame frame)
    {
        if (!cache.TryGetValue(artId, out frame!))
        {
            if (!EntityMotion.TryMakeObjectTexture(art, artId, out AtlasTexture texture, out Vector2 offset))
            {
                return false;
            }

            frame = new Frame(artId, texture, offset);
            cache[artId] = frame;
        }

        return true;
    }

    // Reads every resident's position at the current tick into the "current" values; adds, updates and recycles slots.
    private void Sample()
    {
        if (_sim?.World is not { } world || _art is null)
        {
            return;
        }

        _sampledTick = _sim.Tick;
        _stamp++;
        ReadOnlySpan<EntityId> entities = world.Residents.Entities;
        Span<Resident> residents = world.Residents.Values;
        for (int i = 0; i < entities.Length; i++)
        {
            if (!_sim.Positions.TryGet(entities[i], out Core.Entities.Position position)
                || world.Fog.GetState(position.TileX, position.TileY) == FogState.Unknown)
            {
                continue;
            }

            if (!_slots.TryGetValue(entities[i], out Slot? slot))
            {
                slot = Acquire(entities[i]);
                slot.Fresh = true;
            }

            slot.Stamp = _stamp;
            Vector2 ground = EntityMotion.GroundPoint(position);
            float lift = EntityMotion.Lift(world, _art, position.TileX, position.TileY);
            bool snap = slot.Fresh || slot.Cur.DistanceTo(ground) > EntityMotion.SnapDistancePixels;
            slot.Prev = snap ? ground : slot.Cur;
            slot.PrevLift = snap ? lift : slot.CurLift;
            slot.Cur = ground;
            slot.CurLift = lift;
            slot.Fresh = false;
            SetFrame(slot, _frames.GetValueOrDefault(residents[i].Profession.Value) ?? _unemployed!);
        }

        Recycle();
    }

    // Hides and pools the slots of residents that were not seen in the last sample (left, gone into the fog).
    private void Recycle()
    {
        _gone.Clear();
        foreach (KeyValuePair<EntityId, Slot> pair in _slots)
        {
            if (pair.Value.Stamp != _stamp)
            {
                _gone.Add(pair.Key);
            }
        }

        foreach (EntityId entity in _gone)
        {
            Slot slot = _slots[entity];
            _slots.Remove(entity);
            slot.Root.Visible = false;
            slot.Entity = EntityId.None;
            _free.Push(slot);
        }
    }

    private Slot Acquire(EntityId entity)
    {
        if (!_free.TryPop(out Slot? slot))
        {
            slot = new Slot();
            slot.Root = new Node2D { Name = "Resident", Visible = false };
            slot.Ring = new Ring { Name = "Ring", Visible = false };
            slot.Sprite = new Sprite2D { Name = "Sprite", Centered = false };
            slot.Root.AddChild(slot.Ring);
            slot.Root.AddChild(slot.Sprite);
            AddChild(slot.Root);
        }

        slot.Entity = entity;
        slot.Ring.Visible = entity == _selected;
        _slots[entity] = slot;
        return slot;
    }

    private static void SetFrame(Slot slot, Frame frame)
    {
        if (!ReferenceEquals(slot.Frame, frame))
        {
            slot.Frame = frame;
            slot.Sprite.Texture = frame.Texture;
            slot.Sprite.Offset = frame.Offset;
        }
    }

    private static void Apply(Slot slot, float alpha)
    {
        float lift = Mathf.Lerp(slot.PrevLift, slot.CurLift, alpha);
        slot.Root.Position = slot.Prev.Lerp(slot.Cur, alpha);
        slot.Sprite.Position = new Vector2(0, -lift);
        slot.Ring.Position = slot.Sprite.Position;
        slot.Root.Visible = true;
    }

    private void Discard(Slot slot)
    {
        RemoveChild(slot.Root);
        slot.Root.QueueFree();
    }

    private static Vector2[] BuildRing()
    {
        var ring = new Vector2[RingPoints + 1];
        for (int i = 0; i <= RingPoints; i++)
        {
            float angle = Mathf.Tau * i / RingPoints;
            ring[i] = new Vector2(Mathf.Cos(angle) * RingRadiusX, Mathf.Sin(angle) * RingRadiusY);
        }

        return ring;
    }

    private sealed record Frame(string ArtId, AtlasTexture Texture, Vector2 Offset);

    private sealed class Slot
    {
        public Node2D Root = null!;
        public Sprite2D Sprite = null!;
        public Ring Ring = null!;
        public Frame Frame = null!;
        public EntityId Entity;
        public Vector2 Prev;
        public Vector2 Cur;
        public float PrevLift;
        public float CurLift;
        public long Stamp;
        public bool Fresh;
    }

    /// <summary>Ellipse on the ground under the selected resident, drawn before (under) its sprite.</summary>
    private sealed partial class Ring : Node2D
    {
        public override void _Draw() => DrawPolyline(_ring, _ringColor, 2f);
    }
}
