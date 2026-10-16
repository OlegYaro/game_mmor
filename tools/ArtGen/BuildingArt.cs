namespace ArtGen;

/// <summary>Footprint, level count and sprite headroom of one building; must stay in sync with data/buildings/buildings.json.</summary>
/// <param name="Id">Building id (the data file's <c>id</c>).</param>
/// <param name="Width">Footprint width in tiles.</param>
/// <param name="Height">Footprint height in tiles.</param>
/// <param name="Levels">Number of levels.</param>
/// <param name="Rise">Pixels above the footprint diamond reserved for the building body.</param>
public sealed record BuildingSpec(string Id, int Width, int Height, int Levels, int Rise);

/// <summary>
/// Building frames, four construction stages per level, all drawn on a transparent cell sized to the footprint.
/// <para>
/// Frame size: width <c>16 * (W + H)</c>, height <c>8 * (W + H) + Rise</c> (a tile is 32x16, so the footprint diamond is
/// <c>16 * (W + H)</c> wide and <c>8 * (W + H)</c> tall, with the building body standing above it).
/// Origin: the lowest pixel of the footprint diamond, centred in x, i.e. <c>(Width / 2, Height - 1)</c>. The renderer puts this
/// pixel on the bottom corner of the footprint diamond.
/// </para>
/// <para>
/// Stages: <c>s0</c> staked-out footprint over a ghost of the building; <c>s1</c> the lower third of the final silhouette plus
/// plank piles; <c>s2</c> the lower two thirds plus scaffolding; <c>s3</c> the finished building. Stages 0..2 are derived from
/// the final frame of the level.
/// </para>
/// </summary>
public static class BuildingArt
{
    /// <summary>Number of construction stages per level (<c>s0</c>..<c>s3</c>).</summary>
    public const int Stages = 4;

    /// <summary>The buildings in atlas order.</summary>
    public static readonly IReadOnlyList<BuildingSpec> All =
    [
        new("campfire", 1, 1, 1, 14),
        new("house", 2, 2, 3, 28),
        new("storage", 3, 2, 2, 20),
        new("field", 3, 3, 1, 8),
        new("sawmill", 3, 2, 1, 22),
        new("stonecutter", 2, 2, 1, 22),
    ];

    private static readonly Rgba Dirt = Rgba.Hex(0x8B7355);
    private static readonly Rgba Wood = Rgba.Hex(0x6B4A2B);
    private static readonly Rgba Stone = Rgba.Hex(0x8E9094);
    private static readonly Rgba Door = Rgba.Hex(0x3B2A1A);
    private static readonly Rgba Window = Rgba.Hex(0xBFE3F5);
    private static readonly Rgba Bark = Rgba.Hex(0x6E4B2A);
    private static readonly Rgba CutWood = Rgba.Hex(0xD9B98A);
    private static readonly Rgba Steel = Rgba.Hex(0xB8BEC6);
    private static readonly Rgba CutStone = Rgba.Hex(0xC9CBCE);

    /// <summary>Frame width in pixels.</summary>
    public static int FrameWidth(BuildingSpec b) => 16 * (b.Width + b.Height);

    /// <summary>Frame height in pixels.</summary>
    public static int FrameHeight(BuildingSpec b) => (8 * (b.Width + b.Height)) + b.Rise;

    /// <summary>Origin pixel of every frame of the building.</summary>
    public static Pt Origin(BuildingSpec b) => new(FrameWidth(b) / 2, FrameHeight(b) - 1);

    /// <summary>Frame id; the level is 1-based and the stage 0-based: <c>house_l2_s3</c>.</summary>
    public static string FrameId(string building, int level, int stage) => $"{building}_l{level}_s{stage}";

