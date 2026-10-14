using Core.Content;
using Godot;
using GodotFileAccess = Godot.FileAccess;

namespace Game.Sim;

/// <summary>
/// Content source backed by a Godot-readable directory (a real folder in dev, or <c>res://data</c> in an export).
/// Files are read eagerly by <see cref="TryCreate"/> so that read errors surface as a message instead of an exception.
/// </summary>
public sealed class GodotContentSource : IContentSource
{
    /// <summary>Folder in the exported project holding the synced copy of the root data/ folder.</summary>
    public const string ResourceDataPath = "res://data";

    private const string DevRelativePath = "../../data";
    private const string SchemaFolder = "schema";
    private const string TimeBalanceFile = "balance/time.json";

    private readonly List<ContentFile> _files;

    private GodotContentSource(List<ContentFile> files) => _files = files;

    /// <summary>
    /// Where content is read from: the root <c>data/</c> folder next to the project in a dev run (so edits show up
    /// without a sync), otherwise <c>res://data</c>.
    /// </summary>
    public static string ResolveDataRoot()
    {
        if (!OS.HasFeature("template"))
        {
            string projectDir = ProjectSettings.GlobalizePath("res://");
            string dev = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDir, DevRelativePath));
            if (System.IO.File.Exists(System.IO.Path.Combine(dev, TimeBalanceFile)))
            {
                return dev;
            }
        }

        return ResourceDataPath;
    }

    /// <summary>Reads every <c>*.json</c> file below <paramref name="root"/> except the schema folder.</summary>
    /// <returns>False with a message in <paramref name="error"/> when the folder is missing or a file cannot be read.</returns>
    public static bool TryCreate(string root, out GodotContentSource source, out string error)
    {
        source = new GodotContentSource([]);
        error = string.Empty;
        using DirAccess? dir = DirAccess.Open(root);
        if (dir is null)
        {
            error = $"Content folder '{root}' cannot be opened ({DirAccess.GetOpenError()}); run tools/sync-data.sh or check the data path.";
            return false;
        }

        if (!Collect(root, string.Empty, source._files, out error))
        {
            return false;
        }

        if (source._files.Count == 0)
        {
            error = $"Content folder '{root}' contains no *.json files.";
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentFile> GetFiles() => _files;

    private static bool Collect(string root, string relativeDir, List<ContentFile> files, out string error)
    {
        error = string.Empty;
        string path = relativeDir.Length == 0 ? root : $"{root}/{relativeDir}";
        using DirAccess? dir = DirAccess.Open(path);
        if (dir is null)
        {
            error = $"Cannot open content folder '{path}'.";
            return false;
        }

        foreach (string name in dir.GetFiles())
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            string relative = relativeDir.Length == 0 ? name : $"{relativeDir}/{name}";
            using GodotFileAccess? file = GodotFileAccess.Open($"{root}/{relative}", GodotFileAccess.ModeFlags.Read);
            if (file is null)
            {
                error = $"Cannot read content file '{root}/{relative}': {GodotFileAccess.GetOpenError()}.";
                return false;
            }

            files.Add(new ContentFile(relative, file.GetAsText()));
        }

        foreach (string sub in dir.GetDirectories())
        {
            string relative = relativeDir.Length == 0 ? sub : $"{relativeDir}/{sub}";
            if (relative == SchemaFolder)
            {
                continue;
            }

            if (!Collect(root, relative, files, out error))
            {
                return false;
            }
        }

        return true;
    }
}
