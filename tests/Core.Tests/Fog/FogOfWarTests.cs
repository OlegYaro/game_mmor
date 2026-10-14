using Core.Determinism;
using Core.Fog;
using Core.WorldGen;

namespace Core.Tests.Fog;

/// <summary>Fog of war bit layers, circles, change tracking and the run-length save form (FR-WORLD-06, B3).</summary>
public sealed class FogOfWarTests
{
    // Reference: Visible when some source has dx² + dy² <= r²; Explored when any of the history did.
    private static FogState[] BruteForce(int size, IEnumerable<RevealSource[]> history)
    {
        var states = new FogState[size * size];
        RevealSource[][] steps = [.. history];
        for (int s = 0; s < steps.Length; s++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool seen = steps[s].Any(src => ((long)(x - src.X) * (x - src.X)) + ((long)(y - src.Y) * (y - src.Y)) <= (long)src.Radius * src.Radius);
                    int i = (y * size) + x;
                    if (s == steps.Length - 1)
                    {
                        states[i] = seen ? FogState.Visible : states[i];
                    }
                    else if (seen)
                    {
                        states[i] = FogState.Explored;
                    }
                }
            }
        }

        return states;
    }

    private static void AssertMatches(FogOfWar fog, FogState[] expected)
    {
        var actual = new byte[fog.TileCount];
        fog.CopyStates(actual);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True((byte)expected[i] == actual[i], $"Tile {i % fog.Size},{i / fog.Size}: expected {expected[i]}, got {(FogState)actual[i]}.");
            Assert.Equal(expected[i], fog.GetState(i % fog.Size, i / fog.Size));
        }

        Assert.Equal(expected.Count(s => s == FogState.Unknown), fog.CountStates().Unknown);
        Assert.Equal(expected.Count(s => s == FogState.Explored), fog.CountStates().Explored);
        Assert.Equal(expected.Count(s => s == FogState.Visible), fog.CountStates().Visible);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void CircleUsesIntegerSquaredDistance()
    {
        var fog = new FogOfWar(64);

        Assert.True(fog.Update([new RevealSource(20, 20, 12)]));

        Assert.Equal(new FogCounts(Unknown: (64 * 64) - 441, Explored: 0, Visible: 441), fog.CountStates());
        Assert.True(fog.IsVisible(32, 20));   // dx 12
        Assert.False(fog.IsVisible(33, 20));  // dx 13
        Assert.True(fog.IsVisible(28, 28));   // 64 + 64 = 128 <= 144
        Assert.False(fog.IsVisible(29, 29));  // 81 + 81 = 162 > 144
        Assert.True(fog.IsExplored(29, 20));
        Assert.Equal(FogState.Unknown, fog.GetState(0, 63));
    }

    [Theory]
    [Trait("Req", "FR-WORLD-06")]
    [InlineData(64)]
    [InlineData(96)] // rows do not start on word boundaries
    [InlineData(128)]
    public void RandomSourceSequencesMatchBruteForce(int size)
    {
        var rng = new Xoshiro256StarStar((ulong)size);
        var fog = new FogOfWar(size);
        List<RevealSource[]> history = [];
        for (int step = 0; step < 12; step++)
        {
            RevealSource[] sources = [.. Enumerable.Range(0, rng.NextInt(0, 4)).Select(_ =>
                new RevealSource(rng.NextInt(-20, size + 20), rng.NextInt(-20, size + 20), rng.NextInt(1, 30)))];
            history.Add(sources);
            fog.Update(sources);
            AssertMatches(fog, BruteForce(size, history));
        }
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void ExploredPersistsAndVisibleIsAlwaysASubset()
    {
        var fog = new FogOfWar(64);
        fog.Update([new RevealSource(10, 10, 6)]);
        fog.Update([new RevealSource(50, 50, 6)]);
        fog.Update([]);

        Assert.Equal(FogState.Explored, fog.GetState(10, 10));
        Assert.Equal(FogState.Explored, fog.GetState(50, 50));
        Assert.Equal(0, fog.CountStates().Visible);
        for (int i = 0; i < fog.ExploredBits.Length; i++)
        {
            Assert.Equal(0UL, fog.VisibleBits[i] & ~fog.ExploredBits[i]);
        }
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void SameSourcesDoNotRecompute()
    {
        var fog = new FogOfWar(64);
        Assert.False(fog.Update([]));
        Assert.Equal(0, fog.Version);

        Assert.True(fog.Update([new RevealSource(5, 5, 3)]));
        long version = fog.Version;
        Assert.False(fog.Update([new RevealSource(5, 5, 3)]));
        Assert.Equal(version, fog.Version);
        Assert.True(fog.Update([new RevealSource(5, 5, 2)]));
        Assert.Equal(version + 1, fog.Version);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void ChunkVersionsMarkOnlyTouchedChunks()
    {
        var fog = new FogOfWar(128); // 4x4 chunks
        fog.Update([new RevealSource(40, 40, 5)]); // chunk (1, 1) only
        Assert.Equal(1, fog.GetChunkVersion(1, 1));
        Assert.Equal(1, fog.ChunkVersions.ToArray().Count(v => v != 0));

        fog.Update([new RevealSource(30, 70, 5)]); // old circle's chunk and chunks (0, 2), (1, 2)
        Assert.Equal(2, fog.GetChunkVersion(1, 1));
        Assert.Equal(2, fog.GetChunkVersion(0, 2));
        Assert.Equal(2, fog.GetChunkVersion(1, 2));
        Assert.Equal(0, fog.GetChunkVersion(3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => fog.GetChunkVersion(4, 0));

        fog.ExploreAll();
        Assert.All(fog.ChunkVersions.ToArray(), v => Assert.Equal(3, v));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void ExploreAllKeepsVisible()
    {
        var fog = new FogOfWar(64);
        fog.Update([new RevealSource(20, 20, 4)]);
        int visible = fog.CountStates().Visible;

        fog.ExploreAll();

        Assert.Equal(new FogCounts(0, (64 * 64) - visible, visible), fog.CountStates());
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void ChunkAndRectangleCopiesMatchTileStates()
    {
        var fog = new FogOfWar(64);
        fog.Update([new RevealSource(33, 31, 9)]);
        fog.Update([new RevealSource(30, 33, 7)]);

        var chunk = new byte[ChunkCoord.Size * ChunkCoord.Size];
        fog.CopyStates(new ChunkCoord(1, 0), chunk);
        for (int y = 0; y < ChunkCoord.Size; y++)
        {
            for (int x = 0; x < ChunkCoord.Size; x++)
            {
                Assert.Equal((byte)fog.GetState(32 + x, y), chunk[(y * ChunkCoord.Size) + x]);
            }
        }

        var rect = new byte[3 * 2];
        fog.CopyStates(62, 1, 2, 3, rect.AsSpan(0, 6));
        Assert.Equal((byte)fog.GetState(63, 3), rect[5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => fog.CopyStates(63, 0, 2, 1, rect));
        Assert.Throws<ArgumentOutOfRangeException>(() => fog.CopyStates(-1, 0, 1, 1, rect));
        Assert.Throws<ArgumentException>(() => fog.CopyStates(new byte[10]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fog.GetState(64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FogOfWar(50));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void SetRangeHandlesWordBoundaries()
    {
        foreach ((int first, int last) in new[] { (0, 0), (0, 63), (63, 64), (5, 200), (64, 127), (127, 127), (1, 190) })
        {
            var bits = new ulong[4];
            FogOfWar.SetRange(bits, first, last);
            for (int i = 0; i < 256; i++)
            {
                bool set = ((bits[i / 64] >> (i % 64)) & 1) != 0;
                Assert.True(set == (i >= first && i <= last), $"bit {i} for range {first}..{last}");
            }
        }
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-WORLD-06")]
    public void RunLengthRoundTripsAndIsCanonical()
    {
        const int Tiles = 64 * 64;
        var rng = new Xoshiro256StarStar(7);
        List<ulong[]> layers =
        [
            new ulong[Tiles / 64],
            Enumerable.Repeat(ulong.MaxValue, Tiles / 64).ToArray(),
            [.. Enumerable.Range(0, Tiles / 64).Select(_ => rng.NextUInt64())],
        ];
        var fog = new FogOfWar(64);
        fog.Update([new RevealSource(0, 0, 10), new RevealSource(40, 40, 12)]);
        layers.Add(fog.CopyExplored());

        foreach (ulong[] layer in layers)
        {
            int[] runs = FogRunLength.Encode(layer, Tiles);
            Assert.Equal(Tiles, runs.Sum());
            Assert.All(runs.Skip(1), r => Assert.True(r > 0));
            Assert.Equal(layer, FogRunLength.Decode(runs, Tiles));
        }

        Assert.Equal([Tiles], FogRunLength.Encode(layers[0], Tiles));
        Assert.Equal([0, Tiles], FogRunLength.Encode(layers[1], Tiles));
        Assert.Equal(0, FogRunLength.Encode(layers[3], Tiles)[0]); // tile (0, 0) is explored
        Assert.Empty(FogRunLength.Encode([], 0));
        Assert.Empty(FogRunLength.Decode([], 0));
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData(new int[0], 64)]
    [InlineData(new[] { 0 }, 64)]
    [InlineData(new[] { 10, 0, 54 }, 64)]
    [InlineData(new[] { -1, 65 }, 64)]
    [InlineData(new[] { 32, 31 }, 64)]
    [InlineData(new[] { 32, 33 }, 64)]
    [InlineData(new[] { 1 }, 0)]
    public void NonCanonicalRunsAreRejected(int[] runs, int tiles) =>
        Assert.Throws<InvalidDataException>(() => FogRunLength.Decode(runs, tiles));

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void LoadExploredValidatesTheLayer()
    {
        var fog = new FogOfWar(64);
        fog.Update([new RevealSource(3, 3, 3)]);
        Assert.Throws<ArgumentException>(() => fog.LoadExplored(new ulong[3]));

        ulong[] all = Enumerable.Repeat(ulong.MaxValue, 64).ToArray();
        fog.LoadExplored(all);

        Assert.Equal(new FogCounts(0, 64 * 64, 0), fog.CountStates());
        Assert.True(fog.Update([new RevealSource(3, 3, 3)])); // the circle cache was cleared
    }
}
