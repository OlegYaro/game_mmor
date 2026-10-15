using Core.Geometry;
using Core.WorldGen;
using Godot;

namespace Game.World;

/// <summary>Object kinds drawn on the objects layer, in priority order (lower value wins a shared tile).</summary>
public enum WorldObject : byte
{
    /// <summary>Hero start marker.</summary>
    StartMarker = 0,

    /// <summary>Treasure chest point of interest.</summary>
    PoiChest = 1,

    /// <summary>Ruins point of interest.</summary>
    PoiRuins = 2,

    /// <summary>Monster lair point of interest.</summary>
    PoiLair = 3,

    /// <summary>Reserved settlement site (centre tile).</summary>
    SettlementSite = 4,

    /// <summary>Tree resource node.</summary>
    Tree = 5,

    /// <summary>Stone resource node.</summary>
    Rock = 6,

    /// <summary>Ore resource node.</summary>
    Ore = 7,

    /// <summary>Berry bush resource node.</summary>
    BerryBush = 8,

    /// <summary>Coal resource node.</summary>
    Coal = 9,

    /// <summary>Ore vein tile.</summary>
    OreVein = 10,

    /// <summary>Coal vein tile.</summary>
    CoalVein = 11,
}

/// <summary>
/// The TileSet built in code from the atlas table (ADR 0010) and the lookups from map data to tiles. One TileSet serves
/// every chunk layer: source <see cref="GroundSource"/> holds the ground cells (one tile per biome × height × variant;
/// the height art already contains the raised block, placed with <c>TextureOrigin</c>), source <see cref="ObjectSource"/>
/// holds the objects with one alternative per height step (<c>TextureOrigin</c> lifted by the height step per step), and source
/// <see cref="RiverSource"/> holds raised river blocks composed at load time (see <see cref="RiversRaised"/>).
/// </summary>
public sealed class WorldArt
{
    /// <summary>Atlas source id of ground tiles.</summary>
    public const int GroundSource = 0;

    /// <summary>Atlas source id of objects.</summary>
    public const int ObjectSource = 1;

    /// <summary>Atlas source id of the composed raised river blocks.</summary>
    public const int RiverSource = 2;

    /// <summary>Number of art variants per ground kind and height.</summary>
    public const int Variants = 3;

    private const int LandKinds = 4;
    private const int WaterKind = 4;
    private const int Heights = TerrainMap.MaxHeight + 1;

    private static readonly string[] _landNames = ["meadow", "forest", "mountain", "wasteland"];
    private static readonly string[] _objectIds =
        [WorldNames.StartMarkerArtId, "poi_chest", "poi_ruins", "poi_lair", WorldNames.SettlementSiteArtId, "tree", "rock", "ore", "berry_bush", "coal", "ore_vein", "coal_vein"];

    private static WorldArt? _shared;

    // (source, atlas coords) per land kind × height × variant; index LandKinds is water.
    private readonly (int Source, Vector2I Coords)[] _ground = new (int, Vector2I)[(LandKinds + 1) * Heights * Variants];

    // River tiles per land kind × height × variant (the bank biome shows on the sides of raised river blocks).
    private readonly (int Source, Vector2I Coords)[] _river = new (int, Vector2I)[LandKinds * Heights * Variants];
    private readonly Vector2I[] _objects = new Vector2I[_objectIds.Length];

    private WorldArt(TileSet tileSet) => TileSet = tileSet;

    /// <summary>Pixels the tallest object art reaches above its tile's ground centre (from the table's object cells).</summary>
    public int ObjectRise { get; private set; }

    /// <summary>The shared TileSet (isometric, DiamondDown, <see cref="IsoProjection.TileWidth"/>×<see cref="IsoProjection.TileHeight"/>).</summary>
    public TileSet TileSet { get; }

    /// <summary>
    /// True when rivers on raised land are drawn as raised blocks with the river on top: from <c>river_h1..3</c>
    /// cells of the table when present, otherwise composed from the bank block and the flat river art. False only when
    /// the atlas image cannot be read (headless runs); rivers then lie flat at height 0.
    /// </summary>
    public bool RiversRaised { get; private set; }

    /// <summary>
    /// Loads the atlas table and builds the TileSet once per process; later calls return the same instance.
    /// </summary>
    public static bool TryGetShared(out WorldArt art, out string error)
    {
        if (_shared is not null)
        {
            art = _shared;
            error = string.Empty;
            return true;
        }

        if (!AtlasTable.TryLoad(out AtlasTable table, out error) || !TryBuild(table, out art, out error))
        {
            art = null!;
            return false;
        }

        _shared = art;
        return true;
    }

