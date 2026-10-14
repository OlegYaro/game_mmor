using Core.Determinism;

namespace Core.Tests.Determinism;

// Expected values come from Vigna's reference splitmix64.c (prng.di.unimi.it), transcribed independently into a
// Python script with explicit 64-bit masking, not derived from the C# code. The seed-0 sequence
// (E220A8397B1DCDAF, 6E789E6AA1B965F4, 06C45D188009454F, ...) also matches values widely published for that code.
public sealed class SplitMix64Tests
{
    [Fact]
    [Trait("Req", "NFR-04")]
    public void SeedZeroMatchesReferenceImplementation()
    {
        var rng = new SplitMix64(0);

        Assert.Equal(0xE220A8397B1DCDAFUL, rng.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, rng.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, rng.NextUInt64());
        Assert.Equal(0xF88BB8A8724C81ECUL, rng.NextUInt64());
        Assert.Equal(0x1B39896A51A8749BUL, rng.NextUInt64());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void NonZeroSeedMatchesReferenceImplementation()
    {
        var rng = new SplitMix64(1234567);

        Assert.Equal(0x599ED017FB08FC85UL, rng.NextUInt64());
        Assert.Equal(0x2C73F08458540FA5UL, rng.NextUInt64());
        Assert.Equal(0x883EBCE5A3F27C77UL, rng.NextUInt64());
        Assert.Equal(0x3FBEF740E9177B3FUL, rng.NextUInt64());
        Assert.Equal(0xE3B8346708CB5ECDUL, rng.NextUInt64());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void StateAdvancesByGammaAndMixIsTheOutputFunction()
    {
        var rng = new SplitMix64(1234567);

        ulong first = rng.NextUInt64();

        Assert.Equal(1234567UL + SplitMix64.Gamma, rng.State);
        Assert.Equal(SplitMix64.Mix(rng.State), first);
        Assert.Equal(0UL, SplitMix64.Mix(0));
    }
}
