using Core.Geometry;
using Core.Sim;
using Core.WorldGen;
using Godot;

namespace Game.World;

/// <summary>
/// Placement helpers shared by the creature layers (<see cref="EntityLayer"/>, <see cref="ResidentLayer"/>): where a Core
/// <see cref="Core.Entities.Position"/> is on the ground plane, how high its tile lifts it, and how an object sprite of the
/// atlas becomes a texture. Pure functions over Core state; nothing is cached here.
/// </summary>
public static class EntityMotion
{
    /// <summary>Distance in pixels beyond which a creature jumped (teleport, first sample) and is shown there at once.</summary>
    public const float SnapDistancePixels = 3f * IsoProjection.TileWidth;

    /// <summary>Fallback length of a Core tick in seconds when the host gives none.</summary>
    public const double FallbackTickSeconds = 0.1;

    /// <summary>World point of the feet on the ground plane (without the height lift) for a Core position.</summary>
    public static Vector2 GroundPoint(Core.Entities.Position position)
    {
        (long px, long py) = IsoProjection.PixelOfPosition(position);
        return new Vector2((float)px / Core.Entities.Position.UnitsPerTile, (float)py / Core.Entities.Position.UnitsPerTile);
    }

    /// <summary>Visual height of tile (<paramref name="x"/>, <paramref name="y"/>) in height steps (0 outside the map).</summary>
    public static int HeightAt(GameWorld world, WorldArt art, int x, int y)
    {
        TerrainMap terrain = world.Map.Terrain;
        return terrain.Contains(x, y) ? art.VisualHeight(terrain.GetBiome(x, y), terrain.GetFeatures(x, y), terrain.GetHeight(x, y)) : 0;
    }

    /// <summary>How many pixels a creature on the tile is lifted above the ground plane.</summary>
    public static float Lift(GameWorld world, WorldArt art, int x, int y) => IsoProjection.HeightStep * HeightAt(world, art, x, y);

    /// <summary>
    /// The atlas texture of the object cell <paramref name="artId"/> and the sprite offset that puts its origin on the node
    /// position. False (with an error pushed) when the cell or its atlas image is missing.
    /// </summary>
    public static bool TryMakeObjectTexture(WorldArt art, string artId, out AtlasTexture texture, out Vector2 offset)
    {
        texture = null!;
        offset = default;
        if (!art.Table.TryGet(artId, out AtlasCell cell))
        {
            GD.PushError($"The art table has no '{artId}' cell.");
            return false;
        }

        AtlasImage? image = art.Table.Images.FirstOrDefault(i => i.Id == cell.Atlas);
        Texture2D? atlas = image is not null && ResourceLoader.Exists(image.ResourcePath) ? GD.Load<Texture2D>(image.ResourcePath) : null;
        if (atlas is null)
        {
            GD.PushError($"The atlas of '{artId}' cannot be loaded.");
            return false;
        }

        texture = new AtlasTexture { Atlas = atlas, Region = new Rect2(cell.Region.Position, cell.Region.Size) };
        offset = -new Vector2(cell.Origin.X, cell.Origin.Y);
        return true;
    }
}
