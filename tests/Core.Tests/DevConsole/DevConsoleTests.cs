using Core.DevConsole;
using Core.Logging;
using Core.Sim;
using Core.Tests.Sim;
using ConsoleType = Core.DevConsole.DevConsole;

namespace Core.Tests.DevConsoleTests;

public sealed class DevConsoleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "devconsole-" + Guid.NewGuid().ToString("N"));
    private readonly SimLog _log = new();
    private readonly DevConsoleContext _context;
    private readonly ConsoleType _console;

    public DevConsoleTests()
    {
        var sim = new Simulation(42, SimTestData.Real, _log);
        _context = new DevConsoleContext(new SimulationHolder(sim), new TickScheduler(10), _log, SimTestData.Real, _dir);
        _console = new ConsoleType(_context);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private Simulation Sim => _context.Simulation.Current;

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("a b  c", 3)]
    [InlineData("save \"my game\" x", 3)]
    [InlineData("say \"\"", 2)]
    [InlineData("say \"a\\\"b\\\\c\"", 2)]
    public void ParserSplitsWordsAndQuotes(string line, int count)
    {
        Assert.True(CommandLineParser.TryParse(line, out IReadOnlyList<string> words, out _));
        Assert.Equal(count, words.Count);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void ParserHandlesQuotedContent()
    {
        Assert.True(CommandLineParser.TryParse("say \"a b\" \"q\\\"x\" \"\" tail", out IReadOnlyList<string> words, out _));
        Assert.Equal(["say", "a b", "q\"x", "", "tail"], words);
        Assert.True(CommandLineParser.TryParse(null, out words, out _));
        Assert.Empty(words);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void ParserReportsUnterminatedQuote()
    {
        Assert.False(CommandLineParser.TryParse("say \"oops", out _, out string error));
        Assert.Contains("quote", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_console.Execute("say \"oops").Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void RegisterRejectsBadAndDuplicateNames()
    {
        ConsoleHandler handler = (_, _) => ConsoleResult.Ok("hi");
        _console.Register("echo", "echo", "Says hi.", handler);

        Assert.Throws<ArgumentException>(() => _console.Register("echo", "echo", "dup", handler));
        Assert.Throws<ArgumentException>(() => _console.Register("Echo2", "x", "x", handler));
        Assert.Throws<ArgumentException>(() => _console.Register("two words", "x", "x", handler));
        Assert.Equal("hi", _console.Execute("ECHO").Text);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void CustomCommandReceivesParsedArguments()
    {
        IReadOnlyList<string>? seen = null;
        _console.Register("say", "say <text>", "Test.", (_, args) =>
        {
            seen = args;
            return ConsoleResult.Ok();
        });

        _console.Execute("say \"hello world\" 2");

        Assert.Equal(["hello world", "2"], seen);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void UnknownCommandAndBlankLineAreFriendly()
    {
        ConsoleResult unknown = _console.Execute("frobnicate 1");
        Assert.False(unknown.Success);
        Assert.Contains("frobnicate", unknown.Text, StringComparison.Ordinal);
        Assert.Contains("help", unknown.Text, StringComparison.Ordinal);

        ConsoleResult blank = _console.Execute("  ");
        Assert.True(blank.Success);
        Assert.Empty(blank.Lines);
        Assert.True(_console.Execute(null).Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void ThrowingHandlerBecomesFailureAndIsLogged()
    {
        _console.Register("boom", "boom", "Throws.", (_, _) => throw new InvalidOperationException("kaput"));

        ConsoleResult result = _console.Execute("boom");

        Assert.False(result.Success);
        Assert.Contains("kaput", result.Text, StringComparison.Ordinal);
        Assert.Contains(_log.GetEvents(), e => e.Level == LogLevel.Error && e.Category == "console");
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void HelpListsCommandsInStableOrder()
    {
        ConsoleResult all = _console.Execute("help");
        string[] names = [.. all.Lines.Select(l => l.Split(' ')[0])];

        Assert.True(all.Success);
        Assert.Equal([.. names.Order(StringComparer.Ordinal)], names);
        foreach (string expected in new[] { "help", "tick", "speed", "pause", "resume", "seed", "hash", "spawn", "save", "load", "log" })
        {
            Assert.Contains(expected, names);
        }

        Assert.Equal(all.Text, _console.Execute("help").Text);
        Assert.StartsWith("speed <1|2|4|20>", _console.Execute("help speed").Text, StringComparison.Ordinal);
        Assert.False(_console.Execute("help nope").Success);
        Assert.False(_console.Execute("help a b").Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void TickPrintsTickAndDate()
    {
        Assert.Equal("tick 0 | day 1 00:00 | week 1 | season 1 day 1 | year 1 | day", _console.Execute("tick").Text);

        Sim.Step(Sim.Calendar.TicksPerDay + 1);
        string text = _console.Execute("tick").Text;

        Assert.StartsWith($"tick {Sim.Tick} | day 2 ", text, StringComparison.Ordinal);
        Assert.False(_console.Execute("tick extra").Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void SpeedSetsAndValidates()
    {
        Assert.Equal("speed x1", _console.Execute("speed").Text);
        Assert.Equal("speed x4", _console.Execute("speed 4").Text);
        Assert.Equal(GameSpeed.X4, _context.Scheduler.Speed);

        foreach (string bad in new[] { "speed 3", "speed abc", "speed 1 2", "speed -1", "speed 0", "speed 99999999999" })
        {
            Assert.False(_console.Execute(bad).Success, bad);
        }

        Assert.Equal(GameSpeed.X4, _context.Scheduler.Speed);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void SpeedTwentyNeedsDevFlag()
    {
        ConsoleResult denied = _console.Execute("speed 20");
        Assert.False(denied.Success);
        Assert.Equal(GameSpeed.X1, _context.Scheduler.Speed);

        _context.Scheduler.DevSpeedsEnabled = true;

        Assert.True(_console.Execute("speed 20").Success);
        Assert.Equal(GameSpeed.X20, _context.Scheduler.Speed);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void PauseAndResumeToggleScheduler()
    {
        Assert.Equal("paused", _console.Execute("pause").Text);
        Assert.True(_context.Scheduler.IsPaused);
        Assert.Equal("speed x1 (paused)", _console.Execute("speed").Text);
        Assert.Equal("resumed", _console.Execute("resume").Text);
        Assert.False(_context.Scheduler.IsPaused);
        Assert.False(_console.Execute("pause now").Success);
        Assert.False(_console.Execute("resume now").Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void SeedPrintsWorldSeed()
    {
        Assert.Equal("42", _console.Execute("seed").Text);
        Assert.False(_console.Execute("seed 1").Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void HashPrintsCombinedAndEverySubsystem()
    {
        ConsoleResult result = _console.Execute("hash");
        var hashes = Sim.ComputeHashes();

        Assert.True(result.Success);
        Assert.Equal($"combined {Core.Content.ContentHash.Format(hashes.Combined)}", result.Lines[0]);
        Assert.Equal(1 + hashes.Count, result.Lines.Count);
        Assert.Equal(
            hashes.Entries.Select(e => $"{e.Name} {Core.Content.ContentHash.Format(e.Hash)}"),
            result.Lines.Skip(1));
        Assert.False(_console.Execute("hash x").Success);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void SpawnMarkerQueuesCommandAndEntityAppears()
    {
        ConsoleResult result = _console.Execute("spawn marker 3 -4");

        Assert.Equal("marker queued at 3,-4 for tick 1", result.Text);
        Assert.Single(Sim.PendingCommands);
        Assert.IsType<SpawnMarkerCommand>(Sim.PendingCommands[0].Command);
        Assert.Equal(Core.Entities.Position.FromTile(3, -4), ((SpawnMarkerCommand)Sim.PendingCommands[0].Command).Position);

        Sim.Step();

        Assert.Equal(1, Sim.Positions.Count);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("spawn")]
    [InlineData("spawn marker")]
    [InlineData("spawn marker 1")]
    [InlineData("spawn marker a b")]
    [InlineData("spawn marker 1.5 2")]
    [InlineData("spawn tree 1 2")]
    [InlineData("spawn marker 99999999 0")]
    public void SpawnRejectsBadArguments(string line)
    {
        Assert.False(_console.Execute(line).Success);
        Assert.Empty(Sim.PendingCommands);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void SaveThenLoadReplacesSimulationAndKeepsHash()
    {
        Sim.Step(25);
        _console.Execute("spawn marker 1 1");
        Sim.Step(3);
        _console.Execute("spawn marker 2 2");
        ulong hash = Sim.ComputeHashes().Combined;
        Simulation original = Sim;

        Assert.True(_console.Execute("save slot_1").Success);
        Assert.True(File.Exists(Path.Combine(_dir, "slot_1.sav")));
        Sim.Step(50);
        ConsoleResult loaded = _console.Execute("load slot_1");

        Assert.True(loaded.Success, loaded.Text);
        Assert.NotSame(original, Sim);
        Assert.Equal(hash, Sim.ComputeHashes().Combined);
        Assert.Same(_log, Sim.Log);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("save")]
    [InlineData("save a b")]
    [InlineData("save ../evil")]
    [InlineData("save \"\"")]
    [InlineData("load")]
    [InlineData("load ../evil")]
    [InlineData("load missing")]
    public void SaveAndLoadErrorsAreFriendly(string line)
    {
        Assert.False(_console.Execute(line).Success);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void LoadOfCorruptFileFailsWithoutReplacingSimulation()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bad.sav"), "not a save");
        Simulation before = Sim;

        ConsoleResult result = _console.Execute("load bad");

        Assert.False(result.Success);
        Assert.Same(before, Sim);
    }

    [Fact]
    [Trait("Req", "NFR-07")]
    public void LogSetsLevelAndPrintsLastEvents()
    {
        Assert.Equal("log level Debug", _console.Execute("log debug").Text);
        Assert.Equal(LogLevel.Debug, _log.MinimumLevel);
        _log.Write(1, LogLevel.Info, "t", "one");
        _log.Write(2, LogLevel.Debug, "t", "two");
        _log.Write(3, LogLevel.Warning, "t", "three");

        Assert.Equal(["[2] Debug t: two", "[3] Warning t: three"], _console.Execute("log debug 2").Lines);
        Assert.Equal(["[3] Warning t: three"], _console.Execute("log warn 5").Lines);
        Assert.Empty(_console.Execute("log error 1").Lines);
        Assert.Equal(["[3] Warning t: three"], _console.Execute("log warning 1").Lines);
        Assert.True(_console.Execute("log warn").Success);
        Assert.Equal(LogLevel.Warning, _log.MinimumLevel);
    }

    [Theory]
    [Trait("Req", "NFR-07")]
    [InlineData("log")]
    [InlineData("log loud")]
    [InlineData("log info x")]
    [InlineData("log info 0")]
    [InlineData("log info 100000")]
    [InlineData("log info 1 2")]
    public void LogRejectsBadArguments(string line) => Assert.False(_console.Execute(line).Success);

    [Fact]
    [Trait("Req", "NFR-07")]
    public void OutputIsDeterministicAcrossIdenticalRuns()
    {
        static string Run(string dir)
        {
            var log = new SimLog();
            var sim = new Simulation(7, SimTestData.Real, log);
            var console = new ConsoleType(new DevConsoleContext(new SimulationHolder(sim), new TickScheduler(10), log, SimTestData.Real, dir));
            var lines = new List<string>();
            foreach (string line in new[] { "spawn marker 5 5", "speed 2", "tick", "hash", "seed", "help" })
            {
                lines.AddRange(console.Execute(line).Lines);
                sim.Step(7);
            }

            return string.Join('\n', lines);
        }

        Assert.Equal(Run(_dir), Run(_dir));
    }
}
