using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Content;
using Json.Schema;

namespace Core.Tests.Content;

/// <summary>Eras, races, buildings, professions and relation reasons: real data and reference checks (ADR 0008, ADR 0012).</summary>
public sealed class CivilizationContentTests
{
    private static readonly string[] _files =
    [
        "balance/time.json", "balance/fog.json", "balance/saves.json", "balance/hero.json", "balance/construction.json", "worldgen/world.json", "items/basic.json",
        "eras/eras.json", "races/races.json", "professions/professions.json", "balance/relations.json", "buildings/buildings.json",
    ];

    private static string Read(string path) => File.ReadAllText(Path.Combine(TestPaths.Data, path));

    // The real data with one file replaced by `replacement` (parsed from the real text, edited by the caller).
    private static ContentLoadResult LoadWith(string path, Action<JsonArray> edit)
    {
        InMemoryContentSource source = new();
        foreach (string file in _files)
        {
            if (file == path)
            {
                JsonObject root = JsonNode.Parse(Read(file))!.AsObject();
                edit(root["items"]!.AsArray());
                source.Add(file, root.ToJsonString());
            }
            else
            {
                source.Add(file, Read(file));
            }
        }

        return ContentCatalog.Load(source);
    }

    private static ContentCatalog RealCatalog() => ContentCatalog.Load(TestPaths.LoadDataSource()).Catalog!;

    private static JsonObject Item(JsonArray items, string id) => items.Single(i => i!["id"]!.GetValue<string>() == id)!.AsObject();

