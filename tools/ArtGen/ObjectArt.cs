namespace ArtGen;

/// <summary>Object icons on a transparent 32x32 cell; the base is centred on the diamond at (16, 24).</summary>
public static class ObjectArt
{
    /// <summary>Cell edge in pixels.</summary>
    public const int CellSize = 32;

    /// <summary>Texture origin: the pixel that sits on the map cell's diamond centre.</summary>
    public static readonly Pt Origin = new(16, 24);

    /// <summary>Object ids in atlas order.</summary>
    public static readonly IReadOnlyList<string> Ids =
        ["tree", "rock", "ore", "berry_bush", "poi_chest", "poi_ruins", "poi_lair", "settlement_site", "start_marker", "coal", "ore_vein", "coal_vein"];

    private static readonly Rgba Black = new(20, 20, 24);
    private static readonly Rgba White = new(250, 250, 250);

    /// <summary>Draws the object with the given id.</summary>
    public static Canvas Draw(string id)
    {
        Canvas c = new(CellSize, CellSize);
        switch (id)
        {
            case "tree": Tree(c); break;
            case "rock": Rock(c, false); break;
            case "ore": Rock(c, true); break;
            case "berry_bush": Bush(c); break;
            case "poi_chest": Labelled(c, Chest, "CHEST"); break;
            case "poi_ruins": Labelled(c, Ruins, "RUINS"); break;
            case "poi_lair": Labelled(c, Lair, "LAIR"); break;
            case "settlement_site": Labelled(c, Site, "SITE"); break;
            case "start_marker": Marker(c); break;
            case "coal": Coal(c); break;
            case "ore_vein": Vein(c, Rgba.Hex(0x5A5E6B), Rgba.Hex(0xE8892B), Rgba.Hex(0x4DA6FF)); break;
            case "coal_vein": Vein(c, Rgba.Hex(0x1C1C22), Rgba.Hex(0xF2C230), Rgba.Hex(0xE8E8F0)); break;
            default: throw new ArgumentException("Unknown object id " + id, nameof(id));
        }

        return c;
    }

    private static void Tree(Canvas c)
    {
        Rgba trunk = Rgba.Hex(0x6B4A2B);
        c.FillRect(14, 19, 4, 7, trunk);
        c.Outline([new Pt(14, 19), new Pt(17, 19), new Pt(17, 25), new Pt(14, 25)], trunk.Scale(55));
        Rgba g1 = Rgba.Hex(0x2F6B3F);
        Rgba g2 = Rgba.Hex(0x3F8A50);
        c.FillAndOutline([new Pt(16, 8), new Pt(27, 22), new Pt(5, 22)], g1, g1.Scale(55));
        c.FillAndOutline([new Pt(16, 0), new Pt(25, 14), new Pt(7, 14)], g2, g2.Scale(55));
    }

    private static void Rock(Canvas c, bool ore)
    {
        Rgba grey = Rgba.Hex(0x8E9094);
        Pt[] body = [new Pt(7, 23), new Pt(9, 16), new Pt(15, 13), new Pt(21, 15), new Pt(25, 22), new Pt(20, 26), new Pt(12, 26)];
        c.FillAndOutline(body, grey, grey.Scale(45));
        Rgba light = grey.Shift(26);
        c.FillPolygon([new Pt(10, 16), new Pt(15, 14), new Pt(21, 16), new Pt(16, 19)], light);
        c.FillPolygon([new Pt(16, 19), new Pt(21, 16), new Pt(24, 22), new Pt(20, 25), new Pt(16, 25)], grey.Scale(82));
        if (ore)
        {
            Rgba orange = Rgba.Hex(0xE8892B);
            Rgba blue = Rgba.Hex(0x4DA6FF);
            int[,] flecks = { { 11, 20 }, { 17, 16 }, { 14, 23 }, { 20, 21 } };
            for (int i = 0; i < flecks.GetLength(0); i++)
            {
                c.FillRect(flecks[i, 0], flecks[i, 1], 2, 2, i % 2 == 0 ? orange : blue);
            }
        }
    }

