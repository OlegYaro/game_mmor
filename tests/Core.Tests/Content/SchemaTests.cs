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
            Core.Content.ContentCategory category = Core.Content.ContentCategories.All.Single(c => c.Folder == folder);
            using JsonDocument instance = JsonDocument.Parse(file.Text);
            EvaluationResults results = LoadSchema(category.SchemaFileName)
                .Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
            Assert.True(results.IsValid, file.Path + " does not match " + category.SchemaFileName);
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 2);
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
