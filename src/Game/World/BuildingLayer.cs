using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Sim;
using Godot;

namespace Game.World;

/// <summary>
/// Draws the buildings of the bound simulation (ADR 0010, ADR 0012): one <see cref="Sprite2D"/> per building, a direct child
/// of <see cref="WorldView.ObjectsRoot"/> so the y-sort interleaves it with trees and creatures. The frame comes from the
/// buildings atlas (<c>{building}_l{level}_s{stage}</c>); sprites are created once and only touched when a building's frame
/// or place changes. Reads Core state only. A moving building also gets a translucent marking frame at its target.
/// </summary>
/// <remarks>
/// A plain <see cref="Node"/> on purpose: a <see cref="Node2D"/> parent would hide its sprites from the shared y-sort.
/// Sprites are y-sorted by their position, the lowest pixel of the footprint diamond.
/// </remarks>
public partial class BuildingLayer : Node
{
    /// <summary>Atlas id of the buildings image in <c>art/atlas.json</c>.</summary>
    public const string AtlasId = "buildings";

    /// <summary>Construction stage frames: the staked-out footprint, planks, scaffolding, finished.</summary>
    public const int FinishedStage = 3;

    private static readonly Color _ghostTint = new(0.75f, 1f, 0.75f, 0.8f);

    private readonly Dictionary<EntityId, Entry> _entries = [];
    private readonly List<EntityId> _stale = [];
    private WorldView? _view;
    private Simulation? _sim;
    private AtlasTable? _table;
    private Texture2D? _texture;
    private bool _artFailed;
    private int _pass;

    /// <summary>Number of building sprites (moving targets not counted).</summary>
    public int SpriteCount => _entries.Count;

    /// <summary>Starts following <paramref name="view"/>: sprites go under its <see cref="WorldView.ObjectsRoot"/>.</summary>
    public void Attach(WorldView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Clear();
        _view = view;
    }

    /// <summary>Construction stage frame (0..3) for a progress in permille: below 250, 500, 800, then finished.</summary>
    public static int StageOf(int progressPermille) => progressPermille switch
    {
        < 250 => 0,
        < 500 => 1,
        < 800 => 2,
        _ => FinishedStage,
    };

    /// <summary>
    /// The level and stage a building is drawn with. Only a construction shows stages 0..2; an active, upgrading or moving
    /// building looks finished, and an upgrade keeps the previous level's look until it is done.
    /// </summary>
    public static (int Level, int Stage) FrameOf(ContentCatalog content, Building building)
    {
        ArgumentNullException.ThrowIfNull(content);
        int level = building.State == BuildingState.Upgrading ? Math.Max(1, building.Level - 1) : building.Level;
        int stage = building.State == BuildingState.Constructing ? StageOf(Construction.ProgressPermille(content, building)) : FinishedStage;
        return (level, stage);
    }

    /// <summary>The atlas frame id the sprite of <paramref name="building"/> shows, or null when it has none.</summary>
    public string? FrameIdOf(EntityId building) => _entries.TryGetValue(building, out Entry? entry) ? entry.Body.Cell?.Id : null;

    /// <summary>The sprite of <paramref name="building"/>, or null.</summary>
    public Sprite2D? SpriteOf(EntityId building) => _entries.TryGetValue(building, out Entry? entry) ? entry.Body.Sprite : null;

    /// <summary>The translucent marking sprite at the target of a moving building, or null.</summary>
    public Sprite2D? GhostOf(EntityId building) => _entries.TryGetValue(building, out Entry? entry) ? entry.Ghost?.Sprite : null;

    public override void _Process(double delta) => Refresh();

    public override void _ExitTree() => Clear();

    /// <summary>Brings the sprites in line with the simulation. Runs every frame; public for tests.</summary>
    public void Refresh()
    {
        Simulation? sim = _view?.Simulation;
        if (!ReferenceEquals(sim, _sim))
        {
            Clear();
            _sim = sim;
        }

        if (_view is null || sim?.World is not { } world || !EnsureArt(out WorldArt art, out AtlasTable table, out Texture2D texture))
        {
            return;
        }

        _pass++;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        ReadOnlySpan<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            Building building = buildings[i];
            if (!_entries.TryGetValue(owners[i], out Entry? entry))
            {
                entry = new Entry();
                _entries.Add(owners[i], entry);
            }

            entry.Seen = _pass;
            BuildingDef def = Construction.DefOf(sim.Content, building);
            (int level, int stage) = FrameOf(sim.Content, building);
            Show(entry.Body, _view.ObjectsRoot, texture, table, art, world, def, level, stage, building.X, building.Y, building.Rotation, ghost: false);

            if (building.State == BuildingState.Moving)
            {
                entry.Ghost ??= new Slot();
                Show(entry.Ghost, _view.ObjectsRoot, texture, table, art, world, def, level, 0, building.TargetX, building.TargetY, building.TargetRotation, ghost: true);
            }
            else if (entry.Ghost is not null)
            {
                entry.Ghost.Free();
                entry.Ghost = null;
            }
        }