    private static void AssertSingleError(ContentLoadResult result, ContentErrorKind kind, string file, string jsonPath)
    {
        Assert.Null(result.Catalog);
        ContentError error = Assert.Single(result.Errors);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(file, error.File);
        Assert.Equal(jsonPath, error.JsonPath);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void ErasCoverPopulationWithTheAgreedTerritoryRadii()
    {
        IReadOnlyList<EraDef> eras = RealCatalog().Eras;

        Assert.Equal(["camp", "village", "town", "fortress"], eras.Select(e => e.Id));
        Assert.Equal([48, 80, 128, 192], eras.Select(e => e.TerritoryRadius));
        Assert.Equal([1, 4, 16, 40], eras.Select(e => e.MinPopulation));
        Assert.Equal([3, 15, 39, (int?)null], eras.Select(e => e.MaxPopulation));
        Assert.Empty(EraDef.CheckCoverage(eras));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void EraGapsOverlapsAndBadEndsAreReported()
    {
        ContentLoadResult gap = LoadWith("eras/eras.json", items => Item(items, "village")["minPopulation"] = 5);
        AssertSingleError(gap, ContentErrorKind.Validation, "eras/eras.json", "$.items[1].minPopulation");

        ContentLoadResult overlap = LoadWith("eras/eras.json", items => Item(items, "town")["minPopulation"] = 15);
        AssertSingleError(overlap, ContentErrorKind.Validation, "eras/eras.json", "$.items[2].minPopulation");

        ContentLoadResult start = LoadWith("eras/eras.json", items => Item(items, "camp")["minPopulation"] = 2);
        Assert.Contains(start.Errors, e => e.JsonPath == "$.items[0].minPopulation");

        ContentLoadResult bounded = LoadWith("eras/eras.json", items => Item(items, "fortress")["maxPopulation"] = 99);
        AssertSingleError(bounded, ContentErrorKind.Validation, "eras/eras.json", "$.items[3].maxPopulation");

        ContentLoadResult open = LoadWith("eras/eras.json", items => Item(items, "town")["maxPopulation"] = null);
        Assert.Contains(open.Errors, e => e.JsonPath == "$.items[2].maxPopulation");

        ContentLoadResult inverted = LoadWith("eras/eras.json", items => Item(items, "village")["maxPopulation"] = 2);
        Assert.Contains(inverted.Errors, e => e.JsonPath == "$.items[1].maxPopulation");

        ContentLoadResult order = LoadWith("eras/eras.json", items => Item(items, "town")["order"] = 4);
        Assert.Contains(order.Errors, e => e.Message.Contains("numbered", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "FR-FAC-01")]
    public void RacesHaveTheAgreedStartRelationsAndTechnologies()
    {
        IReadOnlyList<RaceDef> races = RealCatalog().Races;

        Assert.Equal(["human", "orc"], races.Select(r => r.Id));
        Assert.Equal(["good", "evil"], races.Select(r => r.Side));
        Assert.Equal([20, -80], races.Select(r => r.StartRelation));
        Assert.Equal(["meadow", "wasteland"], races.Select(r => r.Biome));
        Assert.Empty(races[0].StartTechnologies);
        Assert.Equal(["siege_ladders"], races[1].StartTechnologies);
        Assert.All(races, r => Assert.Empty(r.SettlementBuildings));
        Assert.All(races, r => Assert.Empty(r.Army));
    }

    [Fact]
    [Trait("Req", "FR-FAC-01")]
    public void RaceReferencesAreChecked()
    {
        ContentLoadResult unknown = LoadWith("races/races.json", items => Item(items, "human")["settlementBuildings"] = new JsonArray("castle"));
        AssertSingleError(unknown, ContentErrorKind.Reference, "races/races.json", "$.items[0].settlementBuildings[0]");

        ContentLoadResult wrongType = LoadWith("races/races.json", items => Item(items, "human")["settlementBuildings"] = new JsonArray("wood"));
        AssertSingleError(wrongType, ContentErrorKind.Reference, "races/races.json", "$.items[0].settlementBuildings[0]");

        ContentLoadResult known = LoadWith("races/races.json", items => Item(items, "human")["settlementBuildings"] = new JsonArray("campfire"));
        Assert.Equal(["campfire"], known.Catalog!.Races.Single(r => r.Id == "human").SettlementBuildings);

        ContentLoadResult badTech = LoadWith("races/races.json", items => Item(items, "orc")["startTechnologies"] = new JsonArray("Bad Id"));
        AssertSingleError(badTech, ContentErrorKind.Validation, "races/races.json", "$.items[1].startTechnologies[0]");

        ContentLoadResult badArmy = LoadWith("races/races.json", items => Item(items, "orc")["army"] = new JsonArray("Bad Id"));
        AssertSingleError(badArmy, ContentErrorKind.Validation, "races/races.json", "$.items[1].army[0]");
    }

    [Fact]
    [Trait("Req", "FR-FAC-01")]
    public void RaceValuesAreRangeChecked()
    {
        Assert.Contains(LoadWith("races/races.json", items => Item(items, "orc")["startRelation"] = -101).Errors, e => e.JsonPath == "$.items[1].startRelation");
        Assert.Contains(LoadWith("races/races.json", items => Item(items, "orc")["side"] = "neutral").Errors, e => e.JsonPath == "$.items[1].side");
        Assert.Contains(LoadWith("races/races.json", items => Item(items, "orc")["biome"] = "lava").Errors, e => e.JsonPath == "$.items[1].biome");
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void AcceptanceBuildingsHaveGrowingLevels()
    {
        ContentCatalog catalog = RealCatalog();

        Assert.Equal(["campfire", "house", "storage"], catalog.Buildings.Select(b => b.Id));
        Assert.All(catalog.Buildings, b => Assert.True(b.Provisional));
        BuildingDef campfire = catalog.Buildings[0];
        BuildingDef house = catalog.Buildings[1];
        BuildingDef storage = catalog.Buildings[2];

        Assert.Equal("settlement_center", campfire.Category);
        Assert.True(campfire.OutsideTerritory);
        Assert.Single(campfire.Levels);
        Assert.Equal([new ItemAmountDef("wood", 5)], campfire.Levels[0].Cost);

        Assert.False(house.OutsideTerritory);
        Assert.Equal(3, house.Levels.Count);
        Assert.Equal([2, 3, 4], house.Levels.Select(l => l.Capacity));
        Assert.All(house.Levels, l => Assert.Empty(l.Requirements)); // the era requirement is lifted for M2 (no residents before M3)
        Assert.Equal(["camp", "village"], storage.Levels.Select(l => l.Requirements.Single().Value));
        Assert.Equal([false, true, true], catalog.Buildings.Select(b => b.Rotatable));
        Assert.Equal([33, 33, 33, 40, 33, 40], catalog.Buildings.SelectMany(b => b.Levels).Select(l => l.MoveTicks));
        Assert.All(catalog.Buildings.SelectMany(b => b.Levels), l => Assert.Equal(Math.Max(1, l.BuildTicks / 3), l.MoveTicks));

        Assert.Equal(2, storage.Levels.Count);
        Assert.Equal([20, 40], storage.Levels.Select(l => l.Storage));

        foreach (BuildingDef building in catalog.Buildings)
        {
            Assert.All(building.Levels, l => Assert.InRange(l.BuildTicks, 100, 150));
            Assert.All(building.Levels.SelectMany(l => l.Cost), c => Assert.True(catalog.TryGet<ItemDef>(c.Item, out _)));
        }
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void BuildingReferencesAreChecked()
    {
        ContentLoadResult item = LoadWith("buildings/buildings.json", items =>
            Item(items, "house")["levels"]![0]!["cost"] = JsonNode.Parse("""[ { "item": "gold", "count": 1 } ]"""));
        AssertSingleError(item, ContentErrorKind.Reference, "buildings/buildings.json", "$.items[1].levels[0].cost[0].item");

        ContentLoadResult era = LoadWith("buildings/buildings.json", items =>
            Item(items, "house")["levels"]![1]!["requirements"] = JsonNode.Parse("""[ { "kind": "era", "value": "empire" } ]"""));
        AssertSingleError(era, ContentErrorKind.Reference, "buildings/buildings.json", "$.items[1].levels[1].requirements[0].value");

        ContentLoadResult eraWrongType = LoadWith("buildings/buildings.json", items =>
            Item(items, "house")["levels"]![1]!["requirements"] = JsonNode.Parse("""[ { "kind": "era", "value": "wood" } ]"""));
        AssertSingleError(eraWrongType, ContentErrorKind.Reference, "buildings/buildings.json", "$.items[1].levels[1].requirements[0].value");

        ContentLoadResult specialist = LoadWith("buildings/buildings.json", items =>
            Item(items, "storage")["levels"]![0]!["requirements"] = JsonNode.Parse("""[ { "kind": "specialist", "value": "wizard" } ]"""));
        AssertSingleError(specialist, ContentErrorKind.Reference, "buildings/buildings.json", "$.items[2].levels[0].requirements[0].value");

        ContentLoadResult ordinary = LoadWith("buildings/buildings.json", items =>
            Item(items, "storage")["levels"]![0]!["requirements"] = JsonNode.Parse("""[ { "kind": "specialist", "value": "builder" } ]"""));
        AssertSingleError(ordinary, ContentErrorKind.Reference, "buildings/buildings.json", "$.items[2].levels[0].requirements[0].value");

        ContentLoadResult valid = LoadWith("buildings/buildings.json", items =>
            Item(items, "storage")["levels"]![0]!["requirements"] = JsonNode.Parse("""[ { "kind": "specialist", "value": "blacksmith" } ]"""));
        Assert.NotNull(valid.Catalog);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-02")]
    [Trait("Req", "FR-BUILD-09")]
    public void AnItemRepeatedInOneCostIsRejected()
    {
        // Paying checks and spends each entry separately: [wood 3, wood 3] passed the check with 3 wood and the tick threw.
        ContentLoadResult repeated = LoadWith("buildings/buildings.json", items =>
            Item(items, "house")["levels"]![1]!["cost"] = JsonNode.Parse("""
                [ { "item": "wood", "count": 3 }, { "item": "stone", "count": 1 }, { "item": "wood", "count": 3 } ]
                """));
        AssertSingleError(repeated, ContentErrorKind.Validation, "buildings/buildings.json", "$.items[1].levels[1].cost[2].item");
        Assert.Contains("'wood'", repeated.Errors[0].Message, StringComparison.Ordinal);

        // The same item in different levels is fine.
        ContentLoadResult levels = LoadWith("buildings/buildings.json", items =>
        {
            Item(items, "house")["levels"]![0]!["cost"] = JsonNode.Parse("""[ { "item": "wood", "count": 3 } ]""");
            Item(items, "house")["levels"]![1]!["cost"] = JsonNode.Parse("""[ { "item": "wood", "count": 3 } ]""");
        });
        Assert.NotNull(levels.Catalog);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-09")]
    public void UpgradesNeverShrinkStorageOrCapacity()
    {
        ContentLoadResult storage = LoadWith("buildings/buildings.json", items => Item(items, "storage")["levels"]![1]!["storage"] = 10);
        AssertSingleError(storage, ContentErrorKind.Validation, "buildings/buildings.json", "$.items[2].levels[1]");

        ContentLoadResult capacity = LoadWith("buildings/buildings.json", items => Item(items, "house")["levels"]![2]!["capacity"] = 1);
        AssertSingleError(capacity, ContentErrorKind.Validation, "buildings/buildings.json", "$.items[1].levels[2]");
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void HeroBalanceLoadsWithAgreedStartValues()
    {
        HeroBalanceDef hero = RealCatalog().Hero;

        Assert.True(hero.Provisional);
        Assert.Equal((2, 2, 1, 1), (hero.Attack, hero.Defense, hero.SpellPower, hero.Knowledge));
        Assert.Equal((100, 100), (hero.Health, hero.Stamina));
        Assert.Equal((3, 1500, 2, 1, 2), (hero.StepTicks, hero.SprintPermille, hero.SprintStaminaDrain, hero.StaminaRegen, hero.InteractionRange));
        Assert.Equal(
            [new GatherDef("tree", "wood", 50, 10), new GatherDef("rock", "stone", 80, 10), new GatherDef("berry_bush", "berries", 20, 3)],
            hero.Gathering);
        Assert.Equal(30, hero.BackpackStacks);
        Assert.Equal(HeroBalanceDef.AllSlots, hero.EquipmentSlots);
        Assert.Equal(["head", "body", "hands", "legs", "weapon", "shield", "ring1", "ring2", "amulet"], hero.EquipmentSlots);
        Assert.Empty(hero.StartItems);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    public void ConstructionBalanceLoadsWithRefundShares()
    {
        ConstructionBalanceDef construction = RealCatalog().Construction;

        Assert.True(construction.Provisional);
        Assert.Equal(750, construction.DemolishRefundPermille);
        Assert.Equal(1000, construction.CancelRefundPermille);

        ContentLoadResult tooMuch = LoadWith("balance/construction.json", items => Item(items, "construction")["demolishRefundPermille"] = 1001);
        AssertSingleError(tooMuch, ContentErrorKind.Validation, "balance/construction.json", "$.items[0].demolishRefundPermille");
    }

    [Fact]
    [Trait("Req", "FR-HERO-01")]
    public void HeroReferencesAndShapeAreChecked()
    {
        ContentLoadResult badStart = LoadWith("balance/hero.json", items =>
            Item(items, "hero")["startItems"] = JsonNode.Parse("""[ { "item": "gold", "count": 1 } ]"""));
        AssertSingleError(badStart, ContentErrorKind.Reference, "balance/hero.json", "$.items[0].startItems[0].item");

        ContentLoadResult notItem = LoadWith("balance/hero.json", items =>
            Item(items, "hero")["startItems"] = JsonNode.Parse("""[ { "item": "camp", "count": 1 } ]"""));
        AssertSingleError(notItem, ContentErrorKind.Reference, "balance/hero.json", "$.items[0].startItems[0].item");

        ContentLoadResult goodStart = LoadWith("balance/hero.json", items =>
            Item(items, "hero")["startItems"] = JsonNode.Parse("""[ { "item": "wood", "count": 5 } ]"""));
        Assert.Equal([new ItemAmountDef("wood", 5)], goodStart.Catalog!.Hero.StartItems);

        ContentLoadResult badGather = LoadWith("balance/hero.json", items =>
            Item(items, "hero")["gathering"]![0]!["item"] = "gold");
        AssertSingleError(badGather, ContentErrorKind.Reference, "balance/hero.json", "$.items[0].gathering[0].item");

        ContentLoadResult dupGather = LoadWith("balance/hero.json", items =>
            Item(items, "hero")["gathering"]![1]!["source"] = "tree");
        AssertSingleError(dupGather, ContentErrorKind.Validation, "balance/hero.json", "$.items[0].gathering[1].source");

        ContentLoadResult badSlot = LoadWith("balance/hero.json", items =>
            Item(items, "hero")["equipmentSlots"]![0] = "feet");
        AssertSingleError(badSlot, ContentErrorKind.Validation, "balance/hero.json", "$.items[0].equipmentSlots[0]");

        ContentLoadResult dupSlot = LoadWith("balance/hero.json", items =>
            Item(items, "hero")["equipmentSlots"]![1] = "head");
        AssertSingleError(dupSlot, ContentErrorKind.Validation, "balance/hero.json", "$.items[0].equipmentSlots[1]");

        ContentLoadResult slowSprint = LoadWith("balance/hero.json", items => Item(items, "hero")["sprintPermille"] = 999);
        AssertSingleError(slowSprint, ContentErrorKind.Validation, "balance/hero.json", "$.items[0].sprintPermille");

        ContentLoadResult noStep = LoadWith("balance/hero.json", items => Item(items, "hero")["stepTicks"] = 0);
        AssertSingleError(noStep, ContentErrorKind.Validation, "balance/hero.json", "$.items[0].stepTicks");
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void MoveTicksMustBePositiveAndRotatableIsRequired()
    {
        ContentLoadResult zero = LoadWith("buildings/buildings.json", items =>
            Item(items, "house")["levels"]![0]!["moveTicks"] = 0);
        AssertSingleError(zero, ContentErrorKind.Validation, "buildings/buildings.json", "$.items[1].levels[0].moveTicks");

        ContentLoadResult missing = LoadWith("buildings/buildings.json", items =>
            Item(items, "house").AsObject().Remove("rotatable"));
        Assert.Null(missing.Catalog);
        Assert.Contains(missing.Errors, e => e.Kind == ContentErrorKind.Parse && e.Message.Contains("rotatable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Req", "FR-BUILD-13")]
    public void BuildingShapeIsValidated()
    {
        ContentLoadResult noLevels = LoadWith("buildings/buildings.json", items => Item(items, "house")["levels"] = new JsonArray());
        AssertSingleError(noLevels, ContentErrorKind.Validation, "buildings/buildings.json", "$.items[1].levels");

        ContentLoadResult nested = LoadWith("buildings/buildings.json", items =>
        {
            JsonNode level = Item(items, "house")["levels"]![0]!;
            level["buildTicks"] = 0;
            level["cost"] = JsonNode.Parse("""[ { "item": "wood", "count": 0 } ]""");
            level["requirements"] = JsonNode.Parse("""[ { "kind": "tech", "value": "wood" } ]""");
            Item(items, "house")["footprint"]!["width"] = 0;
            Item(items, "house")["placement"] = "water";
        });
        Assert.Null(nested.Catalog);
        Assert.All(nested.Errors, e => Assert.Equal(ContentErrorKind.Validation, e.Kind));
        Assert.Equal(
            [
                "$.items[1].placement", "$.items[1].footprint.width", "$.items[1].levels[0].buildTicks",
                "$.items[1].levels[0].cost[0].count", "$.items[1].levels[0].requirements[0].kind",
            ],
            nested.Errors.Select(e => e.JsonPath));
    }

    [Fact]
    [Trait("Req", "FR-NPC-05")]
    public void ProfessionsSplitIntoOrdinaryAndSpecialist()
    {
        IReadOnlyList<ProfessionDef> professions = RealCatalog().Professions;

        Assert.Equal(
            ["armorer", "blacksmith", "builder", "carrier", "farmer", "guard", "healer", "hunter", "lumberjack", "miner", "stonemason"],
            professions.Select(p => p.Id));
        Assert.Equal(["armorer", "blacksmith", "healer"], professions.Where(p => p.Kind == "specialist").Select(p => p.Id));
        Assert.Equal(8, professions.Count(p => p.Kind == "ordinary"));
        Assert.Equal("smithing", professions.Single(p => p.Id == "blacksmith").Skill);
        Assert.Equal("medicine", professions.Single(p => p.Id == "healer").Skill);
        Assert.Contains(LoadWith("professions/professions.json", items => Item(items, "guard")["skill"] = "magic").Errors, e => e.JsonPath == "$.items[7].skill");
        Assert.Contains(LoadWith("professions/professions.json", items => Item(items, "guard")["kind"] = "boss").Errors, e => e.JsonPath == "$.items[7].kind");
    }

    [Fact]
    [Trait("Req", "FR-FAC-04")]
    public void RelationReasonsHaveTheAgreedValues()
    {
        IReadOnlyList<RelationModifierDef> reasons = RealCatalog().RelationModifiers;

        Assert.Equal(["poach_resident", "raid_loot", "settlement_destroyed", "trade"], reasons.Select(r => r.Id));
        Assert.Equal([-5, -50, -80, 1], reasons.Select(r => r.Delta));
        Assert.All(reasons, r => Assert.Equal("none", r.DecayKind));
        Assert.All(reasons, r => Assert.Equal(0, r.DecayTicks));
    }

    [Fact]
    [Trait("Req", "FR-FAC-04")]
    public void RelationDecayMustMatchItsKind()
    {
        ContentLoadResult none = LoadWith("balance/relations.json", items => Item(items, "trade")["decayTicks"] = 100);
        AssertSingleError(none, ContentErrorKind.Validation, "balance/relations.json", "$.items[3].decayTicks");

        ContentLoadResult fading = LoadWith("balance/relations.json", items => Item(items, "raid_loot")["decayKind"] = "fading");
        AssertSingleError(fading, ContentErrorKind.Validation, "balance/relations.json", "$.items[1].decayTicks");

        ContentLoadResult valid = LoadWith("balance/relations.json", items =>
        {
            Item(items, "raid_loot")["decayKind"] = "expiring";
            Item(items, "raid_loot")["decayTicks"] = 3000;
        });
        Assert.Equal(3000, valid.Catalog!.RelationModifiers.Single(r => r.Id == "raid_loot").DecayTicks);

        Assert.Contains(LoadWith("balance/relations.json", items => Item(items, "trade")["decayKind"] = "forever").Errors, e => e.JsonPath == "$.items[3].decayKind");
        Assert.Contains(LoadWith("balance/relations.json", items => Item(items, "trade")["delta"] = 101).Errors, e => e.JsonPath == "$.items[3].delta");
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void NewSchemasRejectBadData()
    {
        (string schemaName, string json)[] bad =
        [
            ("eras.schema.json", """{ "items": [ { "id": "camp", "order": 1, "minPopulation": 1, "territoryRadius": 48 } ] }"""),
            ("races.schema.json", """{ "items": [ { "id": "elf", "side": "good", "startRelation": 500, "biome": "forest", "startTechnologies": [], "settlementBuildings": [], "army": [] } ] }"""),
            ("buildings.schema.json", """{ "items": [ { "id": "a", "category": "housing", "footprint": { "width": 1, "height": 1 }, "outsideTerritory": false, "placement": "flat", "levels": [] } ] }"""),
            ("professions.schema.json", """{ "items": [ { "id": "a", "kind": "ordinary", "skill": "magic" } ] }"""),
            ("relations.schema.json", """{ "items": [ { "id": "a", "delta": 1, "decayKind": "none" } ] }"""),
        ];
        foreach ((string schemaName, string json) in bad)
        {
            JsonSchema schema = JsonSchema.Build(JsonDocument.Parse(Read("schema/" + schemaName)).RootElement);
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid, json);
        }
    }
}
