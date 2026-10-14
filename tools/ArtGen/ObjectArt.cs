namespace ArtGen;

/// <summary>Object icons on a transparent 64x64 cell; the base is centred on the diamond at (32, 48).</summary>
public static class ObjectArt
{
    /// <summary>Cell edge in pixels.</summary>
    public const int CellSize = 64;

    /// <summary>Texture origin: the pixel that sits on the map cell's diamond centre.</summary>
    public static readonly Pt Origin = new(32, 48);

    /// <summary>Object ids in atlas order.</summary>
    public static readonly IReadOnlyList<string> Ids =
        ["tree", "rock", "ore", "berry_bush", "poi_chest", "poi_ruins", "poi_lair", "settlement_site", "start_marker"];

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
            case "poi_chest": Chest(c); break;
            case "poi_ruins": Ruins(c); break;
            case "poi_lair": Lair(c); break;
            case "settlement_site": Site(c); break;
            case "start_marker": Marker(c); break;
            default: throw new ArgumentException("Unknown object id " + id, nameof(id));
        }

        return c;
    }

    private static void Tree(Canvas c)
    {
        Rgba trunk = Rgba.Hex(0x6B4A2B);
        c.FillRect(29, 38, 6, 12, trunk);
        c.Outline([new Pt(29, 38), new Pt(34, 38), new Pt(34, 49), new Pt(29, 49)], trunk.Scale(55));
        Rgba g1 = Rgba.Hex(0x2F6B3F);
        Rgba g2 = Rgba.Hex(0x3F8A50);
        c.FillAndOutline([new Pt(32, 12), new Pt(51, 44), new Pt(13, 44)], g1, g1.Scale(55));
        c.FillAndOutline([new Pt(32, 2), new Pt(48, 28), new Pt(16, 28)], g2, g2.Scale(55));
    }

    private static void Rock(Canvas c, bool ore)
    {
        Rgba grey = Rgba.Hex(0x8E9094);
        Pt[] body = [new Pt(14, 46), new Pt(19, 33), new Pt(30, 27), new Pt(43, 31), new Pt(50, 44), new Pt(40, 53), new Pt(24, 53)];
        c.FillAndOutline(body, grey, grey.Scale(45));
        Rgba light = grey.Shift(26);
        c.FillPolygon([new Pt(19, 33), new Pt(30, 27), new Pt(43, 31), new Pt(32, 38)], light);
        c.FillPolygon([new Pt(32, 38), new Pt(43, 31), new Pt(50, 44), new Pt(40, 53), new Pt(33, 50)], grey.Scale(82));
        if (ore)
        {
            Rgba orange = Rgba.Hex(0xE8892B);
            Rgba blue = Rgba.Hex(0x4DA6FF);
            int[,] flecks = { { 24, 40 }, { 36, 34 }, { 30, 46 }, { 42, 44 }, { 21, 47 } };
            for (int i = 0; i < flecks.GetLength(0); i++)
            {
                c.FillRect(flecks[i, 0], flecks[i, 1], 3, 3, i % 2 == 0 ? orange : blue);
            }
        }
    }

    private static void Bush(Canvas c)
    {
        Rgba green = Rgba.Hex(0x4C9A4F);
        Pt[] blob = [new Pt(16, 44), new Pt(20, 34), new Pt(30, 29), new Pt(42, 32), new Pt(49, 41), new Pt(45, 51), new Pt(32, 54), new Pt(20, 52)];
        c.FillAndOutline(blob, green, green.Scale(50));
        c.FillPolygon([new Pt(22, 36), new Pt(30, 31), new Pt(38, 33), new Pt(30, 39)], green.Shift(18));
        Rgba red = Rgba.Hex(0xD7263D);
        int[,] dots = { { 23, 44 }, { 31, 41 }, { 38, 46 }, { 29, 50 }, { 41, 38 } };
        for (int i = 0; i < dots.GetLength(0); i++)
        {
            c.FillRect(dots[i, 0], dots[i, 1], 3, 3, red);
        }
    }

    private static void Label(Canvas c, string text) => BitmapFont.DrawLabel(c, text, 32, 56, White, Black);

    private static void Chest(Canvas c)
    {
        Rgba wood = Rgba.Hex(0x9A6B33);
        Rgba lid = Rgba.Hex(0x7C521F);
        c.FillAndOutline([new Pt(18, 30), new Pt(46, 30), new Pt(46, 50), new Pt(18, 50)], wood, wood.Scale(45));
        c.FillAndOutline([new Pt(18, 22), new Pt(46, 22), new Pt(46, 32), new Pt(18, 32)], lid, lid.Scale(45));
        Rgba gold = Rgba.Hex(0xF2C230);
        c.FillRect(29, 29, 6, 7, gold);
        c.FillRect(18, 38, 28, 2, gold.Scale(70));
        Label(c, "CHEST");
    }

    private static void Ruins(Canvas c)
    {
        Rgba stone = Rgba.Hex(0xB9B2A3);
        Rgba edge = stone.Scale(45);
        c.FillAndOutline([new Pt(14, 50), new Pt(14, 26), new Pt(18, 22), new Pt(22, 28), new Pt(26, 24), new Pt(26, 50)], stone, edge);
        c.FillAndOutline([new Pt(34, 50), new Pt(34, 16), new Pt(38, 20), new Pt(40, 12), new Pt(46, 18), new Pt(46, 50)], stone.Shift(-14), edge);
        c.FillAndOutline([new Pt(48, 50), new Pt(48, 38), new Pt(54, 40), new Pt(54, 50)], stone.Shift(-26), edge);
        Label(c, "RUINS");
    }

    private static void Lair(Canvas c)
    {
        Rgba rock = Rgba.Hex(0x6F6A66);
        c.FillAndOutline([new Pt(10, 50), new Pt(14, 32), new Pt(26, 20), new Pt(40, 22), new Pt(52, 34), new Pt(56, 50)], rock, rock.Scale(40));
        c.FillPolygon([new Pt(22, 50), new Pt(24, 38), new Pt(32, 33), new Pt(40, 38), new Pt(42, 50)], new Rgba(18, 14, 16));
        Rgba eye = Rgba.Hex(0xFF3B30);
        c.FillRect(27, 42, 2, 2, eye);
        c.FillRect(35, 42, 2, 2, eye);
        Label(c, "LAIR");
    }

    private static void Site(Canvas c)
    {
        Rgba wood = Rgba.Hex(0x6B4A2B);
        c.FillRect(30, 12, 3, 38, wood);
        c.Outline([new Pt(30, 12), new Pt(32, 12), new Pt(32, 49), new Pt(30, 49)], wood.Scale(55));
        Rgba flag = Rgba.Hex(0xD7263D);
        c.FillAndOutline([new Pt(33, 12), new Pt(52, 19), new Pt(33, 27)], flag, flag.Scale(50));
        c.FillRect(26, 48, 11, 3, wood.Scale(80));
        Label(c, "SITE");
    }

    private static void Marker(Canvas c)
    {
        Rgba gold = Rgba.Hex(0xFFD23F);
        Rgba dark = Rgba.Hex(0x8A6A00);
        // Diamond ring around the ground tile centre (32, 48).
        Pt[] outer = [new Pt(12, 48), new Pt(32, 38), new Pt(52, 48), new Pt(32, 58)];
        Pt[] inner = [new Pt(20, 48), new Pt(32, 42), new Pt(44, 48), new Pt(32, 54)];
        c.FillPolygon(outer, gold);
        c.FillPolygon(inner, new Rgba(0, 0, 0, 0));
        c.Outline([new Pt(12, 47), new Pt(32, 37), new Pt(51, 47), new Pt(32, 57)], dark);
        // Arrow pointing down at the start tile.
        c.FillAndOutline([new Pt(26, 12), new Pt(38, 12), new Pt(38, 24), new Pt(46, 24), new Pt(32, 38), new Pt(18, 24), new Pt(26, 24)], gold, dark);
    }
}