    /// <summary>Builds the TileSet and lookups from <paramref name="table"/>.</summary>
    public static bool TryBuild(AtlasTable table, out WorldArt art, out string error)
    {
        ArgumentNullException.ThrowIfNull(table);
        art = null!;
        if (table.TileWidth != IsoProjection.TileWidth || table.TileHeight != IsoProjection.TileHeight || table.HeightStep != IsoProjection.HeightStep)
        {
            error = $"Art table geometry {table.TileWidth}x{table.TileHeight}/{table.HeightStep} does not match the projection {IsoProjection.TileWidth}x{IsoProjection.TileHeight}/{IsoProjection.HeightStep}.";
            return false;
        }

        var tileSet = new TileSet
        {
            TileShape = TileSet.TileShapeEnum.Isometric,
            TileLayout = TileSet.TileLayoutEnum.DiamondDown,
            TileOffsetAxis = TileSet.TileOffsetAxisEnum.Horizontal,
            TileSize = new Vector2I(IsoProjection.TileWidth, IsoProjection.TileHeight),
        };
        var built = new WorldArt(tileSet);

        TileSetAtlasSource? ground = CreateSource(table, "tiles", out Vector2I groundUnit, out error);
        if (ground is null)
        {
            return false;
        }

        TileSetAtlasSource? objects = CreateSource(table, "objects", out Vector2I objectUnit, out error);
        if (objects is null)
        {
            return false;
        }

        tileSet.AddSource(ground, GroundSource);
        tileSet.AddSource(objects, ObjectSource);

        // Land: every biome × height × variant. Water exists only at height 0 (its surface is at the lowest land level).
        for (int kind = 0; kind <= LandKinds; kind++)
        {
            for (int h = 0; h < Heights; h++)
            {
                for (int v = 0; v < Variants; v++)
                {
                    string id = kind == WaterKind ? $"water_h0_v{v}" : $"{_landNames[kind]}_h{h}_v{v}";
                    if (!table.TryGet(id, out AtlasCell cell))
                    {
                        error = $"Art table has no ground cell '{id}'.";
                        return false;
                    }

                    built._ground[Index(kind, h, v)] = (GroundSource, AddTile(ground, cell, groundUnit, heights: 1));
                }
            }
        }

        if (!built.BuildRivers(table, ground, groundUnit, out error))
        {
            return false;
        }

        for (int i = 0; i < _objectIds.Length; i++)
        {
            if (!table.TryGet(_objectIds[i], out AtlasCell cell))
            {
                error = $"Art table has no object cell '{_objectIds[i]}'.";
                return false;
            }

            built.ObjectRise = Math.Max(built.ObjectRise, cell.Origin.Y);
            built._objects[i] = AddTile(objects, cell, objectUnit, heights: Heights);
        }

        art = built;
        error = string.Empty;
        return true;
    }

    /// <summary>Deterministic art variant 0..2 of tile (<paramref name="x"/>, <paramref name="y"/>): a coordinate hash, no RNG state.</summary>
    public static int VariantOf(int x, int y)
    {
        uint h = unchecked(((uint)x * 0x9E3779B1u) ^ ((uint)y * 0x85EBCA77u));
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        return (int)(h % Variants);
    }

    /// <summary>Minimap/biome colour class of a tile: land biomes 0–3, water 4, river 5.</summary>
    public static int GroundKindOf(Biome biome, TileFeatures features)
    {
        if ((features & TileFeatures.Water) != 0 || biome == Biome.Water)
        {
            return WaterKind;
        }

        return (features & TileFeatures.River) != 0 ? WaterKind + 1 : LandKind(biome);
    }

    /// <summary>
    /// Height step the tile is drawn at: water lies at 0, land and rivers at their height (rivers at 0 when
    /// <see cref="RiversRaised"/> is false). The fog mask and screen picking use the same value.
    /// </summary>
    public int VisualHeight(Biome biome, TileFeatures features, int height)
    {
        if ((features & TileFeatures.Water) != 0 || biome == Biome.Water)
        {
            return 0;
        }

        return (features & TileFeatures.River) != 0 && !RiversRaised ? 0 : height;
    }

    /// <summary>Ground cell of a map tile: atlas source and coordinates (alternative 0).</summary>
    public (int Source, Vector2I Coords) GroundCell(Biome biome, TileFeatures features, int height, int variant)
    {
        if ((features & TileFeatures.Water) != 0 || biome == Biome.Water)
        {
            return _ground[Index(WaterKind, 0, variant)];
        }

        return (features & TileFeatures.River) != 0
            ? _river[Index(LandKind(biome), height, variant)]
            : _ground[Index(LandKind(biome), height, variant)];
    }

