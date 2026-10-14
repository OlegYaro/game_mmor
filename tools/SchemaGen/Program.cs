using SchemaGen;

string root = SchemaGenerator.FindRepoRoot(AppContext.BaseDirectory);
string dir = Path.Combine(root, "data", "schema");
Directory.CreateDirectory(dir);
foreach ((string name, string text) in SchemaGenerator.Generate())
{
    // Write bytes explicitly: UTF-8 without BOM, LF endings on every OS.
    File.WriteAllBytes(Path.Combine(dir, name), new System.Text.UTF8Encoding(false).GetBytes(text));
    Console.WriteLine("schemagen: " + Path.Combine("data", "schema", name));
}
