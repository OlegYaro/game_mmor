using System.Text.Json;
using Json.Schema;
using SchemaGen;

namespace Core.Tests.Content;

public sealed class SchemaTests
{
    private static JsonSchema LoadSchema(string name) =>
        JsonSchema.Build(JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Data, "schema", name))).RootElement);

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void DataFilesValidateAgainstTheirSchemas()
    {
        int checkedFiles = 0;
        foreach (Core.Content.ContentFile file in TestPaths.LoadDataSource().GetFiles())
        {
            string folder = file.Path[..file.Path.IndexOf('/', StringComparison.Ordinal)];
            if (Core.Content.ContentCategories.NonDefinitionFolders.Contains(folder))
            {
                continue; // validated by its own test (Art/AtlasTests)
            }

            Core.Content.ContentCategory category = Core.Content.ContentCategories.Find(file.Path)!;
            using JsonDocument instance = JsonDocument.Parse(file.Text);
            EvaluationResults results = LoadSchema(category.SchemaFileName)
                .Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
            Assert.True(results.IsValid, file.Path + " does not match " + category.SchemaFileName);
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 9);
        Assert.Equal("fog.schema.json", Core.Content.ContentCategories.Find("balance/fog.json")!.SchemaFileName);
        Assert.Equal("balance.schema.json", Core.Content.ContentCategories.Find("balance/time.json")!.SchemaFileName);
        Assert.Equal("relations.schema.json", Core.Content.ContentCategories.Find("balance/relations.json")!.SchemaFileName);
        Assert.Equal("eras.schema.json", Core.Content.ContentCategories.Find("eras/eras.json")!.SchemaFileName);
        Assert.Equal("races.schema.json", Core.Content.ContentCategories.Find("races/races.json")!.SchemaFileName);
        Assert.Equal("buildings.schema.json", Core.Content.ContentCategories.Find("buildings/buildings.json")!.SchemaFileName);
        Assert.Equal("professions.schema.json", Core.Content.ContentCategories.Find("professions/professions.json")!.SchemaFileName);
        Assert.Null(Core.Content.ContentCategories.Find("nofolder.json"));
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void SchemaRejectsBadData()
    {
        JsonSchema schema = LoadSchema("items.schema.json");
        string[] bad =
        [
            """{ "items": [ { "id": "wood", "stackSize": 0, "weight": 1 } ] }""",
            """{ "items": [ { "id": "Wood", "stackSize": 1, "weight": 1 } ] }""",
            """{ "items": [ { "id": "wood", "stackSize": 1, "weight": 1, "extra": 1 } ] }""",
            """{ "items": [ { "id": "wood", "weight": 1 } ] }""",
        ];
        foreach (string json in bad)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid, json);
        }
    }

    [Fact]
    [Trait("Req", "FR-MOD-01")]
    public void GeneratedSchemasEqualCommittedSchemas()
    {
        IReadOnlyDictionary<string, string> generated = SchemaGenerator.Generate();

        Assert.NotEmpty(generated);
        foreach ((string name, string text) in generated)
        {
            string path = Path.Combine(TestPaths.Data, "schema", name);
            Assert.True(File.Exists(path), name + " is missing; run tools/schemagen.sh");
            string committed = File.ReadAllText(path);
            Assert.DoesNotContain('\r', committed);
            Assert.True(text == committed, name + " is stale; run tools/schemagen.sh");
        }

        string[] onDisk = Directory.GetFiles(Path.Combine(TestPaths.Data, "schema"), "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(generated.Keys.Order(StringComparer.Ordinal), onDisk);
    }
}