        _stale.Clear();
        foreach ((EntityId id, Entry entry) in _entries)
        {
            if (entry.Seen != _pass)
            {
                _stale.Add(id);
            }
        }

        foreach (EntityId id in _stale)
        {
            _entries[id].Free();
            _entries.Remove(id);
        }
    }

    private bool EnsureArt(out WorldArt art, out AtlasTable table, out Texture2D texture)
    {
        art = null!;
        table = null!;
        texture = null!;
        if (_artFailed || !WorldArt.TryGetShared(out art, out string error))
        {
            return false;
        }

        if (_table is null)
        {
            if (!AtlasTable.TryLoad(out AtlasTable loaded, out error))
            {
                return Fail(error);
            }

            AtlasImage? image = loaded.Images.FirstOrDefault(i => i.Id == AtlasId);
            Texture2D? loadedTexture = image is not null && ResourceLoader.Exists(image.ResourcePath) ? GD.Load<Texture2D>(image.ResourcePath) : null;
            if (loadedTexture is null)
            {
                return Fail($"the '{AtlasId}' atlas image cannot be loaded");
            }

            _table = loaded;
            _texture = loadedTexture;
        }

        table = _table;
        texture = _texture!;
        return true;
    }

    private bool Fail(string reason)
    {
        _artFailed = true;
        GD.PushError($"BuildingLayer: {reason}; buildings are not drawn.");
        return false;
    }

    private static void Show(Slot slot, Node2D parent, Texture2D texture, AtlasTable table, WorldArt art, GameWorld world, BuildingDef def, int level, int stage, int x, int y, int rotation, bool ghost)
    {
        // A level the art does not have yet falls back to the highest lower level.
        AtlasCell cell = null!;
        bool found = false;
        for (int l = level; l >= 1 && !found; l--)
        {
            found = table.TryGetBuildingFrame(def.Id, l, stage, out cell!);
        }

        if (!found)
        {
            slot.Hide();
            return;
        }

        if (ReferenceEquals(slot.Cell, cell) && slot.X == x && slot.Y == y && slot.Rotation == rotation)
        {
            return;
        }

        (int width, int height) = Construction.FootprintOf(def, rotation);
        Sprite2D sprite = slot.Sprite ?? slot.Create(parent, texture, ghost ? _ghostTint : Colors.White);
        sprite.RegionRect = new Rect2(cell.Region.Position, cell.Region.Size);
        sprite.Offset = -new Vector2(cell.Origin.X, cell.Origin.Y);

        // The art is drawn for the unrotated footprint; a quarter turn mirrors it (a 3x2 becomes a 2x3).
        sprite.FlipH = (rotation & 1) == 1;
        sprite.Position = BuildingGeometry.BottomPixel(art, world, x, y, width, height);
        sprite.Visible = true;
        slot.Cell = cell;
        slot.X = x;
        slot.Y = y;
        slot.Rotation = rotation;
    }

    private void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Free();
        }

        _entries.Clear();
    }

    private sealed class Slot
    {
        public Sprite2D? Sprite { get; private set; }

        public AtlasCell? Cell { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public int Rotation { get; set; }

        public Sprite2D Create(Node2D parent, Texture2D texture, Color modulate)
        {
            Sprite = new Sprite2D
            {
                Texture = texture,
                Centered = false,
                RegionEnabled = true,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                Modulate = modulate,
            };
            parent.AddChild(Sprite);
            return Sprite;
        }

        public void Hide()
        {
            Sprite?.Hide();
            Cell = null;
        }

        public void Free()
        {
            if (Sprite is not null && GodotObject.IsInstanceValid(Sprite))
            {
                Sprite.QueueFree();
            }

            Sprite = null;
            Cell = null;
        }
    }

    private sealed class Entry
    {
        public Slot Body { get; } = new();

        public Slot? Ghost { get; set; }

        public int Seen { get; set; }

        public void Free()
        {
            Body.Free();
            Ghost?.Free();
        }
    }
}
