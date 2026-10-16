using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Content;
using Json.Schema;

namespace Core.Tests.Content;

/// <summary>
/// Workshop data loaded in M3a for M3b: recipes, their items (lumber, stone blocks) and buildings (sawmill,
/// stonecutter). Numbers from docs/research/economy-six-chains.md, all provisional.
/// </summary>
public sealed class RecipeContentTests
{
    private const string RecipesPath = "recipes/recipes.json";

    private static string Read(string path) => File.ReadAllText(Path.Combine(TestPaths.Data, path));

    private static ContentCatalog RealCatalog() => ContentCatalog.Load(TestPaths.LoadDataSource()).Catalog!;

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

    private static ContentLoadResult EditLumber(Action<JsonObject> edit) => LoadWith(RecipesPath, items => edit(items[0]!.AsObject()));

    private static void AssertSingleError(ContentLoadResult result, ContentErrorKind kind, string jsonPath)
    {
        Assert.Null(result.Catalog);
        ContentError error = Assert.Single(result.Errors);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(RecipesPath, error.File);
        Assert.Equal(jsonPath, error.JsonPath);
    }

    [Fact]
    [Trait("Req", "FR-ECO-02")]
    public void RecipesHaveTheResearchValues()
    {
        ContentCatalog catalog = RealCatalog();

        Assert.Equal(["recipe_lumber", "recipe_stone_block"], catalog.Recipes.Select(r => r.Id));
        RecipeDef lumber = catalog.Recipes[0];
        Assert.Equal("sawmill", lumber.Building);
        Assert.Equal([new ItemAmountDef("wood", 2)], lumber.Inputs);
        Assert.Equal([new ItemAmountDef("lumber", 1)], lumber.Outputs);
        Assert.Equal(600, lumber.CycleTicks); // 60 s at 10 ticks per second
        Assert.Equal("crafting", lumber.Skill);

        RecipeDef block = catalog.Recipes[1];
        Assert.Equal("stonecutter", block.Building);
        Assert.Equal([new ItemAmountDef("stone", 2)], block.Inputs);
        Assert.Equal([new ItemAmountDef("stone_block", 1)], block.Outputs);
        Assert.Equal(900, block.CycleTicks); // 90 s
        Assert.Equal("mining", block.Skill);

        Assert.All(catalog.Recipes, r => Assert.True(r.Provisional));
        Assert.All(catalog.Recipes, r => Assert.Null(r.Specialist));
        Assert.All(catalog.Recipes, r => Assert.Empty(r.CheckConsistency()));
        Assert.Equal(catalog.Time.TicksPerSecond * 60, lumber.CycleTicks);
        Assert.Equal(catalog.Time.TicksPerSecond * 90, block.CycleTicks);
    }

    [Fact]
    [Trait("Req", "FR-BUILD-03")]
    public void WorkshopItemsAreMaterials()
    {
        ContentCatalog catalog = RealCatalog();

        Assert.True(catalog.TryGet("lumber", out ItemDef lumber));
        Assert.Equal(("material", 100, 1000, 0), (lumber.Category, lumber.StackSize, lumber.Weight, lumber.FoodValue));
        Assert.Equal("item.lumber.name", lumber.EffectiveNameKey);
        Assert.True(catalog.TryGet("stone_block", out ItemDef block));
        Assert.Equal(("material", 50, 2500, 0), (block.Category, block.StackSize, block.Weight, block.FoodValue));
        Assert.Equal("item.stone_block.name", block.EffectiveNameKey);
    }

