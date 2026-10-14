using System.Buffers.Binary;
using Core.Determinism;

namespace Core.WorldGen;

/// <summary>
/// Output of the terrain stage (ADR 0006 steps 1–4) for a square map: one array per layer over the whole map,
/// indexed <c>y * Size + x</c>. Immutable once generated; later stages (resources, points of interest, start,
/// settlements) read it and add their own layers in a separate map type.
/// </summary>
/// <remarks>
/// <para>Height semantics: <see cref="GetHeight"/> is the gameplay step 0–3 (B2). Water tiles have step 0: the water
/// surface is at the level of the lowest land. <see cref="GetElevation"/> is the continuous height before
/// quantization (0..65535), kept for later stages; water tiles are exactly those whose elevation was below the
/// water level (minus filtered small lakes and islands, see <see cref="Content.HeightParams"/>).</para>
/// <para>Moisture and temperature are permille ranks among land tiles (0..999); they are 0 on water.</para>
/// </remarks>
public sealed class TerrainMap : IStateHashable
{
    /// <summary>Value of <see cref="GetRiverDirection"/> on tiles without a river.</summary>
    public const byte NoDirection = 0xFF;

    /// <summary>Highest height step.</summary>
    public const int MaxHeight = 3;

    /// <summary>
    /// The eight flow directions, indexed by the values of <see cref="GetRiverDirection"/>:
    /// 0 E, 1 S, 2 W, 3 N, 4 SE, 5 SW, 6 NW, 7 NE (y grows southwards).
    /// </summary>
    public static IReadOnlyList<(int Dx, int Dy)> Directions { get; } =
        [(1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1)];

    internal TerrainMap(ulong seed, int size, int version)
    {
        Seed = seed;
        Size = size;
        Version = version;
        int count = size * size;
        HeightArray = new byte[count];
        BiomeArray = new Biome[count];
        FeatureArray = new TileFeatures[count];
        RiverDirectionArray = new byte[count];
        ElevationArray = new ushort[count];
        MoistureArray = new ushort[count];
        TemperatureArray = new ushort[count];
        Array.Fill(RiverDirectionArray, NoDirection);
    }

    /// <summary>World seed the map was generated from.</summary>
    public ulong Seed { get; }

    /// <summary>Side of the map in tiles.</summary>
    public int Size { get; }

    /// <summary>Generator version (<see cref="WorldGenerator.Version"/>) that produced the map.</summary>
    public int Version { get; }

    /// <summary>Number of tiles (<c>Size * Size</c>).</summary>
    public int TileCount => Size * Size;

    /// <summary>Chunks along one side.</summary>
    public int ChunksPerSide => ChunkCoord.CountPerSide(Size);

    /// <summary>Height step 0–3 per tile.</summary>
    public ReadOnlySpan<byte> Heights => HeightArray;

    /// <summary>Biome per tile.</summary>
    public ReadOnlySpan<Biome> Biomes => BiomeArray;

    /// <summary>Features (water, ocean, lake, river) per tile.</summary>
    public ReadOnlySpan<TileFeatures> Features => FeatureArray;

    /// <summary>Flow direction index into <see cref="Directions"/> on river tiles, <see cref="NoDirection"/> elsewhere.</summary>
    public ReadOnlySpan<byte> RiverDirections => RiverDirectionArray;

    /// <summary>Continuous height 0..65535 per tile.</summary>
    public ReadOnlySpan<ushort> Elevations => ElevationArray;

    /// <summary>Moisture rank per tile, permille 0..999 (0 on water).</summary>
    public ReadOnlySpan<ushort> Moistures => MoistureArray;

    /// <summary>Temperature rank per tile, permille 0..999 (0 on water); falls with height.</summary>
    public ReadOnlySpan<ushort> Temperatures => TemperatureArray;

    // Writable layers for the generator steps (same assembly only).
    internal byte[] HeightArray { get; }

