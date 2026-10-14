using Core.DevConsole;
using Core.Fog;
using Core.Geometry;
using Core.Sim;
using Core.WorldGen;
using Godot;

namespace Game.World;

/// <summary>
/// Minimap (FR-WORLD-12, FR-UI-01): the map as a diamond like the world view, one texel per tile, coloured by biome
/// and height, black where unknown and dimmed where only explored. Draws the start, points of interest and settlement
/// sites on explored tiles and the camera rectangle; a left click or drag moves the camera. Only chunks whose fog
/// changed are recoloured. Scene: <c>res://World/MinimapView.tscn</c> (anchored bottom-right).
/// </summary>
public partial class MinimapView : Control
{
    /// <summary>Path of the scene.</summary>
    public const string ScenePath = "res://World/MinimapView.tscn";

    private const float Padding = 6f;
    private const float ExploredFactor = 0.55f;

    private static readonly Color _meadow = Color.FromHtml("#7FB069");
    private static readonly Color _forest = Color.FromHtml("#3E7B4F");
    private static readonly Color _mountain = Color.FromHtml("#8A8D91");
    private static readonly Color _wasteland = Color.FromHtml("#C2A878");
    private static readonly Color _water = Color.FromHtml("#3A6EA5");
    private static readonly Color _river = Color.FromHtml("#5B8FD0");
    private static readonly Color _background = new(0.05f, 0.05f, 0.07f, 0.85f);
    private static readonly Color _frame = new(0.75f, 0.72f, 0.62f, 0.9f);
    private static readonly Color _startColor = new(1f, 0.85f, 0.2f);
    private static readonly Color _cameraColor = new(1f, 1f, 1f, 0.9f);

    private readonly byte[] _chunkStates = new byte[ChunkCoord.Size * ChunkCoord.Size];
    private SimulationHolder? _holder;
    private WorldCamera? _camera;
    private Image? _image;
    private ImageTexture? _texture;
    private byte[] _baseColors = [];
    private byte[] _pixels = [];
    private long[] _drawnVersions = [];
    private Rect2 _lastCameraRect;

    /// <summary>The bound simulation, or null.</summary>
    public Simulation? Simulation { get; private set; }

    /// <summary>Number of texture uploads since binding (statistics, tests).</summary>
    public int UploadCount { get; private set; }

    /// <summary>
    /// Shows the map of <paramref name="simulation"/>. <paramref name="camera"/> is the camera to draw and move; when
    /// null the viewport's current camera is used if it is a <see cref="WorldCamera"/>.
    /// </summary>
    public void Bind(Simulation simulation, WorldCamera? camera = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        _holder = null;
        _camera = camera;
        BindSimulation(simulation);
    }

    /// <summary>Like <see cref="Bind(Simulation, WorldCamera)"/>, following the holder when its simulation is replaced.</summary>
    public void Bind(SimulationHolder holder, WorldCamera? camera = null)
    {
        ArgumentNullException.ThrowIfNull(holder);
        _holder = holder;
        _camera = camera;
        BindSimulation(holder.Current);
    }

    /// <summary>Stops showing the map.</summary>
    public void Unbind()
    {
        _holder = null;
        Simulation = null;
        _texture = null;
        _image = null;
        QueueRedraw();
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        ClipContents = true;
    }

    public override void _Process(double delta)
    {
        if (_holder is not null && !ReferenceEquals(_holder.Current, Simulation))
        {
            BindSimulation(_holder.Current);
        }

        if (Simulation?.World is null)
        {
            return;
        }

        bool changed = Refresh();
        WorldCamera? camera = ResolveCamera();
        Rect2 rect = camera?.VisibleWorldRect ?? default;
        if (changed || rect != _lastCameraRect)
        {
            _lastCameraRect = rect;
            QueueRedraw();
        }
    }