    [Fact]
    [Trait("Req", "FR-ECO-02")]
    [Trait("Req", "FR-BUILD-01")]
    public void WorkshopBuildingsAreProductionWithoutWorkYet()
    {
        ContentCatalog catalog = RealCatalog();

        Assert.True(catalog.TryGet("sawmill", out BuildingDef sawmill));
        Assert.Equal(("production", 3, 2, true, false, true), (sawmill.Category, sawmill.Footprint.Width, sawmill.Footprint.Height, sawmill.Rotatable, sawmill.OutsideTerritory, sawmill.Provisional));
        BuildingLevelDef sawmillLevel = Assert.Single(sawmill.Levels);
        Assert.Equal([new ItemAmountDef("wood", 25), new ItemAmountDef("stone", 5)], sawmillLevel.Cost);
        Assert.Equal((100, 33, 0, 0), (sawmillLevel.BuildTicks, sawmillLevel.MoveTicks, sawmillLevel.Capacity, sawmillLevel.Storage));
        Assert.Empty(sawmillLevel.Requirements);
        Assert.Null(sawmillLevel.Work); // the workshop logic that runs recipe_lumber is M3b

        Assert.True(catalog.TryGet("stonecutter", out BuildingDef stonecutter));
        Assert.Equal(("production", 2, 2, true, false, true), (stonecutter.Category, stonecutter.Footprint.Width, stonecutter.Footprint.Height, stonecutter.Rotatable, stonecutter.OutsideTerritory, stonecutter.Provisional));
        BuildingLevelDef stonecutterLevel = Assert.Single(stonecutter.Levels);
        Assert.Equal([new ItemAmountDef("wood", 20), new ItemAmountDef("stone", 10)], stonecutterLevel.Cost);
        Assert.Equal((100, 33, 0, 0), (stonecutterLevel.BuildTicks, stonecutterLevel.MoveTicks, stonecutterLevel.Capacity, stonecutterLevel.Storage));
        Assert.Empty(stonecutterLevel.Requirements);
        Assert.Null(stonecutterLevel.Work);

        // Every recipe names one of these buildings.
        Assert.All(catalog.Recipes, r => Assert.True(catalog.TryGet(r.Building, out BuildingDef _)));
    }

    [Fact]
    [Trait("Req", "FR-ECO-02")]
    public void RecipeReferencesAreChecked()
    {
        AssertSingleError(EditLumber(r => r["building"] = "mill"), ContentErrorKind.Reference, "$.items[0].building");
        AssertSingleError(EditLumber(r => r["building"] = "wood"), ContentErrorKind.Reference, "$.items[0].building");
        AssertSingleError(EditLumber(r => r["inputs"]![0]!["item"] = "plank"), ContentErrorKind.Reference, "$.items[0].inputs[0].item");
        AssertSingleError(EditLumber(r => r["inputs"]![0]!["item"] = "sawmill"), ContentErrorKind.Reference, "$.items[0].inputs[0].item");
        AssertSingleError(EditLumber(r => r["outputs"]![0]!["item"] = "plank"), ContentErrorKind.Reference, "$.items[0].outputs[0].item");
        AssertSingleError(EditLumber(r => r["outputs"]![0]!["item"] = "recipe_stone_block"), ContentErrorKind.Reference, "$.items[0].outputs[0].item");
        AssertSingleError(EditLumber(r => r["specialist"] = "carpenter"), ContentErrorKind.Reference, "$.items[0].specialist");
        AssertSingleError(EditLumber(r => r["specialist"] = "carrier"), ContentErrorKind.Reference, "$.items[0].specialist");
        AssertSingleError(EditLumber(r => r["specialist"] = "wood"), ContentErrorKind.Reference, "$.items[0].specialist");

        // A specialist profession is accepted.
        ContentLoadResult specialist = EditLumber(r => r["specialist"] = "blacksmith");
        Assert.Empty(specialist.Errors);
        Assert.Equal("blacksmith", specialist.Catalog!.Recipes[0].Specialist);
    }

