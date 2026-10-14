using Core.Sim;

namespace Core.Tests.Sim;

public sealed class SeedTextTests
{
    [Fact]
    [Trait("Req", "FR-SAVE-05")]
    public void NumbersAreUsedAsIs()
    {
        Assert.True(SeedText.TryParse(" 12345 ", out ulong seed));
        Assert.Equal(12345UL, seed);
        Assert.True(SeedText.TryParse("18446744073709551615", out seed));
        Assert.Equal(ulong.MaxValue, seed);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-05")]
    public void TextHashesToAFixedSeed()
    {
        Assert.True(SeedText.TryParse("hello", out ulong a));
        Assert.True(SeedText.TryParse("hello", out ulong b));
        Assert.Equal(a, b);
        Assert.Equal(0xA430D84680AABD0BUL, a); // FNV-1a 64 of "hello"
        Assert.True(SeedText.TryParse("-5", out ulong negative));
        Assert.True(SeedText.TryParse("18446744073709551616", out ulong overflow));
        Assert.NotEqual(negative, overflow);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-05")]
    public void EmptyTextIsRejected()
    {
        Assert.False(SeedText.TryParse("", out _));
        Assert.False(SeedText.TryParse("  ", out _));
        Assert.False(SeedText.TryParse(null, out _));
    }
}
