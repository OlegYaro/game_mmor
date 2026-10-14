namespace ArtGen;

/// <summary>Composes a sample isometric scene from the atlas, to eyeball the style and the scale.</summary>
public static class Preview
{
    private const int N = 24;
    private const int Scale = 3;

    /// <summary>Renders the sample scene at 1:1 pixel scale.</summary>
    public static Canvas Render(ArtOutput art)
    {
        Dictionary<string, AtlasCell> byId = art.Cells.ToDictionary(c => c.Id, StringComparer.Ordinal);
        int[,] heights = new int[N, N];
        const int hillX = 12;
        const int hillY = 12;
        for (int x = 0; x < N; x++)
        {
            for (int y = 0; y < N; y++)
            {
                int d = Math.Max(Math.Abs(x - hillX), Math.Abs(y - hillY));
                heights[x, y] = d <= 1 ? 3 : d <= 3 ? 2 : d <= 5 ? 1 : 0;
            }
        }

        int tw = TileArt.TileWidth;
        int th = TileArt.TileHeight;
        int margin = 4 * tw;
        Canvas map = new((tw * N) + margin, (th * N / 2 * 2) + 140);
        map.FillRect(0, 0, map.Width, map.Height, new Rgba(30, 32, 38));
        int originX = (N * tw / 2) + (margin / 2) - (tw / 2);
        int originY = 40;

        Canvas hero = Hero();
        List<string> log = [];
        for (int sum = 0; sum <= 2 * (N - 1); sum++)
        {
            for (int x = 0; x < N; x++)
            {
                int y = sum - x;
                if (y < 0 || y >= N)
                {
                    continue;
                }

                uint h = TileArt.Hash((uint)x, (uint)y, 7);
                int height = heights[x, y];
                string kind;
                bool lake = x is >= 3 and <= 6 && y is >= 17 and <= 20;
                bool river = !lake && (y == 9 || (y == 10 && x >= 16)) && true;
                if (lake)
                {
                    kind = "water";
                    height = 0;
                }
                else if (river)
                {
                    kind = "river";
                }
                else if (height >= 2)
                {
                    kind = "mountain";
                }
                else if (x < 8 && y > 11)
                {
                    kind = "forest";
                }
                else if (x > 17 && y > 17)
                {
                    kind = "wasteland";
                }
                else
                {
                    kind = "meadow";
                }

                AtlasCell tile = byId[$"{kind}_h{height}_v{h % 3}"];
                int cx = originX + (tw * (x - y) / 2) + (tw / 2);
                int cy = originY + (th * (x + y) / 2) + (th / 2);
                map.Blit(art.Tiles, tile.X, tile.Y, tile.W, tile.H, cx - tile.OriginX, cy - tile.OriginY);

                string? obj = Pick(x, y, kind, height, h);
                int lift = TileArt.HeightStep * height;
                if (obj is not null)
                {
                    AtlasCell o = byId[obj];
                    map.Blit(art.Objects, o.X, o.Y, o.W, o.H, cx - o.OriginX, cy - o.OriginY - lift);
                }

                if ((x, y) is (6, 5) or (17, 8) or (12, 12))
                {
                    map.Blit(hero, 0, 0, hero.Width, hero.Height, cx - 8, cy - 23 - lift);
                }
            }
        }

        _ = log;
        return map;
    }

    /// <summary>Nearest-neighbour enlargement, used for the human-readable preview.</summary>
    public static Canvas Enlarge(Canvas src, int factor = Scale)
    {
        Canvas dst = new(src.Width * factor, src.Height * factor);
        for (int y = 0; y < dst.Height; y++)
        {
            for (int x = 0; x < dst.Width; x++)
            {
                dst.Set(x, y, src.Get(x / factor, y / factor));
            }
        }

        return dst;
    }

    private static string? Pick(int x, int y, string kind, int height, uint h)
    {
        string? fixedObj = (x, y) switch
        {
            (5, 5) => "start_marker",
            (16, 16) => "poi_chest",
            (20, 6) => "poi_ruins",
            (2, 14) => "poi_lair",
            (9, 20) => "settlement_site",
            (8, 5) => "rock",
            (13, 11) => "ore",
            (14, 7) => "berry_bush",
            (18, 8) => "tree",
            _ => null,
        };
        if (fixedObj is not null)
        {
            return fixedObj;
        }

        // Keep the surroundings of the fixed objects free so their icons and labels can be judged.
        (int, int)[] anchors = [(5, 5), (16, 16), (20, 6), (2, 14), (9, 20)];
        if (anchors.Any(a => Math.Abs(a.Item1 - x) <= 2 && Math.Abs(a.Item2 - y) <= 2))
        {
            return null;
        }

        if (kind is "water" or "river" || height >= 3 || (x, y) is (6, 5) or (17, 8) or (12, 12))
        {
            return null;
        }

        return kind switch
        {
            "forest" => h % 3 != 0 ? "tree" : null,
            "meadow" => h % 11 == 0 ? "tree" : h % 17 == 1 ? "rock" : null,
            "mountain" => h % 5 == 0 ? "rock" : null,
            _ => null,
        };
    }

    // A 16x24 stand-in hero (about 2x2 small tiles of footprint); origin at the feet (8, 23).
    private static Canvas Hero()
    {
        Canvas c = new(16, 24);
        Rgba skin = Rgba.Hex(0xF1C27D);
        Rgba cloth = Rgba.Hex(0x3A6EEA);
        Rgba legs = Rgba.Hex(0x39476B);
        c.FillAndOutline([new Pt(5, 1), new Pt(11, 1), new Pt(11, 8), new Pt(5, 8)], skin, skin.Scale(45));
        c.FillAndOutline([new Pt(3, 8), new Pt(13, 8), new Pt(13, 17), new Pt(3, 17)], cloth, cloth.Scale(45));
        c.FillAndOutline([new Pt(4, 17), new Pt(8, 17), new Pt(8, 24), new Pt(4, 24)], legs, legs.Scale(45));
        c.FillAndOutline([new Pt(8, 17), new Pt(12, 17), new Pt(12, 24), new Pt(8, 24)], legs, legs.Scale(45));
        return c;
    }
}
