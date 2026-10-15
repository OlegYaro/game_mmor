using Core.Content;

namespace Core.Tests.Content;

public sealed class ContentCatalogTests
{
    private const string TimeJson = """
        { "items": [ { "id": "time", "ticksPerSecond": 10, "realMinutesPerGameDay": 15, "nightPermille": 333,
          "daysPerWeek": 7, "daysPerSeason": 15, "seasonsPerYear": 4 } ] }
        """;

    // The world generator parameters and fog radii are required too; tests that do not care about them use the real files.
    private static readonly string _worldGenJson = File.ReadAllText(Path.Combine(TestPaths.Data, "worldgen", "world.json"));
    private static readonly string _fogJson = File.ReadAllText(Path.Combine(TestPaths.Data, "balance", "fog.json"));
    private static readonly string _savesJson = File.ReadAllText(Path.Combine(TestPaths.Data, "balance", "saves.json"));

    private static InMemoryContentSource WithTime() =>
        new InMemoryContentSource().Add("balance/time.json", TimeJson).Add("balance/fog.json", _fogJson).Add("balance/saves.json", _savesJson).Add("worldgen/world.json", _worldGenJson);

    private static ContentLoadResult LoadItems(string itemsJson) =>
        ContentCatalog.Load(WithTime().Add("items/x.json", itemsJson));

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void RealDataDirectoryLoads()
    {
        ContentLoadResult result = ContentCatalog.Load(TestPaths.LoadDataSource());

        Assert.Empty(result.Errors);
        ContentCatalog catalog = result.Catalog!;
        Assert.Equal(10, catalog.Time.TicksPerSecond);
        Assert.Equal(15, catalog.Time.RealMinutesPerGameDay);
        Assert.Equal(333, catalog.Time.NightPermille);
        Assert.Equal(7, catalog.Time.DaysPerWeek);
        Assert.Equal(15, catalog.Time.DaysPerSeason);
        Assert.Equal(4, catalog.Time.SeasonsPerYear);
        Assert.Equal(["stone", "wheat", "wood"], catalog.Items.Select(i => i.Id));
        Assert.Equal(new FogBalanceDef("fog", HeroDay: 24, HeroNight: 12, VillagerDay: 16, VillagerNight: 8, TowerDay: 32, TowerNight: 32), catalog.Fog);
        Assert.Equal(new SaveBalanceDef("saves", ManualSlots: 5, AutosavesKept: 3), catalog.Saves);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void DefIdsAreAssignedBySortedStringId()
    {
        ContentCatalog catalog = ContentCatalog.Load(TestPaths.LoadDataSource()).Catalog!;

        // Sorted ordinal over all 31 ids (balance, items, world, eras, races, professions, relation reasons, buildings).
        Assert.True(catalog.TryGetId("fog", out DefId fog));
        Assert.True(catalog.TryGetId("stone", out DefId stone));
        Assert.True(catalog.TryGetId("time", out DefId time));
        Assert.True(catalog.TryGetId("wood", out DefId wood));
        Assert.True(catalog.TryGetId("world", out DefId world));
        Assert.Equal(new DefId(8), fog);
        Assert.Equal(new DefId(22), stone);
        Assert.Equal(new DefId(25), time);
        Assert.Equal(new DefId(30), wood);
        Assert.Equal(new DefId(31), world);
        Assert.Equal("wood", catalog.Get(wood).Id);
        Assert.Equal(31, catalog.Count);
        Assert.False(catalog.TryGetId("nope", out _));
        Assert.Throws<KeyNotFoundException>(() => catalog.Get(new DefId(0)));
        Assert.Throws<InvalidCastException>(() => catalog.Get<ItemDef>(time));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void DefIdsDoNotDependOnFileOrderOrContentOfOtherFiles()
    {
        string a = """{ "items": [ { "id": "b_item", "stackSize": 1, "weight": 1 } ] }""";
        string b = """{ "items": [ { "id": "a_item", "stackSize": 1, "weight": 1 } ] }""";
        ContentCatalog first = ContentCatalog.Load(WithTime().Add("items/1.json", a).Add("items/2.json", b)).Catalog!;
        ContentCatalog second = ContentCatalog.Load(WithTime().Add("items/2.json", b).Add("items/1.json", a)).Catalog!;

        Assert.Equal(first.All.Select(d => d.Id), second.All.Select(d => d.Id));
        first.TryGetId("a_item", out DefId id1);
        second.TryGetId("a_item", out DefId id2);
        Assert.Equal(id1, id2);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void TypedLookupByIdWorks()
    {
        ContentCatalog catalog = ContentCatalog.Load(TestPaths.LoadDataSource()).Catalog!;

        Assert.True(catalog.TryGet("wood", out ItemDef wood));
        Assert.Equal("item.wood.name", wood.EffectiveNameKey);
        Assert.False(catalog.TryGet("time", out ItemDef _));
        Assert.False(catalog.TryGet("missing", out ItemDef _));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void UnknownMemberIsRejectedWithFileAndPath()
    {
        ContentLoadResult result = LoadItems("""{ "items": [ { "id": "a", "stackSize": 1, "weight": 1, "bogus": 3 } ] }""");

        Assert.Null(result.Catalog);
        ContentError error = Assert.Single(result.Errors);
        Assert.Equal(ContentErrorKind.Parse, error.Kind);
        Assert.Equal("items/x.json", error.File);
        Assert.Contains("bogus", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void MissingRequiredMemberIsRejected()
    {
        ContentLoadResult result = LoadItems("""{ "items": [ { "id": "a", "weight": 1 } ] }""");

        ContentError error = Assert.Single(result.Errors);
        Assert.Equal(ContentErrorKind.Parse, error.Kind);
        Assert.Contains("stackSize", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void MalformedJsonAndWrongTypesAreReportedWithJsonPath()
    {
        ContentLoadResult result = LoadItems("""{ "items": [ { "id": "a", "stackSize": "many", "weight": 1 } ] }""");

        ContentError error = Assert.Single(result.Errors);
        Assert.StartsWith("$.items[0]", error.JsonPath, StringComparison.Ordinal);

        Assert.Single(LoadItems("{ not json").Errors);
        Assert.Single(LoadItems("""{ "items": [ ], }""").Errors); // trailing comma
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void ConstraintViolationsAreCollected()
    {
        ContentLoadResult result = LoadItems("""
            { "items": [ { "id": "Bad Id", "stackSize": 0, "weight": 1 }, { "id": "ok:id_1", "stackSize": 5, "weight": 1 } ] }
            """);

        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal(ContentErrorKind.Validation, e.Kind));
        Assert.Contains(result.Errors, e => e.JsonPath == "$.items[0].id");
        Assert.Contains(result.Errors, e => e.JsonPath == "$.items[0].stackSize");
    }

    [Theory]
    [InlineData("wood", true)]
    [InlineData("mod_a:wood_2", true)]
    [InlineData("Wood", false)]
    [InlineData("2wood", false)]
    [InlineData("a:b:c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [Trait("Req", "FR-MOD-01")]
    public void IdFormatIsValidated(string? id, bool expected) => Assert.Equal(expected, ContentId.IsValid(id));

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void AllErrorsAreCollectedAcrossFiles()
    {
        InMemoryContentSource source = new InMemoryContentSource()
            .Add("items/a.json", """{ "items": [ { "id": "wood", "stackSize": 1, "weight": 1 } ] }""")
            .Add("items/b.json", """{ "items": [ { "id": "wood", "stackSize": 1, "weight": 1 } ] }""")
            .Add("items/c.json", """{ "items": [ { "id": "x", "stackSize": 1 } ] }""")
            .Add("other/d.json", "{}");
        ContentLoadResult result = ContentCatalog.Load(source);

        Assert.Null(result.Catalog);
        Assert.Contains(result.Errors, e => e.Kind == ContentErrorKind.DuplicateId && e.File == "items/b.json");
        Assert.Contains(result.Errors, e => e.Kind == ContentErrorKind.Parse && e.File == "items/c.json");
        Assert.Contains(result.Errors, e => e.Kind == ContentErrorKind.Parse && e.File == "other/d.json");
        Assert.Contains(result.Errors, e => e.Kind == ContentErrorKind.Missing);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void DanglingReferenceIsReportedInSeparatePass()
    {
        ContentLoadResult result = LoadItems("""
            { "items": [ { "id": "wheat", "stackSize": 1, "weight": 1, "spoilsInto": "rot" },
                         { "id": "bread", "stackSize": 1, "weight": 1, "spoilsInto": "time" },
                         { "id": "ok", "stackSize": 1, "weight": 1, "spoilsInto": "wheat" } ] }
            """);

        Assert.Null(result.Catalog);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal(ContentErrorKind.Reference, e.Kind));
        Assert.Contains(result.Errors, e => e.JsonPath == "$.items[0].spoilsInto" && e.Message.Contains("rot", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.JsonPath == "$.items[1].spoilsInto");
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void ValidReferenceAndModPrefixLoad()
    {
        ContentLoadResult result = LoadItems("""
            { "items": [ { "id": "grain", "stackSize": 1, "weight": 1, "spoilsInto": "mod:rot" },
                         { "id": "mod:rot", "stackSize": 1, "weight": 1, "nameKey": "custom.rot" } ] }
            """);

        Assert.Empty(result.Errors);
        Assert.Equal("custom.rot", result.Catalog!.Items.Single(i => i.Id == "mod:rot").EffectiveNameKey);
        Assert.Equal("item.mod.rot.name", ContentKeys.ItemName("mod:rot"));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void MissingTimeBalanceIsAnError()
    {
        ContentLoadResult result = ContentCatalog.Load(new InMemoryContentSource().Add("balance/fog.json", _fogJson).Add("balance/saves.json", _savesJson).Add("worldgen/world.json", _worldGenJson));

        ContentError error = Assert.Single(result.Errors);
        Assert.Equal(ContentErrorKind.Missing, error.Kind);
        Assert.Contains("TimeBalanceDef", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    [Trait("Req", "FR-WORLD-06")]
    public void ExactlyOneFogBalanceIsRequiredAndRadiiAreRangeChecked()
    {
        ContentLoadResult missing = ContentCatalog.Load(new InMemoryContentSource().Add("balance/time.json", TimeJson).Add("balance/saves.json", _savesJson).Add("worldgen/world.json", _worldGenJson));
        ContentError error = Assert.Single(missing.Errors);
        Assert.Equal(ContentErrorKind.Missing, error.Kind);
        Assert.Contains("FogBalanceDef", error.Message, StringComparison.Ordinal);

        ContentLoadResult twice = ContentCatalog.Load(WithTime().Add("balance/fog.json", _fogJson.Replace("\"fog\"", "\"fog_two\"", StringComparison.Ordinal)));
        Assert.Contains(twice.Errors, e => e.Kind == ContentErrorKind.Validation && e.Message.Contains("FogBalanceDef", StringComparison.Ordinal));

        string zero = _fogJson.Replace("\"heroNight\": 12", "\"heroNight\": 0", StringComparison.Ordinal);
        ContentError range = Assert.Single(ContentCatalog.Load(new InMemoryContentSource()
            .Add("balance/time.json", TimeJson).Add("balance/saves.json", _savesJson).Add("balance/fog.json", zero).Add("worldgen/world.json", _worldGenJson)).Errors);
        Assert.Equal(ContentErrorKind.Validation, range.Kind);
        Assert.Equal("balance/fog.json", range.File);
        Assert.Equal("$.items[0].heroNight", range.JsonPath);

        // fog.json is parsed as fog radii, every other balance file as time constants.
        ContentError wrongShape = Assert.Single(ContentCatalog.Load(new InMemoryContentSource()
            .Add("balance/time.json", TimeJson).Add("balance/saves.json", _savesJson).Add("balance/fog.json", TimeJson).Add("worldgen/world.json", _worldGenJson)).Errors,
            e => e.Kind == ContentErrorKind.Parse);
        Assert.Equal("balance/fog.json", wrongShape.File);
    }
}
