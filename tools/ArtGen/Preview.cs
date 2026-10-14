namespace ArtGen;

/// <summary>Composes a small isometric sample map from the atlas, to eyeball the style.</summary>
public static class Preview
{
    /// <summary>Renders an 8x8 sample map.</summary>
    public static Canvas Render(ArtOutput art)
    {
        Dictionary<string, AtlasCell> byId = art.Cells.ToDictionary(c => c.Id, StringComparer.Ordinal);
        const int n = 8;
        Canvas map = new(32 * 2 * n + 64, 16 * 2 * n + 160);
        map.FillRect(0, 0, map.Width, map.Height, new Rgba(30, 32, 38));
        int originX = 32 * n;
        int originY = 80;

        string[] biomes = ["meadow", "forest", "mountain", "wasteland"];
        for (int sum = 0; sum <= 2 * (n - 1); sum++)
        {
            for (int x = 0; x < n; x++)
            {
                int y = sum - x;
                if (y < 0 || y >= n)
                {
                    continue;
                }

                uint h = TileArt.Hash((uint)x, (uint)y, 7);
                string kind;
                int height;
                if (x is >= 5 and <= 6 && y is >= 1 and <= 3)
                {
                    kind = "water";
                    height = 0;
                }
                else if (y == 5)
                {
                    kind = "river";
                    height = 0;
                }
                else
                {
                    kind = biomes[(x / 4) + (2 * (y / 4))];
                    height = x + y < 4 ? 0 : (int)(TileArt.Hash((uint)(x / 3), (uint)(y / 3), 9) % 4);
                }

                AtlasCell tile = byId[$"{kind}_h{height}_v{h % 3}"];
                int cx = originX + (32 * (x - y));
                int cy = originY + (16 * (x + y)) + 16;
                map.Blit(art.Tiles, tile.X, tile.Y, tile.W, tile.H, cx - tile.OriginX, cy - tile.OriginY);

                string? obj = (x, y) switch
                {
                    (0, 0) => "start_marker",
                    (1, 1) => "tree",
                    (2, 0) => "tree",
                    (3, 2) => "rock",
                    (4, 4) => "ore",
                    (2, 3) => "berry_bush",
                    (6, 6) => "poi_chest",
                    (7, 3) => "poi_ruins",
                    (1, 6) => "poi_lair",
                    (3, 7) => "settlement_site",
                    _ => null,
                };
                if (obj is not null)
                {
                    AtlasCell o = byId[obj];
                    int lift = 16 * height;
                    map.Blit(art.Objects, o.X, o.Y, o.W, o.H, cx - o.OriginX, cy - o.OriginY - lift);
                }
            }
        }

        return map;
    }
}
