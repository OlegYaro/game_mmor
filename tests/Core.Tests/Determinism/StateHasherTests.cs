using System.Text;
using Core.Determinism;

namespace Core.Tests.Determinism;

// Golden values. Only the empty-input value is a published XXH3-64 vector (xxHash sanity tests, seed 0).
// The rest were produced by this code with System.IO.Hashing 10.0.13 on macOS arm64 on 2026-10-14 (no independent
// xxhash implementation was available) and are pinned: they must stay identical on every platform and runtime.
// The lengths cover every XXH3 code path (0, 1-3, 4-8, 9-16, 17-128, 129-240, >240 bytes), which matters
// because the library uses different, partly vectorized code for each range on x64 and arm64.
public sealed class StateHasherTests
{
    public static TheoryData<int, ulong> PatternVectors => new()
    {
        { 0, 0x2D06800538D394C2UL },
        { 1, 0x4C5CCA45D0F4811FUL },
        { 3, 0x15F7093B173D005CUL },
        { 4, 0xDCA012F95811B6B9UL },
        { 8, 0xDEC6A9A43575982EUL },
        { 9, 0xCBE393399F17FFBDUL },
        { 16, 0x7E484C18D74895D0UL },
        { 17, 0x208BDE5EE2BED407UL },
        { 128, 0xF92B70EAA21A6288UL },
        { 129, 0xF8F76713F2BB60FAUL },
        { 240, 0xCCC7375172C41F03UL },
        { 241, 0x0B3B630948CE4A00UL },
        { 1024, 0x23BC880EBF0D29C6UL },
        { 4096, 0xA3C19F8174CDE0BBUL },
    };

    [Theory]
    [MemberData(nameof(PatternVectors))]
    [Trait("Req", "NFR-04")]
    public void HashOfPatternMatchesGolden(int length, ulong expected)
    {
        Assert.Equal(expected, StateHasher.HashBytes(Pattern(length)));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void HashOfKnownStringMatchesGolden()
    {
        Assert.Equal(0x41F9F6E422757D1CUL, StateHasher.HashBytes(Encoding.UTF8.GetBytes("colony")));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void HashOfRngStreamsMatchesGolden()
    {
        var hasher = new StateHasher();

        Assert.Equal(0x2933C2537DE42E02UL, hasher.Hash(new RngStreams(42)));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void HashEqualsHashOfWrittenBytesAndWriterIsReused()
    {
        var hasher = new StateHasher();
        var streams = new RngStreams(7);
        var writer = new StateWriter();
        streams.WriteState(writer);

        ulong first = hasher.Hash(streams);
        hasher.Hash(new RngStreams(8));
        ulong again = hasher.Hash(streams);

        Assert.Equal(StateHasher.HashBytes(writer.WrittenSpan), first);
        Assert.Equal(first, again);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void HashChangesWhenStateChanges()
    {
        var hasher = new StateHasher();
        var streams = new RngStreams(7);

        ulong before = hasher.Hash(streams);
        streams[RngStreamId.Factions].NextUInt64();

        Assert.NotEqual(before, hasher.Hash(streams));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SubsystemHashesKeepOrderAndCombinedMatchesGolden()
    {
        var hasher = new StateHasher();
        var rng = new RngStreams(42);
        var single = new Xoshiro256StarStar(new Xoshiro256State(1, 2, 3, 4));

        SubsystemHashes hashes = hasher.HashSubsystems(("rng", rng), ("single", single));

        Assert.Equal(["rng", "single"], hashes.Entries.Select(e => e.Name));
        Assert.Equal(hasher.Hash(rng), hashes.Entries[0].Hash);
        Assert.Equal(hasher.Hash(single), hashes.Entries[1].Hash);
        Assert.True(hashes.TryGetHash("single", out ulong singleHash));
        Assert.Equal(hashes.Entries[1].Hash, singleHash);
        Assert.False(hashes.TryGetHash("missing", out _));
        Assert.Equal(0x263DF38BD7B21004UL, hashes.Combined);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CombinedDependsOnOrderAndIsRecomputedAfterAdd()
    {
        var ab = new SubsystemHashes();
        ab.Add("a", 1);
        ab.Add("b", 2);
        var ba = new SubsystemHashes();
        ba.Add("b", 2);
        ba.Add("a", 1);

        ulong before = ab.Combined;
        Assert.Equal(before, ab.Combined);
        Assert.NotEqual(before, ba.Combined);

        ab.Add("c", 3);
        Assert.NotEqual(before, ab.Combined);
        Assert.Equal(3, ab.Count);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void FindFirstMismatchReportsFirstDivergingSubsystem()
    {
        SubsystemHashes expected = Make(("entities", 1), ("jobs", 2), ("rng", 3));

        Assert.Null(Make(("entities", 1), ("jobs", 2), ("rng", 3)).FindFirstMismatch(expected));
        Assert.Equal("jobs", Make(("entities", 1), ("jobs", 9), ("rng", 8)).FindFirstMismatch(expected));
        Assert.Equal("other", Make(("entities", 1), ("other", 2), ("rng", 3)).FindFirstMismatch(expected));
        Assert.Equal("rng", Make(("entities", 1), ("jobs", 2)).FindFirstMismatch(expected));
        Assert.Equal("extra", Make(("entities", 1), ("jobs", 2), ("rng", 3), ("extra", 4)).FindFirstMismatch(expected));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void InvalidInputsThrow()
    {
        var hashes = new SubsystemHashes();
        hashes.Add("a", 1);

        Assert.Throws<ArgumentException>(() => hashes.Add("a", 2));
        Assert.Throws<ArgumentException>(() => hashes.Add("", 2));
        Assert.Throws<ArgumentNullException>(() => hashes.Add(null!, 2));
        Assert.Throws<ArgumentNullException>(() => new StateHasher().Hash(null!));
        Assert.Throws<ArgumentNullException>(() => hashes.FindFirstMismatch(null!));
    }

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }

        return bytes;
    }

    private static SubsystemHashes Make(params (string Name, ulong Hash)[] entries)
    {
        var hashes = new SubsystemHashes();
        foreach ((string name, ulong hash) in entries)
        {
            hashes.Add(name, hash);
        }

        return hashes;
    }
}
