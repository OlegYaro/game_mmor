using System.Globalization;
using System.Text;

namespace ArtGen;

/// <summary>One cell of the atlas table.</summary>
/// <param name="Id">Tile cell id (<c>meadow_h2_v1</c>) or object id (<c>tree</c>).</param>
/// <param name="Atlas">Atlas id: <c>tiles</c>, <c>objects</c> or <c>buildings</c>.</param>
/// <param name="Kind"><c>tile</c>, <c>object</c> or <c>building</c>.</param>
/// <param name="X">Left edge in the atlas image.</param>
/// <param name="Y">Top edge in the atlas image.</param>
/// <param name="W">Width.</param>
/// <param name="H">Height.</param>
/// <param name="OriginX">Cell pixel (x) that sits on the map cell's ground diamond centre (buildings: bottom corner of the footprint diamond).</param>
/// <param name="OriginY">Cell pixel (y) that sits on the map cell's ground diamond centre (buildings: bottom corner of the footprint diamond).</param>
/// <param name="Height">Height level of a tile (0 for objects and buildings).</param>
/// <param name="Variant">Colour variant of a tile (0 for objects); construction stage 0..3 of a building frame.</param>
public sealed record AtlasCell(string Id, string Atlas, string Kind, int X, int Y, int W, int H, int OriginX, int OriginY, int Height, int Variant);

/// <summary>Everything the generator produces; all of it is a pure function of the code.</summary>
/// <param name="Tiles">Tile atlas image.</param>
/// <param name="Objects">Object atlas image.</param>
/// <param name="Cells">The table, in file order.</param>
/// <param name="TilesPng">Encoded tile atlas.</param>
/// <param name="ObjectsPng">Encoded object atlas.</param>
/// <param name="Buildings">Building atlas image.</param>
/// <param name="BuildingsPng">Encoded building atlas.</param>
/// <param name="AtlasJson">Text of data/art/atlas.json.</param>
/// <param name="SchemaJson">Text of data/art/atlas.schema.json.</param>
public sealed record ArtOutput(Canvas Tiles, Canvas Objects, Canvas Buildings, IReadOnlyList<AtlasCell> Cells, byte[] TilesPng, byte[] ObjectsPng, byte[] BuildingsPng, string AtlasJson, string SchemaJson);

/// <summary>Builds the placeholder atlases and the lookup table.</summary>
public static class ArtGenerator
{
    /// <summary>Repo-relative output paths.</summary>
    public const string TilesPath = "src/Game/art/atlas_tiles.png";

    /// <summary>Repo-relative output path of the object atlas.</summary>
    public const string ObjectsPath = "src/Game/art/atlas_objects.png";

    /// <summary>Repo-relative output path of the building atlas.</summary>
    public const string BuildingsPath = "src/Game/art/atlas_buildings.png";

    /// <summary>Repo-relative output path of the table.</summary>
    public const string TablePath = "data/art/atlas.json";

    /// <summary>Repo-relative output path of the table's schema.</summary>
    public const string SchemaPath = "data/art/atlas.schema.json";

    private const int TilesAtlasWidth = 512;
    private const int ObjectColumns = 3;
    private const int BuildingsAtlasWidth = 512;

