namespace ArtGen;

/// <summary>Isometric diamond tile drawing (32x16 top face, side faces for raised tiles).</summary>
public static class TileArt
{
    /// <summary>Tile width in pixels.</summary>
    public const int TileWidth = 32;

    /// <summary>Tile top-face height in pixels.</summary>
    public const int TileHeight = 16;

    /// <summary>Pixels a tile is raised per height step.</summary>
    public const int HeightStep = 8;

    /// <summary>Highest height level.</summary>
    public const int MaxHeight = 3;

    /// <summary>The tile kinds in atlas order: id, base colour, whether raised variants exist.</summary>
    public static readonly IReadOnlyList<TileKind> Kinds =
    [
        new("meadow", Rgba.Hex(0x7FB069), true, null),
        new("forest", Rgba.Hex(0x3E7B4F), true, null),
        new("mountain", Rgba.Hex(0x8A8D91), true, null),
        new("wasteland", Rgba.Hex(0xC2A878), true, null),
        new("water", Rgba.Hex(0x3A6EA5), false, null),
        new("river", Rgba.Hex(0x5B8FD0), true, Rgba.Hex(0x7A5C3E)),
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

    /// <summary>Draws one tile into a new canvas of TileWidth x CellHeight(height).</summary>
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
        int hw = TileWidth / 2;
        int hh = TileHeight / 2;

        if (height > 0)
        {
            // Raised river blocks keep the riverbed colour on top and use the bank colour on the sides.
            Rgba sideBase = kind.Side ?? top;
            Rgba left = sideBase.Scale(75);
            Rgba right = sideBase.Scale(55);
            Rgba edge = sideBase.Scale(38);
            cell.FillPolygon([new Pt(0, hh), new Pt(hw, TileHeight), new Pt(hw, TileHeight + raise), new Pt(0, hh + raise)], left);
            cell.FillPolygon([new Pt(TileWidth, hh), new Pt(hw, TileHeight), new Pt(hw, TileHeight + raise), new Pt(TileWidth, hh + raise)], right);
            cell.Line(0, hh - 1, 0, hh - 1 + raise, edge);
            cell.Line(TileWidth - 1, hh - 1, TileWidth - 1, hh - 1 + raise, edge);
            cell.Line(0, hh - 1 + raise, hw - 1, TileHeight - 2 + raise, edge);
            cell.Line(hw, TileHeight - 2 + raise, TileWidth - 1, hh - 1 + raise, edge);
            // Seam between the two side faces.
            cell.Line(hw - 1, TileHeight - 1, hw - 1, TileHeight - 2 + raise, edge);
        }

        cell.FillPolygon([new Pt(0, hh), new Pt(hw, 0), new Pt(TileWidth, hh), new Pt(hw, TileHeight)], top);
        AddSpecks(cell, kindIndex, height, variant, top, kind.Id is "water" or "river");

        // 1px outline of the top face (the apex pixels are two wide so it is mirror symmetric).
        cell.Line(0, hh - 1, hw - 1, 0, outline);
        cell.Line(hw, 0, TileWidth - 1, hh - 1, outline);
        cell.Line(TileWidth - 1, hh, hw, TileHeight - 1, outline);
        cell.Line(hw - 1, TileHeight - 1, 0, hh, outline);
        return cell;
    }

    /// <summary>Texture origin of a tile cell: the pixel that lands on the map cell's ground diamond centre.</summary>
    public static Pt Origin(int height) => new(TileWidth / 2, (TileHeight / 2) + (HeightStep * height));

    private static void AddSpecks(Canvas cell, int kindIndex, int height, int variant, Rgba top, bool wet)
    {
        for (uint i = 0; i < 4; i++)
        {
            uint h = Hash((uint)kindIndex, ((uint)height << 8) | (uint)variant, i + 100);
            int x = 6 + (int)(h % 20);
            int y = 3 + (int)((h >> 8) % 10);
            // Keep the speck (and its right neighbours) well inside the diamond.
            if (Math.Abs((2 * x) - (TileWidth - 1)) + (2 * Math.Abs((2 * y) - (TileHeight - 1))) > 22)
            {
                continue;
            }

            bool light = (h & 0x10000) != 0;
            Rgba c = top.Shift(light ? 12 : -12);
            cell.Set(x, y, c);
            if (wet)
            {
                cell.Set(x + 1, y, c);
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
/// <param name="Raisable">Whether heights 1-3 exist (water is flat).</param>
/// <param name="Side">Base colour of the side faces of raised tiles; null means the top colour.</param>
public sealed record TileKind(string Id, Rgba Base, bool Raisable, Rgba? Side);