    private static void Coal(Canvas c)
    {
        Rgba coal = Rgba.Hex(0x2B2B31);
        Pt[] body = [new Pt(7, 23), new Pt(9, 16), new Pt(15, 13), new Pt(21, 15), new Pt(25, 22), new Pt(20, 26), new Pt(12, 26)];
        c.FillAndOutline(body, coal, Black);
        c.FillPolygon([new Pt(10, 16), new Pt(15, 14), new Pt(21, 16), new Pt(16, 19)], coal.Shift(30));
        c.FillPolygon([new Pt(16, 19), new Pt(21, 16), new Pt(24, 22), new Pt(20, 25), new Pt(16, 25)], coal.Scale(70));
        Rgba glint = Rgba.Hex(0x9AA0B4);
        c.FillRect(12, 20, 2, 2, glint);
        c.FillRect(19, 18, 2, 2, glint);
    }

    // Veins fill nearly the whole cell and carry big bright crystals, so they read from far away on the map.
    private static void Vein(Canvas c, Rgba rock, Rgba glowA, Rgba glowB)
    {
        Pt[] body = [new Pt(2, 24), new Pt(4, 14), new Pt(10, 8), new Pt(17, 6), new Pt(25, 9), new Pt(30, 16), new Pt(29, 25), new Pt(21, 29), new Pt(9, 29)];
        c.FillAndOutline(body, rock, Black);
        c.FillPolygon([new Pt(6, 15), new Pt(11, 9), new Pt(17, 7), new Pt(24, 10), new Pt(18, 15)], rock.Shift(26));
        c.FillPolygon([new Pt(18, 15), new Pt(24, 10), new Pt(29, 16), new Pt(28, 24), new Pt(21, 28), new Pt(18, 26)], rock.Scale(75));
        c.FillAndOutline([new Pt(7, 24), new Pt(9, 15), new Pt(12, 15), new Pt(13, 24)], glowA, glowA.Scale(45));
        c.FillAndOutline([new Pt(15, 27), new Pt(16, 11), new Pt(20, 11), new Pt(21, 27)], glowB, glowB.Scale(45));
        c.FillAndOutline([new Pt(23, 25), new Pt(24, 17), new Pt(27, 17), new Pt(27, 25)], glowA, glowA.Scale(45));
        c.FillRect(17, 12, 1, 6, White);
    }

    private static void Bush(Canvas c)
    {
        Rgba green = Rgba.Hex(0x4C9A4F);
        Pt[] blob = [new Pt(8, 22), new Pt(10, 17), new Pt(15, 14), new Pt(21, 16), new Pt(24, 20), new Pt(22, 25), new Pt(16, 27), new Pt(10, 26)];
        c.FillAndOutline(blob, green, green.Scale(50));
        c.FillPolygon([new Pt(11, 18), new Pt(15, 15), new Pt(19, 16), new Pt(15, 19)], green.Shift(18));
        Rgba red = Rgba.Hex(0xD7263D);
        int[,] dots = { { 11, 22 }, { 15, 20 }, { 19, 23 }, { 14, 25 }, { 20, 18 } };
        for (int i = 0; i < dots.GetLength(0); i++)
        {
            c.FillRect(dots[i, 0], dots[i, 1], 2, 2, red);
        }
    }

    // Icons are drawn in ground-standing coordinates (base near y=22) and lifted by IconLift so the label
    // (own row, 5 glyphs * 6 px fit the 32 px cell) stays above the ground centre row and is never covered
    // by the tiles in front of the cell.
    private const int IconLift = 6;

    private static void Labelled(Canvas c, Action<Canvas> icon, string text)
    {
        Canvas tmp = new(CellSize, CellSize);
        icon(tmp);
        c.Blit(tmp, 0, IconLift, CellSize, CellSize - IconLift, 0, 0);
        BitmapFont.DrawLabel(c, text, 16, 17, White, Black);
    }

