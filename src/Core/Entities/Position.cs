using Core.Determinism;

namespace Core.Entities;

/// <summary>
/// Position in fixed point: 1/256 of a tile per unit (ADR 0005). Game converts to float only for rendering.
/// </summary>
/// <param name="X">X in 1/256 tile.</param>
/// <param name="Y">Y in 1/256 tile.</param>
public readonly record struct Position(int X, int Y) : IComponent
{
    /// <summary>Fixed-point units per tile (a format constant from ADR 0005, not a balance value).</summary>
    public const int UnitsPerTile = 256;

    /// <summary>Tile column (floor division, also correct for negative coordinates).</summary>
    public int TileX => FloorDiv(X);

    /// <summary>Tile row (floor division, also correct for negative coordinates).</summary>
    public int TileY => FloorDiv(Y);

    /// <summary>Position of the corner of a tile.</summary>
    public static Position FromTile(int tileX, int tileY) => new(checked(tileX * UnitsPerTile), checked(tileY * UnitsPerTile));

    /// <summary>Writes X, then Y, as Int32.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(X);
        writer.WriteInt32(Y);
    }

    private static int FloorDiv(int value)
    {
        int quotient = value / UnitsPerTile;
        return value % UnitsPerTile < 0 ? quotient - 1 : quotient;
    }
}