    /// <summary>Atlas coordinates of an object (source <see cref="ObjectSource"/>; the alternative is the height step).</summary>
    public Vector2I ObjectTile(WorldObject obj) => _objects[(int)obj];

    /// <summary>Object drawn for a resource node, or null for none.</summary>
    public static WorldObject? ObjectOf(ResourceKind kind) => kind switch
    {
        ResourceKind.Tree => WorldObject.Tree,
        ResourceKind.Stone => WorldObject.Rock,
        ResourceKind.Ore => WorldObject.Ore,
        ResourceKind.BerryBush => WorldObject.BerryBush,
        ResourceKind.Coal => WorldObject.Coal,
        ResourceKind.OreVein => WorldObject.OreVein,
        ResourceKind.CoalVein => WorldObject.CoalVein,
        _ => null,
    };

    /// <summary>Object drawn for a point of interest.</summary>
    public static WorldObject ObjectOf(PoiKind kind) => kind switch
    {
        PoiKind.Ruins => WorldObject.PoiRuins,
        PoiKind.Lair => WorldObject.PoiLair,
        _ => WorldObject.PoiChest,
    };

    private static int Index(int kind, int height, int variant) => (((kind * Heights) + height) * Variants) + variant;

    private static int LandKind(Biome biome) => biome switch
    {
        Biome.Forest => 1,
        Biome.Mountain => 2,
        Biome.Wasteland => 3,
        _ => 0,
    };

    // River cells: flat river art at height 0; at heights 1-3 either river_h{h} cells from the table or blocks composed
    // from the bank biome's block with the flat river diamond pasted on its top face (source RiverSource).
    private bool BuildRivers(AtlasTable table, TileSetAtlasSource ground, Vector2I unit, out string error)
    {
        for (int v = 0; v < Variants; v++)
        {
            if (!table.TryGet($"river_h0_v{v}", out AtlasCell flat))
            {
                error = $"Art table has no ground cell 'river_h0_v{v}'.";
                return false;
            }

            Vector2I coords = AddTile(ground, flat, unit, heights: 1);
            for (int kind = 0; kind < LandKinds; kind++)
            {
                for (int h = 0; h < Heights; h++)
                {
                    _river[Index(kind, h, v)] = (GroundSource, coords);
                }
            }
        }

        error = string.Empty;
        bool fromTable = true;
        for (int h = 1; h < Heights && fromTable; h++)
        {
            for (int v = 0; v < Variants; v++)
            {
                fromTable &= table.TryGet($"river_h{h}_v{v}", out _);
            }
        }

        if (fromTable)
        {
            for (int h = 1; h < Heights; h++)
            {
                for (int v = 0; v < Variants; v++)
                {
                    table.TryGet($"river_h{h}_v{v}", out AtlasCell cell);
                    Vector2I coords = AddTile(ground, cell, unit, heights: 1);
                    for (int kind = 0; kind < LandKinds; kind++)
                    {
                        _river[Index(kind, h, v)] = (GroundSource, coords);
                    }
                }
            }

            RiversRaised = true;
            return true;
        }

        Image? atlas = ReadImage(ground.Texture);
        if (atlas is null)
        {
            return true; // flat rivers (headless or unreadable atlas)
        }

        // Composite layout: one column per (bank biome, variant), rows for heights 1..3 stacked; cell sizes come from the table's block cells.
        int columns = LandKinds * Variants;
        int cellWidth = table.TileWidth;
        int step = table.HeightStep;
        int[] rowTop = new int[Heights];
        int imageHeight = 0;
        for (int h = 1; h < Heights; h++)
        {
            rowTop[h] = imageHeight;
            imageHeight += table.TileHeight + (step * h);
        }

        var composite = Image.CreateEmpty(columns * cellWidth, imageHeight, false, Image.Format.Rgba8);
        var source = new TileSetAtlasSource
        {
            Texture = null,
            TextureRegionSize = new Vector2I(cellWidth, step),
            UseTexturePadding = false,
        };
        var pending = new List<(Vector2I Coords, Vector2I Size, Vector2I Origin, int Kind, int Height, int Variant)>();
        for (int kind = 0; kind < LandKinds; kind++)
        {
            for (int v = 0; v < Variants; v++)
            {
                table.TryGet($"river_h0_v{v}", out AtlasCell flat);
                int column = (kind * Variants) + v;
                for (int h = 1; h < Heights; h++)
                {
                    if (!table.TryGet($"{_landNames[kind]}_h{h}_v{v}", out AtlasCell block))
                    {
                        continue;
                    }

                    var at = new Vector2I(column * cellWidth, rowTop[h]);
                    composite.BlitRect(atlas, block.Region, at);

                    // The top face of a block cell spans its top TileHeight rows (origin minus h steps minus half a tile).
                    int topFace = block.Origin.Y - (step * h) - (table.TileHeight / 2);
                    composite.BlendRect(atlas, flat.Region, at + new Vector2I(0, topFace));
                    var size = new Vector2I(1, block.Region.Size.Y / step);
                    var origin = new Vector2I(block.Origin.X - (block.Region.Size.X / 2), block.Origin.Y - (block.Region.Size.Y / 2));
                    pending.Add((new Vector2I(column, rowTop[h] / step), size, origin, kind, h, v));
                }
            }
        }

        source.Texture = ImageTexture.CreateFromImage(composite);
        TileSet.AddSource(source, RiverSource);
        foreach ((Vector2I coords, Vector2I size, Vector2I origin, int kind, int h, int v) in pending)
        {
            source.CreateTile(coords, size);
            source.GetTileData(coords, 0).TextureOrigin = origin;
            _river[Index(kind, h, v)] = (RiverSource, coords);
        }

        RiversRaised = true;
        return true;
    }