    private static void Chest(Canvas c)
    {
        Rgba wood = Rgba.Hex(0x9A6B33);
        Rgba lid = Rgba.Hex(0x7C521F);
        c.FillAndOutline([new Pt(9, 13), new Pt(23, 13), new Pt(23, 22), new Pt(9, 22)], wood, wood.Scale(45));
        c.FillAndOutline([new Pt(9, 7), new Pt(23, 7), new Pt(23, 14), new Pt(9, 14)], lid, lid.Scale(45));
        Rgba gold = Rgba.Hex(0xF2C230);
        c.FillRect(14, 12, 3, 4, gold);
        c.FillRect(10, 17, 12, 1, gold.Scale(70));
    }

    private static void Ruins(Canvas c)
    {
        Rgba stone = Rgba.Hex(0xB9B2A3);
        Rgba edge = stone.Scale(45);
        c.FillAndOutline([new Pt(6, 22), new Pt(6, 12), new Pt(8, 10), new Pt(10, 13), new Pt(12, 11), new Pt(12, 22)], stone, edge);
        c.FillAndOutline([new Pt(15, 22), new Pt(15, 9), new Pt(17, 10), new Pt(18, 7), new Pt(21, 10), new Pt(21, 22)], stone.Shift(-14), edge);
        c.FillAndOutline([new Pt(24, 22), new Pt(24, 16), new Pt(27, 17), new Pt(27, 22)], stone.Shift(-26), edge);
    }

    private static void Lair(Canvas c)
    {
        Rgba rock = Rgba.Hex(0x6F6A66);
        c.FillAndOutline([new Pt(4, 22), new Pt(6, 14), new Pt(12, 8), new Pt(20, 9), new Pt(26, 15), new Pt(28, 22)], rock, rock.Scale(40));
        c.FillPolygon([new Pt(10, 21), new Pt(11, 16), new Pt(16, 13), new Pt(21, 16), new Pt(22, 21)], new Rgba(18, 14, 16));
        Rgba eye = Rgba.Hex(0xFF3B30);
        c.FillRect(13, 17, 2, 2, eye);
        c.FillRect(18, 17, 2, 2, eye);
    }

    private static void Site(Canvas c)
    {
        Rgba wood = Rgba.Hex(0x6B4A2B);
        c.FillRect(14, 7, 2, 15, wood);
        c.Outline([new Pt(14, 7), new Pt(15, 7), new Pt(15, 21), new Pt(14, 21)], wood.Scale(55));
        Rgba flag = Rgba.Hex(0xD7263D);
        c.FillAndOutline([new Pt(16, 7), new Pt(26, 11), new Pt(16, 16)], flag, flag.Scale(50));
        c.FillRect(11, 21, 8, 1, wood.Scale(80));
    }

    private static void Marker(Canvas c)
    {
        Rgba gold = Rgba.Hex(0xFFD23F);
        Rgba dark = Rgba.Hex(0x8A6A00);
        // Diamond ring around the ground tile centre (16, 24).
        Pt[] outer = [new Pt(6, 24), new Pt(16, 19), new Pt(26, 24), new Pt(16, 29)];
        Pt[] inner = [new Pt(10, 24), new Pt(16, 21), new Pt(22, 24), new Pt(16, 27)];
        c.FillPolygon(outer, gold);
        c.FillPolygon(inner, new Rgba(0, 0, 0, 0));
        c.Outline([new Pt(6, 23), new Pt(16, 18), new Pt(25, 23), new Pt(16, 28)], dark);
        // Arrow pointing down at the start tile.
        c.FillAndOutline([new Pt(13, 3), new Pt(19, 3), new Pt(19, 10), new Pt(23, 10), new Pt(16, 18), new Pt(9, 10), new Pt(13, 10)], gold, dark);
    }
}
