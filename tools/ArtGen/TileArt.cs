namespace ArtGen;

/// <summary>Isometric diamond tile drawing (64x32 top face, side faces for raised tiles).</summary>
public static class TileArt
{
    /// <summary>Tile width in pixels.</summary>
    public const int TileWidth = 64;

    /// <summary>Tile top-face height in pixels.</summary>
    public const int TileHeight = 32;

    /// <summary>Pixels a tile is raised per height step.</summary>
    public const int HeightStep = 16;

    /// <summary>Highest height level.</summary>
    public const int MaxHeight = 3;

    /// <summary>The tile kinds in atlas order: id, base colour, whether raised variants exist.</summary>
    public static readonly IReadOnlyList<TileKind> Kinds =
    [
        new("meadow", Rgba.Hex(0x7FB069), true),
        new("forest", Rgba.Hex(0x3E7B4F), true),
        new("mountain", Rgba.Hex(0x8A8D91), true),
        new("wasteland", Rgba.Hex(0xC2A878), true),
        new("water", Rgba.Hex(0x3A6EA5), false),
        new("river", Rgba.Hex(0x5B8FD0), false),
    ];

    /// <summary>Variants per tile kind and height.</summary>
    public const int Variants = 3;

    /// <summary>Cell height of a tile at <paramref name="height"/>: the top diamond plus the side faces.</summary>
    public static int CellHeight(int height) => TileHeight + (HeightStep * height);

    /// <summary>Fixed integer hash (SplitMix-style finaliser); no System.Random anywhere.</summary>
    public static uint Hash(uint a, uint b, uint c)
    {
        ulong z = ((ulong)a << 40) ^ ((ulong)b << 20) ^ c ^ 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (uint)((z ^ (z >> 31)) >> 16);
    }

    /// <summary>Draws one tile into a new canvas of 64 x CellHeight(height).</summary>
    public static Canvas Draw(int kindIndex, int height, int variant)
    {
        TileKind kind = Kinds[kindIndex];
        Canvas cell = new(TileWidth, CellHeight(height));
        Rgba top = kind.Base;
        if (variant > 0)
        {
            uint h = Hash((uint)kindIndex, (uint)height, (uint)variant);
            top = top.Shift((int)(h % 13) - 6, (int)((h >> 4) % 13) - 6, (int)((h >> 8) % 13) - 6);
        }

        Rgba outline = top.Scale(55);
        int raise = HeightStep * height;

        if (height > 0)
        {
            Rgba left = top.Scale(75);
            Rgba right = top.Scale(55);
            Rgba edge = top.Scale(38);
            cell.FillPolygon([new Pt(0, 16), new Pt(32, 32), new Pt(32, 32 + raise), new Pt(0, 16 + raise)], left);
            cell.FillPolygon([new Pt(64, 16), new Pt(32, 32), new Pt(32, 32 + raise), new Pt(64, 16 + raise)], right);
            cell.Line(0, 15, 0, 15 + raise, edge);
            cell.Line(63, 15, 63, 15 + raise, edge);
            cell.Line(0, 15 + raise, 31, 30 + raise, edge);
            cell.Line(32, 30 + raise, 63, 15 + raise, edge);
            // Seam between the two side faces.
            cell.Line(31, 31, 31, 30 + raise, edge);
        }

        cell.FillPolygon([new Pt(0, 16), new Pt(32, 0), new Pt(64, 16), new Pt(32, 32)], top);
        AddSpecks(cell, kindIndex, height, variant, top, kind.Id is "water" or "river");

        // 1px outline of the top face (the apex pixels are two wide so it is mirror symmetric).
        cell.Line(0, 15, 31, 0, outline);
        cell.Line(32, 0, 63, 15, outline);
        cell.Line(63, 16, 32, 31, outline);
        cell.Line(31, 31, 0, 16, outline);
        return cell;
    }

    private static void AddSpecks(Canvas cell, int kindIndex, int height, int variant, Rgba top, bool wet)
    {
        for (uint i = 0; i < 6; i++)
        {
            uint h = Hash((uint)kindIndex, ((uint)height << 8) | (uint)variant, i + 100);
            int x = 10 + (int)(h % 44);
            int y = 6 + (int)((h >> 8) % 20);
            if (Math.Abs((2 * x) - 63) + (2 * Math.Abs((2 * y) - 31)) > 52)
            {
                continue;
            }

            bool light = (h & 0x10000) != 0;
            Rgba c = top.Shift(light ? 12 : -12);
            cell.Set(x, y, c);
            if (wet)
            {
                cell.Set(x + 1, y, c);
                cell.Set(x + 2, y, c);
            }
            else if ((h & 0x20000) != 0)
            {
                cell.Set(x + 1, y, c);
            }
        }
    }
}

/// <summary>One tile kind.</summary>
/// <param name="Id">Content id.</param>
/// <param name="Base">Top-face colour of variant 0.</param>
/// <param name="Raisable">Whether heights 1-3 exist (water and river are flat).</param>
public sealed record TileKind(string Id, Rgba Base, bool Raisable);
