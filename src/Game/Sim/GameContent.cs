using Core.Content;

namespace Game.Sim;

/// <summary>Loads the content catalog for the running game. Failures are returned as text, never thrown.</summary>
public static class GameContent
{
    /// <summary>Loads and validates all content.</summary>
    /// <param name="catalog">The catalog when loading worked.</param>
    /// <param name="error">All problems, one per line, when it did not.</param>
    public static bool TryLoad(out ContentCatalog catalog, out string error)
    {
        string root = GodotContentSource.ResolveDataRoot();
        if (!GodotContentSource.TryCreate(root, out GodotContentSource source, out error))
        {
            catalog = null!;
            return false;
        }

        ContentLoadResult result = ContentCatalog.Load(source);
        if (result.Catalog is null)
        {
            catalog = null!;
            error = $"Content in '{root}' is invalid:\n" + string.Join('\n', result.Errors.Select(e => e.ToString()));
            return false;
        }

        catalog = result.Catalog;
        error = string.Empty;
        return true;
    }
}
