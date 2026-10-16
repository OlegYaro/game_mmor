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
        ["tree", "rock", "ore", "berry_bush", "poi_chest", "poi_ruins", "poi_lair", "settlement_site", "start_marker", "coal", "ore_vein", "coal_vein", "hero",
         "resident_carrier", "resident_lumberjack", "resident_stonemason", "resident_farmer", "resident_miner", "resident_guard", "resident_specialist", "resident_unemployed",
         "sapling", "stump"];

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
            case "hero": Hero(c); break;
            case "resident_carrier": Carrier(c); break;
            case "resident_lumberjack": Lumberjack(c); break;
            case "resident_stonemason": Stonemason(c); break;
            case "resident_farmer": Farmer(c); break;
            case "resident_miner": Miner(c); break;
            case "resident_guard": Guard(c); break;
            case "resident_specialist": Specialist(c); break;
            case "resident_unemployed": Unemployed(c); break;
            case "sapling": Sapling(c); break;
            case "stump": Stump(c); break;
            default: throw new ArgumentException("Unknown object id " + id, nameof(id));
        }

        return c;
    }

    // Static front-facing figure, feet on the ground centre (16, 24): head with hair, tunic with belt, arms, legs, boots, sword.
    private static void Hero(Canvas c)
    {
        Rgba skin = Rgba.Hex(0xF1C27D);
        Rgba cloth = Rgba.Hex(0x3A6EEA);
        Rgba pants = Rgba.Hex(0x39476B);
        Rgba boots = Rgba.Hex(0x4A3320);
        Rgba hair = Rgba.Hex(0x5B3A1E);
        Rgba steel = Rgba.Hex(0xC9CED6);

        c.FillAndOutline([new Pt(13, 19), new Pt(16, 19), new Pt(16, 25), new Pt(13, 25)], boots, boots.Scale(45));
        c.FillAndOutline([new Pt(16, 19), new Pt(19, 19), new Pt(19, 25), new Pt(16, 25)], boots, boots.Scale(45));
        c.FillAndOutline([new Pt(13, 17), new Pt(16, 17), new Pt(16, 22), new Pt(13, 22)], pants, pants.Scale(45));
        c.FillAndOutline([new Pt(16, 17), new Pt(19, 17), new Pt(19, 22), new Pt(16, 22)], pants, pants.Scale(45));
        c.FillAndOutline([new Pt(9, 10), new Pt(12, 10), new Pt(12, 18), new Pt(9, 18)], skin, skin.Scale(45));
        c.FillAndOutline([new Pt(20, 10), new Pt(23, 10), new Pt(23, 18), new Pt(20, 18)], skin, skin.Scale(45));
        c.FillAndOutline([new Pt(11, 10), new Pt(21, 10), new Pt(21, 18), new Pt(11, 18)], cloth, cloth.Scale(45));
        c.FillRect(12, 15, 8, 1, Rgba.Hex(0x7C521F));
        c.FillRect(15, 15, 2, 1, Rgba.Hex(0xF2C230));
        c.Line(25, 8, 25, 18, steel);
        c.Line(26, 9, 26, 17, steel.Scale(70));
        c.FillRect(24, 18, 3, 1, Rgba.Hex(0x7C521F));
        c.FillAndOutline([new Pt(14, 2), new Pt(18, 2), new Pt(20, 4), new Pt(20, 8), new Pt(18, 10), new Pt(14, 10), new Pt(12, 8), new Pt(12, 4)], skin, skin.Scale(45));
        c.FillPolygon([new Pt(13, 4), new Pt(14, 3), new Pt(18, 3), new Pt(19, 4), new Pt(19, 6), new Pt(13, 6)], hair);
        c.Set(14, 7, Black);
        c.Set(17, 7, Black);
    }

    // ---- Residents: the hero's pose without the sword, so a resident never reads as the hero. Each one differs by
    // clothes colour and one big attribute that stays readable at zoom 0.5 (the figure is 16 px wide there).

    private static readonly Rgba Skin = Rgba.Hex(0xF1C27D);
    private static readonly Rgba Boots = Rgba.Hex(0x4A3320);
    private static readonly Rgba Steel = Rgba.Hex(0xC9CED6);
    private static readonly Rgba Haft = Rgba.Hex(0x8A5A2B);

    // Feet on (16, 24); a null `hair` leaves the head to the caller's hat.
    private static void Person(Canvas c, Rgba cloth, Rgba pants, Rgba? hair)
    {
        c.FillAndOutline([new Pt(13, 19), new Pt(16, 19), new Pt(16, 25), new Pt(13, 25)], Boots, Boots.Scale(45));
        c.FillAndOutline([new Pt(16, 19), new Pt(19, 19), new Pt(19, 25), new Pt(16, 25)], Boots, Boots.Scale(45));
        c.FillAndOutline([new Pt(13, 17), new Pt(16, 17), new Pt(16, 22), new Pt(13, 22)], pants, pants.Scale(45));
        c.FillAndOutline([new Pt(16, 17), new Pt(19, 17), new Pt(19, 22), new Pt(16, 22)], pants, pants.Scale(45));
        c.FillAndOutline([new Pt(9, 10), new Pt(12, 10), new Pt(12, 18), new Pt(9, 18)], cloth.Scale(88), cloth.Scale(45));
        c.FillAndOutline([new Pt(20, 10), new Pt(23, 10), new Pt(23, 18), new Pt(20, 18)], cloth.Scale(88), cloth.Scale(45));
        c.FillRect(9, 16, 3, 3, Skin);
        c.FillRect(20, 16, 3, 3, Skin);
        c.FillAndOutline([new Pt(11, 10), new Pt(21, 10), new Pt(21, 18), new Pt(11, 18)], cloth, cloth.Scale(45));
        c.FillAndOutline([new Pt(14, 2), new Pt(18, 2), new Pt(20, 4), new Pt(20, 8), new Pt(18, 10), new Pt(14, 10), new Pt(12, 8), new Pt(12, 4)], Skin, Skin.Scale(45));
        if (hair is Rgba h)
        {
            c.FillPolygon([new Pt(13, 4), new Pt(14, 3), new Pt(18, 3), new Pt(19, 4), new Pt(19, 6), new Pt(13, 6)], h);
        }

        c.Set(14, 7, Black);
        c.Set(17, 7, Black);
    }

    // A tool shaft 2 px wide from y0 (top) to y1 (bottom).
    private static void Shaft(Canvas c, int x, int y0, int y1)
    {
        c.FillRect(x, y0, 2, y1 - y0 + 1, Haft);
        c.FillRect(x + 1, y0, 1, y1 - y0 + 1, Haft.Scale(70));
    }

    // Carrier: brown tunic and a huge wicker basket on the back, red headband.
    private static void Carrier(Canvas c)
    {
        Rgba basket = Rgba.Hex(0xD9A441);
        c.FillAndOutline([new Pt(20, 6), new Pt(30, 6), new Pt(30, 19), new Pt(20, 19)], basket, basket.Scale(45));
        for (int y = 9; y < 19; y += 3)
        {
            c.FillRect(21, y, 8, 1, basket.Scale(70));
        }

        c.FillRect(22, 3, 6, 3, Rgba.Hex(0x7FB069));
        Person(c, Rgba.Hex(0x8B5E34), Rgba.Hex(0x5A4630), Rgba.Hex(0x3B2A1A));
        c.FillRect(12, 4, 8, 2, Rgba.Hex(0xD7263D));
        c.FillRect(20, 12, 2, 5, basket.Scale(70));
    }

    // Lumberjack: red tunic, beard, big axe with a steel blade.
    private static void Lumberjack(Canvas c)
    {
        Person(c, Rgba.Hex(0xC0392B), Rgba.Hex(0x3C4A5C), Rgba.Hex(0x6B3A1E));
        c.FillRect(14, 8, 4, 2, Rgba.Hex(0x6B3A1E));
        c.FillRect(11, 14, 10, 1, Rgba.Hex(0x1E1E22));
        Shaft(c, 25, 5, 19);
        c.FillAndOutline([new Pt(23, 3), new Pt(30, 3), new Pt(30, 11), new Pt(23, 9)], Steel, Steel.Scale(45));
        c.FillRect(24, 4, 6, 1, Steel.Shift(30));
    }

    // Stonemason: light grey tunic with a leather apron, hammer with a square head, a stone block beside the feet.
    private static void Stonemason(Canvas c)
    {
        Person(c, Rgba.Hex(0xB9B6AE), Rgba.Hex(0x5C5F6A), Rgba.Hex(0x7A7A7A));
        c.FillAndOutline([new Pt(13, 13), new Pt(19, 13), new Pt(19, 21), new Pt(13, 21)], Rgba.Hex(0x7C521F), Rgba.Hex(0x4A3014));
        Shaft(c, 25, 8, 19);
        Rgba iron = Rgba.Hex(0x4A4D55);
        c.FillAndOutline([new Pt(22, 3), new Pt(30, 3), new Pt(30, 9), new Pt(22, 9)], iron, Black);
        c.FillRect(23, 4, 6, 1, iron.Shift(40));
        Rgba block = Rgba.Hex(0x9A9DA3);
        c.FillAndOutline([new Pt(2, 21), new Pt(9, 21), new Pt(9, 26), new Pt(2, 26)], block, block.Scale(45));
        c.FillRect(2, 21, 7, 1, block.Shift(30));
    }

    // Farmer: green tunic, wide straw hat, hoe (shaft with a blade bent at the top).
    private static void Farmer(Canvas c)
    {
        Person(c, Rgba.Hex(0x5BA24A), Rgba.Hex(0x7A6240), Rgba.Hex(0x5B3A1E));
        Rgba straw = Rgba.Hex(0xE8C95A);
        c.FillAndOutline([new Pt(8, 5), new Pt(12, 2), new Pt(20, 2), new Pt(24, 5), new Pt(24, 6), new Pt(8, 6)], straw, straw.Scale(55));
        c.FillRect(12, 0, 8, 3, straw.Shift(-16));
        Shaft(c, 25, 6, 19);
        c.FillAndOutline([new Pt(25, 5), new Pt(30, 6), new Pt(30, 10), new Pt(27, 10), new Pt(27, 7), new Pt(25, 7)], Steel.Scale(80), Steel.Scale(40));
    }

    // Miner: dark tunic, yellow helmet with a lamp, pickaxe with a curved steel head.
    private static void Miner(Canvas c)
    {
        Person(c, Rgba.Hex(0x4B4F63), Rgba.Hex(0x2C2F3B), null);
        Rgba helmet = Rgba.Hex(0xF2C230);
        c.FillAndOutline([new Pt(12, 6), new Pt(12, 3), new Pt(14, 1), new Pt(18, 1), new Pt(20, 3), new Pt(20, 6)], helmet, helmet.Scale(50));
        c.FillRect(11, 5, 10, 2, helmet.Scale(85));
        c.FillRect(15, 2, 2, 2, White);
        Shaft(c, 25, 7, 19);
        c.FillAndOutline([new Pt(20, 6), new Pt(25, 3), new Pt(30, 6), new Pt(30, 8), new Pt(25, 6), new Pt(20, 8)], Steel, Steel.Scale(45));
        c.FillRect(11, 14, 10, 1, Rgba.Hex(0xE8892B));
    }

    // Guard: steel helmet with a red plume, dark red tabard, round-ish shield on the left, spear on the right.
    private static void Guard(Canvas c)
    {
        Person(c, Rgba.Hex(0x7E2A2F), Rgba.Hex(0x30343F), null);
        c.FillRect(15, 10, 2, 8, Rgba.Hex(0xE8C95A));
        c.FillAndOutline([new Pt(12, 6), new Pt(12, 3), new Pt(14, 1), new Pt(18, 1), new Pt(20, 3), new Pt(20, 6)], Steel, Steel.Scale(45));
        c.FillRect(15, 6, 2, 2, Steel.Scale(70));
        c.FillRect(15, 0, 2, 2, Rgba.Hex(0xD7263D));
        Shaft(c, 26, 3, 24);
        c.FillAndOutline([new Pt(26, 0), new Pt(28, 5), new Pt(26, 5)], Steel, Steel.Scale(45));
        Rgba shield = Rgba.Hex(0x2F5DA8);
        c.FillAndOutline([new Pt(1, 11), new Pt(11, 11), new Pt(11, 19), new Pt(6, 24), new Pt(1, 19)], shield, Steel.Scale(60));
        c.FillRect(5, 12, 2, 10, Rgba.Hex(0xF2C230));
        c.FillRect(2, 15, 8, 2, Rgba.Hex(0xF2C230));
    }

    // Specialist: long purple robe with a white collar, pointed hat with a star, an open book.
    private static void Specialist(Canvas c)
    {
        Rgba robe = Rgba.Hex(0x7B4FB0);
        c.FillAndOutline([new Pt(10, 10), new Pt(22, 10), new Pt(24, 25), new Pt(8, 25)], robe, robe.Scale(45));
        c.FillAndOutline([new Pt(8, 11), new Pt(11, 11), new Pt(11, 18), new Pt(8, 18)], robe.Scale(85), robe.Scale(45));
        c.FillAndOutline([new Pt(21, 11), new Pt(24, 11), new Pt(24, 18), new Pt(21, 18)], robe.Scale(85), robe.Scale(45));
        c.FillRect(8, 17, 3, 2, Skin);
        c.FillRect(21, 17, 3, 2, Skin);
        c.FillRect(13, 10, 6, 2, White);
        c.FillRect(15, 12, 2, 12, Rgba.Hex(0xF2C230));
        c.FillAndOutline([new Pt(14, 2), new Pt(18, 2), new Pt(20, 4), new Pt(20, 8), new Pt(18, 10), new Pt(14, 10), new Pt(12, 8), new Pt(12, 4)], Skin, Skin.Scale(45));
        Rgba hat = Rgba.Hex(0x4B2E83);
        c.FillAndOutline([new Pt(16, 0), new Pt(21, 5), new Pt(11, 5)], hat, hat.Scale(50));
        c.FillRect(10, 4, 12, 2, hat);
        c.FillRect(15, 2, 2, 2, Rgba.Hex(0xF2C230));
        c.Set(14, 7, Black);
        c.Set(17, 7, Black);
        Rgba book = Rgba.Hex(0xE9E2CF);
        c.FillAndOutline([new Pt(3, 16), new Pt(10, 16), new Pt(10, 22), new Pt(3, 22)], book, Rgba.Hex(0x7C521F));
        c.FillRect(6, 16, 1, 6, Rgba.Hex(0x7C521F));
    }

    // Unemployed: faded grey-beige rags with patches and a ragged hem, empty hands, a small bundle on a stick.
    private static void Unemployed(Canvas c)
    {
        Person(c, Rgba.Hex(0x9C9684), Rgba.Hex(0x6E6A5C), Rgba.Hex(0x8A8A8A));
        c.FillRect(12, 12, 3, 3, Rgba.Hex(0x7F9B7A));
        c.FillRect(17, 15, 3, 2, Rgba.Hex(0xB07A5A));
        for (int x = 11; x < 21; x += 2)
        {
            c.Set(x, 18, new Rgba(0, 0, 0, 0));
        }

        Shaft(c, 4, 8, 22);
        Rgba cloth = Rgba.Hex(0xC9B98A);
        c.FillAndOutline([new Pt(1, 3), new Pt(8, 3), new Pt(8, 9), new Pt(1, 9)], cloth, cloth.Scale(50));
    }

    // Sapling: a thin stem with leaf pairs in a small soil mound.
    private static void Sapling(Canvas c)
    {
        Rgba soil = Rgba.Hex(0x6B4A2B);
        c.FillAndOutline([new Pt(10, 24), new Pt(13, 22), new Pt(19, 22), new Pt(22, 24), new Pt(19, 26), new Pt(13, 26)], soil, soil.Scale(55));
        Rgba stem = Rgba.Hex(0x7A5A33);
        c.FillRect(15, 12, 2, 11, stem);
        Rgba g1 = Rgba.Hex(0x4C9A4F);
        Rgba g2 = Rgba.Hex(0x6BBF6A);
        c.FillAndOutline([new Pt(16, 18), new Pt(23, 14), new Pt(24, 17), new Pt(17, 21)], g1, g1.Scale(50));
        c.FillAndOutline([new Pt(16, 18), new Pt(9, 14), new Pt(8, 17), new Pt(15, 21)], g1, g1.Scale(50));
        c.FillAndOutline([new Pt(16, 12), new Pt(21, 6), new Pt(22, 10), new Pt(17, 15)], g2, g2.Scale(50));
        c.FillAndOutline([new Pt(16, 12), new Pt(11, 6), new Pt(10, 10), new Pt(15, 15)], g2, g2.Scale(50));
        c.FillAndOutline([new Pt(14, 9), new Pt(16, 4), new Pt(18, 9), new Pt(16, 12)], g2.Shift(14), g2.Scale(50));
    }

    // Stump: a cut trunk with a light top with rings, short roots.
    private static void Stump(Canvas c)
    {
        Rgba bark = Rgba.Hex(0x6B4A2B);
        Rgba top = Rgba.Hex(0xC8A06A);
        c.FillAndOutline([new Pt(8, 25), new Pt(11, 22), new Pt(11, 17), new Pt(21, 17), new Pt(21, 22), new Pt(24, 25), new Pt(20, 26), new Pt(16, 24), new Pt(12, 26)], bark, bark.Scale(50));
        c.FillRect(13, 19, 1, 4, bark.Scale(75));
        c.FillRect(18, 19, 1, 5, bark.Scale(75));
        c.FillAndOutline([new Pt(10, 17), new Pt(13, 14), new Pt(19, 14), new Pt(22, 17), new Pt(19, 20), new Pt(13, 20)], top, bark.Scale(55));
        c.Outline([new Pt(13, 17), new Pt(15, 15), new Pt(18, 15), new Pt(20, 17), new Pt(18, 19), new Pt(15, 19)], top.Scale(75));
        c.FillRect(15, 16, 2, 2, top.Scale(60));
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
