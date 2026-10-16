namespace ArtGen;

/// <summary>
/// Road overlays: 32x16 diamonds drawn on top of the ground tile of a map cell (atlas <c>tiles</c>, kind <c>overlay</c>).
/// <para>
/// Ids are <c>trail_v0..3</c> and <c>road_v0..3</c>; the variant is a shape variation (pick it by a coordinate hash).
/// Every cell is 32x16 with origin (16, 8), the centre of the top face, and <c>height</c> 0. The renderer puts the origin on
/// the ground centre of the cell and lifts it exactly like the ground tile: 8 px up per height step (the same alternative
/// tile / texture-origin shift the ground uses). The art covers only the inside of the diamond and keeps a transparent rim,
/// so the ground edge and the neighbouring tiles stay visible. A road tile is nearly opaque over its whole top face, so
/// adjacent road tiles read as one continuous surface; a trail is a half-transparent worn patch that lets the ground show through.
/// </para>
/// </summary>
public static class OverlayArt
{
    /// <summary>Overlay kinds in atlas order.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["trail", "road"];

    /// <summary>Shape variants per kind.</summary>
    public const int Variants = 4;

    /// <summary>Cell width.</summary>
    public const int Width = 32;

    /// <summary>Cell height.</summary>
    public const int Height = 16;

    /// <summary>Origin: the centre of the diamond.</summary>
    public static readonly Pt Origin = new(16, 8);

    /// <summary>Cell id, e.g. <c>road_v2</c>.</summary>
    public static string CellId(string kind, int variant) => $"{kind}_v{variant}";

    /// <summary>Draws one overlay cell.</summary>
    public static Canvas Draw(string kind, int variant)
    {
        int kindIndex = kind switch { "trail" => 0, "road" => 1, _ => throw new ArgumentException("Unknown overlay " + kind, nameof(kind)) };
        Canvas c = new(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                // Diamond metric: 32 on the rim, 0 in the centre.
                int m = Math.Abs((2 * x) + 1 - Width) + (2 * Math.Abs((2 * y) + 1 - Height));
                uint h = TileArt.Hash((uint)(kindIndex + 11), (uint)variant, (uint)((y * Width) + x));
                Rgba p = kindIndex == 0 ? Trail(m, h, variant) : Road(m, h);
                if (p.A != 0)
                {
                    c.Set(x, y, p);
                }
            }
        }

        return c;
    }

    // Worn earth: a dense core and ragged half-transparent patches around it; the variant shifts the ragged edge.
    private static Rgba Trail(int m, uint h, int variant)
    {
        Rgba earth = Rgba.Hex(0x9A7B52);
        int reach = 20 + (int)(h % 7) + (variant % 2);
        if (m > reach)
        {
            return default;
        }

        if (m <= 12)
        {
            return (h >> 5) % 5 == 0 ? earth.Scale(82) with { A = 200 } : earth with { A = 190 };
        }

        return (h >> 3) % 3 == 0 ? default : earth.Scale(90) with { A = 110 };
    }

    // Packed road: opaque light surface, darker rim, pebbles and worn streaks; the transparent rim is 1 px wide.
    private static Rgba Road(int m, uint h)
    {
        Rgba packed = Rgba.Hex(0xCBB88C);
        if (m > 30)
        {
            return default;
        }

        if (m > 27)
        {
            return packed.Scale(72);
        }

        return (h % 11) switch
        {
            0 => packed.Scale(80),
            1 => packed.Shift(18),
            2 => Rgba.Hex(0x8E9094),
            _ => packed,
        };
    }
}
