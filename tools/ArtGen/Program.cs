using ArtGen;

string root = ArtGenerator.FindRepoRoot(AppContext.BaseDirectory);

if (args.Length >= 2 && args[0] == "preview")
{
    File.WriteAllBytes(args[1], PngWriter.Encode(Preview.Enlarge(Preview.Render(ArtGenerator.Generate()))));
    Console.WriteLine("artgen: preview -> " + args[1]);
    return;
}

ArtOutput art = ArtGenerator.Generate();
Write(ArtGenerator.TilesPath, art.TilesPng);
Write(ArtGenerator.ObjectsPath, art.ObjectsPng);
// UTF-8 without BOM, LF endings on every OS.
Write(ArtGenerator.TablePath, new System.Text.UTF8Encoding(false).GetBytes(art.AtlasJson));
Write(ArtGenerator.SchemaPath, new System.Text.UTF8Encoding(false).GetBytes(art.SchemaJson));

void Write(string relative, byte[] bytes)
{
    string path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    Console.WriteLine("artgen: " + relative);
}
