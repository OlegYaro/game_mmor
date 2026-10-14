using Core.Determinism;

namespace Core.Tests.Determinism;

public sealed class RngStreamsTests
{
    // Stream seeds and first outputs for world seed 42, from the independent Python reference script
    // (seed = Mix(world ^ Mix((id + 1) * Gamma)), then xoshiro256** seeded through SplitMix64).
    public static TheoryData<RngStreamId, ulong, ulong, ulong> WorldSeed42Vectors => new()
    {
        { RngStreamId.WorldGen, 0x4579B960BB007F46UL, 0xD469C81CC8CC0C29UL, 0xF32FEBA53743E4FFUL },
        { RngStreamId.Combat, 0xDB6685C74BCFF7FDUL, 0xCA4CD78FE3230B99UL, 0x1601EE16D675FF0AUL },
        { RngStreamId.Gathering, 0x617EB6CEC11D9485UL, 0x7E6BBA60778FA134UL, 0x5AD9CBC15B25B925UL },
        { RngStreamId.Ai, 0x09E8B0543D5D9451UL, 0xC95EEFB350647EF2UL, 0x3F38BF9186011477UL },
        { RngStreamId.Storyteller, 0x94DCC1F74C13BE26UL, 0x02D4A61F1F5E95DDUL, 0x679AB17F4BAF405EUL },
        { RngStreamId.Factions, 0xB60E0D3654ED45C0UL, 0x3609ED41ECE96F7AUL, 0x8A8FA4F6EF08A23EUL },
    };

    [Theory]
    [MemberData(nameof(WorldSeed42Vectors))]
    [Trait("Req", "NFR-04")]
    public void StreamDerivationMatchesReferenceVectors(RngStreamId id, ulong seed, ulong first, ulong second)
    {
        var streams = new RngStreams(42);

        Assert.Equal(seed, RngStreams.DeriveSeed(42, id));
        Assert.Equal(first, streams[id].NextUInt64());
        Assert.Equal(second, streams[id].NextUInt64());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CountCoversEveryStreamId()
    {
        Assert.Equal(RngStreams.Count, Enum.GetValues<RngStreamId>().Length);
        Assert.Equal(Enum.GetValues<RngStreamId>().Select(v => (int)v), Enumerable.Range(0, RngStreams.Count));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void StreamsAreDistinct()
    {
        var streams = new RngStreams(42);
        var seen = new HashSet<ulong>();

        foreach (RngStreamId id in Enum.GetValues<RngStreamId>())
        {
            Assert.True(seen.Add(streams[id].NextUInt64()), $"Stream {id} repeats another stream's first output.");
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DrawingFromOneStreamDoesNotShiftOthers()
    {
        var busy = new RngStreams(2026);
        var quiet = new RngStreams(2026);

        for (int i = 0; i < 500; i++)
        {
            busy[RngStreamId.Combat].NextInt(100);
        }

        foreach (RngStreamId id in Enum.GetValues<RngStreamId>().Where(id => id != RngStreamId.Combat))
        {
            Assert.Equal(quiet[id].NextUInt64(), busy[id].NextUInt64());
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SameWorldSeedIsDeterministicAndDifferentSeedsDiffer()
    {
        var a = new RngStreams(9);
        var b = new RngStreams(9);
        var c = new RngStreams(10);

        foreach (RngStreamId id in Enum.GetValues<RngStreamId>())
        {
            Assert.Equal(a[id].GetState(), b[id].GetState());
            Assert.NotEqual(a[id].GetState(), c[id].GetState());
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    public void SavedStatesRestoreAndContinueIdentically()
    {
        var original = new RngStreams(123);
        original[RngStreamId.Ai].NextInt(10);
        original[RngStreamId.WorldGen].NextUInt64();

        var restored = new RngStreams(original.WorldSeed, original.GetStates());

        Assert.Equal(original.WorldSeed, restored.WorldSeed);
        foreach (RngStreamId id in Enum.GetValues<RngStreamId>())
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(original[id].NextUInt64(), restored[id].NextUInt64());
            }
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void WriteStateIsSeedThenStreamsInIdOrder()
    {
        var streams = new RngStreams(42);
        var expected = new StateWriter();
        expected.WriteUInt64(42);
        foreach (RngStreamId id in Enum.GetValues<RngStreamId>())
        {
            streams[id].WriteState(expected);
        }

        var actual = new StateWriter();
        streams.WriteState(actual);

        Assert.Equal(8 + (RngStreams.Count * 32), actual.Length);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void InvalidInputsThrow()
    {
        var streams = new RngStreams(1);
        Xoshiro256State[] tooFew = new Xoshiro256State[RngStreams.Count - 1];

        Assert.Throws<ArgumentOutOfRangeException>(() => streams[(RngStreamId)RngStreams.Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => RngStreams.DeriveSeed(1, (RngStreamId)(-1)));
        Assert.Throws<ArgumentException>(() => new RngStreams(1, tooFew));
        Assert.Throws<ArgumentException>(() => new RngStreams(1, new Xoshiro256State[RngStreams.Count]));
    }
}