    // Pixels of an atlas texture as RGBA8, or null when the renderer cannot give them back (headless dummy renderer).
    private static Image? ReadImage(Texture2D texture)
    {
        if (DisplayServer.GetName() == "headless")
        {
            return null;
        }

        Image? image = texture.GetImage();
        if (image is null || image.IsEmpty())
        {
            return null;
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        return image;
    }

    // One source per atlas image. The region unit is the GCD of all cell positions and sizes, so differently sized
    // cells (flat diamonds, tall blocks, 32x32 objects) are tiles spanning several units.
    private static TileSetAtlasSource? CreateSource(AtlasTable table, string atlasId, out Vector2I unit, out string error)
    {
        unit = default;
        AtlasImage? image = table.Images.FirstOrDefault(i => i.Id == atlasId);
        if (image is null)
        {
            error = $"Art table has no atlas '{atlasId}'.";
            return null;
        }

        Texture2D? texture = ResourceLoader.Exists(image.ResourcePath) ? GD.Load<Texture2D>(image.ResourcePath) : null;
        if (texture is null)
        {
            error = $"Atlas image '{image.ResourcePath}' cannot be loaded.";
            return null;
        }

        int gx = 0;
        int gy = 0;
        foreach (AtlasCell cell in table.Cells.Where(c => c.Atlas == atlasId))
        {
            gx = Gcd(Gcd(gx, cell.Region.Position.X), cell.Region.Size.X);
            gy = Gcd(Gcd(gy, cell.Region.Position.Y), cell.Region.Size.Y);
        }

        if (gx == 0 || gy == 0)
        {
            error = $"Atlas '{atlasId}' has no cells.";
            return null;
        }

        unit = new Vector2I(gx, gy);
        error = string.Empty;
        return new TileSetAtlasSource { Texture = texture, TextureRegionSize = unit, UseTexturePadding = false };
    }

    // Creates the tile of `cell` and `heights` alternatives (alternative h = lifted by h steps); returns its atlas coords.
    private static Vector2I AddTile(TileSetAtlasSource source, AtlasCell cell, Vector2I unit, int heights)
    {
        var coords = new Vector2I(cell.Region.Position.X / unit.X, cell.Region.Position.Y / unit.Y);
        if (source.HasTile(coords))
        {
            return coords;
        }

        source.CreateTile(coords, new Vector2I(cell.Region.Size.X / unit.X, cell.Region.Size.Y / unit.Y));

        // The region is drawn centred on the cell centre, minus TextureOrigin: shift so that the art's origin pixel
        // lands on the ground centre of the cell.
        var baseOrigin = new Vector2I(cell.Origin.X - (cell.Region.Size.X / 2), cell.Origin.Y - (cell.Region.Size.Y / 2));
        source.GetTileData(coords, 0).TextureOrigin = baseOrigin;
        for (int h = 1; h < heights; h++)
        {
            int alternative = source.CreateAlternativeTile(coords, h);
            source.GetTileData(coords, alternative).TextureOrigin = baseOrigin + new Vector2I(0, IsoProjection.HeightStep * h);
        }

        return coords;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Abs(a);
    }
}
