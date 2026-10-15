using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Content;
using Json.Schema;

namespace Core.Tests.Content;

/// <summary>Loading and validation of <c>data/worldgen</c> (ADR 0006, ADR 0008).</summary>
public sealed class WorldGenContentTests
{
    private const string TimeJson = """
        { "items": [ { "id": "time", "ticksPerSecond": 10, "realMinutesPerGameDay": 15, "nightPermille": 333,
          "daysPerWeek": 7, "daysPerSeason": 15, "seasonsPerYear": 4 } ] }
        """;

    private static readonly string _worldJson = File.ReadAllText(Path.Combine(TestPaths.Data, "worldgen", "world.json"));
    private static readonly string _fogJson = File.ReadAllText(Path.Combine(TestPaths.Data, "balance", "fog.json"));
    private static readonly string _savesJson = File.ReadAllText(Path.Combine(TestPaths.Data, "balance", "saves.json"));
    // The hero data references items; tests with their own item lists use the real hero file without gathering entries.
    private static readonly string _heroJson = NoGathering(File.ReadAllText(Path.Combine(TestPaths.Data, "balance", "hero.json")));
    private static readonly string _constructionJson = File.ReadAllText(Path.Combine(TestPaths.Data, "balance", "construction.json"));

    private static string NoGathering(string json)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        root["items"]![0]!["gathering"] = new System.Text.Json.Nodes.JsonArray();
        return root.ToJsonString();
    }

    private static InMemoryContentSource Base() =>
        new InMemoryContentSource().Add("balance/time.json", TimeJson).Add("balance/fog.json", _fogJson).Add("balance/saves.json", _savesJson)
            .Add("balance/hero.json", _heroJson).Add("balance/construction.json", _constructionJson);

    private static ContentLoadResult LoadWorld(string worldJson) =>
        ContentCatalog.Load(Base().Add("worldgen/world.json", worldJson));

    // Applies an edit to the first definition of the real file.
    private static string Edited(Action<JsonObject> edit)
    {
        JsonObject root = JsonNode.Parse(_worldJson)!.AsObject();
        edit(root["items"]![0]!.AsObject());
        return root.ToJsonString();
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    [Trait("Req", "FR-WORLD-03")]
    public void RealParametersLoad()
    {
        WorldGenParamsDef world = Assert.IsType<ContentCatalog>(LoadWorld(_worldJson).Catalog).WorldGen;

        Assert.Equal("world", world.Id);
        Assert.Equal(["mountain", "mountain", "wasteland", "wasteland", "forest", "meadow"], world.Biomes.Rules.Select(r => r.Biome));
        Assert.True(world.Biomes.Rules[^1].IsCatchAll);
        Assert.False(world.Biomes.Rules[0].IsCatchAll);
        Assert.Empty(world.CheckConsistency());
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void NestedConstraintViolationsReportFullJsonPaths()
    {
        ContentLoadResult result = LoadWorld(Edited(d =>
        {
            d["height"]!["octaves"] = 0;
            d["rivers"]!["minLength"] = 0;
            d["biomes"]!["rules"]![1]!["biome"] = "lava";
            d["biomes"]!["rules"]![2]!["maxMoisture"] = 1001;
        }));

        Assert.Null(result.Catalog);
        Assert.All(result.Errors, e => Assert.Equal(ContentErrorKind.Validation, e.Kind));
        Assert.All(result.Errors, e => Assert.Equal("worldgen/world.json", e.File));
        Assert.Equal(
            ["$.items[0].height.octaves", "$.items[0].biomes.rules[1].biome", "$.items[0].biomes.rules[2].maxMoisture", "$.items[0].rivers.minLength"],
            result.Errors.Select(e => e.JsonPath));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void CrossFieldChecksRunInTheReferencePass()
    {
        ContentLoadResult result = LoadWorld(Edited(d =>
        {
            d["height"]!["step2Permille"] = 500; // below step1
            d["biomes"]!["rules"]![0]!["minHeight"] = 3;
            d["biomes"]!["rules"]![0]!["maxHeight"] = 2;
            d["biomes"]!["rules"]!.AsArray().RemoveAt(5); // no catch-all any more
        }));

        Assert.Null(result.Catalog);
        Assert.All(result.Errors, e => Assert.Equal(ContentErrorKind.Validation, e.Kind));
        Assert.Equal(["$.items[0].height", "$.items[0].biomes.rules[0]", "$.items[0].biomes.rules"], result.Errors.Select(e => e.JsonPath));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-WORLD-09")]
    public void RealPlacementParametersLoad()
    {
        WorldGenParamsDef world = Assert.IsType<ContentCatalog>(LoadWorld(_worldJson).Catalog).WorldGen;

        Assert.Equal(["forest", "meadow"], world.Resources.Trees.Biomes.Select(b => b.Biome));
        Assert.Equal(["mountain"], world.Resources.Ore.Biomes.Select(b => b.Biome));
        Assert.Equal(["mountain"], world.Resources.Coal.Biomes.Select(b => b.Biome));
        Assert.Equal(["mountain"], world.Resources.Veins.Biomes.Select(b => b.Biome));
        Assert.Equal(["ore", "coal"], world.Resources.Veins.Kinds.Select(k => k.Kind));
        Assert.Equal(["chest", "ruins", "lair"], world.PointsOfInterest.Kinds.Select(k => k.Kind));
        // Lengths in tiles on the 32x16 grid: twice the 64x32 values (40, 120), areas four times (1600), P-07.
        Assert.Equal(6400, world.PointsOfInterest.TilesPerPoi);
        Assert.Equal(80, world.Start.ZoneRadius);
        Assert.Equal(["orc", "human"], world.Settlements.Factions.Select(f => f.Faction));
        Assert.Equal(240, world.Settlements.Factions[0].MinStartDistance);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void PlacementCrossFieldChecksRunInTheReferencePass()
    {
        ContentLoadResult result = LoadWorld(Edited(d =>
        {
            d["resources"]!["trees"]!["stockMin"] = 100; // above stockMax 80
            d["resources"]!["ore"]!["biomes"]!.AsArray().Add(JsonNode.Parse("""{ "biome": "mountain", "chancePermille": 10, "radius": 2 }"""));
            d["pointsOfInterest"]!["kinds"] = new JsonArray();
            d["settlements"]!["siteSize"] = 8;
            d["settlements"]!["factions"]![1]!["minStartDistance"] = 30; // inside the start zone
        }));

        Assert.Null(result.Catalog);
        Assert.All(result.Errors, e => Assert.Equal(ContentErrorKind.Validation, e.Kind));
        Assert.Equal(
            [
                "$.items[0].resources.trees", "$.items[0].resources.ore.biomes", "$.items[0].pointsOfInterest.kinds",
                "$.items[0].settlements.siteSize", "$.items[0].settlements.factions[1].minStartDistance",
            ],
            result.Errors.Select(e => e.JsonPath));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    [Trait("Req", "FR-WORLD-04")]
    public void VeinCrossFieldChecksRunInTheReferencePass()
    {
        ContentLoadResult result = LoadWorld(Edited(d =>
        {
            JsonNode resources = d["resources"]!;
            JsonNode veins = resources["veins"]!;
            veins["radiusMin"] = 5; // above radiusMax 4
            veins["minSpacing"] = 8; // two veins of radius 4 could touch
            veins["kinds"]![0]!["stockMultiplierMinPermille"] = 9000; // above the max 6000
            veins["kinds"]![0]!["stockMultiplierMaxPermille"] = 6000;
            resources["coal"]!["stockMax"] = 20000; // 10150 x 10 does not fit 16 bits
            resources["ore"]!["stockMin"] = 10;
            resources["ore"]!["stockMax"] = 20; // 15 x 1 cannot give each of 81 tiles a stock
            veins["kinds"]!.AsArray().Add(JsonNode.Parse("""{ "kind": "ore", "weight": 1, "stockMultiplierMinPermille": 1000, "stockMultiplierMaxPermille": 1000 }"""));
        }));

        Assert.Null(result.Catalog);
        Assert.All(result.Errors, e => Assert.Equal(ContentErrorKind.Validation, e.Kind));
        Assert.Equal(
            [
                "$.items[0].resources.veins.kinds", "$.items[0].resources.veins", "$.items[0].resources.veins.minSpacing",
                "$.items[0].resources.veins.kinds[0]", "$.items[0].resources.veins.kinds[1]", "$.items[0].resources.veins.kinds[2]",
            ],
            result.Errors.Select(e => e.JsonPath));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void PlacementNamesAreValidatedWithFullJsonPaths()
    {
        ContentLoadResult result = LoadWorld(Edited(d =>
        {
            d["resources"]!["stone"]!["biomes"]![0]!["biome"] = "water";
            d["pointsOfInterest"]!["kinds"]![2]!["kind"] = "portal";
            d["start"]!["biome"] = "lava";
            d["settlements"]!["factions"]![0]!["faction"] = "elf";
        }));

        Assert.Null(result.Catalog);
        Assert.Equal(
            [
                "$.items[0].resources.stone.biomes[0].biome", "$.items[0].pointsOfInterest.kinds[2].kind",
                "$.items[0].start.biome", "$.items[0].settlements.factions[0].faction",
            ],
            result.Errors.Select(e => e.JsonPath));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void StrictParsingRejectsUnknownAndMissingMembers()
    {
        // A file that does not parse also leaves the catalog without its WorldGenParamsDef.
        foreach (string json in new[] { Edited(d => d["climate"]!["rain"] = 1), Edited(d => d.Remove("rivers")) })
        {
            ContentLoadResult result = LoadWorld(json);
            Assert.Null(result.Catalog);
            Assert.Equal([ContentErrorKind.Parse, ContentErrorKind.Missing], result.Errors.Select(e => e.Kind));
            Assert.Equal("worldgen/world.json", result.Errors[0].File);
        }

        ContentError nullRule = Assert.Single(LoadWorld(Edited(d => d["biomes"]!["rules"]![0] = null)).Errors);
        Assert.Equal(ContentErrorKind.Parse, nullRule.Kind);
        Assert.Equal("$.items[0].biomes.rules[0]", nullRule.JsonPath);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void ExactlyOneWorldGenDefinitionIsRequired()
    {
        ContentLoadResult missing = ContentCatalog.Load(Base());
        ContentError error = Assert.Single(missing.Errors);
        Assert.Equal(ContentErrorKind.Missing, error.Kind);
        Assert.Contains("WorldGenParamsDef", error.Message, StringComparison.Ordinal);

        string second = Edited(d => d["id"] = "world_two");
        ContentLoadResult twice = ContentCatalog.Load(Base().Add("worldgen/world.json", _worldJson).Add("worldgen/two.json", second));
        Assert.Equal(ContentErrorKind.Validation, Assert.Single(twice.Errors).Kind);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void SchemaRejectsBadWorldGenData()
    {
        JsonSchema schema = JsonSchema.Build(JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Data, "schema", "worldgen.schema.json"))).RootElement);
        string[] bad =
        [
            Edited(d => d["height"]!["octaves"] = 0),
            Edited(d => d["biomes"]!["rules"]![0]!["biome"] = "water"),
            Edited(d => d["climate"]!["extra"] = 1),
            Edited(d => d.Remove("height")),
            Edited(d => d.Remove("settlements")),
            Edited(d => d["pointsOfInterest"]!["kinds"]![0]!["kind"] = "portal"),
            Edited(d => d["settlements"]!["factions"]![0]!["faction"] = "elf"),
            Edited(d => d["start"]!["biome"] = "water"),
            Edited(d => d["resources"]!["trees"]!["extra"] = 1),
            Edited(d => d["resources"]!.AsObject().Remove("coal")),
            Edited(d => d["resources"]!["veins"]!["kinds"]![0]!["kind"] = "gold"),
            Edited(d => d["resources"]!["veins"]!["biomes"]![0]!["biome"] = "water"),
        ];
        foreach (string json in bad)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid, json);
        }

        using JsonDocument good = JsonDocument.Parse(_worldJson);
        Assert.True(schema.Evaluate(good.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid);
    }
}