    /// <summary>Recolours chunks whose fog changed and uploads the texture if anything changed.</summary>
    /// <returns>True when the texture was uploaded.</returns>
    public bool Refresh()
    {
        GameWorld? world = Simulation?.World;
        if (world is null)
        {
            return false;
        }

        FogOfWar fog = world.Fog;
        ReadOnlySpan<long> versions = fog.ChunkVersions;
        int perSide = fog.ChunksPerSide;
        int size = fog.Size;
        bool changed = false;
        for (int c = 0; c < versions.Length; c++)
        {
            if (versions[c] == _drawnVersions[c])
            {
                continue;
            }

            _drawnVersions[c] = versions[c];
            changed = true;
            var coord = new ChunkCoord(c % perSide, c / perSide);
            fog.CopyStates(coord, _chunkStates);
            int x0 = coord.X * ChunkCoord.Size;
            int y0 = coord.Y * ChunkCoord.Size;
            for (int ly = 0; ly < ChunkCoord.Size; ly++)
            {
                for (int lx = 0; lx < ChunkCoord.Size; lx++)
                {
                    int tile = ((y0 + ly) * size) + x0 + lx;
                    WritePixel(tile, (FogState)_chunkStates[(ly * ChunkCoord.Size) + lx]);
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        if (_image is null || _texture is null)
        {
            _image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, _pixels);
            _image.GenerateMipmaps();
            _texture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _image.SetData(size, size, false, Image.Format.Rgba8, _pixels);
            _image.GenerateMipmaps();
            _texture.Update(_image);
        }

        UploadCount++;
        return true;
    }

    /// <summary>Colour of a tile in the texture for a fog state (unknown is black).</summary>
    public Color GetTileColor(int x, int y)
    {
        if (_image is null || Simulation?.World is null)
        {
            return Colors.Black;
        }

        int i = ((y * Simulation.World.Size) + x) * 4;
        return Color.Color8(_pixels[i], _pixels[i + 1], _pixels[i + 2], _pixels[i + 3]);
    }

    /// <summary>Minimap-local point of a world point.</summary>
    public Vector2 WorldToMinimap(Vector2 world)
    {
        (float scale, Vector2 origin, Vector2 worldMin) = Layout();
        return ((world - worldMin) * scale) + origin;
    }

    /// <summary>World point of a minimap-local point.</summary>
    public Vector2 MinimapToWorld(Vector2 local)
    {
        (float scale, Vector2 origin, Vector2 worldMin) = Layout();
        return ((local - origin) / scale) + worldMin;
    }

    public override void _GuiInput(InputEvent @event)
    {
        bool move = @event switch
        {
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } => true,
            InputEventMouseMotion motion => (motion.ButtonMask & MouseButtonMask.Left) != 0,
            _ => false,
        };
        if (!move || Simulation?.World is null || ResolveCamera() is not WorldCamera camera)
        {
            return;
        }

        Vector2 local = @event is InputEventMouse mouse ? mouse.Position : Vector2.Zero;
        camera.CenterOnWorld(MinimapToWorld(local));
        AcceptEvent();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), _background);
        GameWorld? world = Simulation?.World;
        if (world is null || _texture is null)
        {
            return;
        }

        int size = world.Size;
        (float scale, Vector2 origin, _) = Layout();
        float a = IsoProjection.HalfWidth * scale;
        float b = IsoProjection.HalfHeight * scale;

        // Texel (i, j) covers tile-space [i, i+1) x [j, j+1): x' = a(i - j) + a*size, y' = b(i + j).
        DrawSetTransformMatrix(new Transform2D(new Vector2(a, b), new Vector2(-a, b), origin + new Vector2(a * size, 0)));
        DrawTexture(_texture, Vector2.Zero);
        DrawSetTransformMatrix(Transform2D.Identity);

        Vector2 top = origin + new Vector2(a * size, 0);
        Vector2 right = origin + new Vector2(2 * a * size, b * size);
        Vector2 bottom = origin + new Vector2(a * size, 2 * b * size);
        Vector2 left = origin + new Vector2(0, b * size);
        DrawPolyline([top, right, bottom, left, top], _frame, 1f);

        FogOfWar fog = world.Fog;
        WorldMap map = world.Map;
        foreach (SettlementSite site in map.SettlementSites)
        {
            if (fog.IsExplored(site.X, site.Y))
            {
                DrawMarker(TilePoint(site.X, site.Y), 2.5f, WorldNames.IsEvil(site.Faction) ? new Color(0.75f, 0.15f, 0.15f) : new Color(0.3f, 0.6f, 1f));
            }
        }

        foreach (PointOfInterest poi in map.PointsOfInterest)
        {
            if (fog.IsExplored(poi.X, poi.Y))
            {
                DrawMarker(TilePoint(poi.X, poi.Y), 2f, poi.Kind switch
                {
                    PoiKind.Lair => new Color(0.9f, 0.25f, 0.2f),
                    PoiKind.Ruins => new Color(0.85f, 0.85f, 0.85f),
                    _ => new Color(0.95f, 0.78f, 0.25f),
                });
            }
        }

