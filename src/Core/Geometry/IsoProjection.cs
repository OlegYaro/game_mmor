using Core.Entities;
using Core.WorldGen;

namespace Core.Geometry;

/// <summary>
/// Isometric 2:1 projection of the tile grid (ADR 0010, GDD §3): tiles are 64×32 diamonds, height steps lift by 16 px.
/// It is the same mapping as a Godot <c>TileMapLayer</c> with <c>TileShape = Isometric</c>,
/// <c>TileLayout = DiamondDown</c>, <c>TileOffsetAxis = Horizontal</c> and a 64×32 tile size (checked against
/// <c>MapToLocal</c>/<c>LocalToMap</c> by the Game smoke test), so Core and the renderer agree on every pixel.
/// </summary>
/// <remarks>
/// <para>World pixels: x grows to the right, y grows down. Tile (x, y) has its ground centre at
/// <c>px = 32(x − y) + 32</c>, <c>py = 16(x + y) + 16</c>; its top corner is at <c>(32(x − y) + 32, 16(x + y))</c>.
/// A continuous tile-space point (tx, ty) (tile (x, y) covers [x, x + 1) × [y, y + 1)) maps to
/// <c>px = 32(tx − ty) + 32</c>, <c>py = 16(tx + ty)</c>.</para>
/// <para>The inverse is linear: for a ground point, <c>x = floor((u + 2v + 32) / 64)</c>,
/// <c>y = floor((2v − u + 32) / 64)</c> with <c>u = px − 32</c>, <c>v = py − 16</c>. Everything here is integer
/// math; sub-pixel inputs use a caller-chosen scale.</para>
/// </remarks>
public static class IsoProjection
{
    /// <summary>Width of a tile diamond in pixels.</summary>
    public const int TileWidth = 64;

    /// <summary>Height of a tile diamond in pixels.</summary>
    public const int TileHeight = 32;

    /// <summary>Half of <see cref="TileWidth"/>.</summary>
    public const int HalfWidth = TileWidth / 2;

    /// <summary>Half of <see cref="TileHeight"/>.</summary>
    public const int HalfHeight = TileHeight / 2;

    /// <summary>Vertical lift of one height step in pixels.</summary>
    public const int HeightStep = 16;

    /// <summary>Ground centre of tile (<paramref name="x"/>, <paramref name="y"/>) in world pixels.</summary>
    public static (int Px, int Py) TileCenter(int x, int y) =>
        ((HalfWidth * (x - y)) + HalfWidth, (HalfHeight * (x + y)) + HalfHeight);

    /// <summary>Centre of the top face of tile (<paramref name="x"/>, <paramref name="y"/>) raised by <paramref name="height"/> steps.</summary>
    public static (int Px, int Py) TileCenter(int x, int y, int height)
    {
        (int px, int py) = TileCenter(x, y);
        return (px, py - (HeightStep * height));
    }

    /// <summary>
    /// Ground tile under world pixel (<paramref name="px"/>, <paramref name="py"/>), taking the pixel's centre
    /// (px + ½, py + ½), which never lies on a diamond edge. Ignores heights (see <see cref="PickTile"/>).
    /// </summary>
    public static (int X, int Y) TileAtPixel(int px, int py) => TileAtSubPixel((2L * px) + 1, (2L * py) + 1, 2);

    /// <summary>
    /// Ground tile under the world point (<paramref name="px"/> / <paramref name="scale"/>,
    /// <paramref name="py"/> / <paramref name="scale"/>). Points exactly on an edge go to the tile with the larger
    /// coordinate. Ignores heights.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not positive.</exception>
    public static (int X, int Y) TileAtSubPixel(long px, long py, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        long u = px - ((long)HalfWidth * scale);
        long v = py - ((long)HalfHeight * scale);
        long size = (long)TileWidth * scale;
        long half = size / 2;
        return ((int)FloorDiv(u + (2 * v) + half, size), (int)FloorDiv((2 * v) - u + half, size));
    }

    /// <summary>
    /// The tile whose block (top face or side, see <paramref name="heightAt"/>) is drawn in front at world pixel
    /// (<paramref name="px"/>, <paramref name="py"/>), as the renderer paints blocks back to front: candidates are the
    /// ground picks of the pixel moved down by k steps (k = <see cref="TerrainMap.MaxHeight"/>..0); the first whose
    /// height is at least k wins. Tiles outside the map are skipped.
    /// </summary>
    /// <param name="px">World pixel x.</param>
    /// <param name="py">World pixel y.</param>
    /// <param name="mapSize">Side of the map in tiles.</param>
    /// <param name="heightAt">Visual height step of an on-map tile.</param>
    /// <param name="tile">The picked tile.</param>
    /// <returns>False when no on-map block covers the pixel.</returns>
    public static bool PickTile(int px, int py, int mapSize, Func<int, int, int> heightAt, out (int X, int Y) tile)
    {
        ArgumentNullException.ThrowIfNull(heightAt);
        for (int k = TerrainMap.MaxHeight; k >= 0; k--)
        {
            (int x, int y) = TileAtPixel(px, py + (HeightStep * k));
            if ((uint)x < (uint)mapSize && (uint)y < (uint)mapSize && heightAt(x, y) >= k)
            {
                tile = (x, y);
                return true;
            }
        }

        tile = default;
        return false;
    }

    /// <summary>
    /// World point of a fixed-point <see cref="Position"/> in 1/<see cref="Position.UnitsPerTile"/> pixel (divide by
    /// <see cref="Position.UnitsPerTile"/> for pixels). The centre of a tile maps to <see cref="TileCenter(int, int)"/>.
    /// </summary>
    public static (long Px, long Py) PixelOfPosition(Position position) =>
        ((HalfWidth * ((long)position.X - position.Y)) + ((long)HalfWidth * Position.UnitsPerTile),
         HalfHeight * ((long)position.X + position.Y));

    /// <summary>
    /// Bounding box of a <paramref name="mapSize"/>² map in world pixels, including the top faces of the highest
    /// blocks (<see cref="TerrainMap.MaxHeight"/>): <c>[MinX, MaxX) × [MinY, MaxY)</c>.
    /// </summary>
    public static (int MinX, int MinY, int MaxX, int MaxY) MapBounds(int mapSize) =>
        (HalfWidth * (1 - mapSize), -HeightStep * TerrainMap.MaxHeight, HalfWidth * (1 + mapSize), TileHeight * mapSize);

    private static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }
}