    [Fact]
    [Trait("Req", "FR-ECO-02")]
    public void RecipeValuesAreChecked()
    {
        AssertSingleError(EditLumber(r => r["cycleTicks"] = 0), ContentErrorKind.Validation, "$.items[0].cycleTicks");
        AssertSingleError(EditLumber(r => r["inputs"]![0]!["count"] = 0), ContentErrorKind.Validation, "$.items[0].inputs[0].count");
        AssertSingleError(EditLumber(r => r["outputs"]![0]!["count"] = 0), ContentErrorKind.Validation, "$.items[0].outputs[0].count");
        AssertSingleError(EditLumber(r => r["skill"] = "carpentry"), ContentErrorKind.Validation, "$.items[0].skill");
        AssertSingleError(EditLumber(r => r["inputs"] = new JsonArray()), ContentErrorKind.Validation, "$.items[0].inputs");
        AssertSingleError(EditLumber(r => r["outputs"] = new JsonArray()), ContentErrorKind.Validation, "$.items[0].outputs");
        AssertSingleError(
            EditLumber(r => r["inputs"] = new JsonArray(Amount("wood", 1), Amount("stone", 1), Amount("wood", 1))),
            ContentErrorKind.Validation, "$.items[0].inputs[2].item");
        AssertSingleError(
            EditLumber(r => r["outputs"] = new JsonArray(Amount("lumber", 1), Amount("lumber", 1))),
            ContentErrorKind.Validation, "$.items[0].outputs[1].item");

        // The same item may be an input and an output, and several different items are fine.
        ContentLoadResult mixed = EditLumber(r =>
        {
            r["inputs"] = new JsonArray(Amount("wood", 2), Amount("stone", 1));
            r["outputs"] = new JsonArray(Amount("lumber", 1), Amount("wood", 1));
        });
        Assert.Empty(mixed.Errors);

        Assert.Contains(EditLumber(r => r.Remove("building")).Errors, e => e.Kind == ContentErrorKind.Parse && e.File == RecipesPath);
        Assert.Contains(EditLumber(r => r["bogus"] = 1).Errors, e => e.Kind == ContentErrorKind.Parse && e.File == RecipesPath);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void RecipeIdsShareTheNamespaceWithItems()
    {
        // `lumber` is the item, so a recipe may not reuse it (one id namespace across categories, ADR 0008).
        ContentLoadResult result = EditLumber(r => r["id"] = "lumber");

        AssertSingleError(result, ContentErrorKind.DuplicateId, "$.items[0].id");
        Assert.Contains("items/basic.json", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void RecipesAreOptional()
    {
        InMemoryContentSource source = new();
        foreach (ContentFile file in TestPaths.LoadDataSource().GetFiles().Where(f => f.Path != RecipesPath))
        {
            source.Add(file.Path, file.Text);
        }

        ContentLoadResult result = ContentCatalog.Load(source);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Catalog!.Recipes);
        Assert.Equal("recipes.schema.json", ContentCategories.Find(RecipesPath)!.SchemaFileName);
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void RecipeSchemaRejectsBadData()
    {
        JsonSchema schema = JsonSchema.Build(JsonDocument.Parse(Read("schema/recipes.schema.json")).RootElement);
        const string Good = """{ "items": [ { "id": "r", "building": "b", "inputs": [ { "item": "a", "count": 1 } ], "outputs": [ { "item": "c", "count": 1 } ], "cycleTicks": 1, "skill": "crafting", "provisional": true } ] }""";
        using (JsonDocument good = JsonDocument.Parse(Good))
        {
            Assert.True(schema.Evaluate(good.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid);
        }

        string[] bad =
        [
            Good.Replace("\"cycleTicks\": 1", "\"cycleTicks\": 0", StringComparison.Ordinal),
            Good.Replace("\"crafting\"", "\"carpentry\"", StringComparison.Ordinal),
            Good.Replace("\"count\": 1 } ], \"outputs\"", "\"count\": 0 } ], \"outputs\"", StringComparison.Ordinal),
            Good.Replace("\"building\": \"b\"", "\"building\": \"Bad Id\"", StringComparison.Ordinal),
            Good.Replace("\"provisional\": true", "\"provisional\": true, \"specialist\": \"Bad\"", StringComparison.Ordinal),
            Good.Replace("\"building\": \"b\", ", string.Empty, StringComparison.Ordinal),
            Good.Replace("\"provisional\": true", "\"provisional\": true, \"extra\": 1", StringComparison.Ordinal),
        ];
        foreach (string json in bad)
        {
            Assert.NotEqual(Good, json);
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid, json);
        }
    }

    private static JsonObject Amount(string item, int count) => new() { ["item"] = item, ["count"] = count };
}
