namespace ArtGen;

/// <summary>An RGBA colour with 8 bits per channel.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Builds a colour from 0xRRGGBB.</summary>
    public static Rgba Hex(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Multiplies the colour channels by <paramref name="percent"/>/100 (integer math).</summary>
    public Rgba Scale(int percent) => new(Clamp(R * percent / 100), Clamp(G * percent / 100), Clamp(B * percent / 100), A);

    /// <summary>Adds a signed delta to every colour channel.</summary>
    public Rgba Shift(int delta) => new(Clamp(R + delta), Clamp(G + delta), Clamp(B + delta), A);

    /// <summary>Adds separate signed deltas to the colour channels.</summary>
    public Rgba Shift(int dr, int dg, int db) => new(Clamp(R + dr), Clamp(G + dg), Clamp(B + db), A);

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}

/// <summary>A pixel position.</summary>
public readonly record struct Pt(int X, int Y);

/// <summary>RGBA pixel buffer with integer-only scanline polygon fill and Bresenham lines (no anti-aliasing).</summary>
public sealed class Canvas
{
    /// <summary>Creates a fully transparent canvas.</summary>
    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>RGBA bytes, row-major.</summary>
    public byte[] Pixels { get; }

    /// <summary>Sets a pixel; positions outside the canvas are ignored.</summary>
    public void Set(int x, int y, Rgba c)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        int i = (y * Width + x) * 4;
        Pixels[i] = c.R;
        Pixels[i + 1] = c.G;
        Pixels[i + 2] = c.B;
        Pixels[i + 3] = c.A;
    }

    /// <summary>Reads a pixel; transparent outside the canvas.</summary>
    public Rgba Get(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return new Rgba(0, 0, 0, 0);
        }

        int i = (y * Width + x) * 4;
        return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    /// <summary>Fills a rectangle of pixels <c>[x, x+w) × [y, y+h)</c>.</summary>
    public void FillRect(int x, int y, int w, int h, Rgba c)
    {
        for (int j = y; j < y + h; j++)
        {
            for (int i = x; i < x + w; i++)
            {
                Set(i, j, c);
            }
        }
    }

    /// <summary>
    /// Even-odd polygon fill. Vertices are in pixel-edge coordinates; a pixel is covered when its centre is inside.
    /// </summary>
    public void FillPolygon(IReadOnlyList<Pt> poly, Rgba c)
    {
        int n = poly.Count;
        int minY = int.MaxValue;
        int maxY = int.MinValue;
        foreach (Pt p in poly)
        {
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        List<int> xs = [];
        for (int y = minY; y < maxY; y++)
        {
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                Pt a = poly[i];
                Pt b = poly[(i + 1) % n];
                if (a.Y == b.Y)
                {
                    continue;
                }

                if (a.Y > b.Y)
                {
                    (a, b) = (b, a);
                }

                if (y < a.Y || y >= b.Y)
                {
                    continue;
                }

                // x at the pixel row centre (y + 0.5), rounded to the nearest pixel edge: all in integers.
                long dy = b.Y - a.Y;
                long num = (2L * a.X * dy) + ((long)(b.X - a.X) * ((2L * y) + 1 - (2L * a.Y)));
                xs.Add((int)FloorDiv(num + dy, 2 * dy));
            }

            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                for (int x = xs[k]; x < xs[k + 1]; x++)
                {
                    Set(x, y, c);
                }
            }
        }
    }

    /// <summary>Draws a 1px line between two pixel positions (both included).</summary>
    public void Line(int x0, int y0, int x1, int y1, Rgba c)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            Set(x0, y0, c);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>Draws a closed 1px outline through the vertices (pixel positions).</summary>
    public void Outline(IReadOnlyList<Pt> poly, Rgba c)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            Pt a = poly[i];
            Pt b = poly[(i + 1) % poly.Count];
            Line(a.X, a.Y, b.X, b.Y, c);
        }
    }

    /// <summary>
    /// Fills a polygon, then outlines it. Outline pixels sit one pixel inside the fill edge: vertices are pulled by
    /// the caller, so this simply draws the same polygon with its extreme edges moved to the last covered pixel.
    /// </summary>
    public void FillAndOutline(IReadOnlyList<Pt> poly, Rgba fill, Rgba outline)
    {
        FillPolygon(poly, fill);
        Pt[] inner = Inset(poly);
        Outline(inner, outline);
    }

    /// <summary>Copies the non-transparent pixels of <paramref name="src"/> region onto this canvas.</summary>
    public void Blit(Canvas src, int sx, int sy, int w, int h, int dx, int dy)
    {
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                Rgba p = src.Get(sx + i, sy + j);
                if (p.A != 0)
                {
                    Set(dx + i, dy + j, p);
                }
            }
        }
    }

    // Pulls vertices on the right/bottom bounding-box edge back by one pixel so the outline lands on covered pixels.
    private static Pt[] Inset(IReadOnlyList<Pt> poly)
    {
        int maxX = poly.Max(p => p.X);
        int maxY = poly.Max(p => p.Y);
        Pt[] result = new Pt[poly.Count];
        for (int i = 0; i < poly.Count; i++)
        {
            int x = poly[i].X == maxX ? poly[i].X - 1 : poly[i].X;
            int y = poly[i].Y == maxY ? poly[i].Y - 1 : poly[i].Y;
            result[i] = new Pt(x, y);
        }

        return result;
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
    }
}