        DrawMarker(TilePoint(map.Start.X, map.Start.Y), 3.5f, _startColor);

        if (ResolveCamera() is WorldCamera camera)
        {
            Rect2 view = camera.VisibleWorldRect;
            Vector2 p0 = WorldToMinimap(view.Position);
            Vector2 p1 = WorldToMinimap(view.End);
            DrawRect(new Rect2(p0, p1 - p0), _cameraColor, filled: false, width: 1f);
        }
    }

    private static Color BaseColor(Biome biome, TileFeatures features, int height)
    {
        Color c = WorldArt.GroundKindOf(biome, features) switch
        {
            1 => _forest,
            2 => _mountain,
            3 => _wasteland,
            4 => _water,
            5 => _river,
            _ => _meadow,
        };

        // Higher ground is a little lighter, so relief reads on the minimap.
        float shade = (features & (TileFeatures.Water | TileFeatures.River)) != 0 ? 1f : 0.86f + (0.07f * height);
        return new Color(Math.Min(c.R * shade, 1f), Math.Min(c.G * shade, 1f), Math.Min(c.B * shade, 1f));
    }

    private void BindSimulation(Simulation simulation)
    {
        Simulation = simulation;
        _image = null;
        _texture = null;
        UploadCount = 0;
        GameWorld? world = simulation.World;
        if (world is null)
        {
            QueueRedraw();
            return;
        }

        int size = world.Size;
        TerrainMap terrain = world.Map.Terrain;
        ReadOnlySpan<byte> heights = terrain.Heights;
        ReadOnlySpan<Biome> biomes = terrain.Biomes;
        ReadOnlySpan<TileFeatures> features = terrain.Features;
        _baseColors = new byte[size * size * 3];
        _pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            Color c = BaseColor(biomes[i], features[i], heights[i]);
            _baseColors[i * 3] = (byte)c.R8;
            _baseColors[(i * 3) + 1] = (byte)c.G8;
            _baseColors[(i * 3) + 2] = (byte)c.B8;
        }

        _drawnVersions = new long[world.Fog.ChunksPerSide * world.Fog.ChunksPerSide];
        Array.Fill(_drawnVersions, -1);
        Refresh();
        QueueRedraw();
    }

    private void WritePixel(int tile, FogState state)
    {
        int p = tile * 4;
        int c = tile * 3;
        float factor = state switch
        {
            FogState.Visible => 1f,
            FogState.Explored => ExploredFactor,
            _ => 0f,
        };
        _pixels[p] = (byte)(_baseColors[c] * factor);
        _pixels[p + 1] = (byte)(_baseColors[c + 1] * factor);
        _pixels[p + 2] = (byte)(_baseColors[c + 2] * factor);
        _pixels[p + 3] = 255;
    }

    private void DrawMarker(Vector2 at, float radius, Color color)
    {
        DrawCircle(at, radius + 1f, Colors.Black);
        DrawCircle(at, radius, color);
    }

    private Vector2 TilePoint(int x, int y)
    {
        (int px, int py) = IsoProjection.TileCenter(x, y);
        return WorldToMinimap(new Vector2(px, py));
    }

    // Scale (minimap px per world px), top-left of the diamond's box in the control, world top-left of the map box.
    private (float Scale, Vector2 Origin, Vector2 WorldMin) Layout()
    {
        int size = Simulation?.World?.Size ?? 1;
        float worldWidth = IsoProjection.TileWidth * size;
        float worldHeight = IsoProjection.TileHeight * size;
        Vector2 avail = Size - new Vector2(2 * Padding, 2 * Padding);
        float scale = Math.Max(Math.Min(avail.X / worldWidth, avail.Y / worldHeight), 1e-6f);
        Vector2 box = new Vector2(worldWidth, worldHeight) * scale;
        Vector2 origin = ((Size - box) / 2).Round();
        (int minX, _, _, _) = IsoProjection.MapBounds(size);
        return (scale, origin, new Vector2(minX, 0));
    }

    private WorldCamera? ResolveCamera() => _camera ?? GetViewport()?.GetCamera2D() as WorldCamera;
}
