using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Core.Content;
using Core.Fog;
using Core.Save;
using Core.Sim;
using Core.Tests.Content;
using Core.Tests.Sim;

namespace Core.Tests.Save;

/// <summary>Saves of games with a world: format v2, fog, difficulty, regeneration and drift detection (FR-SAVE-02/04/05).</summary>
public sealed class SaveWorldTests
{
    private static SaveLoadResult Load(byte[] bytes, ContentCatalog? content = null) => SaveGame.Read(bytes, content ?? SimTestData.Real);

    private static (JsonObject Header, JsonObject Body) Split(byte[] indented)
    {
        int newline = Array.IndexOf(indented, (byte)'\n');
        return (JsonNode.Parse(indented.AsSpan(0, newline))!.AsObject(),
            JsonNode.Parse(Encoding.UTF8.GetString(indented, newline + 1, indented.Length - newline - 1))!.AsObject());
    }

    private static byte[] Edit(Simulation sim, Action<JsonObject, JsonObject> edit)
    {
        (JsonObject header, JsonObject body) = Split(SaveGame.ToBytes(sim, new SaveWriteOptions(Indented: true)));
        edit(header, body);
        return Encoding.UTF8.GetBytes(header.ToJsonString() + "\n" + body.ToJsonString());
    }

    private static JsonObject Body(byte[] compressed)
    {
        int newline = Array.IndexOf(compressed, (byte)'\n');
        using var input = new MemoryStream(compressed, newline + 1, compressed.Length - newline - 1);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return JsonNode.Parse(brotli)!.AsObject();
    }

