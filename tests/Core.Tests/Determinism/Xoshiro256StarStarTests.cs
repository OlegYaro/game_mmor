using Core.Determinism;

namespace Core.Tests.Determinism;

// Expected values come from an independent Python transcription of the reference C code
// (prng.di.unimi.it: xoshiro256starstar.c, splitmix64.c) with explicit 64-bit masking. The bounded-integer
// vectors come from the same script, which implements Lemire's 32-bit nearly-divisionless method on the
// upper 32 bits of each output, as Xoshiro256StarStar does. The {1,2,3,4} sequence (11520, 0, 1509978240, ...)
// also matches values widely published for the reference code.
public sealed class Xoshiro256StarStarTests
{
    [Fact]
    [Trait("Req", "NFR-04")]
    public void RawStateMatchesReferenceImplementation()
    {
        var rng = new Xoshiro256StarStar(new Xoshiro256State(1, 2, 3, 4));

        Assert.Equal(11520UL, rng.NextUInt64());
        Assert.Equal(0UL, rng.NextUInt64());
        Assert.Equal(1509978240UL, rng.NextUInt64());
        Assert.Equal(1215971899390074240UL, rng.NextUInt64());
        Assert.Equal(1216172134540287360UL, rng.NextUInt64());
        Assert.Equal(607988272756665600UL, rng.NextUInt64());
        Assert.Equal(
            new Xoshiro256State(0xC060100412050281UL, 0x00706014140A0305UL, 0xC07030000A040007UL, 0x60306800100183C1UL),
            rng.GetState());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SeedingUsesFirstFourSplitMix64Outputs()
    {
        var rng = new Xoshiro256StarStar(42);

        Assert.Equal(
            new Xoshiro256State(0xBDD732262FEB6E95UL, 0x28EFE333B266F103UL, 0x47526757130F9F52UL, 0x581CE1FF0E4AE394UL),
            rng.GetState());
        Assert.Equal(0x15780B2E0C2EC716UL, rng.NextUInt64());
        Assert.Equal(0x6104D9866D113A7EUL, rng.NextUInt64());
        Assert.Equal(0xAE17533239E499A1UL, rng.NextUInt64());
        Assert.Equal(0xECB8AD4703B360A1UL, rng.NextUInt64());
        Assert.Equal(0xFDE6DC7FE2EC5E64UL, rng.NextUInt64());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void NextUInt32IsUpperHalfOfOutput()
    {
        var a = new Xoshiro256StarStar(42);
        var b = new Xoshiro256StarStar(42);

        Assert.Equal((uint)(a.NextUInt64() >> 32), b.NextUInt32());
        Assert.Equal(0x6104D986u, b.NextUInt32());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void BoundedIntegersMatchReferenceVectors()
    {
        var a = new Xoshiro256StarStar(42);
        var b = new Xoshiro256StarStar(42);
        var c = new Xoshiro256StarStar(42);

        Assert.Equal([0, 3, 6, 9, 9, 7, 7, 8, 7, 5], Draw(10, () => a.NextInt(10)));
        Assert.Equal([-5, -1, 2, 5, 5, 3, 2, 4, 3, 1], Draw(10, () => b.NextInt(-5, 6)));
        Assert.Equal([83, 378, 680, 924, 991, 769, 719, 850, 761, 583], Draw(10, c.NextPermille));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void RejectionPathMatchesReferenceAndConsumesExtraDraw()
    {
        // Range 3 * 2^30 rejects 25% of draws; the reference script consumed 9 draws for these 8 values.
        const int Min = int.MinValue;
        const int Max = 1 << 30; // Min + 3 * 2^30
        var rng = new Xoshiro256StarStar(7);

        int[] values = Draw(8, () => rng.NextInt(Min, Max));

        long[] expectedOffsets = [2256714809, 897920560, 2704629366, 3160336982, 2811401642, 195696145, 336411224, 1300429744];
        Assert.Equal(expectedOffsets, values.Select(v => (long)v - Min).ToArray());

        var counter = new Xoshiro256StarStar(7);
        for (int i = 0; i < 9; i++)
        {
            counter.NextUInt64();
        }

        Assert.Equal(counter.GetState(), rng.GetState());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void BoundedIntegersStayInRange()
    {
        var rng = new Xoshiro256StarStar(2024);

        for (int i = 0; i < 10_000; i++)
        {
            Assert.InRange(rng.NextInt(1), 0, 0);
            Assert.InRange(rng.NextInt(7), 0, 6);
            Assert.InRange(rng.NextInt(int.MaxValue), 0, int.MaxValue - 1);
            Assert.InRange(rng.NextInt(-3, 4), -3, 3);
            Assert.InRange(rng.NextInt(int.MinValue, int.MaxValue), int.MinValue, int.MaxValue - 1);
            Assert.InRange(rng.NextPermille(), 0, 999);
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DistributionIsRoughlyUniformForFixedSeed()
    {
        const int Buckets = 10;
        const int Draws = 100_000;
        var rng = new Xoshiro256StarStar(12345);
        int[] counts = new int[Buckets];

        for (int i = 0; i < Draws; i++)
        {
            counts[rng.NextInt(Buckets)]++;
        }

        // Fixed seed, so this never flakes; +-3% of the expected 10 000 per bucket is about 3 standard deviations.
        Assert.All(counts, c => Assert.InRange(c, 9_700, 10_300));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ChanceHandlesEdgesAndAlwaysConsumesOneDraw()
    {
        var rng = new Xoshiro256StarStar(99);
        var shadow = new Xoshiro256StarStar(99);

        for (int i = 0; i < 1_000; i++)
        {
            Assert.False(rng.ChancePermille(0));
            Assert.False(rng.ChancePermille(-5));
            Assert.True(rng.ChancePermille(1000));
            Assert.True(rng.ChancePermille(5000));
            Assert.False(rng.ChancePercent(0));
            Assert.True(rng.ChancePercent(100));
            for (int d = 0; d < 6; d++)
            {
                shadow.NextUInt64();
            }
        }

        Assert.Equal(shadow.GetState(), rng.GetState());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ChanceMatchesThresholdOnSameDraw()
    {
        var a = new Xoshiro256StarStar(5);
        var b = new Xoshiro256StarStar(5);
        int hits = 0;

        for (int i = 0; i < 10_000; i++)
        {
            bool hit = a.ChancePermille(250);
            Assert.Equal(b.NextPermille() < 250, hit);
            hits += hit ? 1 : 0;
        }

        Assert.InRange(hits, 2_350, 2_650);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SameSeedGivesSameSequence()
    {
        var a = new Xoshiro256StarStar(31337);
        var b = new Xoshiro256StarStar(31337);

        for (int i = 0; i < 1_000; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
            Assert.Equal(a.NextInt(-100, 100), b.NextInt(-100, 100));
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    public void RestoredStateContinuesIdenticalSequence()
    {
        var original = new Xoshiro256StarStar(777);
        for (int i = 0; i < 37; i++)
        {
            original.NextInt(1000);
        }

        Xoshiro256State saved = original.GetState();
        var restored = new Xoshiro256StarStar(saved);
        var overwritten = new Xoshiro256StarStar(1);
        overwritten.SetState(saved);

        for (int i = 0; i < 1_000; i++)
        {
            ulong expected = original.NextUInt64();
            Assert.Equal(expected, restored.NextUInt64());
            Assert.Equal(expected, overwritten.NextUInt64());
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void InvalidArgumentsThrow()
    {
        var rng = new Xoshiro256StarStar(1);

        Assert.Throws<ArgumentException>(() => new Xoshiro256StarStar(default(Xoshiro256State)));
        Assert.Throws<ArgumentException>(() => rng.SetState(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(6, 5));
        Assert.True(default(Xoshiro256State).IsZero);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void WriteStateWritesFourLittleEndianWords()
    {
        var rng = new Xoshiro256StarStar(new Xoshiro256State(1, 2, 3, 0x0102030405060708UL));
        var writer = new StateWriter();

        rng.WriteState(writer);

        byte[] expected =
        [
            1, 0, 0, 0, 0, 0, 0, 0,
            2, 0, 0, 0, 0, 0, 0, 0,
            3, 0, 0, 0, 0, 0, 0, 0,
            8, 7, 6, 5, 4, 3, 2, 1,
        ];
        Assert.Equal(expected, writer.ToArray());
    }

    private static int[] Draw(int count, Func<int> next)
    {
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = next();
        }

        return values;
    }
}