    /// <summary>Draws one frame.</summary>
    public static Canvas Draw(BuildingSpec b, int level, int stage)
    {
        Geo g = new(b);
        if (b.Id == "field")
        {
            return Field(g, stage);
        }

        Canvas final = DrawFinal(b, level);
        return stage switch
        {
            0 => Staked(final, g),
            1 => Planks(b, Crop(final, 1), g),
            2 => Scaffold(Crop(final, 2), g),
            3 => final,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
    }

    // Footprint geometry shared by every stage. Cy is the diamond's centre row, Hw/Hh its half extents.
    private readonly record struct Geo(int W, int H, int Cx, int Cy, int Hw, int Hh, int BodyHw, int BodyHh, bool Small)
    {
        public Geo(BuildingSpec b)
            : this(FrameWidth(b), FrameHeight(b), FrameWidth(b) / 2, b.Rise + (4 * (b.Width + b.Height)), FrameWidth(b) / 2, 4 * (b.Width + b.Height),
                  FrameWidth(b) / 2 * 78 / 100, 4 * (b.Width + b.Height) * 78 / 100, b.Width + b.Height <= 2)
        {
        }
    }

    private static Pt[] Diamond(int cx, int cy, int hw, int hh) => [new Pt(cx, cy - hh), new Pt(cx + hw, cy), new Pt(cx, cy + hh), new Pt(cx - hw, cy)];

    private static Pt Lerp(Pt a, Pt b, int num, int den) => new(a.X + ((b.X - a.X) * num / den), a.Y + ((b.Y - a.Y) * num / den));

    private static Pt Up(Pt p, int dy) => new(p.X, p.Y - dy);

    private static Canvas DrawFinal(BuildingSpec b, int level)
    {
        Geo g = new(b);
        Canvas c = new(g.W, g.H);
        c.FillAndOutline(Diamond(g.Cx, g.Cy, g.Hw, g.Hh), Dirt, Dirt.Scale(70));
        switch (b.Id)
        {
            case "campfire": Campfire(c, g); break;
            case "house": House(c, g, level); break;
            case "storage": Storage(c, g, level); break;
            case "sawmill": Sawmill(c, g); break;
            case "stonecutter": Stonecutter(c, g); break;
            default: throw new ArgumentException("Unknown building id " + b.Id, nameof(b));
        }

        return c;
    }

    // Field stages: s0 pegged-out plot, s1 furrows, s2 green sprouts, s3 ripe wheat. Furrows run parallel to the top-right edge.
    private static Canvas Field(Geo g, int stage)
    {
        if (stage == 0)
        {
            return Staked(new Canvas(g.W, g.H), g);
        }

        Canvas c = new(g.W, g.H);
        Rgba soil = Rgba.Hex(0x7A5A3A);
        c.FillAndOutline(Diamond(g.Cx, g.Cy, g.Hw, g.Hh), soil, soil.Scale(60));
        Pt[] d = Diamond(g.Cx, g.Cy, g.Hw - 4, g.Hh - 2);
        const int rows = 8;
        const int steps = 14;
        for (int i = 1; i < rows; i++)
        {
            Pt a = Lerp(d[0], d[3], i, rows);
            Pt z = Lerp(d[1], d[2], i, rows);
            c.Line(a.X, a.Y, z.X, z.Y, soil.Scale(62));
            c.Line(a.X, a.Y - 1, z.X, z.Y - 1, soil.Shift(22));
        }

        if (stage == 1)
        {
            return c;
        }

        Rgba sprout = Rgba.Hex(0x5BB04A);
        Rgba stalk = Rgba.Hex(0xD9B94A);
        for (int i = 1; i < rows; i++)
        {
            Pt a = Lerp(d[0], d[3], i, rows);
            Pt z = Lerp(d[1], d[2], i, rows);
            for (int u = 1; u < steps; u++)
            {
                Pt p = Lerp(a, z, u, steps);
                if (stage == 2)
                {
                    c.FillRect(p.X, p.Y - 3, 1, 3, sprout);
                    c.Set(p.X - 1, p.Y - 3, sprout.Shift(24));
                    c.Set(p.X + 1, p.Y - 3, sprout.Shift(24));
                }
                else
                {
                    c.FillRect(p.X, p.Y - 7, 1, 7, stalk.Scale(80));
                    c.FillRect(p.X, p.Y - 8, 2, 4, stalk);
                    c.Set(p.X, p.Y - 9, stalk.Shift(20));
                }
            }
        }

        return c;
    }

    private static void Campfire(Canvas c, Geo g)
    {
        // Ring of stones, crossed logs, flame.
        int[,] ring = { { -10, -1 }, { -7, 3 }, { -2, 5 }, { 3, 5 }, { 8, 3 }, { 11, -1 }, { 8, -4 }, { 3, -5 }, { -3, -5 }, { -8, -4 } };
        for (int i = 0; i < ring.GetLength(0); i++)
        {
            int x = g.Cx + ring[i, 0] - 1;
            int y = g.Cy + ring[i, 1];
            c.FillRect(x, y, 3, 2, i % 2 == 0 ? Stone : Stone.Shift(-24));
            c.FillRect(x, y, 3, 1, Stone.Shift(22));
        }

        Rgba log = Rgba.Hex(0x7C521F);
        for (int k = -1; k <= 0; k++)
        {
            c.Line(g.Cx - 7, g.Cy + 2 + k, g.Cx + 6, g.Cy - 3 + k, k == 0 ? log : log.Scale(60));
            c.Line(g.Cx - 6, g.Cy - 3 + k, g.Cx + 7, g.Cy + 2 + k, k == 0 ? log.Shift(14) : log.Scale(60));
        }

        Rgba orange = Rgba.Hex(0xE8641B);
        Rgba yellow = Rgba.Hex(0xFFD23F);
        c.FillAndOutline([new Pt(g.Cx, g.Cy - 13), new Pt(g.Cx + 5, g.Cy - 5), new Pt(g.Cx + 3, g.Cy), new Pt(g.Cx - 3, g.Cy), new Pt(g.Cx - 5, g.Cy - 5)], orange, orange.Scale(60));
        c.FillPolygon([new Pt(g.Cx, g.Cy - 9), new Pt(g.Cx + 3, g.Cy - 4), new Pt(g.Cx + 2, g.Cy - 1), new Pt(g.Cx - 2, g.Cy - 1), new Pt(g.Cx - 3, g.Cy - 4)], yellow);
    }

    private sealed record Style(int WallH, int RoofH, Rgba Wall, Rgba Roof, int StoneBase, bool Chimney, bool Windows);

    // Level I is modest (wood and thatch), II gets a stone base and tiles, III has taller walls, a larger roof and a chimney.
    private static void House(Canvas c, Geo g, int level)
    {
        Style s = level switch
        {
            1 => new Style(11, 9, Rgba.Hex(0xB08A5A), Rgba.Hex(0xC9A94E), 0, false, true),
            2 => new Style(13, 10, Rgba.Hex(0xD2BC96), Rgba.Hex(0xA8483A), 5, false, true),
            3 => new Style(14, 16, Rgba.Hex(0xDCCDB0), Rgba.Hex(0x7A3B34), 7, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
        Body(c, g, s, doorFrom: 3, doorTo: 6);
    }

    // Level I is a plank shed with two crates; II is larger, has a stone base, a slate roof and more crates.
    private static void Storage(Canvas c, Geo g, int level)
    {
        Style s = level switch
        {
            1 => new Style(10, 7, Rgba.Hex(0x9C7A4B), Rgba.Hex(0x7E6A4A), 0, false, false),
            2 => new Style(14, 8, Rgba.Hex(0xB59360), Rgba.Hex(0x5F6B7A), 5, false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
        Body(c, g, s, doorFrom: 2, doorTo: 7);

        (int Dx, int Dy, int Size)[] crates = level == 1
            ? [(-20, 7, 6), (-13, 11, 6)]
            : [(-22, 6, 7), (-14, 11, 6), (-14, 5, 5), (21, 8, 7)];
        foreach ((int dx, int dy, int size) in crates)
        {
            Crate(c, g.Cx + dx, g.Cy + dy, size);
        }
    }

    // Walls, optional stone base and a hip roof standing on the (inset) body diamond; door on the right wall, windows on the left.
    private static void Body(Canvas c, Geo g, Style s, int doorFrom, int doorTo)
    {
        Pt[] ground = Diamond(g.Cx, g.Cy, g.BodyHw, g.BodyHh);
        Pt right = ground[1];
        Pt front = ground[2];
        Pt left = ground[3];

        c.FillAndOutline([left, front, Up(front, s.WallH), Up(left, s.WallH)], s.Wall.Scale(78), s.Wall.Scale(40));
        c.FillAndOutline([front, right, Up(right, s.WallH), Up(front, s.WallH)], s.Wall, s.Wall.Scale(40));
        if (s.StoneBase > 0)
        {
            Rgba stone = Stone.Shift(14);
            c.FillPolygon([left, front, Up(front, s.StoneBase), Up(left, s.StoneBase)], stone.Scale(80));
            c.FillPolygon([front, right, Up(right, s.StoneBase), Up(front, s.StoneBase)], stone);
            c.Line(left.X, left.Y - s.StoneBase, front.X, front.Y - s.StoneBase, stone.Scale(55));
            c.Line(front.X, front.Y - s.StoneBase, right.X, right.Y - s.StoneBase, stone.Scale(60));
        }

        int doorH = Math.Min(s.WallH - 3, 9);
        Pt d0 = Lerp(front, right, doorFrom, 10);
        Pt d1 = Lerp(front, right, doorTo, 10);
        c.FillAndOutline([d0, d1, Up(d1, doorH), Up(d0, doorH)], Door, Door.Shift(30));
        if (s.Windows)
        {
            int lift = s.StoneBase + 3;
            foreach ((int from, int to) in new[] { (3, 5), (7, 9) })
            {
                Pt w0 = Lerp(left, front, from, 10);
                Pt w1 = Lerp(left, front, to, 10);
                c.FillAndOutline([Up(w0, lift), Up(w1, lift), Up(w1, lift + 4), Up(w0, lift + 4)], Window, Window.Scale(40));
            }
        }

        // Hip roof: eaves overhang the walls a little, the apex sits above the diamond centre.
        int eaveHw = Math.Min(g.Hw - 1, g.BodyHw + 5);
        int eaveHh = Math.Min(g.Hh - 1, g.BodyHh + 3);
        Pt[] eaves = Diamond(g.Cx, g.Cy - s.WallH, eaveHw, eaveHh);
        Pt apex = new(g.Cx, g.Cy - s.WallH - s.RoofH);
        c.FillAndOutline([eaves[3], eaves[0], apex], s.Roof.Scale(90), s.Roof.Scale(45));
        c.FillAndOutline([eaves[0], eaves[1], apex], s.Roof.Scale(100), s.Roof.Scale(45));
        if (s.Chimney)
        {
            c.FillAndOutline([new Pt(g.Cx + 6, apex.Y - 3), new Pt(g.Cx + 10, apex.Y - 3), new Pt(g.Cx + 10, apex.Y + 7), new Pt(g.Cx + 6, apex.Y + 7)], Stone.Scale(85), Stone.Scale(45));
        }

        c.FillAndOutline([eaves[3], eaves[2], apex], s.Roof.Scale(78), s.Roof.Scale(45));
        c.FillAndOutline([eaves[2], eaves[1], apex], s.Roof.Scale(88), s.Roof.Scale(45));
    }

    // Open shed on posts at the back of the yard; in front a saw bench with a circular blade cutting a log, a pile of
    // logs on the left and a stack of boards on the right.
    private static void Sawmill(Canvas c, Geo g)
    {
        Pt[] shed = Diamond(g.Cx + 2, g.Cy - 6, 24, 12);
        ShedBack(c, shed, 13);

        // Log pile under the left eave: three logs below, two on top, cut ends towards the viewer.
        foreach ((int dx, int dy) in new[] { (-27, 6), (-22, 8), (-17, 10), (-25, 2), (-20, 4) })
        {
            Log(c, new Pt(g.Cx + dx + 12, g.Cy + dy - 6), new Pt(g.Cx + dx, g.Cy + dy), 4);
        }

        ShedFront(c, shed, 13, 9, Rgba.Hex(0x8A6A44));

        // Saw bench along the front: a plank top on two legs, a log lying on it and the blade standing through the top.
        Pt benchLeft = new(g.Cx - 8, g.Cy + 6);
        Pt benchRight = new(g.Cx + 12, g.Cy + 16);
        foreach (Pt leg in new[] { benchLeft, benchRight })
        {
            c.FillRect(leg.X, leg.Y - 4, 2, 5, Wood.Scale(70));
        }

        for (int i = 0; i < 2; i++)
        {
            c.Line(benchLeft.X - 1, benchLeft.Y - 5 + i, benchRight.X + 2, benchRight.Y - 5 + i, i == 0 ? CutWood : CutWood.Scale(70));
        }

        Log(c, new Pt(benchLeft.X - 3, benchLeft.Y - 6), new Pt(benchLeft.X + 7, benchLeft.Y - 1), 4);
        Blade(c, new Pt(g.Cx + 5, g.Cy + 5), 6);

        // Finished boards stacked on the right.
        for (int layer = 0; layer < 4; layer++)
        {
            Rgba board = layer % 2 == 0 ? CutWood : CutWood.Shift(-18);
            Pt back = new(g.Cx + 30, g.Cy + 2 - (2 * layer));
            Pt front = new(g.Cx + 18, g.Cy + 8 - (2 * layer));
            c.Line(back.X, back.Y, front.X, front.Y, board);
            c.Line(back.X, back.Y + 1, front.X, front.Y + 1, board.Scale(70));
        }
    }

    // Open shed at the back; in front a rough stone slab with a half-cut block and a chisel, cut blocks stacked in a
    // pyramid on the left and rough stones on the right.
    private static void Stonecutter(Canvas c, Geo g)
    {
        Pt[] shed = Diamond(g.Cx + 2, g.Cy - 6, 16, 8);
        ShedBack(c, shed, 12);
        ShedFront(c, shed, 12, 7, Rgba.Hex(0x6B7380));

        // Rough stones: irregular lumps.
        foreach ((int dx, int dy, int r) in new[] { (17, 5, 3), (22, 3, 3), (20, 8, 2), (25, 6, 2) })
        {
            Disc(c, new Pt(g.Cx + dx, g.Cy + dy - r), r, Stone.Shift(-10), Stone.Scale(55));
            c.Set(g.Cx + dx - 1, g.Cy + dy - (2 * r) + 1, Stone.Shift(26));
        }

        // Cut blocks: three, two and one, back to front within each row.
        foreach ((int dx, int dy) in new[] { (-24, 6), (-20, 8), (-16, 10), (-22, 1), (-18, 3), (-20, -4) })
        {
            Cube(c, g.Cx + dx, g.Cy + dy, 8, CutStone, 62);
        }

        // Workbench: a rough slab with a half-cut block and a chisel on it.
        Cube(c, g.Cx + 2, g.Cy + 13, 10, Stone, 50);
        Cube(c, g.Cx + 1, g.Cy + 6, 6, CutStone, 62);
        c.Line(g.Cx + 5, g.Cy + 3, g.Cx + 8, g.Cy, Steel.Scale(70));
        c.Set(g.Cx + 8, g.Cy, Wood);
    }

    // Posts at the back, left and right corners of the shed ground diamond (top, right, bottom, left order).
    private static void ShedBack(Canvas c, Pt[] ground, int postH)
    {
        foreach (Pt p in new[] { ground[0], ground[3], ground[1] })
        {
            Post(c, p, postH);
        }
    }

    // The front post and a hip roof over the shed; the roof hides whatever stands under it above the eaves.
    private static void ShedFront(Canvas c, Pt[] ground, int postH, int roofH, Rgba roof)
    {
        Post(c, ground[2], postH);
        int cx = ground[0].X;
        int cy = ground[3].Y;
        int hw = ground[1].X - cx;
        int hh = cy - ground[0].Y;
        Pt[] eaves = Diamond(cx, cy - postH, hw + 3, hh + 2);
        Pt apex = new(cx, cy - postH - roofH);
        c.FillAndOutline([eaves[3], eaves[0], apex], roof.Scale(90), roof.Scale(45));
        c.FillAndOutline([eaves[0], eaves[1], apex], roof, roof.Scale(45));
        c.FillAndOutline([eaves[3], eaves[2], apex], roof.Scale(78), roof.Scale(45));
        c.FillAndOutline([eaves[2], eaves[1], apex], roof.Scale(88), roof.Scale(45));
    }

    private static void Post(Canvas c, Pt foot, int height)
    {
        c.FillRect(foot.X - 1, foot.Y - height, 1, height + 1, Wood);
        c.FillRect(foot.X, foot.Y - height, 1, height + 1, Wood.Shift(30));
    }

    // A log lying from `back` to `front`, `thick` pixels tall, its light cut end facing the viewer.
    private static void Log(Canvas c, Pt back, Pt front, int thick)
    {
        for (int i = 0; i < thick; i++)
        {
            Rgba shade = i == 0 ? Bark.Shift(26) : i == thick - 1 ? Bark.Scale(60) : Bark;
            c.Line(back.X, back.Y - thick + 1 + i, front.X, front.Y - thick + 1 + i, shade);
        }

        int r = (thick / 2) + 1;
        Pt end = new(front.X, front.Y - (thick / 2));
        Disc(c, end, r, CutWood, Bark.Scale(60));
        c.Set(end.X, end.Y, CutWood.Scale(75));
    }

    // Circular saw blade standing upright with its centre at `hub`: steel disc, darker teeth on the rim, dark hub.
    private static void Blade(Canvas c, Pt hub, int r)
    {
        Disc(c, hub, r, Steel, Steel.Scale(55));
        foreach ((int dx, int dy) in new[] { (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1) })
        {
            int len = dx != 0 && dy != 0 ? r * 3 / 4 : r;
            c.Set(hub.X + (dx * (len + 1)), hub.Y + (dy * (len + 1)), Steel.Scale(45));
        }

        c.Line(hub.X - (r / 2), hub.Y - (r / 2), hub.X + (r / 2), hub.Y + (r / 2), Steel.Shift(30));
        c.FillRect(hub.X - 1, hub.Y - 1, 2, 2, Door);
    }

    // Filled disc of radius `r` around `centre` with a 1px rim; all integer.
    private static void Disc(Canvas c, Pt centre, int r, Rgba fill, Rgba rim)
    {
        int outer = (r * r) + r;
        int inner = ((r - 1) * (r - 1)) + r - 1;
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int d = (dx * dx) + (dy * dy);
                if (d <= outer)
                {
                    c.Set(centre.X + dx, centre.Y + dy, d <= inner ? fill : rim);
                }
            }
        }
    }

    // A small cube whose bottom corner is at (x, y).
    private static void Crate(Canvas c, int x, int y, int size) => Cube(c, x, y, size, Rgba.Hex(0xA9783F), 40);

    // A small cube of `color` whose bottom corner is at (x, y): darker left face, full right face, lighter top; the
    // outline is `color` scaled to `outlinePercent`.
    private static void Cube(Canvas c, int x, int y, int size, Rgba color, int outlinePercent)
    {
        int hw = size / 2;
        int hh = Math.Max(1, size / 4);
        Pt bottom = new(x, y);
        Pt left = new(x - hw, y - hh);
        Pt right = new(x + hw, y - hh);
        Rgba outline = color.Scale(outlinePercent);
        c.FillAndOutline([left, bottom, Up(bottom, size - hh), Up(left, size - hh)], color.Scale(78), outline);
        c.FillAndOutline([bottom, right, Up(right, size - hh), Up(bottom, size - hh)], color, outline);
        Pt tb = Up(bottom, size - hh);
        c.FillAndOutline([Up(left, size - hh), new Pt(tb.X, tb.Y - hh), Up(right, size - hh), tb], color.Shift(22), outline);
    }

    // Index of the first pixel row that has any opaque pixel.
    private static int FirstRow(Canvas c)
    {
        for (int y = 0; y < c.Height; y++)
        {
            for (int x = 0; x < c.Width; x++)
            {
                if (c.Get(x, y).A != 0)
                {
                    return y;
                }
            }
        }

        return c.Height - 1;
    }

    // Keeps the bottom `thirds`/3 of the silhouette (rows from the first opaque row down to the frame bottom).
    private static Canvas Crop(Canvas final, int thirds)
    {
        int first = FirstRow(final);
        int span = final.Height - first;
        int cut = final.Height - (span * thirds / 3);
        Canvas c = new(final.Width, final.Height);
        c.Blit(final, 0, cut, final.Width, final.Height - cut, 0, cut);
        return c;
    }

    // Footprint pegged out with rope and four stakes, over a faint ghost of the finished building.
    private static Canvas Staked(Canvas final, Geo g)
    {
        Canvas c = new(final.Width, final.Height);
        for (int y = 0; y < final.Height; y++)
        {
            for (int x = 0; x < final.Width; x++)
            {
                Rgba p = final.Get(x, y);
                if (p.A != 0)
                {
                    c.Set(x, y, p with { A = (byte)(p.A * 30 / 100) });
                }
            }
        }

        c.FillPolygon(Diamond(g.Cx, g.Cy, g.Hw, g.Hh), Dirt with { A = 150 });
        Rgba rope = Rgba.Hex(0xE6D8A8);
        Pt[] ring = [new Pt(g.Cx, g.Cy - g.Hh), new Pt(g.Cx + g.Hw - 1, g.Cy), new Pt(g.Cx, g.Cy + g.Hh - 1), new Pt(g.Cx - g.Hw, g.Cy)];
        c.Outline(ring, rope);
        Rgba red = Rgba.Hex(0xD7263D);
        foreach (Pt corner in ring)
        {
            Pt tip = Up(corner, 5);
            c.Line(corner.X, corner.Y, tip.X, tip.Y, Wood);
            c.Set(tip.X, tip.Y, red);
            c.Set(tip.X, tip.Y + 1, red);
        }

        return c;
    }

    private static Canvas Planks(BuildingSpec b, Canvas c, Geo g)
    {
        int pw = 4 + (2 * (b.Width + b.Height));
        (int Dx, int Dy)[] piles = g.Small ? [(0, g.Hh / 3)] : [(-g.Hw / 3, g.Hh / 3), (g.Hw / 3, g.Hh / 4)];
        Rgba[] shades = [Rgba.Hex(0xD8B27A), Rgba.Hex(0xC59A5E), Rgba.Hex(0xB68A50)];
        foreach ((int dx, int dy) in piles)
        {
            int baseY = g.Cy + dy;
            for (int i = 0; i < 3; i++)
            {
                int x = g.Cx + dx - (pw / 2) + (i == 1 ? 1 : 0);
                int y = baseY - (2 * i) - 2;
                c.FillRect(x, y, pw, 2, shades[i]);
                c.FillRect(x, y + 1, pw, 1, shades[i].Scale(72));
                c.Set(x, y, shades[i].Scale(55));
                c.Set(x + pw - 1, y, shades[i].Scale(55));
            }
        }

        return c;
    }

    // Posts at the left, front and right corners of the body diamond, rails at the top and the middle, two diagonal braces.
    private static Canvas Scaffold(Canvas c, Geo g)
    {
        int first = FirstRow(c);
        Pt[] ground = Diamond(g.Cx, g.Cy, g.BodyHw, g.BodyHh);
        Pt[] posts = [ground[3], ground[2], ground[1]];
        int height = ground[2].Y - first + 2;
        Rgba light = Rgba.Hex(0xC9A06A);
        Pt[] tops = posts.Select(p => Up(p, height)).ToArray();
        Pt[] mids = posts.Select(p => Up(p, height / 2)).ToArray();

        c.Line(posts[0].X, posts[0].Y, tops[1].X, tops[1].Y, light.Scale(85));
        c.Line(posts[1].X, posts[1].Y, tops[2].X, tops[2].Y, light.Scale(85));
        foreach (Pt[] row in new[] { tops, mids })
        {
            c.Line(row[0].X, row[0].Y, row[1].X, row[1].Y, light);
            c.Line(row[1].X, row[1].Y, row[2].X, row[2].Y, light);
            c.Line(row[0].X, row[0].Y + 1, row[1].X, row[1].Y + 1, Wood);
            c.Line(row[1].X, row[1].Y + 1, row[2].X, row[2].Y + 1, Wood);
        }

        for (int i = 0; i < posts.Length; i++)
        {
            c.Line(posts[i].X, posts[i].Y, tops[i].X, tops[i].Y, Wood);
            c.Line(posts[i].X + 1, posts[i].Y, tops[i].X + 1, tops[i].Y, light);
        }

        return c;
    }
}
