using Core.Content;

namespace Core.Tests.Content;

/// <summary>Locates the repository and exposes the real data/ folder as a content source (filesystem lives in tests, not Core).</summary>
internal static class TestPaths
{
    public static string Root { get; } = FindRoot();

    public static string Data => Path.Combine(Root, "data");

    public static string Locale => Path.Combine(Root, "locale");

    private static string FindRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Game.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Reads every data/**/*.json except data/schema into an in-memory source with forward-slash relative paths.</summary>
    public static InMemoryContentSource LoadDataSource()
    {
        InMemoryContentSource source = new();
        foreach (string path in Directory.GetFiles(Data, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(Data, path).Replace('\\', '/');
            if (!relative.StartsWith("schema/", StringComparison.Ordinal))
            {
                source.Add(relative, File.ReadAllText(path));
            }
        }

        return source;
    }
}
