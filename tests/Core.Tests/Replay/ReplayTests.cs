using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Replay;
using Core.Sim;
using Core.Tests.Content;
using Core.Tests.Sim;

namespace Core.Tests.Replay;

public sealed class ReplayTests
{
    private const ulong Seed = 77;

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ReplayJsonRoundTripsCanonically()
    {
        ReplayFile replay = Record(out _);

        string json = replay.ToJson();
        ReplayFile parsed = ReplayFile.Parse(json);

        Assert.Equal(json, parsed.ToJson());
        Assert.Equal(replay.Seed, parsed.Seed);
        Assert.Equal(replay.ContentHash, parsed.ContentHash);
        Assert.Equal(replay.EndTick, parsed.EndTick);
        Assert.Equal(replay.Commands, parsed.Commands);
        Assert.DoesNotContain('\r', json);
        Assert.Contains("\n    {\"tick\":1,\"command\":{\"kind\":\"spawn_marker\",\"x\":5,\"y\":6}},\n", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void EmptyReplayHasCompactCommandList()
    {
        var replay = new ReplayFile(1, 2, 0, []);

        string json = replay.ToJson();

        Assert.Equal("{\n  \"formatVersion\": 1,\n  \"seed\": 1,\n  \"contentHash\": \"0000000000000002\",\n  \"endTick\": 0,\n  \"commands\": []\n}\n", json);
        Assert.Empty(ReplayFile.Parse(json).Commands);
    }

    [Theory]
    [Trait("Req", "NFR-04")]
    [InlineData("not json")]
    [InlineData("""{"formatVersion":2,"seed":1,"contentHash":"0000000000000002","endTick":0,"commands":[]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"XYZ","endTick":0,"commands":[]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"000000000000000A","endTick":0,"commands":[]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"0000000000000002","endTick":0,"commands":[],"extra":1}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"0000000000000002","endTick":5,"commands":[null]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"0000000000000002","endTick":5,"commands":[{"tick":1,"command":{"kind":"fly"}}]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"0000000000000002","endTick":5,"commands":[{"tick":1,"command":{"x":1,"y":1}}]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"0000000000000002","endTick":5,"commands":[{"tick":0,"command":{"kind":"spawn_marker","x":1,"y":1}}]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"0000000000000002","endTick":5,"commands":[{"tick":6,"command":{"kind":"spawn_marker","x":1,"y":1}}]}""")]
    [InlineData("""{"formatVersion":1,"seed":1,"contentHash":"0000000000000002","endTick":5,"commands":[{"tick":3,"command":{"kind":"spawn_marker","x":1,"y":1}},{"tick":2,"command":{"kind":"spawn_marker","x":1,"y":1}}]}""")]
    public void InvalidReplayIsRejectedWithInvalidDataException(string json)
    {
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse(json));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ReplayOfRecordedRunReproducesItsHashes()
    {
        ReplayFile replay = Record(out Simulation original);

        ReplayPlayer player = ReplayPlayer.Start(replay, SimTestData.Real);
        player.RunToEnd();

        Assert.True(player.IsFinished);
        Assert.Equal(original.Tick, player.Simulation.Tick);
        Assert.Equal(original.ComputeHashes().Combined, player.Simulation.ComputeHashes().Combined);
        Assert.Equal(replay.EndTick, player.Hashes.Count);
        player.RunTo(1);
        Assert.Equal(replay.EndTick, player.Simulation.Tick);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void PlayerRejectsWrongSeedContentOrMissedCommands()
    {
        ReplayFile replay = Record(out _);

        Assert.Throws<ArgumentException>(() => new ReplayPlayer(replay, new Simulation(Seed + 1, SimTestData.Real)));
        ArgumentException content = Assert.Throws<ArgumentException>(() => ReplayPlayer.Start(replay, ChangedContent()));
        Assert.Contains("regenerate", content.Message, StringComparison.Ordinal);

        ReplayPlayer player = ReplayPlayer.Start(replay, SimTestData.Real);
        player.Simulation.Step(); // Behind the player's back: the tick-1 command is missed.
        Assert.Throws<InvalidOperationException>(player.Step);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void RecorderNeedsANewSimulation()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        sim.Step();

        Assert.Throws<ArgumentException>(() => new ReplayRecorder(sim));
        Assert.Throws<ArgumentNullException>(() => new ReplayFile(1, 1, 5, [new ReplayCommand(2, null!)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayFile(1, 1, -1, []));

        var fresh = new Simulation(Seed, SimTestData.Real);
        var recorder = new ReplayRecorder(fresh);
        recorder.Enqueue(new SpawnMarkerCommand(new Position(1, 1)));
        Assert.Equal(1, recorder.ToReplay().EndTick); // The pending command's tick, although the simulation is at 0.
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void HashTraceTextRoundTrips()
    {
        HashTrace trace = Trace(1, 2, 3, 10);

        string text = trace.ToText();
        HashTrace parsed = HashTrace.Parse(text.Replace("\n", "\r\n", StringComparison.Ordinal) + "\n# trailing comment\n");

        Assert.Equal(text, parsed.ToText());
        Assert.Equal(4, parsed.Count);
        Assert.True(parsed.TryGet(10, out SubsystemHashes ten));
        Assert.Equal(trace.Last!.Value.Hashes.Combined, ten.Combined);
        Assert.False(parsed.TryGet(4, out _));
        Assert.Equal([2L, 10L], trace.Sample(2).Entries.Select(e => e.Tick));
        Assert.Throws<ArgumentException>(() => trace.Add(10, Hashes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => trace.Sample(0));
        Assert.Null(new HashTrace().Last);
    }

    [Theory]
    [Trait("Req", "NFR-04")]
    [InlineData("x 0000000000000000")]
    [InlineData("5")]
    [InlineData("5 zzzzzzzzzzzzzzzz a=0000000000000001")]
    [InlineData("5 0000000000000000 a=0000000000000001")]
    [InlineData("5 COMBINED a=nothex")]
    [InlineData("5 COMBINED noequals")]
    [InlineData("5 COMBINED =0000000000000001")]
    [InlineData("5 COMBINED a=0000000000000001 a=0000000000000002")]
    [InlineData("5 COMBINED a=0000000000000001\n4 COMBINED a=0000000000000001")]
    public void MalformedHashTraceIsRejected(string text)
    {
        var single = new SubsystemHashes();
        single.Add("a", 1);
        text = text.Replace("COMBINED", ContentHash.Format(single.Combined), StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => HashTrace.Parse(text));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ComparerReportsMissingTicksAndChangedSubsystems()
    {
        HashTrace expected = Trace(1, 2, 3);

        Assert.Null(ReplayComparer.FindFirstDivergence(expected, Trace(1, 2, 3, 4)));
        Assert.Null(ReplayComparer.FindFirstDivergence(new HashTrace(), expected));

        ReplayDivergence missing = ReplayComparer.FindFirstDivergence(expected, Trace(1, 2))!;
        Assert.Equal(3, missing.Tick);
        Assert.Equal(ReplayComparer.MissingTick, missing.Subsystem);
        Assert.Null(missing.ActualHash);
        Assert.Equal(2, missing.LastMatchingTick);
        Assert.Contains("<none>", missing.ToString(), StringComparison.Ordinal);

        var changed = new HashTrace();
        changed.Add(1, Hashes(1, componentsSalt: 99));
        ReplayDivergence first = ReplayComparer.FindFirstDivergence(expected, changed)!;
        Assert.Equal(1, first.Tick);
        Assert.Equal("components", first.Subsystem);
        Assert.Null(first.LastMatchingTick);
        Assert.Contains("no compared tick matched", first.Describe(), StringComparison.Ordinal);

        var fewer = new HashTrace();
        var partial = new SubsystemHashes();
        partial.Add("calendar", Hashes(1).Entries[0].Hash);
        fewer.Add(1, partial);
        ReplayDivergence shorter = ReplayComparer.FindFirstDivergence(expected, fewer)!;
        Assert.Equal("rng", shorter.Subsystem);
        Assert.Null(shorter.ActualHash);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ContentHashIsCanonical()
    {
        ContentFile a = new("balance/time.json", "{\n\"x\": 1\n}\n");
        ContentFile b = new("items/basic.json", "[]");
        ulong hash = ContentHash.Compute([a, b]);

        Assert.Equal(hash, ContentHash.Compute([b, a]));
        Assert.Equal(hash, ContentHash.Compute([a with { Text = "﻿{\r\n\"x\": 1\r\n}\r\n" }, b]));
        Assert.Equal(hash, ContentHash.Compute([a with { Text = "{\r\"x\": 1\r}\r" }, b]));
        Assert.NotEqual(hash, ContentHash.Compute([a with { Text = "{\n\"x\": 2\n}\n" }, b]));
        Assert.NotEqual(hash, ContentHash.Compute([a with { Path = "balance/time2.json" }, b]));
        Assert.NotEqual(hash, ContentHash.Compute([a]));
        Assert.Throws<ArgumentException>(() => ContentHash.Compute([a, a]));

        Assert.True(ContentHash.TryParse(ContentHash.Format(hash), out ulong parsed));
        Assert.Equal(hash, parsed);
        Assert.False(ContentHash.TryParse(ContentHash.Format(hash).ToUpperInvariant(), out _));
        Assert.False(ContentHash.TryParse("123", out _));
        Assert.False(ContentHash.TryParse(null, out _));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-MOD-01")]
    public void CatalogCarriesTheHashOfItsFiles()
    {
        InMemoryContentSource source = TestPaths.LoadDataSource();

        Assert.Equal(ContentHash.Compute(source.GetFiles()), SimTestData.Real.ContentHash);
        Assert.NotEqual(SimTestData.Real.ContentHash, ChangedContent().ContentHash);
    }

    private static ReplayFile Record(out Simulation sim)
    {
        sim = new Simulation(Seed, SimTestData.Real);
        var recorder = new ReplayRecorder(sim);
        recorder.Enqueue(new SpawnMarkerCommand(new Position(5, 6)));
        recorder.Enqueue(new SpawnMarkerCommand(new Position(7, 8)));
        sim.Step(3);
        recorder.Enqueue(new DestroyEntityCommand(new EntityId(0, 1)));
        sim.Step(4);
        recorder.Enqueue(new SpawnMarkerCommand(new Position(-1, -2)));
        sim.Step(2);
        return recorder.ToReplay();
    }

    // The real data with one insignificant whitespace change: same definitions, different file hash.
    private static ContentCatalog ChangedContent()
    {
        var changed = new InMemoryContentSource();
        foreach (ContentFile file in TestPaths.LoadDataSource().GetFiles())
        {
            changed.Add(file.Path, file.Text + " ");
        }

        return ContentCatalog.Load(changed).Catalog!;
    }

    private static HashTrace Trace(params long[] ticks)
    {
        var trace = new HashTrace();
        foreach (long tick in ticks)
        {
            trace.Add(tick, Hashes(tick));
        }

        return trace;
    }

    private static SubsystemHashes Hashes(long tick, ulong componentsSalt = 0)
    {
        var hashes = new SubsystemHashes();
        hashes.Add("calendar", (ulong)tick);
        hashes.Add("rng", 7);
        hashes.Add("components", ((ulong)tick * 31) + componentsSalt);
        return hashes;
    }
}
