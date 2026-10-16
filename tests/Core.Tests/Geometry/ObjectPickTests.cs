using Core.Geometry;

namespace Core.Tests.Geometry;

/// <summary>Picking an object sprite that rises above its tile (a click on a crown, FR-HERO-02).</summary>
public sealed class ObjectPickTests
{
    private const int Size = 64;

    private static bool Pick(int px, int py, Func<int, int, int> height, HashSet<(int, int)> objects, out (int X, int Y) tile) =>
        IsoProjection.PickObject(px, py, Size, height, (x, y) => objects.Contains((x, y)), out tile);

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void AClickOnTheCrownOfATreeOnFlatGroundPicksTheTree()
    {
        HashSet<(int, int)> trees = [(20, 20)];
        (int cx, int cy) = IsoProjection.TileCenter(20, 20);

        // The crown top lies 20 px above the centre; the ground pick there is a tile far behind the tree.
        (int gx, int gy) = IsoProjection.TileAtPixel(cx, cy - 20);
        Assert.NotEqual((20, 20), (gx, gy));
        Assert.True(Pick(cx, cy - 20, (_, _) => 0, trees, out (int X, int Y) tile));
        Assert.Equal((20, 20), tile);
    }

    [Theory]
    [Trait("Req", "FR-HERO-02")]
    [InlineData(0, 3)]
    [InlineData(0, 21)]
    [InlineData(12, -4)]
    [InlineData(-12, 0)]
    public void ClicksInsideTheSpriteRectangleHit(int dx, int dy)
    {
        HashSet<(int, int)> trees = [(30, 25)];
        (int cx, int cy) = IsoProjection.TileCenter(30, 25);

        Assert.True(Pick(cx + dx, cy - dy, (_, _) => 0, trees, out (int X, int Y) tile));
        Assert.Equal((30, 25), tile);
    }

    [Theory]
    [Trait("Req", "FR-HERO-02")]
    [InlineData(13, 0)]
    [InlineData(-13, 0)]
    [InlineData(0, 26)]
    [InlineData(0, -6)]
    public void ClicksOutsideTheSpriteRectangleMiss(int dx, int dy)
    {
        HashSet<(int, int)> trees = [(30, 25)];
        (int cx, int cy) = IsoProjection.TileCenter(30, 25);

        // A pixel beside or beyond the sprite must not report this tree (no other objects exist).
        Assert.False(Pick(cx + dx, cy - dy, (_, _) => 0, trees, out _));
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void ATreeOnRaisedGroundIsPickedByItsLiftedCrown()
    {
        // The tree stands on a height-1 tile with height-0 ground in front, so its crown reaches 8 px higher.
        HashSet<(int, int)> trees = [(40, 40)];
        int Height(int x, int y) => (x, y) == (40, 40) ? 1 : 0;
        (int cx, int cy) = IsoProjection.TileCenter(40, 40, 1);

        Assert.True(Pick(cx, cy - 22, Height, trees, out (int X, int Y) tile));
        Assert.Equal((40, 40), tile);
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void ATreeOnTheHighestGroundIsStillPickedByItsCrownTop()
    {
        HashSet<(int, int)> trees = [(40, 40)];
        int Height(int x, int y) => (x, y) == (40, 40) ? 3 : 0;
        (int cx, int cy) = IsoProjection.TileCenter(40, 40, 3);

        Assert.True(Pick(cx, cy - 23, Height, trees, out (int X, int Y) tile));
        Assert.Equal((40, 40), tile);
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void AnObjectHiddenByFogIsNotPicked()
    {
        HashSet<(int, int)> visible = [];
        (int cx, int cy) = IsoProjection.TileCenter(20, 20);

        // hasObject reports false for unexplored tiles, so the tree behind the fog cannot catch the click.
        Assert.False(Pick(cx, cy - 10, (_, _) => 0, visible, out _));
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void WhenTwoSpritesCoverThePixelTheNearerToTheCameraWins()
    {
        // Tree B stands one diagonal step (16 px) in front of tree A; the pixel at A's centre is inside both sprites.
        HashSet<(int, int)> trees = [(20, 20), (21, 21)];
        (int bx, int by) = IsoProjection.TileCenter(21, 21);
        (int ax, int ay) = IsoProjection.TileCenter(20, 20);
        Assert.Equal(ax, bx);
        Assert.InRange(ay, by - IsoProjection.ObjectHitAbove, by + IsoProjection.ObjectHitBelow);

        Assert.True(Pick(ax, ay, (_, _) => 0, trees, out (int X, int Y) tile));
        Assert.Equal((21, 21), tile);
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void TilesOutsideTheMapAreSkipped()
    {
        HashSet<(int, int)> trees = [(Size - 1, Size - 1)];
        (int cx, int cy) = IsoProjection.TileCenter(Size - 1, Size - 1);

        Assert.True(Pick(cx, cy - 10, (_, _) => 0, trees, out (int X, int Y) tile));
        Assert.Equal((Size - 1, Size - 1), tile);
        // A pixel whose front candidates would lie beyond the map edge still resolves without throwing.
        Assert.False(Pick(cx, cy + 200, (_, _) => 0, trees, out _));
    }

    [Fact]
    [Trait("Req", "FR-HERO-02")]
    public void NullDelegatesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => IsoProjection.PickObject(0, 0, Size, null!, (_, _) => false, out _));
        Assert.Throws<ArgumentNullException>(() => IsoProjection.PickObject(0, 0, Size, (_, _) => 0, null!, out _));
    }
}