    /// <summary>Builds all outputs in memory.</summary>
    public static ArtOutput Generate()
    {
        List<AtlasCell> cells = [];

        // Tile cells, ordered by height then kind then variant; shelf-packed left to right.
        List<(string Id, int Kind, int Height, int Variant)> tiles = [];
        for (int h = 0; h <= TileArt.MaxHeight; h++)
        {
            for (int k = 0; k < TileArt.Kinds.Count; k++)
            {
                if (h > 0 && !TileArt.Kinds[k].Raisable)
                {
                    continue;
                }

                for (int v = 0; v < TileArt.Variants; v++)
                {
                    tiles.Add(($"{TileArt.Kinds[k].Id}_h{h}_v{v}", k, h, v));
                }
            }
        }

        List<(int X, int Y)> positions = [];
        int cx = 0;
        int cy = 0;
        int shelfHeight = 0;
        foreach ((string _, int _, int h, int _) in tiles)
        {
            int ch = TileArt.CellHeight(h);
            if (cx + TileArt.TileWidth > TilesAtlasWidth)
            {
                cx = 0;
                cy += shelfHeight;
                shelfHeight = 0;
            }

            positions.Add((cx, cy));
            cx += TileArt.TileWidth;
            shelfHeight = Math.Max(shelfHeight, ch);
        }

        Canvas tileAtlas = new(TilesAtlasWidth, cy + shelfHeight);
        for (int i = 0; i < tiles.Count; i++)
        {
            (string id, int kind, int height, int variant) = tiles[i];
            Canvas cell = TileArt.Draw(kind, height, variant);
            tileAtlas.Blit(cell, 0, 0, cell.Width, cell.Height, positions[i].X, positions[i].Y);
            Pt origin = TileArt.Origin(height);
            cells.Add(new AtlasCell(id, "tiles", "tile", positions[i].X, positions[i].Y, cell.Width, cell.Height, origin.X, origin.Y, height, variant));
        }

        int rows = (ObjectArt.Ids.Count + ObjectColumns - 1) / ObjectColumns;
        Canvas objectAtlas = new(ObjectColumns * ObjectArt.CellSize, rows * ObjectArt.CellSize);
        for (int i = 0; i < ObjectArt.Ids.Count; i++)
        {
            int x = (i % ObjectColumns) * ObjectArt.CellSize;
            int y = (i / ObjectColumns) * ObjectArt.CellSize;
            objectAtlas.Blit(ObjectArt.Draw(ObjectArt.Ids[i]), 0, 0, ObjectArt.CellSize, ObjectArt.CellSize, x, y);
            cells.Add(new AtlasCell(ObjectArt.Ids[i], "objects", "object", x, y, ObjectArt.CellSize, ObjectArt.CellSize, ObjectArt.Origin.X, ObjectArt.Origin.Y, 0, 0));
        }

        Canvas buildingAtlas = PackBuildings(cells);
        return new ArtOutput(tileAtlas, objectAtlas, buildingAtlas, cells, PngWriter.Encode(tileAtlas), PngWriter.Encode(objectAtlas), PngWriter.Encode(buildingAtlas), BuildJson(tileAtlas, objectAtlas, buildingAtlas, cells), Schema);
    }

    // Frames of variable size, shelf-packed left to right in building, level, stage order.
    private static Canvas PackBuildings(List<AtlasCell> cells)
    {
        List<(BuildingSpec Spec, int Level, int Stage, int X, int Y)> placed = [];
        int cx = 0;
        int cy = 0;
        int shelfHeight = 0;
        foreach (BuildingSpec b in BuildingArt.All)
        {
            for (int level = 1; level <= b.Levels; level++)
            {
                for (int stage = 0; stage < BuildingArt.Stages; stage++)
                {
                    int w = BuildingArt.FrameWidth(b);
                    if (cx + w > BuildingsAtlasWidth)
                    {
                        cx = 0;
                        cy += shelfHeight;
                        shelfHeight = 0;
                    }

                    placed.Add((b, level, stage, cx, cy));
                    cx += w;
                    shelfHeight = Math.Max(shelfHeight, BuildingArt.FrameHeight(b));
                }
            }
        }

        Canvas atlas = new(BuildingsAtlasWidth, cy + shelfHeight);
        foreach ((BuildingSpec b, int level, int stage, int x, int y) in placed)
        {
            Canvas frame = BuildingArt.Draw(b, level, stage);
            atlas.Blit(frame, 0, 0, frame.Width, frame.Height, x, y);
            Pt origin = BuildingArt.Origin(b);
            cells.Add(new AtlasCell(BuildingArt.FrameId(b.Id, level, stage), "buildings", "building", x, y, frame.Width, frame.Height, origin.X, origin.Y, 0, stage));
        }

        return atlas;
    }

