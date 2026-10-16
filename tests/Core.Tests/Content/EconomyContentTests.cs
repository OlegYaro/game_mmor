using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Content;
using Json.Schema;

namespace Core.Tests.Content;

/// <summary>M3a content: item categories, job types, economy, roads, regrowth, name lists and the field building.</summary>
public sealed class EconomyContentTests
{
    private static string Read(string path) => File.ReadAllText(Path.Combine(TestPaths.Data, path));

    // The real data with one file edited by the caller (every file except the schemas).
    private static ContentLoadResult LoadWith(string path, Action<JsonArray> edit)
    {
        InMemoryContentSource source = new();
        foreach (ContentFile file in TestPaths.LoadDataSource().GetFiles())
        {
            if (file.Path == path)
            {
                JsonObject root = JsonNode.Parse(file.Text)!.AsObject();
                edit(root["items"]!.AsArray());
                source.Add(file.Path, root.ToJsonString());
            }
            else
            {
                source.Add(file.Path, file.Text);
            }
        }

        return ContentCatalog.Load(source);
    }

    private static ContentCatalog RealCatalog() => ContentCatalog.Load(TestPaths.LoadDataSource()).Catalog!;

    private static JsonObject Item(JsonArray items, string id) => items.Single(i => i!["id"]!.GetValue<string>() == id)!.AsObject();

    private static JsonObject First(JsonArray items) => items[0]!.AsObject();

