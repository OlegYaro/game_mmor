using Core.Buildings;
using Core.Geometry;
using Core.Sim;
using Game.Gameplay;
using Godot;

namespace Game.World;

/// <summary>
/// Footprint drawings above the fog (so they stay visible at night): the build-mode preview (one translucent diamond per tile,
/// green when Core accepts the placement, red when it refuses) and the frame around the selected building. It only draws what
/// <see cref="BuildMode"/> and <see cref="BuildingSelection"/> tell it; validity comes from Core. Add it as a child of the
/// <see cref="WorldView"/> (it draws in world coordinates).
/// </summary>
public partial class FootprintOverlay : Node2D
{
    /// <summary>Draw order above the fog pass (z-index 100).</summary>
    public const int OverlayZIndex = 110;

    private static readonly Color _okFill = new(0.25f, 0.85f, 0.35f, 0.45f);
    private static readonly Color _badFill = new(0.9f, 0.2f, 0.2f, 0.45f);
    private static readonly Color _badTileFill = new(1f, 0.1f, 0.1f, 0.75f);
    private static readonly Color _okLine = new(0.6f, 1f, 0.65f, 0.95f);
    private static readonly Color _badLine = new(1f, 0.55f, 0.5f, 0.95f);
    private static readonly Color _selectionLine = new(1f, 0.9f, 0.3f, 0.95f);

    private WorldView? _view;
    private BuildingSelection? _selection;
    private bool _hasPreview;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private PlacementResult _result;
    private (int X, int Y, int W, int H, bool Has) _drawnSelection;

    /// <summary>True while a preview is shown.</summary>
    public bool HasPreview => _hasPreview;

    /// <summary>True while the shown preview is green.</summary>
    public bool PreviewIsOk => _hasPreview && _result.IsOk;

    /// <summary>Top-left tile and size of the shown preview.</summary>
    public (int X, int Y, int Width, int Height) PreviewRect => (_x, _y, _width, _height);

    /// <summary>Selection frame currently drawn, as top-left tile and size; null when none.</summary>
    public (int X, int Y, int Width, int Height)? SelectionRect => _drawnSelection.Has ? (_drawnSelection.X, _drawnSelection.Y, _drawnSelection.W, _drawnSelection.H) : null;

    public override void _Ready()
    {
        ZIndex = OverlayZIndex;
        Name = "FootprintOverlay";
    }

    /// <summary>Connects the overlay to the world view and the selection it frames.</summary>
    public void Attach(WorldView view, BuildingSelection selection)
    {
        _view = view;
        _selection = selection;
        ZIndex = OverlayZIndex;
    }

    /// <summary>Shows the preview of a <paramref name="width"/>×<paramref name="height"/> footprint with its top-left tile at (x, y).</summary>
    public void ShowPreview(int x, int y, int width, int height, PlacementResult result)
    {
        if (_hasPreview && _x == x && _y == y && _width == width && _height == height && _result == result)
        {
            return;
        }

        _hasPreview = true;
        (_x, _y, _width, _height, _result) = (x, y, width, height, result);
        QueueRedraw();
    }

    /// <summary>Hides the preview.</summary>
    public void HidePreview()
    {
        if (_hasPreview)
        {
            _hasPreview = false;
            QueueRedraw();
        }
    }

    public override void _Process(double delta)
    {
        // The selection frame follows the building (a move changes its place, a demolition removes it).
        (int X, int Y, int W, int H, bool Has) now = CurrentSelection();
        if (now != _drawnSelection)
        {
            _drawnSelection = now;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        GameWorld? world = _view?.Simulation?.World;
        if (_view is null || world is null || !WorldArt.TryGetShared(out WorldArt art, out _))
        {
            return;
        }

        if (_hasPreview)
        {
            DrawPreview(art, world);
        }

        if (_drawnSelection.Has)
        {
            (int x, int y, int w, int h, _) = _drawnSelection;
            Vector2 top = BuildingGeometry.TileTop(art, world, x, y) + new Vector2(0, -IsoProjection.HalfHeight);
            Vector2 right = BuildingGeometry.TileTop(art, world, x + w - 1, y) + new Vector2(IsoProjection.HalfWidth, 0);
            Vector2 bottom = BuildingGeometry.TileTop(art, world, x + w - 1, y + h - 1) + new Vector2(0, IsoProjection.HalfHeight);
            Vector2 left = BuildingGeometry.TileTop(art, world, x, y + h - 1) + new Vector2(-IsoProjection.HalfWidth, 0);
            DrawPolyline([top, right, bottom, left, top], _selectionLine, 2f);
        }
    }

    private (int X, int Y, int W, int H, bool Has) CurrentSelection()
    {
        if (_selection is null || _view?.Simulation is not { World: { } world } sim || !world.Buildings.TryGet(_selection.Selected, out Building building))
        {
            return default;
        }

        (int w, int h) = Construction.FootprintOf(Construction.DefOf(sim.Content, building), building.Rotation);
        return (building.X, building.Y, w, h, true);
    }

    private void DrawPreview(WorldArt art, GameWorld world)
    {
        bool ok = _result.IsOk;
        for (int ty = _y; ty < _y + _height; ty++)
        {
            for (int tx = _x; tx < _x + _width; tx++)
            {
                if (!BuildingGeometry.InMap(world, tx, ty))
                {
                    continue;
                }

                Vector2 c = BuildingGeometry.TileTop(art, world, tx, ty);
                Vector2[] diamond =
                [
                    c + new Vector2(0, -IsoProjection.HalfHeight),
                    c + new Vector2(IsoProjection.HalfWidth, 0),
                    c + new Vector2(0, IsoProjection.HalfHeight),
                    c + new Vector2(-IsoProjection.HalfWidth, 0),
                ];
                bool offending = !ok && tx == _result.TileX && ty == _result.TileY;
                DrawColoredPolygon(diamond, ok ? _okFill : offending ? _badTileFill : _badFill);
                DrawPolyline([diamond[0], diamond[1], diamond[2], diamond[3], diamond[0]], ok ? _okLine : _badLine, 1f);
            }
        }
    }
}
