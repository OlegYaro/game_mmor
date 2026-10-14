using System.Text.Json;
using Game.Sim;
using Godot;
using GodotFileAccess = Godot.FileAccess;

namespace Game.World;

/// <summary>One cell of a placeholder art atlas (<c>data/art/atlas.json</c>, ADR 0010).</summary>
/// <param name="Id">Art id, e.g. <c>meadow_h2_v1</c> or <c>tree</c>.</param>
/// <param name="Atlas">Atlas id (<c>tiles</c> or <c>objects</c>).</param>
/// <param name="Kind"><c>tile</c> or <c>object</c>.</param>
/// <param name="Region">Pixel rectangle in the atlas image.</param>
/// <param name="Origin">Pixel inside <paramref name="Region"/> that sits on the tile's ground centre.</param>
/// <param name="Height">Height step the art is drawn for.</param>
/// <param name="Variant">Variant number.</param>
public sealed record AtlasCell(string Id, string Atlas, string Kind, Rect2I Region, Vector2I Origin, int Height, int Variant);

/// <summary>One atlas image of the table.</summary>
/// <param name="Id">Atlas id.</param>
/// <param name="ResourcePath">Path of the image under <c>res://</c>.</param>
public sealed record AtlasImage(string Id, string ResourcePath);

/// <summary>
/// The "art id → atlas cell" table. Replacing the placeholder art means replacing the PNGs and this table; the renderer
/// only looks cells up by id.
/// </summary>
public sealed class AtlasTable
{
    private const string TableFile = "art/atlas.json";

    private readonly Dictionary<string, AtlasCell> _cells;

    private AtlasTable(int tileWidth, int tileHeight, int heightStep, IReadOnlyList<AtlasImage> images, Dictionary<string, AtlasCell> cells)
    {
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        HeightStep = heightStep;
        Images = images;
        _cells = cells;
    }

    /// <summary>Tile diamond width.</summary>
    public int TileWidth { get; }

    /// <summary>Tile diamond height.</summary>
    public int TileHeight { get; }

    /// <summary>Lift of one height step in pixels.</summary>
    public int HeightStep { get; }

    /// <summary>Atlas images.</summary>
    public IReadOnlyList<AtlasImage> Images { get; }

    /// <summary>All cells.</summary>
    public IEnumerable<AtlasCell> Cells => _cells.Values;

    /// <summary>Looks a cell up by art id.</summary>
    public bool TryGet(string id, out AtlasCell cell) => _cells.TryGetValue(id, out cell!);

    /// <summary>Reads the table from the content data folder (<see cref="GodotContentSource.ResolveDataRoot"/>).</summary>
    public static bool TryLoad(out AtlasTable table, out string error)
    {
        string path = $"{GodotContentSource.ResolveDataRoot()}/{TableFile}";
        using GodotFileAccess? file = GodotFileAccess.Open(path, GodotFileAccess.ModeFlags.Read);
        if (file is null)
        {
            table = null!;
            error = $"Cannot read the art table '{path}': {GodotFileAccess.GetOpenError()}.";
            return false;
        }

        try
        {
            table = Parse(file.GetAsText());
            error = string.Empty;
            return true;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            table = null!;
            error = $"Art table '{path}' is invalid: {e.Message}";
            return false;
        }
    }

    /// <summary>Parses the JSON text of the table.</summary>
    /// <exception cref="JsonException">Malformed JSON.</exception>
    /// <exception cref="KeyNotFoundException">A required property is missing.</exception>
    public static AtlasTable Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        var images = new List<AtlasImage>();
        foreach (JsonElement atlas in root.GetProperty("atlases").EnumerateArray())
        {
            images.Add(new AtlasImage(atlas.GetProperty("id").GetString()!, "res://" + atlas.GetProperty("file").GetString()));
        }

        var cells = new Dictionary<string, AtlasCell>(StringComparer.Ordinal);
        foreach (JsonElement c in root.GetProperty("cells").EnumerateArray())
        {
            var cell = new AtlasCell(
                c.GetProperty("id").GetString()!,
                c.GetProperty("atlas").GetString()!,
                c.GetProperty("kind").GetString()!,
                new Rect2I(Int(c, "x"), Int(c, "y"), Int(c, "w"), Int(c, "h")),
                new Vector2I(Int(c, "originX"), Int(c, "originY")),
                Int(c, "height"),
                Int(c, "variant"));
            if (!cells.TryAdd(cell.Id, cell))
            {
                throw new InvalidOperationException($"Duplicate art id '{cell.Id}'.");
            }
        }

        return new AtlasTable(Int(root, "tileWidth"), Int(root, "tileHeight"), Int(root, "heightStep"), images, cells);
    }

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();
}
