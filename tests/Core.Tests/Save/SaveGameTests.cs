using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Core.Determinism;
using Core.Entities;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Save;

public sealed class SaveGameTests
{
    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void SaveLoadSaveIsByteIdentical()
    {
        Simulation sim = SaveTestData.BuildBusy();

        byte[] first = SaveGame.ToBytes(sim);
        SaveLoadResult loaded = Load(first);
        byte[] second = SaveGame.ToBytes(loaded.Simulation!);

        Assert.Equal(first, second);
        Assert.Equal(SaveGame.ToBytes(sim), first);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void IndentedSaveRoundTripsAndIsReadable()
    {
        Simulation sim = SaveTestData.BuildBusy();
        var pretty = new SaveWriteOptions(Indented: true);

        byte[] first = SaveGame.ToBytes(sim, pretty);
        SaveLoadResult loaded = Load(first);
        (JsonObject header, string body) = Split(first);

        Assert.Equal(SaveFormat.CodecJson, (string?)header["codec"]);
        Assert.StartsWith("{\n  \"tick\": ", body, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', body);
        Assert.Equal(first, SaveGame.ToBytes(loaded.Simulation!, pretty));
        Assert.Equal(SaveGame.ToBytes(sim), SaveGame.ToBytes(loaded.Simulation!));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "NFR-04")]
    public void StateHashBeforeSaveEqualsHashAfterLoad()
    {
        Simulation sim = SaveTestData.BuildBusy();

        Simulation loaded = Load(SaveGame.ToBytes(sim)).Simulation!;

        Assert.Null(loaded.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(sim.ComputeHashes().Combined, loaded.ComputeHashes().Combined);
        Assert.Equal(sim.Now, loaded.Now);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "NFR-04")]
    public void NextEntityIdAfterLoadEqualsUninterruptedRun()
    {
        Simulation uninterrupted = SaveTestData.BuildBusy();
        Simulation loaded = Load(SaveGame.ToBytes(uninterrupted)).Simulation!;

        // The pending spawn and destroy run in the next tick in both; the destroy leaves a free slot to reuse.
        uninterrupted.Step(3);
        loaded.Step(3);
        Assert.True(uninterrupted.Entities.Registry.Capacity > uninterrupted.Entities.Registry.AliveCount, "Test needs a free slot.");

        Assert.Null(loaded.ComputeHashes().FindFirstMismatch(uninterrupted.ComputeHashes()));
        Assert.Equal(uninterrupted.Entities.Registry.Create(), loaded.Entities.Registry.Create());
        Assert.Equal(uninterrupted.Entities.Registry.Create(), loaded.Entities.Registry.Create());
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void FirstLineIsUncompressedJsonHeaderAndBodyIsBrotliJson()
    {
        Simulation sim = SaveTestData.BuildBusy(seed: 48879);

        byte[] bytes = SaveGame.ToBytes(sim);
        int newline = Array.IndexOf(bytes, (byte)'\n');
        string header = Encoding.UTF8.GetString(bytes, 0, newline);
        byte[] body = DecompressBrotli(bytes.AsSpan(newline + 1).ToArray());

        Assert.Equal(
            $"{{\"formatVersion\":{SaveFormat.CurrentVersion},\"gameVersion\":\"{SaveMapper.GameVersion}\",\"worldGenVersion\":2,\"seed\":48879,\"codec\":\"json-brotli\"}}",
            header);
        Assert.Equal(2, SaveFormat.WorldGenVersion);
        Assert.Equal(Core.WorldGen.WorldGenerator.Version, SaveFormat.WorldGenVersion);
        string json = Encoding.UTF8.GetString(body);
        Assert.StartsWith("{\"tick\":120,\"rng\":[{\"stream\":\"world_gen\",", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', json);

        SaveLoadResult loaded = Load(bytes);
        Assert.Equal(new SaveHeader(SaveFormat.CurrentVersion, SaveMapper.GameVersion, 2, 48879, SaveFormat.CodecBrotliJson), loaded.Header);
        Assert.Equal(SaveFormat.CurrentVersion, loaded.SourceFormatVersion);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void NewerFormatVersionIsRejectedWithClearError()
    {
        byte[] bytes = EditHeader(SaveGame.ToBytes(SaveTestData.BuildBusy()), h => h["formatVersion"] = SaveFormat.CurrentVersion + 1);

        SaveLoadResult result = SaveGame.Read(bytes, SimTestData.Real);

        Assert.False(result.Succeeded);
        Assert.Equal(SaveErrorKind.UnsupportedVersion, result.Error!.Kind);
        Assert.Contains("newer", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(SaveFormat.CurrentVersion + 1, result.SourceFormatVersion);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-WORLD-01")]
    [InlineData(1)] // the generator before the veins (P-21): its worlds can no longer be rebuilt
    [InlineData(-1)]
    [InlineData(99)]
    public void UnknownWorldGenVersionIsRejectedWithClearError(int worldGenVersion)
    {
        byte[] bytes = EditHeader(SaveGame.ToBytes(SaveTestData.BuildBusy()), h => h["worldGenVersion"] = worldGenVersion);

        SaveLoadResult result = SaveGame.Read(bytes, SimTestData.Real);

        Assert.False(result.Succeeded);
        Assert.Equal(SaveErrorKind.UnsupportedWorldGenVersion, result.Error!.Kind);
        Assert.Contains($"world generator v{worldGenVersion}", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(worldGenVersion, result.Header!.WorldGenVersion);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData(SaveFormat.NoWorldGenVersion)]
    [InlineData(SaveFormat.WorldGenVersion)]
    public void PreGeneratorAndCurrentWorldGenVersionsLoad(int worldGenVersion)
    {
        byte[] bytes = EditHeader(SaveGame.ToBytes(SaveTestData.BuildBusy()), h => h["worldGenVersion"] = worldGenVersion);

        SaveLoadResult result = SaveGame.Read(bytes, SimTestData.Real);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(worldGenVersion, result.Header!.WorldGenVersion);
        Assert.True(SaveFormat.IsLoadableWorldGenVersion(worldGenVersion));
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData("", SaveErrorKind.InvalidHeader)]
    [InlineData("no newline at all", SaveErrorKind.InvalidHeader)]
    [InlineData("not json\n", SaveErrorKind.InvalidHeader)]
    [InlineData("[1,2]\n", SaveErrorKind.InvalidHeader)]
    [InlineData("{\"codec\":\"json\"}\n{}", SaveErrorKind.InvalidHeader)]
    [InlineData("{\"formatVersion\":\"1\",\"codec\":\"json\"}\n{}", SaveErrorKind.InvalidHeader)]
    [InlineData("{\"formatVersion\":1}\n{}", SaveErrorKind.InvalidHeader)]
    [InlineData("{\"formatVersion\":1,\"codec\":\"zip\"}\n{}", SaveErrorKind.UnknownCodec)]
    [InlineData("{\"formatVersion\":1,\"codec\":\"json\"}\n{\"tick\":", SaveErrorKind.CorruptBody)]
    [InlineData("{\"formatVersion\":1,\"codec\":\"json\"}\n[]", SaveErrorKind.CorruptBody)]
    [InlineData("{\"formatVersion\":1,\"codec\":\"json\"}\n{}", SaveErrorKind.CorruptBody)]
    [InlineData("{\"formatVersion\":1,\"codec\":\"json-brotli\"}\nthis is not brotli", SaveErrorKind.CorruptBody)]
    [InlineData("{\"formatVersion\":0,\"codec\":\"json\"}\n{}", SaveErrorKind.MissingMigration)]
    public void MalformedFilesGiveErrorsNotExceptions(string text, SaveErrorKind expected)
    {
        SaveLoadResult result = SaveGame.Read(Encoding.UTF8.GetBytes(text), SimTestData.Real);

        Assert.False(result.Succeeded);
        Assert.Null(result.Simulation);
        Assert.Equal(expected, result.Error!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Message));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void HeaderLongerThanLimitIsNotASave()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string(' ', SaveFormat.MaxHeaderBytes + 1) + "\n{}");

        Assert.Equal(SaveErrorKind.InvalidHeader, SaveGame.Read(bytes, SimTestData.Real).Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void CorruptedOrTruncatedBodyIsReported()
    {
        byte[] good = SaveGame.ToBytes(SaveTestData.BuildBusy());
        int bodyStart = Array.IndexOf(good, (byte)'\n') + 1;

        byte[] truncated = good[..(bodyStart + ((good.Length - bodyStart) / 2))];
        byte[] garbage = [.. good[..bodyStart], .. Enumerable.Range(0, 200).Select(i => (byte)(i * 37))];
        byte[] flipped = (byte[])good.Clone();
        for (int i = bodyStart + 10; i < flipped.Length; i += 7)
        {
            flipped[i] ^= 0x5A;
        }

        Assert.Equal(SaveErrorKind.CorruptBody, SaveGame.Read(truncated, SimTestData.Real).Error!.Kind);
        Assert.Equal(SaveErrorKind.CorruptBody, SaveGame.Read(garbage, SimTestData.Real).Error!.Kind);
        SaveLoadResult flippedResult = SaveGame.Read(flipped, SimTestData.Real);
        Assert.False(flippedResult.Succeeded);
        Assert.True(flippedResult.Error!.Kind is SaveErrorKind.CorruptBody or SaveErrorKind.InvalidState, flippedResult.Error.ToString());
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData("unknown member", SaveErrorKind.CorruptBody)]
    [InlineData("missing member", SaveErrorKind.CorruptBody)]
    [InlineData("null member", SaveErrorKind.CorruptBody)]
    [InlineData("unknown command kind", SaveErrorKind.CorruptBody)]
    [InlineData("missing command kind", SaveErrorKind.CorruptBody)]
    [InlineData("wrong number type", SaveErrorKind.CorruptBody)]
    [InlineData("null pending entry", SaveErrorKind.InvalidState)]
    [InlineData("unknown rng stream", SaveErrorKind.InvalidState)]
    [InlineData("duplicate rng stream", SaveErrorKind.InvalidState)]
    [InlineData("missing rng stream", SaveErrorKind.InvalidState)]
    [InlineData("zero rng state", SaveErrorKind.InvalidState)]
    [InlineData("position on dead entity", SaveErrorKind.InvalidState)]
    [InlineData("negative tick", SaveErrorKind.InvalidState)]
    public void BodyThatDoesNotMatchTheFormatIsReported(string damage, SaveErrorKind expected)
    {
        byte[] bytes = EditBody(SaveGame.ToBytes(SaveTestData.BuildBusy(), new SaveWriteOptions(Indented: true)), body =>
        {
            JsonArray pending = body["commands"]!["pending"]!.AsArray();
            JsonArray rng = body["rng"]!.AsArray();
            switch (damage)
            {
                case "unknown member": body["extra"] = 1; break;
                case "missing member": body.Remove("tick"); break;
                case "null member": body["entities"] = null; break;
                case "unknown command kind": pending[0]!["command"]!["kind"] = "teleport"; break;
                case "missing command kind": pending[0]!["command"]!.AsObject().Remove("kind"); break;
                case "wrong number type": body["tick"] = "120"; break;
                case "null pending entry": pending[0] = null; break;
                case "unknown rng stream": rng[0]!["stream"] = "weather"; break;
                case "duplicate rng stream": rng[1]!["stream"] = "world_gen"; break;
                case "missing rng stream": rng.RemoveAt(5); break;
                case "zero rng state":
                    rng[0]!["s0"] = 0;
                    rng[0]!["s1"] = 0;
                    rng[0]!["s2"] = 0;
                    rng[0]!["s3"] = 0;
                    break;
                case "position on dead entity": body["components"]!["position"]!.AsArray().Add(new JsonObject { ["index"] = 999, ["generation"] = 1, ["x"] = 0, ["y"] = 0 }); break;
                case "negative tick": body["tick"] = -1; break;
                default: throw new ArgumentOutOfRangeException(nameof(damage));
            }
        });

        SaveLoadResult result = SaveGame.Read(bytes, SimTestData.Real);

        Assert.False(result.Succeeded, damage);
        Assert.Equal(expected, result.Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void DuplicatePropertyIsCorruption()
    {
        byte[] good = SaveGame.ToBytes(SaveTestData.BuildBusy(), new SaveWriteOptions(Indented: true));
        string text = Encoding.UTF8.GetString(good).Replace("\"tick\": 120,", "\"tick\": 120, \"tick\": 121,", StringComparison.Ordinal);

        Assert.Equal(SaveErrorKind.CorruptBody, SaveGame.Read(Encoding.UTF8.GetBytes(text), SimTestData.Real).Error!.Kind);
    }

    // FR-SAVE-02 / ADR 0007: the migration chain on a synthetic older version. v1 is the first shipped format, so
    // the "v0" layout and its migration exist only in this test; after it, the shipped v1 -> v2 migration runs too.
    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void OlderVersionIsMigratedBeforeBinding()
    {
        Simulation sim = SaveTestData.BuildBusy();
        byte[] v0 = MakeSyntheticV0(SaveGame.ToBytes(sim, new SaveWriteOptions(Indented: true)));

        SaveLoadResult withoutMigration = SaveGame.Read(v0, SimTestData.Real);
        SaveLoadResult migrated = SaveGame.Read(v0, SimTestData.Real, null, [new TestV0ToV1Migration(), .. SaveMigrations.All]);

        Assert.Equal(SaveErrorKind.MissingMigration, withoutMigration.Error!.Kind);
        Assert.Contains("v0", withoutMigration.Error.Message, StringComparison.Ordinal);
        Assert.True(migrated.Succeeded, migrated.Error?.Message);
        Assert.Equal(0, migrated.SourceFormatVersion);
        Assert.Equal(SaveFormat.CurrentVersion, migrated.Header!.FormatVersion);
        Assert.Null(migrated.Simulation!.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(SaveGame.ToBytes(sim), SaveGame.ToBytes(migrated.Simulation));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void ChainRunsEveryStepInOrderUpToTheCurrentVersion()
    {
        Simulation sim = SaveTestData.BuildBusy();
        byte[] v1 = AsFormatVersion(SaveGame.ToBytes(sim), 1);
        List<int> applied = [];

        // Pretend the build is at v3 (labels only; the body is today's layout): v1 -> v2 renames "commands", v2 -> v3 renames it back. Registered out of order.
        SaveLoadResult result = SaveGame.Read(v1, SimTestData.Real, null,
        [
            new RenameMigration(2, "queue", "commands", applied),
            new RenameMigration(1, "commands", "queue", applied),
        ], currentVersion: 3);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([1, 2], applied);
        Assert.Equal(1, result.SourceFormatVersion);
        Assert.Equal(3, result.Header!.FormatVersion);
        Assert.Null(result.Simulation!.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void BrokenChainsAreReported()
    {
        byte[] v1 = AsFormatVersion(SaveGame.ToBytes(SaveTestData.BuildBusy()), 1);
        List<int> applied = [];

        SaveLoadResult gap = SaveGame.Read(v1, SimTestData.Real, null, [new RenameMigration(2, "a", "b", applied)], currentVersion: 3);
        SaveLoadResult twice = SaveGame.Read(v1, SimTestData.Real, null,
            [new RenameMigration(1, "a", "b", applied), new RenameMigration(1, "a", "b", applied)], currentVersion: 2);

        Assert.Equal(SaveErrorKind.MissingMigration, gap.Error!.Kind);
        Assert.Contains("v1 to v2", gap.Error.Message, StringComparison.Ordinal);
        Assert.Equal(SaveErrorKind.MissingMigration, twice.Error!.Kind);
        Assert.Empty(applied);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void ShippedMigrationsFormAChainEndingAtTheCurrentVersion()
    {
        int[] from = SaveMigrations.All.Select(m => m.FromVersion).Order().ToArray();

        Assert.Equal(from.Distinct().Count(), from.Length);
        Assert.All(from, v => Assert.InRange(v, 1, SaveFormat.CurrentVersion - 1));
        int steps = SaveFormat.CurrentVersion - 1; // one migration per version step since v1
        Assert.Equal(steps, from.Length);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void AtomicWriteReplacesTheFileAndLeavesNoTemp()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"core-tests-save-{Environment.ProcessId}-{nameof(AtomicWriteReplacesTheFileAndLeavesNoTemp)}");
        string path = Path.Combine(dir, "nested", "slot" + SaveFormat.FileExtension);
        try
        {
            Simulation first = SaveTestData.BuildBusy(seed: 1);
            Simulation second = SaveTestData.BuildBusy(seed: 2);

            SaveGame.WriteFile(first, path);
            Assert.Equal(SaveGame.ToBytes(first), File.ReadAllBytes(path));
            SaveGame.WriteFile(second, path);

            Assert.Equal(SaveGame.ToBytes(second), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + AtomicFile.TempSuffix));
            SaveLoadResult loaded = SaveGame.ReadFile(path, SimTestData.Real);
            Assert.True(loaded.Succeeded, loaded.Error?.Message);
            Assert.Equal(2UL, loaded.Simulation!.Seed);

            using FileStream stream = File.OpenRead(path);
            Assert.True(SaveGame.Read(stream, SimTestData.Real).Succeeded);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void FailedAtomicWriteRemovesTempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"core-tests-save-{Environment.ProcessId}-{nameof(FailedAtomicWriteRemovesTempFile)}");
        Directory.CreateDirectory(dir);
        try
        {
            // The target is a directory, so the final rename fails.
            Exception? error = Record.Exception(() => AtomicFile.WriteAllBytes(dir, [1, 2, 3]));

            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.False(File.Exists(dir + AtomicFile.TempSuffix));
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void MissingFileIsAnIoError()
    {
        SaveLoadResult result = SaveGame.ReadFile(Path.Combine(Path.GetTempPath(), "core-tests-no-such-save.sav"), SimTestData.Real);

        Assert.Equal(SaveErrorKind.Io, result.Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void ReadFromStreamThatFailsIsAnIoError()
    {
        SaveLoadResult result = SaveGame.Read(new FailingStream(), SimTestData.Real);

        Assert.Equal(SaveErrorKind.Io, result.Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void WriteToStreamMatchesToBytes()
    {
        Simulation sim = SaveTestData.BuildBusy();
        using var stream = new MemoryStream();

        SaveGame.Write(sim, stream);

        Assert.Equal(SaveGame.ToBytes(sim), stream.ToArray());
    }

    internal static SaveLoadResult Load(byte[] bytes)
    {
        SaveLoadResult result = SaveGame.Read(bytes, SimTestData.Real);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result;
    }

    private static (JsonObject Header, string Body) Split(byte[] bytes)
    {
        int newline = Array.IndexOf(bytes, (byte)'\n');
        return (JsonNode.Parse(bytes.AsSpan(0, newline))!.AsObject(), Encoding.UTF8.GetString(bytes, newline + 1, bytes.Length - newline - 1));
    }

    private static byte[] Join(JsonObject header, string body) =>
        Encoding.UTF8.GetBytes(header.ToJsonString() + "\n" + body);

    private static byte[] EditHeader(byte[] bytes, Action<JsonObject> edit)
    {
        int newline = Array.IndexOf(bytes, (byte)'\n');
        JsonObject header = JsonNode.Parse(bytes.AsSpan(0, newline))!.AsObject();
        edit(header);
        return [.. Encoding.UTF8.GetBytes(header.ToJsonString()), .. bytes.AsSpan(newline)];
    }

    // Works on indented (codec "json") saves.
    private static byte[] EditBody(byte[] bytes, Action<JsonObject> edit)
    {
        (JsonObject header, string body) = Split(bytes);
        JsonObject tree = JsonNode.Parse(body)!.AsObject();
        edit(tree);
        return Join(header, tree.ToJsonString());
    }

    // The synthetic "v0" layout: the command queue was called "commandQueue" and its counter "next".
    private static byte[] AsFormatVersion(byte[] bytes, int version) => EditHeader(bytes, h => h["formatVersion"] = version);

    // Today's save of a core simulation, turned into the v1 layout (no game, fog, revealer) and then into "v0".
    private static byte[] MakeSyntheticV0(byte[] indentedCurrent)
    {
        (JsonObject header, string body) = Split(indentedCurrent);
        JsonObject tree = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(0, (int)tree["game"]!["mapSize"]!);
        tree.Remove("game");
        tree.Remove("fog");
        tree["components"]!.AsObject().Remove("revealer");
        tree.Remove("commands", out JsonNode? commands);
        commands!.AsObject().Remove("nextSequence", out JsonNode? next);
        commands.AsObject().Add("next", next);
        tree.Add("commandQueue", commands);
        header["formatVersion"] = 0;
        return Join(header, tree.ToJsonString());
    }

    private static byte[] DecompressBrotli(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private sealed class TestV0ToV1Migration : ISaveMigration
    {
        public int FromVersion => 0;

        public void Migrate(JsonObject header, JsonObject body)
        {
            body.Remove("commandQueue", out JsonNode? queue);
            queue!.AsObject().Remove("next", out JsonNode? next);
            var commands = new JsonObject { ["nextSequence"] = next };
            queue.AsObject().Remove("pending", out JsonNode? pending);
            commands["pending"] = pending;
            body["commands"] = commands;
        }
    }

    private sealed class RenameMigration(int from, string oldName, string newName, List<int> applied) : ISaveMigration
    {
        public int FromVersion => from;

        public void Migrate(JsonObject header, JsonObject body)
        {
            applied.Add(from);
            body.Remove(oldName, out JsonNode? value);
            body[newName] = value;
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Disk on fire.");

        public override int Read(Span<byte> buffer) => throw new IOException("Disk on fire.");

        public override void CopyTo(Stream destination, int bufferSize) => throw new IOException("Disk on fire.");
    }
}