    private static string BuildJson(Canvas tiles, Canvas objects, Canvas buildings, List<AtlasCell> cells)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        StringBuilder sb = new();
        sb.Append("{\n");
        sb.Append("  \"$schema\": \"./atlas.schema.json\",\n");
        sb.Append(inv, $"  \"tileWidth\": {TileArt.TileWidth},\n");
        sb.Append(inv, $"  \"tileHeight\": {TileArt.TileHeight},\n");
        sb.Append(inv, $"  \"heightStep\": {TileArt.HeightStep},\n");
        sb.Append("  \"atlases\": [\n");
        sb.Append(inv, $"    {{ \"id\": \"tiles\", \"file\": \"art/atlas_tiles.png\", \"width\": {tiles.Width}, \"height\": {tiles.Height} }},\n");
        sb.Append(inv, $"    {{ \"id\": \"objects\", \"file\": \"art/atlas_objects.png\", \"width\": {objects.Width}, \"height\": {objects.Height} }},\n");
        sb.Append(inv, $"    {{ \"id\": \"buildings\", \"file\": \"art/atlas_buildings.png\", \"width\": {buildings.Width}, \"height\": {buildings.Height} }}\n");
        sb.Append("  ],\n");
        sb.Append("  \"cells\": [\n");
        for (int i = 0; i < cells.Count; i++)
        {
            AtlasCell c = cells[i];
            sb.Append(inv, $"    {{ \"id\": \"{c.Id}\", \"atlas\": \"{c.Atlas}\", \"kind\": \"{c.Kind}\", \"x\": {c.X}, \"y\": {c.Y}, \"w\": {c.W}, \"h\": {c.H}, \"originX\": {c.OriginX}, \"originY\": {c.OriginY}, \"height\": {c.Height}, \"variant\": {c.Variant} }}");
            sb.Append(i + 1 < cells.Count ? ",\n" : "\n");
        }

        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    /// <summary>JSON schema of data/art/atlas.json (hand written; the file is not a content definition list).</summary>
    public const string Schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "AtlasTable",
          "type": "object",
          "additionalProperties": false,
          "required": ["tileWidth", "tileHeight", "heightStep", "atlases", "cells"],
          "properties": {
            "$schema": { "type": "string" },
            "tileWidth": { "type": "integer", "minimum": 1 },
            "tileHeight": { "type": "integer", "minimum": 1 },
            "heightStep": { "type": "integer", "minimum": 0 },
            "atlases": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "file", "width", "height"],
                "properties": {
                  "id": { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" },
                  "file": { "type": "string", "minLength": 1 },
                  "width": { "type": "integer", "minimum": 1 },
                  "height": { "type": "integer", "minimum": 1 }
                }
              }
            },
            "cells": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "atlas", "kind", "x", "y", "w", "h", "originX", "originY", "height", "variant"],
                "properties": {
                  "id": { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" },
                  "atlas": { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" },
                  "kind": { "enum": ["tile", "object", "building"] },
                  "x": { "type": "integer", "minimum": 0 },
                  "y": { "type": "integer", "minimum": 0 },
                  "w": { "type": "integer", "minimum": 1 },
                  "h": { "type": "integer", "minimum": 1 },
                  "originX": { "type": "integer", "minimum": 0, "description": "Cell pixel on the ground diamond centre; for kind building: x of the bottom corner of the footprint diamond (frame width / 2)." },
                  "originY": { "type": "integer", "minimum": 0, "description": "Cell pixel on the ground diamond centre; for kind building: y of the bottom corner of the footprint diamond (frame height - 1)." },
                  "height": { "type": "integer", "minimum": 0, "maximum": 3 },
                  "variant": { "type": "integer", "minimum": 0, "description": "Colour variant of a tile; for kind building: construction stage 0..3 (marking, planks, scaffolding, finished)." }
                }
              }
            }
          }
        }
        """ + "\n";

    /// <summary>Finds the repository root by walking up to Game.sln.</summary>
    public static string FindRepoRoot(string start)
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Game.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Game.sln not found above " + start);
    }
}
