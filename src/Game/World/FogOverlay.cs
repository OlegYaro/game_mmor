using Core.Fog;
using Core.Geometry;
using Core.Sim;
using Core.WorldGen;
using Godot;

namespace Game.World;

/// <summary>
/// Fog of war and day/night tint drawn as one multiplicative full-map pass (ADR 0010): a shader reads a mask texture
/// with one texel per tile (R = fog state, G = visual height). The mask is re-uploaded only when the fog version of
/// some chunk changed since the last upload.
/// </summary>
public partial class FogOverlay : Node2D
{
    /// <summary>Mask value of an unknown tile.</summary>
    public const byte UnknownValue = 0;

    /// <summary>Mask value of an explored (not visible) tile.</summary>
    public const byte ExploredValue = 128;

    /// <summary>Mask value of a visible tile.</summary>
    public const byte VisibleValue = 255;

    private const string ShaderPath = "res://World/fog.gdshader";

    // Covers the screen around the map up to the widest zoom (beyond the map everything is unknown, i.e. black).
    private const float OuterMargin = 8192f;

    private readonly byte[] _chunkStates = new byte[ChunkCoord.Size * ChunkCoord.Size];
    private ShaderMaterial? _material;
    private Image? _image;
    private ImageTexture? _texture;
    private byte[] _mask = [];
    private long[] _drawnVersions = [];
    private GameWorld? _world;
    private Rect2 _rect;

    /// <summary>Number of mask uploads since binding (statistics, tests).</summary>
    public int UploadCount { get; private set; }

    /// <summary>The mask texture (R = fog, G = visual height), null while unbound.</summary>
    public ImageTexture? MaskTexture => _texture;

    /// <summary>Starts drawing the fog of <paramref name="world"/>.</summary>
    public void Bind(GameWorld world, WorldArt art)
    {
        _world = world;
        int size = world.Size;
        _mask = new byte[size * size * 2];
        TerrainMap terrain = world.Map.Terrain;
        ReadOnlySpan<byte> heights = terrain.Heights;
        ReadOnlySpan<Biome> biomes = terrain.Biomes;
        ReadOnlySpan<TileFeatures> features = terrain.Features;
        for (int i = 0; i < size * size; i++)
        {
            _mask[(i * 2) + 1] = (byte)art.VisualHeight(biomes[i], features[i], heights[i]);
        }

        _drawnVersions = new long[world.Fog.ChunksPerSide * world.Fog.ChunksPerSide];
        Array.Fill(_drawnVersions, -1);
        _image = null;
        _texture = null;
        UploadCount = 0;

        _material ??= new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        Material = _material;
        _material.SetShaderParameter("map_size", size);
        _material.SetShaderParameter("geometry", new Vector3(IsoProjection.TileWidth, IsoProjection.TileHeight, IsoProjection.HeightStep));
        (int minX, int minY, int maxX, int maxY) = IsoProjection.MapBounds(size);
        _rect = new Rect2(minX - OuterMargin, minY - OuterMargin, maxX - minX + (2 * OuterMargin), maxY - minY + (2 * OuterMargin));
        Refresh();
        Visible = true;
        QueueRedraw();
    }

    /// <summary>Stops drawing.</summary>
    public void Unbind()
    {
        _world = null;
        Visible = false;
    }

    /// <summary>Sets the day/night colour multiplied over the world.</summary>
    public void SetTint(Color tint) => _material?.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));

    /// <summary>Copies the states of chunks whose fog version changed and uploads the mask if anything changed.</summary>
    /// <returns>True when the mask was uploaded.</returns>
    public bool Refresh()
    {
        if (_world is null)
        {
            return false;
        }

        FogOfWar fog = _world.Fog;
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
                int target = (((y0 + ly) * size) + x0) * 2;
                int source = ly * ChunkCoord.Size;
                for (int lx = 0; lx < ChunkCoord.Size; lx++)
                {
                    _mask[target + (lx * 2)] = MaskValue((FogState)_chunkStates[source + lx]);
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        if (_image is null || _texture is null)
        {
            _image = Image.CreateFromData(size, size, false, Image.Format.Rg8, _mask);
            _texture = ImageTexture.CreateFromImage(_image);
            _material!.SetShaderParameter("fog_mask", _texture);
        }
        else
        {
            _image.SetData(size, size, false, Image.Format.Rg8, _mask);
            _texture.Update(_image);
        }

        UploadCount++;
        return true;
    }

    /// <summary>Mask byte of a fog state.</summary>
    public static byte MaskValue(FogState state) => state switch
    {
        FogState.Visible => VisibleValue,
        FogState.Explored => ExploredValue,
        _ => UnknownValue,
    };

    public override void _Draw()
    {
        if (_world is not null)
        {
            DrawRect(_rect, Colors.White);
        }
    }
}
