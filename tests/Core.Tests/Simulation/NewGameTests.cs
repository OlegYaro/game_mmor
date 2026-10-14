using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Replay;
using Core.Sim;
using Core.WorldGen;

namespace Core.Tests.Sim;

/// <summary>New games: world in the simulation, start marker, fog over time, difficulty (FR-WORLD-01/06/08, FR-SAVE-05).</summary>
public sealed class NewGameTests
{
    private const int Size = 64;

    private static Simulation NewGame(ulong seed = 0xF06, Difficulty difficulty = Difficulty.Normal, int size = Size) =>
        Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(seed, size, difficulty));

    private static int DiscTiles(GameWorld world, int cx, int cy, int radius)
    {
        int count = 0;
        for (int y = 0; y < world.Size; y++)
        {
            for (int x = 0; x < world.Size; x++)
            {
                count += ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius ? 1 : 0;
            }
        }

        return count;
    }

    private static void AssertVisibleSubsetOfExplored(FogOfWar fog)
    {
        for (int i = 0; i < fog.ExploredBits.Length; i++)
        {
            Assert.Equal(0UL, fog.VisibleBits[i] & ~fog.ExploredBits[i]);
        }
    }

    private static EntityId StartMarker(Simulation sim) => sim.World!.Revealers.Entities[0];

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    [Trait("Req", "FR-WORLD-06")]
    [Trait("Req", "NFR-04")]
    public void SameOptionsGiveIdenticalHashesIncludingFog()
    {
        Simulation a = NewGame();
        Simulation b = NewGame();
        Assert.Null(a.ComputeHashes().FindFirstMismatch(b.ComputeHashes()));
        a.Step(40);
        b.Step(40);
        Assert.Null(a.ComputeHashes().FindFirstMismatch(b.ComputeHashes()));
        Assert.Equal(a.World!.MapHash, b.World!.MapHash);
        Assert.Equal(WorldGenerator.Generate(new WorldGenRequest(0xF06, Size, SimTestData.Real.WorldGen)).ComputeHash(), a.World.MapHash);

        Assert.Equal(
            [Simulation.CalendarHashName, Simulation.RngHashName, Simulation.EntitiesHashName, Simulation.ComponentsHashName,
                Simulation.CommandsHashName, Simulation.GameHashName, Simulation.FogHashName],
            a.ComputeHashes().Entries.Select(e => e.Name));
        Assert.NotEqual(a.World.MapHash, NewGame(seed: 0xF07).World!.MapHash);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-05")]
    public void DifficultyIsStoredAndHashedInTheGameSubsystem()
    {
        SubsystemHashes normal = NewGame().ComputeHashes();
        Simulation hard = NewGame(difficulty: Difficulty.Hard);

        Assert.Equal(Difficulty.Hard, hard.Difficulty);
        Assert.Equal(Difficulty.Hard, hard.CaptureState().Difficulty);
        Assert.Equal(Simulation.GameHashName, hard.ComputeHashes().FindFirstMismatch(normal));
        Assert.Equal(1, hard.ComputeHashes().Entries.Zip(normal.Entries).Count(p => p.First.Hash != p.Second.Hash));
        Assert.Equal(["peaceful", "normal", "hard"], DifficultyNames.All.Select(DifficultyNames.ToName));
        Assert.True(DifficultyNames.TryParse("peaceful", out Difficulty peaceful));
        Assert.Equal(Difficulty.Peaceful, peaceful);
        Assert.False(DifficultyNames.TryParse("Hard", out _));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-08")]
    [Trait("Req", "FR-WORLD-06")]
    public void NewGameSpawnsTheStartMarkerAndAppliesInitialFog()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        StartPoint start = world.Map.Start;

        Assert.Equal(0, sim.Tick);
        Assert.Empty(sim.PendingCommands);
        EntityId marker = Assert.Single(sim.Entities.Registry.AliveEntities());
        Assert.Equal(Simulation.TileCenter(start.X, start.Y), sim.Positions.Get(marker));
        Assert.Equal(start.X, sim.Positions.Get(marker).TileX);
        Assert.Equal(new Revealer(RevealerKind.Hero), world.Revealers.Get(marker));
        Assert.False(sim.Now.IsNight);

        int disc = DiscTiles(world, start.X, start.Y, SimTestData.Real.Fog.HeroDay);
        Assert.Equal(new FogCounts(world.Fog.TileCount - disc, 0, disc), world.Fog.CountStates());
        Assert.Equal(FogState.Visible, world.Fog.GetState(start.X, start.Y));
        Assert.Equal([Simulation.PositionStoreName, Simulation.RevealerStoreName], sim.Entities.Stores.Select(s => s.Name));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    [Trait("Req", "FR-TIME-01")]
    public void NightHalvesTheHeroRadiusAndDawnRestoresIt()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        StartPoint start = world.Map.Start;
        long nightfall = sim.Calendar.FirstTickOf(0, sim.Calendar.NightStartHour);
        int day = DiscTiles(world, start.X, start.Y, SimTestData.Real.Fog.HeroDay);
        int night = DiscTiles(world, start.X, start.Y, SimTestData.Real.Fog.HeroNight);
        Assert.Equal(12, SimTestData.Real.Fog.HeroDay);
        Assert.Equal(6, SimTestData.Real.Fog.HeroNight);

        sim.Step((int)nightfall - 1);
        Assert.False(sim.Now.IsNight);
        Assert.Equal(day, world.Fog.CountStates().Visible);

        sim.Step();
        Assert.True(sim.Now.IsNight);
        Assert.Equal(new FogCounts(world.Fog.TileCount - day, day - night, night), world.Fog.CountStates());
        AssertVisibleSubsetOfExplored(world.Fog);

        sim.Step(sim.Calendar.TicksPerDay - (int)nightfall);
        Assert.False(sim.Now.IsNight);
        Assert.Equal(day, world.Fog.CountStates().Visible);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void ExploredPersistsAfterTheSourceMovesAndIsDestroyed()
    {
        Simulation sim = NewGame();
        GameWorld world = sim.World!;
        EntityId marker = StartMarker(sim);
        StartPoint start = world.Map.Start;
        int firstDisc = world.Fog.CountStates().Visible;

        // Systems move entities in M2; here the test moves the marker between ticks.
        int dx = start.X < Size / 2 ? 20 : -20;
        sim.Positions.Get(marker) = Simulation.TileCenter(start.X + dx, start.Y);
        sim.Step();

        Assert.Equal(FogState.Explored, world.Fog.GetState(start.X, start.Y));
        Assert.Equal(FogState.Visible, world.Fog.GetState(start.X + dx, start.Y));
        AssertVisibleSubsetOfExplored(world.Fog);
        FogCounts moved = world.Fog.CountStates();
        Assert.True(moved.Explored > 0);
        Assert.True(moved.Explored + moved.Visible > firstDisc);

        sim.Enqueue(new DestroyEntityCommand(marker));
        sim.Step();

        Assert.Equal(new FogCounts(moved.Unknown, moved.Explored + moved.Visible, 0), world.Fog.CountStates());
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void FogIsOnlyRecomputedWhenACircleChanges()
    {
        Simulation sim = NewGame();
        FogOfWar fog = sim.World!.Fog;
        long version = fog.Version;

        sim.Step(100);
        Assert.Equal(version, fog.Version);

        sim.Positions.Get(StartMarker(sim)) = Simulation.TileCenter(sim.World.Map.Start.X, sim.World.Map.Start.Y + 1);
        sim.Step();
        Assert.Equal(version + 1, fog.Version);

        // Moving inside the same tile changes no circle.
        Position p = sim.Positions.Get(StartMarker(sim));
        sim.Positions.Get(StartMarker(sim)) = new Position(p.X + 1, p.Y + 1);
        sim.Step();
        Assert.Equal(version + 1, fog.Version);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    [Trait("Req", "NFR-07")]
    [Trait("Req", "NFR-04")]
    public void RevealRunsThroughTheQueueAndReplays()
    {
        Simulation sim = NewGame(difficulty: Difficulty.Peaceful);
        var recorder = new ReplayRecorder(sim);
        sim.Step(5);
        recorder.Enqueue(new RevealAllCommand());
        Assert.NotEqual(0, sim.World!.Fog.CountStates().Unknown);
        sim.Step();
        Assert.Equal(0, sim.World.Fog.CountStates().Unknown);
        sim.Step(3);

        ReplayFile replay = ReplayFile.Parse(recorder.ToReplay().ToJson());
        Assert.Equal(new NewGameOptions(0xF06, Size, Difficulty.Peaceful), replay.NewGame);
        ReplayPlayer player = ReplayPlayer.Start(replay, SimTestData.Real);
        player.RunToEnd();

        Assert.Equal(sim.Tick, player.Simulation.Tick);
        Assert.Null(player.Simulation.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(0, player.Simulation.World!.Fog.CountStates().Unknown);
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ReplayPlayerRejectsADifferentWorld()
    {
        Simulation sim = NewGame();
        ReplayFile replay = new ReplayRecorder(sim).ToReplay();

        Assert.Throws<ArgumentException>(() => new ReplayPlayer(replay, NewGame(difficulty: Difficulty.Hard)));
        Assert.Throws<ArgumentException>(() => new ReplayPlayer(replay, NewGame(size: 96)));
        Assert.Throws<ArgumentException>(() => new ReplayPlayer(replay, new Simulation(0xF06, SimTestData.Real)));
        Assert.Throws<ArgumentException>(() => new ReplayFile(1, 0, 0, [], mapSize: 0, Difficulty.Hard));
        Assert.Throws<ArgumentException>(() => new ReplayFile(1, 0, 0, [], mapSize: 50));
    }

    [Theory]
    [Trait("Req", "NFR-04")]
    [InlineData("\"mapSize\": 64,", false)]                          // size without difficulty
    [InlineData("\"difficulty\": \"hard\",", false)]                 // difficulty without size
    [InlineData("\"mapSize\": 64, \"difficulty\": \"brutal\",", false)]
    [InlineData("\"mapSize\": 50, \"difficulty\": \"hard\",", false)]
    [InlineData("\"mapSize\": 64, \"difficulty\": \"hard\",", true)]
    [InlineData("", true)]
    public void ReplayParseValidatesGameFields(string fields, bool valid)
    {
        string json = $$"""{ "formatVersion": 1, "seed": 1, "contentHash": "0000000000000000", "endTick": 0, {{fields}} "commands": [] }""";

        if (valid)
        {
            ReplayFile replay = ReplayFile.Parse(json);
            Assert.Equal(fields.Length == 0 ? 0 : 64, replay.MapSize);
            Assert.Equal(json.Contains("hard", StringComparison.Ordinal) ? Difficulty.Hard : Difficulty.Normal, replay.Difficulty);
            Assert.Equal(replay.MapSize, ReplayFile.Parse(replay.ToJson()).MapSize);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => ReplayFile.Parse(json));
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void CoreSimulationKeepsTheM0LayoutAndIgnoresReveal()
    {
        var sim = new Simulation(5, SimTestData.Real);

        Assert.Null(sim.World);
        Assert.Equal(Difficulty.Normal, sim.Difficulty);
        Assert.Equal([Simulation.PositionStoreName], sim.Entities.Stores.Select(s => s.Name));
        Assert.Equal(5, sim.ComputeHashes().Count);

        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        Assert.Contains(sim.Log.GetEvents(), e => e.Level == Core.Logging.LogLevel.Warning && e.Category == "fog");
        Assert.Null(sim.CaptureState().World);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-05")]
    [InlineData(0, Difficulty.Normal)]
    [InlineData(32, Difficulty.Normal)]
    [InlineData(100, Difficulty.Normal)]
    [InlineData(4096, Difficulty.Normal)]
    [InlineData(64, (Difficulty)7)]
    public void InvalidOptionsAreRejected(int size, Difficulty difficulty) =>
        Assert.Throws<ArgumentException>(() => Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, size, difficulty)));

    [Fact]
    [Trait("Req", "FR-WORLD-02")]
    public void DefaultMapSizeIsTheMvpSize() => Assert.Equal(512, new NewGameOptions(1).MapSize);
}
