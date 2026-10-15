using Core.Determinism;
using Core.Entities;
using Core.Logging;
using Core.Sim;

namespace Core.Tests.Sim;

public sealed class SimulationTests
{
    private const ulong Seed = 0xC0FFEE;

    [Fact]
    [Trait("Req", "NFR-04")]
    public void NewSimulationStartsAtTickZero()
    {
        var sim = new Simulation(Seed, SimTestData.Real);

        Assert.Equal(0, sim.Tick);
        Assert.Equal(Seed, sim.Seed);
        Assert.Equal(sim.Calendar.At(0), sim.Now);
        Assert.Empty(sim.PendingCommands);
        Assert.Equal(0, sim.Entities.Registry.Capacity);
        Assert.Same(SimTestData.Real, sim.Content);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SystemOrderIsFixed()
    {
        var sim = new Simulation(Seed, SimTestData.Real);

        Assert.Equal(["clock", "commands", "construction", "movement", "gather", "fog"], sim.Systems.Select(s => s.Name));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SystemsRunInRegistrationOrderEveryTick()
    {
        List<string> calls = [];
        var sim = new Simulation(Seed, SimTestData.Real, null,
            [new ProbeSystem("c", calls), new ProbeSystem("a", calls), new ProbeSystem("b", calls)]);

        sim.Step(2);

        Assert.Equal(["c@1", "a@1", "b@1", "c@2", "a@2", "b@2"], calls);
        Assert.Throws<ArgumentException>(() => new Simulation(Seed, SimTestData.Real, null,
            [new ProbeSystem("a", calls), new ProbeSystem("a", calls)]));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void BufferIsPlayedBackAfterEachSystem()
    {
        bool seenBySecond = false;
        var create = new DelegateSystem("create", s => s.Buffer.Add(s.Buffer.Create(), new Position(1, 1)));
        var check = new DelegateSystem("check", s => seenBySecond = s.Positions.Count == 1);
        var sim = new Simulation(Seed, SimTestData.Real, null, [create, check]);

        sim.Step();

        Assert.True(seenBySecond);
        Assert.Equal(0, sim.Buffer.Count);
    }

    [Fact]
    [Trait("Req", "FR-TIME-01")]
    public void StepAdvancesTickAndCalendar()
    {
        var sim = new Simulation(Seed, SimTestData.Real);

        sim.Step(3);

        Assert.Equal(3, sim.Tick);
        Assert.Equal(sim.Calendar.At(3), sim.Now);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Step(-1));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CommandEnqueuedInTickTRunsInTickTPlusOne()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        List<string> executed = [];
        sim.Step(5);

        PendingCommand pending = sim.Enqueue(new RecordingCommand("x", executed));

        Assert.Equal(6, pending.ExecuteTick);
        Assert.Empty(executed);
        sim.Step();
        Assert.Equal(["x@6"], executed);
        Assert.Empty(sim.PendingCommands);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CommandsOfOneTickRunInEnqueueOrder()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        List<string> executed = [];
        foreach (string name in new[] { "b", "c", "a" })
        {
            sim.Enqueue(new RecordingCommand(name, executed));
        }

        Assert.Equal([0L, 1L, 2L], sim.PendingCommands.Select(p => p.Sequence));
        Assert.Equal(3, sim.NextCommandSequence);
        sim.Step();
        Assert.Equal(["b@1", "c@1", "a@1"], executed);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CommandEnqueuedDuringAStepRunsInTheNextTick()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        List<string> executed = [];
        sim.Enqueue(new RecordingCommand("first", executed, Then: new RecordingCommand("second", executed)));

        sim.Step();
        Assert.Equal(["first@1"], executed);
        Assert.Equal(2, sim.PendingCommands.Single().ExecuteTick);

        sim.Step();
        Assert.Equal(["first@1", "second@2"], executed);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SpawnMarkerCreatesAnEntityWithAPosition()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        sim.Enqueue(new SpawnMarkerCommand(new Position(300, -40)));
        sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(2, 3)));

        sim.Step();

        Assert.Equal([new EntityId(0, 1), new EntityId(1, 1)], sim.Positions.Entities.ToArray());
        Assert.Equal(new Position(300, -40), sim.Positions.Get(new EntityId(0, 1)));
        Assert.Equal(new Position(512, 768), sim.Positions.Get(new EntityId(1, 1)));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DestroyingADeadEntityIsSkippedWithAWarning()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        sim.Enqueue(new SpawnMarkerCommand(new Position(0, 0)));
        sim.Step();
        var id = new EntityId(0, 1);
        sim.Enqueue(new DestroyEntityCommand(id));
        sim.Enqueue(new DestroyEntityCommand(id));

        sim.Step();

        Assert.False(sim.Entities.IsAlive(id));
        Assert.Equal(0, sim.Positions.Count);
        LogEvent warning = Assert.Single(sim.Log.GetEvents(), e => e.Level == LogLevel.Warning);
        Assert.Equal("ecb", warning.Category);
        Assert.Equal("1 buffered operations of commands targeted dead entities", warning.Render());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void SameSeedAndCommandsGiveIdenticalHashesEveryTick()
    {
        var a = new Simulation(Seed, SimTestData.Real);
        var b = new Simulation(Seed, SimTestData.Real);

        for (int tick = 0; tick < 200; tick++)
        {
            Script(a, 0);
            Script(b, 0);
            a.Step();
            b.Step();
            SubsystemHashes ha = a.ComputeHashes();
            Assert.Null(ha.FindFirstMismatch(b.ComputeHashes()));
        }

        Assert.Equal(a.ComputeHashes().Combined, b.ComputeHashes().Combined);
        Assert.Equal(StateBytes(a), StateBytes(b));
        Assert.Equal(["calendar", "rng", "entities", "components", "commands"], a.ComputeHashes().Entries.Select(e => e.Name));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DifferentCommandChangesTheHash()
    {
        var a = new Simulation(Seed, SimTestData.Real);
        var b = new Simulation(Seed, SimTestData.Real);
        for (int tick = 0; tick < 50; tick++)
        {
            Script(a, 0);
            Script(b, tick >= 30 ? 1 : 0);
            a.Step();
            b.Step();
        }

        Assert.Equal("components", b.ComputeHashes().FindFirstMismatch(a.ComputeHashes()));
        Assert.NotEqual(a.ComputeHashes().Combined, b.ComputeHashes().Combined);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void PendingCommandChangesTheHashBeforeItRuns()
    {
        var a = new Simulation(Seed, SimTestData.Real);
        var b = new Simulation(Seed, SimTestData.Real);
        b.Enqueue(new SpawnMarkerCommand(new Position(1, 1)));

        Assert.Equal("commands", b.ComputeHashes().FindFirstMismatch(a.ComputeHashes()));
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void DifferentSeedChangesTheRngHash()
    {
        var a = new Simulation(Seed, SimTestData.Real);
        var b = new Simulation(Seed + 1, SimTestData.Real);

        Assert.Equal("rng", b.ComputeHashes().FindFirstMismatch(a.ComputeHashes()));
    }

    // The log is a viewer setting, not state: verbosity must not change the hash.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "NFR-08")]
    public void LogVerbosityDoesNotAffectTheHash()
    {
        var quiet = new Simulation(Seed, SimTestData.Real, new SimLog(capacity: 1, minimumLevel: LogLevel.Error));
        var verbose = new Simulation(Seed, SimTestData.Real, new SimLog(capacity: 4096, minimumLevel: LogLevel.Trace));
        for (int tick = 0; tick < 100; tick++)
        {
            Script(quiet, 0);
            Script(verbose, 0);
            quiet.Step();
            verbose.Step();
        }

        Assert.Equal(0, quiet.Log.Count);
        Assert.NotEqual(0, verbose.Log.Count);
        Assert.Equal(quiet.ComputeHashes().Combined, verbose.ComputeHashes().Combined);
    }

    [Fact]
    [Trait("Req", "NFR-08")]
    public void SameRunsProduceTheSameLog()
    {
        var a = new Simulation(Seed, SimTestData.Real, new SimLog(minimumLevel: LogLevel.Trace));
        var b = new Simulation(Seed, SimTestData.Real, new SimLog(minimumLevel: LogLevel.Trace));
        for (int tick = 0; tick < 100; tick++)
        {
            Script(a, 0);
            Script(b, 0);
            a.Step();
            b.Step();
        }

        Assert.Equal(a.Log.GetEvents().Select(e => e.ToString()), b.Log.GetEvents().Select(e => e.ToString()));
    }

    [Fact]
    [Trait("Req", "FR-TIME-01")]
    [Trait("Req", "NFR-08")]
    public void ClockLogsNightfallDawnAndNewDays()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        int ticksPerDay = sim.Calendar.TicksPerDay;
        long nightfall = sim.Calendar.FirstTickOf(0, sim.Calendar.NightStartHour);

        sim.Step(ticksPerDay);

        Assert.Equal(
            [$"[{nightfall}] Info calendar: Night falls", $"[{ticksPerDay}] Info calendar: Day 1 begins", $"[{ticksPerDay}] Info calendar: Dawn breaks"],
            sim.Log.GetEvents().Select(e => e.ToString()));
    }

    [Fact]
    [Trait("Req", "FR-TIME-01")]
    public void ClockLogsNewSeasonsAndYears()
    {
        var sim = new Simulation(Seed, SimTestData.Real, new SimLog(capacity: 4096));

        sim.Step(sim.Calendar.TicksPerDay * sim.Calendar.DaysPerYear);

        Assert.Equal(["Year 1 begins", "Season 0 begins", "Day 60 begins", "Dawn breaks"],
            sim.Log.GetEvents().Where(e => e.Tick == sim.Tick).Select(e => e.Render()));
        Assert.Equal(SimTestData.Real.Time.SeasonsPerYear,
            sim.Log.GetEvents().Count(e => e.Template.StartsWith("Season", StringComparison.Ordinal)));
    }

    // ADR 0004/0005: save at tick N, restore, continue == uninterrupted run, including the next entity id.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    public void RestoredSimulationContinuesExactlyLikeAnUninterruptedRun()
    {
        var uninterrupted = new Simulation(Seed, SimTestData.Real);
        var saved = new Simulation(Seed, SimTestData.Real);
        for (int tick = 0; tick < 60; tick++)
        {
            Script(uninterrupted, 0);
            Script(saved, 0);
            uninterrupted.Step();
            saved.Step();
        }

        // A command is pending at save time.
        uninterrupted.Enqueue(new SpawnMarkerCommand(new Position(7, 7)));
        saved.Enqueue(new SpawnMarkerCommand(new Position(7, 7)));
        uninterrupted.Rng[RngStreamId.Ai].NextUInt64();
        saved.Rng[RngStreamId.Ai].NextUInt64();

        Simulation restored = Simulation.Restore(SimTestData.Real, saved.CaptureState());
        Assert.Equal(StateBytes(uninterrupted), StateBytes(restored));

        for (int tick = 60; tick < 140; tick++)
        {
            Script(uninterrupted, 0);
            Script(restored, 0);
            uninterrupted.Step();
            restored.Step();
            Assert.Null(restored.ComputeHashes().FindFirstMismatch(uninterrupted.ComputeHashes()));
        }

        Assert.Equal(StateBytes(uninterrupted), StateBytes(restored));
        Assert.Equal(uninterrupted.Entities.Registry.Create(), restored.Entities.Registry.Create());
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void RestoreRejectsInconsistentState()
    {
        var sim = new Simulation(Seed, SimTestData.Real);
        sim.Enqueue(new SpawnMarkerCommand(new Position(1, 1)));
        sim.Step();
        sim.Enqueue(new SpawnMarkerCommand(new Position(2, 2)));
        SimulationState good = sim.CaptureState();

        SimulationState deadOwner = good with { Positions = [new ComponentEntry<Position>(new EntityId(0, 2), new Position(0, 0))] };
        SimulationState pastCommand = good with { PendingCommands = [good.PendingCommands[0] with { ExecuteTick = good.Tick }] };
        SimulationState badSequence = good with { NextCommandSequence = 0 };
        SimulationState negativeTick = good with { Tick = -1 };

        Assert.Throws<ArgumentException>(() => Simulation.Restore(SimTestData.Real, deadOwner));
        Assert.Throws<ArgumentException>(() => Simulation.Restore(SimTestData.Real, pastCommand));
        Assert.Throws<ArgumentException>(() => Simulation.Restore(SimTestData.Real, badSequence));
        Assert.Throws<ArgumentOutOfRangeException>(() => Simulation.Restore(SimTestData.Real, negativeTick));
        Assert.Equal(sim.ComputeHashes().Combined, Simulation.Restore(SimTestData.Real, good).ComputeHashes().Combined);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void StepAndCaptureAreNotAllowedInsideAStep()
    {
        Exception? nestedStep = null;
        Exception? nestedCapture = null;
        var probe = new DelegateSystem("probe", s =>
        {
            nestedStep = Record.Exception(s.Step);
            nestedCapture = Record.Exception(() => s.CaptureState());
        });
        var sim = new Simulation(Seed, SimTestData.Real, null, [probe]);

        sim.Step();

        Assert.IsType<InvalidOperationException>(nestedStep);
        Assert.IsType<InvalidOperationException>(nestedCapture);
        Assert.Equal(1, sim.Tick);
    }

    // Deterministic pseudo-player input: spawns most ticks, destroys the oldest marker every fifth tick.
    private static void Script(Simulation sim, int variant)
    {
        long tick = sim.Tick;
        if (tick % 5 == 4 && sim.Positions.Count > 0)
        {
            sim.Enqueue(new DestroyEntityCommand(sim.Positions.Entities[0]));
        }
        else if (tick % 2 == 0)
        {
            sim.Enqueue(new SpawnMarkerCommand(new Position((int)tick + variant, (int)(tick * 3))));
        }
    }

    private static byte[] StateBytes(Simulation sim)
    {
        var writer = new StateWriter();
        sim.WriteState(writer);
        return writer.ToArray();
    }

    private sealed class ProbeSystem(string name, List<string> calls) : ISimSystem
    {
        public string Name => name;

        public void Update(Simulation sim) => calls.Add($"{name}@{sim.Tick}");
    }

    private sealed class DelegateSystem(string name, Action<Simulation> update) : ISimSystem
    {
        public string Name => name;

        public void Update(Simulation sim) => update(sim);
    }

    private sealed record RecordingCommand(string Name, List<string> Executed, ISimCommand? Then = null) : ISimCommand
    {
        public string Kind => "test_recording";

        public void Execute(Simulation sim)
        {
            Executed.Add($"{Name}@{sim.Tick}");
            if (Then is not null)
            {
                sim.Enqueue(Then);
            }
        }

        public void WriteState(StateWriter writer) => writer.WriteString(Name);
    }
}
