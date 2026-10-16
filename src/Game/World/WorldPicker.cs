using Core.Geometry;
using Core.Sim;
using Godot;

namespace Game.World;

/// <summary>Screen-to-tile picking shared by hero input, build mode and the inspector (ADR 0010).</summary>
public static class WorldPicker
{
    /// <summary>
    /// The tile whose block is drawn in front at a viewport position, using the same visual heights as the renderer
    /// (<see cref="WorldArt.VisualHeight"/>). False when nothing is drawn there or no game is bound.
    /// </summary>
    public static bool TryPickTile(WorldView view, Vector2 screen, out int x, out int y)
    {
        x = 0;
        y = 0;
        GameWorld? world = view.Simulation?.World;
        WorldArt? art = view.Chunks.Art;
        if (world is null || art is null)
        {
            return false;
        }

        Vector2 point = view.Camera.ScreenToWorld(screen);
        Core.WorldGen.TerrainMap terrain = world.Map.Terrain;
        bool found = IsoProjection.PickTile(
            (int)Mathf.Floor(point.X),
            (int)Mathf.Floor(point.Y),
            world.Size,
            (tx, ty) => art.VisualHeight(terrain.GetBiome(tx, ty), terrain.GetFeatures(tx, ty), terrain.GetHeight(tx, ty)),
            out (int X, int Y) tile);
        if (found)
        {
            x = tile.X;
            y = tile.Y;
        }

        return found;
    }
}
