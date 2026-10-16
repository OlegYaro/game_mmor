using Core.Geometry;
using Core.Sim;
using Godot;

namespace Game.World;

/// <summary>Screen positions of building footprints, shared by the building sprites and the footprint overlay.</summary>
public static class BuildingGeometry
{
    /// <summary>Height of tile (<paramref name="x"/>, <paramref name="y"/>) as the renderer draws it (<see cref="WorldArt.VisualHeight"/>).</summary>
    public static int VisualHeightAt(WorldArt art, GameWorld world, int x, int y)
    {
        Core.WorldGen.TerrainMap terrain = world.Map.Terrain;
        return art.VisualHeight(terrain.GetBiome(x, y), terrain.GetFeatures(x, y), terrain.GetHeight(x, y));
    }

    /// <summary>Centre of the top face of a tile in world pixels.</summary>
    public static Vector2 TileTop(WorldArt art, GameWorld world, int x, int y)
    {
        (int px, int py) = IsoProjection.TileCenter(x, y, VisualHeightAt(art, world, x, y));
        return new Vector2(px, py);
    }

    /// <summary>
    /// Lowest pixel of the footprint diamond of a <paramref name="width"/>×<paramref name="height"/> footprint whose top-left
    /// tile is (<paramref name="x"/>, <paramref name="y"/>): the pixel a building frame's origin sits on. The footprint is flat,
    /// so the height of the bottom tile is the height of all.
    /// </summary>
    public static Vector2 BottomPixel(WorldArt art, GameWorld world, int x, int y, int width, int height)
    {
        Vector2 centre = TileTop(art, world, x + width - 1, y + height - 1);
        return new Vector2(centre.X, centre.Y + IsoProjection.HalfHeight - 1);
    }

    /// <summary>True when the tile is on the map.</summary>
    public static bool InMap(GameWorld world, int x, int y) => x >= 0 && y >= 0 && x < world.Size && y < world.Size;
}