    internal Biome[] BiomeArray { get; }

    internal TileFeatures[] FeatureArray { get; }

    internal byte[] RiverDirectionArray { get; }

    internal ushort[] ElevationArray { get; }

    internal ushort[] MoistureArray { get; }

    internal ushort[] TemperatureArray { get; }

    /// <summary>Array index of tile (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tile is outside the map.</exception>
    public int Index(int x, int y)
    {
        if (!Contains(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Tile ({x}, {y}) is outside the {Size}x{Size} map.");
        }

        return (y * Size) + x;
    }

    /// <summary>True when the tile is on the map.</summary>
    public bool Contains(int x, int y) => (uint)x < (uint)Size && (uint)y < (uint)Size;

    /// <summary>Height step 0–3.</summary>
    public int GetHeight(int x, int y) => HeightArray[Index(x, y)];

    /// <summary>Biome of the tile.</summary>
    public Biome GetBiome(int x, int y) => BiomeArray[Index(x, y)];

    /// <summary>Features of the tile.</summary>
    public TileFeatures GetFeatures(int x, int y) => FeatureArray[Index(x, y)];

    /// <summary>True for standing water (ocean or lake).</summary>
    public bool IsWater(int x, int y) => (FeatureArray[Index(x, y)] & TileFeatures.Water) != 0;

    /// <summary>True when a river flows over the tile.</summary>
    public bool IsRiver(int x, int y) => (FeatureArray[Index(x, y)] & TileFeatures.River) != 0;

    /// <summary>Flow direction (index into <see cref="Directions"/>) or <see cref="NoDirection"/>.</summary>
    public int GetRiverDirection(int x, int y) => RiverDirectionArray[Index(x, y)];

    /// <summary>Continuous height 0..65535.</summary>
    public int GetElevation(int x, int y) => ElevationArray[Index(x, y)];

    /// <summary>Moisture rank, permille.</summary>
    public int GetMoisture(int x, int y) => MoistureArray[Index(x, y)];

    /// <summary>Temperature rank, permille.</summary>
    public int GetTemperature(int x, int y) => TemperatureArray[Index(x, y)];

    /// <summary>The access window of a chunk.</summary>
    public ChunkWindow GetChunk(int cx, int cy) => new(new ChunkCoord(cx, cy), Size);

    /// <summary>Number of tiles of each biome, indexed by the <see cref="Biome"/> value.</summary>
    public int[] CountBiomes()
    {
        int[] counts = new int[BiomeNames.Generated.Count + 1];
        foreach (Biome biome in BiomeArray)
        {
            counts[(int)biome]++;
        }

        return counts;
    }

    /// <summary>
    /// Writes the canonical terrain bytes: a tag, version, seed, size, then every layer length-prefixed in this fixed
    /// order: heights, biomes, features, river directions, elevations, moistures, temperatures (16-bit layers
    /// little-endian).
    /// </summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("terrain");
        writer.WriteInt32(Version);
        writer.WriteUInt64(Seed);
        writer.WriteInt32(Size);
        writer.WriteBytes(HeightArray);
        writer.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(BiomeArray.AsSpan()));
        writer.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(FeatureArray.AsSpan()));
        writer.WriteBytes(RiverDirectionArray);
        WriteUInt16Layer(writer, ElevationArray);
        WriteUInt16Layer(writer, MoistureArray);
        WriteUInt16Layer(writer, TemperatureArray);
    }

    /// <summary>XXH3-64 of <see cref="WriteState"/> (the terrain hash in golden tests).</summary>
    public ulong ComputeHash() => new StateHasher().Hash(this);

    internal static void WriteUInt16Layer(StateWriter writer, ushort[] layer)
    {
        byte[] bytes = new byte[layer.Length * sizeof(ushort)];
        for (int i = 0; i < layer.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), layer[i]);
        }

        writer.WriteBytes(bytes);
    }
}
