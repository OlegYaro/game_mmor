namespace Core.Buildings;

/// <summary>
/// The territory of a settlement (FR-BUILD-06, A1): every tile whose Euclidean distance from the center tile is at
/// most <paramref name="Radius"/> (exact integer test <c>dx² + dy² ≤ r²</c>). The radius comes from the era
/// (<see cref="Content.EraDef.TerritoryRadius"/>, <see cref="Construction.CurrentEra"/>).
/// </summary>
/// <param name="CenterX">Column of the center tile (the center building's middle tile).</param>
/// <param name="CenterY">Row of the center tile.</param>
/// <param name="Radius">Radius in tiles.</param>
public readonly record struct Territory(int CenterX, int CenterY, int Radius)
{
    /// <summary>True when tile (<paramref name="x"/>, <paramref name="y"/>) is inside.</summary>
    public bool Contains(int x, int y)
    {
        long dx = (long)x - CenterX;
        long dy = (long)y - CenterY;
        return (dx * dx) + (dy * dy) <= (long)Radius * Radius;
    }

    /// <summary>
    /// True when every tile of the <paramref name="width"/> × <paramref name="height"/> rectangle with top-left tile
    /// (<paramref name="x"/>, <paramref name="y"/>) is inside. A disc is convex, so the four corner tiles decide.
    /// </summary>
    public bool ContainsArea(int x, int y, int width, int height)
    {
        int right = x + width - 1;
        int bottom = y + height - 1;
        return Contains(x, y) && Contains(right, y) && Contains(x, bottom) && Contains(right, bottom);
    }
}