    [Theory]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-SAVE-05")]
    [Trait("Req", "FR-WORLD-06")]
    [InlineData(Difficulty.Peaceful)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void SaveLoadSaveIsByteIdenticalWithWorldAndFog(Difficulty difficulty)
    {
        Simulation sim = SaveTestData.BuildWorld(difficulty: difficulty);

        byte[] first = SaveGame.ToBytes(sim);
        SaveLoadResult loaded = Load(first);
        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Simulation restored = loaded.Simulation!;

        Assert.Equal(first, SaveGame.ToBytes(restored));
        Assert.Null(restored.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(difficulty, restored.Difficulty);
        Assert.Equal(sim.World!.MapHash, restored.World!.MapHash);
        Assert.Equal(sim.World.Fog.ExploredBits.ToArray(), restored.World.Fog.ExploredBits.ToArray());
        Assert.Equal(sim.World.Fog.VisibleBits.ToArray(), restored.World.Fog.VisibleBits.ToArray());
        Assert.Equal(sim.World.Revealers.GetEntries(), restored.World.Revealers.GetEntries());

        byte[] pretty = SaveGame.ToBytes(sim, new SaveWriteOptions(Indented: true));
        Assert.Equal(pretty, SaveGame.ToBytes(Load(pretty).Simulation!, new SaveWriteOptions(Indented: true)));

        sim.Step(25);
        restored.Step(25);
        Assert.Null(restored.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(0, restored.World.Fog.CountStates().Unknown); // the pending reveal ran
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-SAVE-05")]
    public void BodyHoldsGameParametersFogRunsAndRevealersButNoMap()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(77, 64, Difficulty.Hard));
        byte[] bytes = SaveGame.ToBytes(sim);
        JsonObject body = Body(bytes);

        Assert.Equal(
            ["tick", "rng", "entities", "components", "commands", "game", "fog", "tiles", "settlement", "jobs"],
            body.Select(p => p.Key));
        Assert.Equal(["position", "revealer", "hero", "inventory", "mover", "building", "resident", "job", "workOutput", "storageFilter"],
            body["components"]!.AsObject().Select(p => p.Key));
        Assert.Empty(body["components"]!["building"]!.AsArray());
        Assert.Empty(body["components"]!["resident"]!.AsArray());
        Assert.Equal("camp", (string?)body["settlement"]!["reachedEra"]);
        Assert.Equal(0, (int)body["components"]!["hero"]![0]!["index"]!);
        Assert.Equal("idle", (string?)body["components"]!["hero"]![0]!["activity"]);
        Assert.Equal(SimTestData.Real.Hero.BackpackStacks, (int)body["components"]!["inventory"]![0]!["backpackSize"]!);
        Assert.Empty(body["components"]!["mover"]![0]!["path"]!.AsArray());
        Assert.Empty(body["tiles"]!["resources"]!.AsArray());
        Assert.Empty(body["tiles"]!["occupancy"]!.AsArray());
        Assert.Equal("hard", (string?)body["game"]!["difficulty"]);
        Assert.Equal(64, (int)body["game"]!["mapSize"]!);
        Assert.Equal(sim.World!.MapHash, (ulong)body["game"]!["mapHash"]!);
        Assert.Equal("hero", (string?)body["components"]!["revealer"]![0]!["kind"]);
        JsonArray runs = body["fog"]!["exploredRuns"]!.AsArray();
        Assert.Equal(64 * 64, runs.Sum(r => (int)r!));
        int rows = (2 * SimTestData.Real.Fog.HeroDay) + 1;
        Assert.InRange(runs.Count, 3, (2 * rows) + 1); // two runs per row of the hero's day disc (radius 24), at most
        Assert.Contains("\"worldGenVersion\":2,", Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)'\n')), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-WORLD-01")]
    public void FullSizeWorldSaveStaysSmall()
    {
        // The smallest player size (P-07); the 2048² default is measured in LargeMapMeasureTests (Benchmark), because
        // generating it twice (new game and load) takes about 15 s in Debug.
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(1, 1024));
        byte[] fresh = SaveGame.ToBytes(sim);
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        byte[] revealed = SaveGame.ToBytes(sim);

        // The 1024² map is regenerated, not stored; the fog is a handful of runs.
        Assert.InRange(fresh.Length, 1, 2048);
        Assert.InRange(revealed.Length, 1, 2048);
        Assert.Equal([0, 1024 * 1024], Body(revealed)["fog"]!["exploredRuns"]!.AsArray().Select(r => (int)r!));
        Assert.Equal(revealed, SaveGame.ToBytes(Load(revealed).Simulation!));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-WORLD-01")]
    public void TamperedMapHashIsReportedAsGeneratorMismatch()
    {
        Simulation sim = SaveTestData.BuildWorld();
        byte[] bytes = Edit(sim, (_, body) => body["game"]!["mapHash"] = sim.World!.MapHash ^ 1);

        SaveLoadResult result = Load(bytes);

        Assert.False(result.Succeeded);
        Assert.Equal(SaveErrorKind.WorldGenMismatch, result.Error!.Kind);
        Assert.Contains(ContentHash.Format(sim.World!.MapHash ^ 1), result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("version bump", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-WORLD-01")]
    public void ChangedGeneratorDataIsReportedAsGeneratorMismatch()
    {
        // Simulates generator drift: same seed and version, different data/worldgen.
        InMemoryContentSource source = new();
        foreach (ContentFile file in TestPaths.LoadDataSource().GetFiles())
        {
            string text = file.Path == "worldgen/world.json"
                ? ReplaceFirst(file.Text, "\"stockMax\": 80", "\"stockMax\": 81")
                : file.Text;
            source.Add(file.Path, text);
        }

        ContentCatalog drifted = ContentCatalog.Load(source).Catalog!;
        SaveLoadResult result = Load(SaveGame.ToBytes(SaveTestData.BuildWorld()), drifted);

        Assert.Equal(SaveErrorKind.WorldGenMismatch, result.Error!.Kind);
        Assert.NotNull(result.Header);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-WORLD-01")]
    public void WorldSaveOfThePreviousGeneratorIsRejectedBeforeTheMapIsRebuilt()
    {
        // v2 added the veins and cut the density (P-21): a v1 world save fails at the header, not with a map mismatch.
        byte[] bytes = Edit(SaveTestData.BuildWorld(), (header, _) => header["worldGenVersion"] = SaveFormat.WorldGenVersion - 1);

        SaveLoadResult result = Load(bytes);

        Assert.Equal(2, SaveFormat.WorldGenVersion);
        Assert.Equal(SaveErrorKind.UnsupportedWorldGenVersion, result.Error!.Kind);
        Assert.Contains("world generator v1", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void WorldSaveWithoutAGeneratorVersionIsRejected()
    {
        byte[] bytes = Edit(SaveTestData.BuildWorld(), (header, _) => header["worldGenVersion"] = SaveFormat.NoWorldGenVersion);

        Assert.Equal(SaveErrorKind.UnsupportedWorldGenVersion, Load(bytes).Error!.Kind);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData("difficulty")]
    [InlineData("runs-sum")]
    [InlineData("runs-zero")]
    [InlineData("revealer-kind")]
    [InlineData("revealer-dead")]
    [InlineData("map-size")]
    public void InvalidWorldPartsAreInvalidState(string damage)
    {
        byte[] bytes = Edit(SaveTestData.BuildWorld(), (_, body) =>
        {
            JsonArray runs = body["fog"]!["exploredRuns"]!.AsArray();
            switch (damage)
            {
                case "difficulty": body["game"]!["difficulty"] = "nightmare"; break;
                case "runs-sum": runs.Add(5); break;
                case "runs-zero": runs.Add(0); break;
                case "revealer-kind": body["components"]!["revealer"]![0]!["kind"] = "dragon"; break;
                case "revealer-dead": body["components"]!["revealer"]![0]!["generation"] = 7; break;
                case "map-size": body["game"]!["mapSize"] = 50; break;
            }
        });

        SaveLoadResult result = Load(bytes);

        Assert.False(result.Succeeded, damage);
        Assert.Equal(SaveErrorKind.InvalidState, result.Error!.Kind);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData("hard")]
    [InlineData("runs")]
    [InlineData("revealer")]
    [InlineData("hash")]
    public void CoreSaveCannotCarryWorldParts(string damage)
    {
        byte[] bytes = Edit(SaveTestData.BuildBusy(), (_, body) =>
        {
            switch (damage)
            {
                case "hard": body["game"]!["difficulty"] = "hard"; break;
                case "runs": body["fog"]!["exploredRuns"] = new JsonArray(1); break;
                case "revealer": body["components"]!["revealer"] = new JsonArray(new JsonObject { ["index"] = 0, ["generation"] = 1, ["kind"] = "hero" }); break;
                case "hash": body["game"]!["mapHash"] = 5; break;
            }
        });

        Assert.Equal(SaveErrorKind.InvalidState, Load(bytes).Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void MigrationRejectsAV1BodyThatAlreadyHasV2Members()
    {
        byte[] bytes = Edit(SaveTestData.BuildBusy(), (header, _) => header["formatVersion"] = 1);

        SaveLoadResult result = Load(bytes);

        Assert.Equal(SaveErrorKind.CorruptBody, result.Error!.Kind);
        Assert.Contains("v2 members", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-05")]
    public void RestoreRejectsDifficultyWithoutAWorld()
    {
        SimulationState state = SaveTestData.BuildBusy().CaptureState() with { Difficulty = Difficulty.Hard };

        Assert.Throws<ArgumentException>(() => Simulation.Restore(SimTestData.Real, state));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-06")]
    public void VisibleIsRecomputedOnLoadNotStored()
    {
        Simulation sim = SaveTestData.BuildWorld();
        SimulationState state = sim.CaptureState();

        Simulation restored = Simulation.Restore(SimTestData.Real, state);

        Assert.Equal(sim.World!.Fog.CountStates(), restored.World!.Fog.CountStates());
        Assert.Equal(FogState.Visible, restored.World.Fog.GetState(restored.World.Map.Start.X, restored.World.Map.Start.Y));
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int index = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, oldValue);
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}