    private static void AssertSingleError(ContentLoadResult result, ContentErrorKind kind, string file, string jsonPath)
    {
        Assert.Null(result.Catalog);
        ContentError error = Assert.Single(result.Errors);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(file, error.File);
        Assert.Equal(jsonPath, error.JsonPath);
    }

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void ItemsHaveCategoriesAndFoodValues()
    {
        ContentCatalog catalog = RealCatalog();
        ItemDef Get(string id) => catalog.Items.Single(i => i.Id == id);

        Assert.Equal("resource", Get("wood").Category);
        Assert.Equal("resource", Get("stone").Category);
        Assert.Equal("resource", Get("wheat").Category);
        Assert.Equal(0, Get("wheat").FoodValue);
        Assert.Equal("food", Get("berries").Category);
        Assert.Equal(1, Get("berries").FoodValue);
        Assert.Equal("food", Get("vegetable").Category);
        Assert.Equal(1, Get("vegetable").FoodValue);
        Assert.Equal("currency", Get("gold").Category);
        Assert.Equal(0, Get("gold").FoodValue);
        Assert.All(catalog.Items, i => Assert.True(i.FoodValue == 0 || i.Category == "food"));
    }

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void ItemCategoryAndFoodValueAreChecked()
    {
        AssertSingleError(
            LoadWith("items/basic.json", items => Item(items, "wood")["category"] = "junk"),
            ContentErrorKind.Validation, "items/basic.json", "$.items[0].category");
        AssertSingleError(
            LoadWith("items/basic.json", items => Item(items, "wood")["foodValue"] = 1),
            ContentErrorKind.Validation, "items/basic.json", "$.items[0].foodValue");
        AssertSingleError(
            LoadWith("items/basic.json", items => Item(items, "berries")["foodValue"] = -1),
            ContentErrorKind.Validation, "items/basic.json", "$.items[3].foodValue");

        // An item without a category is allowed while it is not edible.
        ContentLoadResult none = LoadWith("items/basic.json", items => Item(items, "wood").Remove("category"));
        Assert.Null(none.Catalog!.Items.Single(i => i.Id == "wood").Category);
        Assert.NotNull(LoadWith("items/basic.json", items => Item(items, "gold")["category"] = "material").Catalog);

        // An item without a category cannot be edible.
        AssertSingleError(
            LoadWith("items/basic.json", items => Item(items, "berries").Remove("category")),
            ContentErrorKind.Validation, "items/basic.json", "$.items[3].foodValue");
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void ProfessionsListTheirJobTypes()
    {
        IReadOnlyList<ProfessionDef> professions = RealCatalog().Professions;
        string[] JobsOf(string id) => professions.Single(p => p.Id == id).JobTypes.ToArray();

        Assert.Equal(["haul"], JobsOf("carrier"));
        Assert.Equal(["chop", "plant"], JobsOf("lumberjack"));
        Assert.Equal(["quarry"], JobsOf("stonemason"));
        Assert.Equal(["farm", "forage"], JobsOf("farmer"));
        foreach (string id in new[] { "miner", "builder", "hunter", "guard", "blacksmith", "healer", "armorer" })
        {
            Assert.Empty(JobsOf(id));
        }

        // One skill "mining" for lumberjack, stonemason and miner (F4).
        Assert.Equal("mining", professions.Single(p => p.Id == "lumberjack").Skill);
        Assert.Equal("mining", professions.Single(p => p.Id == "stonemason").Skill);
        Assert.Equal("mining", professions.Single(p => p.Id == "miner").Skill);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void JobTypesMustBeKnownAndUnique()
    {
        AssertSingleError(
            LoadWith("professions/professions.json", items => Item(items, "carrier")["jobTypes"] = new JsonArray("fly")),
            ContentErrorKind.Validation, "professions/professions.json", "$.items[0].jobTypes[0]");
        AssertSingleError(
            LoadWith("professions/professions.json", items => Item(items, "carrier")["jobTypes"] = new JsonArray("haul", "haul")),
            ContentErrorKind.Validation, "professions/professions.json", "$.items[0].jobTypes[1]");
        ContentLoadResult all = LoadWith("professions/professions.json", items =>
            Item(items, "guard")["jobTypes"] = new JsonArray("haul", "chop", "plant", "quarry", "forage", "farm"));
        Assert.Equal(6, all.Catalog!.Professions.Single(p => p.Id == "guard").JobTypes.Count);
        Assert.True(JobTypes.IsValid("farm"));
        Assert.False(JobTypes.IsValid("farming"));
        Assert.Contains(
            LoadWith("professions/professions.json", items => Item(items, "carrier").Remove("jobTypes")).Errors,
            e => e.Kind == ContentErrorKind.Parse);
    }

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    [Trait("Req", "FR-NPC-01")]
    public void EconomyBalanceHasTheAgreedValues()
    {
        EconomyBalanceDef economy = RealCatalog().Economy;

        Assert.Equal("economy", economy.Id);
        Assert.Equal(1, economy.FoodPerResidentPerDay);
        Assert.Equal(500, economy.ShortageSlowdownPermille);
        Assert.Equal(2, economy.DaysWithoutFoodUntilFirstLeaves);
        Assert.Equal(1, economy.LeaveIntervalDays);
        Assert.Equal(2, economy.WarnDaysBeforeEmpty);
        Assert.Equal(2, economy.TaxPerResidentPerDay);
        Assert.Equal(100, economy.MaxResidents);
        Assert.Equal(4, economy.ResidentStepTicks);
        Assert.Equal(6, economy.ArrivalHour);
        Assert.Equal(25, economy.ArrivalFreeSlotPoints);
        Assert.Equal(2, economy.ArrivalMaxSlots);
        Assert.Equal(25, economy.ArrivalFoodPoints);
        Assert.Equal(3, economy.ArrivalFoodDays);
        Assert.Equal(20, economy.ArrivalNoRaidPoints);
        Assert.Equal(2, economy.ArrivalNoRaidDays);
        Assert.Equal(5, economy.ArrivalFamePointsDivisor);
        Assert.Equal([0, 10, 20, 30], economy.ArrivalEraPoints);
        Assert.Equal(800, economy.ArrivalChanceCapPermille);
        Assert.Equal(20, economy.HaulCapacity);
        Assert.Equal(600, economy.JobRetryCooldownTicks);
        Assert.Equal(10, economy.ThinkIntervalTicks);
        Assert.Equal(25, economy.SkillSpeedPermillePerPoint);
        Assert.Equal(6, economy.InitialSkillMax);
        Assert.Equal(["carrier", "lumberjack", "farmer", "stonemason"], economy.AutoAssignPattern);
        Assert.Equal(RealCatalog().Eras.Count, economy.ArrivalEraPoints.Count);
    }

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void EconomyRangesAreChecked()
    {
        (string Member, int Value)[] bad =
        [
            ("foodPerResidentPerDay", 0), ("shortageSlowdownPermille", 1001), ("shortageSlowdownPermille", 0),
            ("leaveIntervalDays", 0), ("taxPerResidentPerDay", -1), ("maxResidents", 0), ("arrivalHour", 24),
            ("arrivalFreeSlotPoints", 101), ("arrivalMaxSlots", 0), ("arrivalFoodDays", 0), ("arrivalFamePointsDivisor", 0),
            ("arrivalChanceCapPermille", 1001), ("haulCapacity", 0), ("thinkIntervalTicks", 0),

            // A ban of 0 ticks would end in the tick it was given, and a save of that tick would hold an ended ban
            // (review of M3a).
            ("jobRetryCooldownTicks", 0),
            ("skillSpeedPermillePerPoint", 101), ("initialSkillMax", 21),
        ];
        foreach ((string member, int value) in bad)
        {
            ContentLoadResult result = LoadWith("balance/economy.json", items => First(items)[member] = value);
            AssertSingleError(result, ContentErrorKind.Validation, "balance/economy.json", "$.items[0]." + member);
        }
    }

    [Fact]
    [Trait("Req", "FR-NPC-01")]
    public void EconomyListsAreChecked()
    {
        AssertSingleError(
            LoadWith("balance/economy.json", items => First(items)["arrivalEraPoints"] = new JsonArray(0, 10, 20)),
            ContentErrorKind.Validation, "balance/economy.json", "$.items[0].arrivalEraPoints");
        AssertSingleError(
            LoadWith("balance/economy.json", items => First(items)["arrivalEraPoints"] = new JsonArray(0, 10, 20, 101)),
            ContentErrorKind.Validation, "balance/economy.json", "$.items[0].arrivalEraPoints[3]");
        AssertSingleError(
            LoadWith("balance/economy.json", items => First(items)["autoAssignPattern"] = new JsonArray()),
            ContentErrorKind.Validation, "balance/economy.json", "$.items[0].autoAssignPattern");
        AssertSingleError(
            LoadWith("balance/economy.json", items => First(items)["autoAssignPattern"] = new JsonArray("Bad Id")),
            ContentErrorKind.Reference, "balance/economy.json", "$.items[0].autoAssignPattern[0]");
        AssertSingleError(
            LoadWith("balance/economy.json", items => First(items)["autoAssignPattern"] = new JsonArray("carrier", "wizard")),
            ContentErrorKind.Reference, "balance/economy.json", "$.items[0].autoAssignPattern[1]");
        AssertSingleError(
            LoadWith("balance/economy.json", items => First(items)["autoAssignPattern"] = new JsonArray("wood")),
            ContentErrorKind.Reference, "balance/economy.json", "$.items[0].autoAssignPattern[0]");
        AssertSingleError(
            LoadWith("balance/economy.json", items => First(items)["autoAssignPattern"] = new JsonArray("blacksmith")),
            ContentErrorKind.Reference, "balance/economy.json", "$.items[0].autoAssignPattern[0]");

        // An extra era needs an extra points entry.
        ContentLoadResult extraEra = LoadWith("balance/economy.json", items => First(items)["arrivalEraPoints"] = new JsonArray(0, 10, 20, 30, 40));
        AssertSingleError(extraEra, ContentErrorKind.Validation, "balance/economy.json", "$.items[0].arrivalEraPoints");
    }

    [Fact]
    [Trait("Req", "FR-ECO-10")]
    public void EconomyRoadsRegrowthAndNamesAreRequired()
    {
        foreach ((string path, string type) in new[]
        {
            ("balance/economy.json", "EconomyBalanceDef"), ("balance/roads.json", "RoadsBalanceDef"),
            ("balance/regrowth.json", "RegrowthBalanceDef"), ("names/names.json", "NameListDef"),
        })
        {
            InMemoryContentSource source = new();
            foreach (ContentFile file in TestPaths.LoadDataSource().GetFiles().Where(f => f.Path != path))
            {
                source.Add(file.Path, file.Text);
            }

            ContentLoadResult result = ContentCatalog.Load(source);
            Assert.Null(result.Catalog);
            Assert.Contains(result.Errors, e => e.Kind == ContentErrorKind.Missing && e.Message.Contains(type, StringComparison.Ordinal));
        }

        ContentLoadResult twice = LoadWith("balance/roads.json", items => items.Add(JsonNode.Parse(items[0]!.ToJsonString().Replace("\"roads\"", "\"roads_two\"", StringComparison.Ordinal))));
        Assert.Contains(twice.Errors, e => e.Message.Contains("RoadsBalanceDef", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    public void RoadsHaveTheResearchValues()
    {
        RoadsBalanceDef roads = RealCatalog().Roads;

        Assert.Equal("roads", roads.Id);
        Assert.Equal(4, roads.StepIncrement);
        Assert.Equal(90, roads.DecayEveryTicks);
        Assert.Equal(255, roads.MaxCounter);
        Assert.Equal(40, roads.TrailThreshold);
        Assert.Equal(120, roads.RoadThreshold);
        Assert.Equal(25, roads.TrailKeepThreshold);
        Assert.Equal(90, roads.RoadKeepThreshold);
        Assert.Equal(800, roads.TrailCostPermille);
        Assert.Equal(600, roads.RoadCostPermille);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-15")]
    public void RoadsConsistencyIsChecked()
    {
        (string Member, int Value, string Reported)[] bad =
        [
            ("roadThreshold", 40, "roadThreshold"),
            ("maxCounter", 100, "maxCounter"),
            ("trailKeepThreshold", 41, "trailKeepThreshold"),
            ("roadKeepThreshold", 121, "roadKeepThreshold"),
            ("roadKeepThreshold", 25, "roadKeepThreshold"),
            ("roadCostPermille", 800, "roadCostPermille"),
        ];
        foreach ((string member, int value, string reported) in bad)
        {
            ContentLoadResult result = LoadWith("balance/roads.json", items => First(items)[member] = value);
            Assert.Null(result.Catalog);
            Assert.Contains(result.Errors, e => e.Kind == ContentErrorKind.Validation && e.File == "balance/roads.json" && e.JsonPath == "$.items[0]." + reported);
        }

        AssertSingleError(
            LoadWith("balance/roads.json", items => First(items)["stepIncrement"] = 0),
            ContentErrorKind.Validation, "balance/roads.json", "$.items[0].stepIncrement");
        AssertSingleError(
            LoadWith("balance/roads.json", items => First(items)["decayEveryTicks"] = 0),
            ContentErrorKind.Validation, "balance/roads.json", "$.items[0].decayEveryTicks");
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void RegrowthHasTheAgreedValues()
    {
        RegrowthBalanceDef regrowth = RealCatalog().Regrowth;

        Assert.Equal("regrowth", regrowth.Id);
        Assert.Equal(10, regrowth.StumpRegrowDaysNearTree);
        Assert.Equal(30, regrowth.StumpRegrowDaysFar);
        Assert.Equal(2, regrowth.NearTreeRadius);
        Assert.Equal(10, regrowth.SaplingGrowDays);
        Assert.Equal(30, regrowth.PlantActionTicks);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void RegrowthRulesAreChecked()
    {
        AssertSingleError(
            LoadWith("balance/regrowth.json", items => First(items)["stumpRegrowDaysFar"] = 5),
            ContentErrorKind.Validation, "balance/regrowth.json", "$.items[0].stumpRegrowDaysFar");
        AssertSingleError(
            LoadWith("balance/regrowth.json", items => First(items)["nearTreeRadius"] = 0),
            ContentErrorKind.Validation, "balance/regrowth.json", "$.items[0].nearTreeRadius");
        AssertSingleError(
            LoadWith("balance/regrowth.json", items => First(items)["saplingGrowDays"] = 0),
            ContentErrorKind.Validation, "balance/regrowth.json", "$.items[0].saplingGrowDays");
        AssertSingleError(
            LoadWith("balance/regrowth.json", items => First(items)["plantActionTicks"] = 0),
            ContentErrorKind.Validation, "balance/regrowth.json", "$.items[0].plantActionTicks");
        Assert.NotNull(LoadWith("balance/regrowth.json", items => First(items)["stumpRegrowDaysFar"] = 10).Catalog);
    }

    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void HumanNamesAreUniqueLatinAndNumerous()
    {
        NameListDef human = RealCatalog().Names.Single(n => n.Id == "human_names");

        Assert.True(human.Names.Count >= 60, "need at least 60 names");
        Assert.Equal(human.Names.Count, human.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(human.Names, name => Assert.Matches(NameListDef.NamePattern, name));
        Assert.Empty(human.CheckConsistency());
    }

    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void NameListProblemsAreReported()
    {
        AssertSingleError(
            LoadWith("names/names.json", items => First(items)["names"] = new JsonArray("Alden", "Brann", "Alden")),
            ContentErrorKind.Validation, "names/names.json", "$.items[0].names[2]");
        AssertSingleError(
            LoadWith("names/names.json", items => First(items)["names"] = new JsonArray("Alden", "alden")),
            ContentErrorKind.Validation, "names/names.json", "$.items[0].names[1]");
        AssertSingleError(
            LoadWith("names/names.json", items => First(items)["names"] = new JsonArray("Алден")),
            ContentErrorKind.Validation, "names/names.json", "$.items[0].names[0]");
        AssertSingleError(
            LoadWith("names/names.json", items => First(items)["names"] = new JsonArray("Al")),
            ContentErrorKind.Validation, "names/names.json", "$.items[0].names[0]");
        AssertSingleError(
            LoadWith("names/names.json", items => First(items)["names"] = new JsonArray("Mary Ann")),
            ContentErrorKind.Validation, "names/names.json", "$.items[0].names[0]");
        AssertSingleError(
            LoadWith("names/names.json", items => First(items)["names"] = new JsonArray()),
            ContentErrorKind.Validation, "names/names.json", "$.items[0].names");
        Assert.Equal(
            "names.schema.json",
            ContentCategories.Find("names/names.json")!.SchemaFileName);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void FieldBuildingHasWorkAtLevelOne()
    {
        BuildingDef field = RealCatalog().Buildings.Single(b => b.Id == "field");

        Assert.Equal("production", field.Category);
        Assert.Equal(3, field.Footprint.Width);
        Assert.Equal(3, field.Footprint.Height);
        Assert.True(field.Provisional);
        BuildingLevelDef level = Assert.Single(field.Levels);
        Assert.Equal([new ItemAmountDef("wood", 15)], level.Cost);
        Assert.Equal(new WorkDef("farm", Slots: 2, CycleTicks: 400, Item: "vegetable", Yield: 6), level.Work);
        Assert.Null(RealCatalog().Buildings.Single(b => b.Id == "house").Levels[0].Work);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    public void WorkIsChecked()
    {
        static JsonObject Work(JsonArray items) => Item(items, "field")["levels"]![0]!["work"]!.AsObject();

        AssertSingleError(
            LoadWith("buildings/buildings.json", items => Work(items)["job"] = "fish"),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[3].levels[0].work.job");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => Work(items)["item"] = "mithril"),
            ContentErrorKind.Reference, "buildings/buildings.json", "$.items[3].levels[0].work.item");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => Work(items)["item"] = "carrier"),
            ContentErrorKind.Reference, "buildings/buildings.json", "$.items[3].levels[0].work.item");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => Work(items)["slots"] = 0),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[3].levels[0].work.slots");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => Work(items)["cycleTicks"] = 0),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[3].levels[0].work.cycleTicks");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => Work(items)["yield"] = 0),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[3].levels[0].work.yield");
    }

    /// <summary>
    /// Review of M3a: an upgrade keeps the building's work (the saved output and the jobs name the item of the current
    /// level, and the output exists while the building is upgraded), so work may not appear, disappear or change its job
    /// or item between levels; slots, cycle and yield may change.
    /// </summary>
    [Fact]
    [Trait("Req", "FR-BUILD-06")]
    [Trait("Req", "FR-SAVE-02")]
    public void WorkKeepsItsJobAndItemOverTheLevels()
    {
        static JsonArray Levels(JsonArray items, string id) => Item(items, id)["levels"]!.AsArray();

        static void AddFieldLevel(JsonArray items, Action<JsonObject> edit)
        {
            JsonObject level = Levels(items, "field")[0]!.DeepClone().AsObject();
            edit(level);
            Levels(items, "field").Add(level);
        }

        ContentLoadResult better = LoadWith("buildings/buildings.json", items => AddFieldLevel(items, level =>
        {
            level["work"]!["slots"] = 3;
            level["work"]!["cycleTicks"] = 300;
            level["work"]!["yield"] = 8;
        }));
        Assert.Empty(better.Errors);
        Assert.Equal(new WorkDef("farm", 3, 300, "vegetable", 8), better.Catalog!.Buildings.Single(b => b.Id == "field").Levels[1].Work);

        AssertSingleError(
            LoadWith("buildings/buildings.json", items => AddFieldLevel(items, level => level["work"]!["item"] = "wood")),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[3].levels[1].work");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => AddFieldLevel(items, level => level["work"]!["job"] = "haul")),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[3].levels[1].work");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => AddFieldLevel(items, level => level.Remove("work"))),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[3].levels[1].work");
        AssertSingleError(
            LoadWith("buildings/buildings.json", items => Levels(items, "house")[1]!["work"] = Levels(items, "field")[0]!["work"]!.DeepClone()),
            ContentErrorKind.Validation, "buildings/buildings.json", "$.items[1].levels[1].work");
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void M3aSchemasRejectBadData()
    {
        (string schemaName, string json)[] bad =
        [
            ("items.schema.json", """{ "items": [ { "id": "a", "stackSize": 1, "weight": 1, "category": "junk" } ] }"""),
            ("items.schema.json", """{ "items": [ { "id": "a", "stackSize": 1, "weight": 1, "foodValue": -1 } ] }"""),
            ("professions.schema.json", """{ "items": [ { "id": "a", "kind": "ordinary", "skill": "mining" } ] }"""),
            ("names.schema.json", """{ "items": [ { "id": "human_names" } ] }"""),
            ("economy.schema.json", """{ "items": [ { "id": "economy", "foodPerResidentPerDay": 0 } ] }"""),
            ("roads.schema.json", """{ "items": [ { "id": "roads", "stepIncrement": 4 } ] }"""),
            ("regrowth.schema.json", """{ "items": [ { "id": "regrowth", "nearTreeRadius": 2 } ] }"""),
            ("buildings.schema.json", """{ "items": [ { "id": "a", "category": "production", "footprint": { "width": 1, "height": 1 }, "outsideTerritory": false, "placement": "flat", "rotatable": false, "provisional": true, "levels": [ { "cost": [], "buildTicks": 1, "moveTicks": 1, "requirements": [], "work": { "job": "fish", "slots": 1, "cycleTicks": 1, "item": "wood", "yield": 1 } } ] } ] }"""),
        ];
        foreach ((string schemaName, string json) in bad)
        {
            JsonSchema schema = JsonSchema.Build(JsonDocument.Parse(Read("schema/" + schemaName)).RootElement);
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid, json);
        }
    }
}
