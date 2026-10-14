using Core.Content;
using Core.WorldGen.Placement;
using Core.WorldGen.Terrain;

namespace Core.WorldGen;

/// <summary>Input of the generator: the pure function's arguments <c>{Seed, Size, Version}</c> plus the data parameters.</summary>
/// <param name="Seed">World seed.</param>
/// <param name="Size">Side of the square map in tiles: a multiple of 32 from <see cref="MinSize"/> to <see cref="MaxSize"/>.</param>
/// <param name="Params">Generator parameters from <c>data/worldgen</c> (<see cref="ContentCatalog.WorldGen"/>).</param>
/// <param name="Version">Generator version to reproduce; only <see cref="WorldGenerator.Version"/> is supported.</param>
public sealed record WorldGenRequest(ulong Seed, int Size, WorldGenParamsDef Params, int Version = WorldGenerator.Version)
{
    /// <summary>Smallest supported map side (tests use small maps; the game uses <see cref="StandardSizes"/>).</summary>
    public const int MinSize = 64;

    /// <summary>Largest supported map side.</summary>
    public const int MaxSize = 2048;

    /// <summary>Map sizes offered to players (FR-WORLD-02; MVP uses 512).</summary>
    public static IReadOnlyList<int> StandardSizes { get; } = [512, 1024, 2048];

    /// <summary>Throws when the request cannot be generated.</summary>
    /// <exception cref="ArgumentException">Bad size or missing parameters.</exception>
    /// <exception cref="NotSupportedException">The version is not the one this build implements.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Params, nameof(Params));
        if (Size < MinSize || Size > MaxSize || Size % ChunkCoord.Size != 0)
        {
            throw new ArgumentException($"Map size {Size} must be a multiple of {ChunkCoord.Size} between {MinSize} and {MaxSize}.", nameof(Size));
        }

        if (!WorldGenerator.IsSupportedVersion(Version))
        {
            throw new NotSupportedException(
                $"World generator v{Version} is not available in this build (it implements v{WorldGenerator.Version}); the same seed would give a different world.");
        }
    }
}

/// <summary>
/// The world generator (ADR 0006): a pipeline of pure functions of <see cref="WorldGenRequest"/>. Same request,
/// same bytes, on every platform (FR-WORLD-01).
/// </summary>
/// <remarks>
/// Numbers: noise is sampled with FastNoiseLite (double coordinates, float internals) using only + − × ÷ √, and is
/// converted to 16-bit fixed point right after sampling. Everything after that (quantiles, climate, biome table,
/// smoothing, priority-flood, river tracing) is integer arithmetic.
/// </remarks>
public static class WorldGenerator
{
    /// <summary>
    /// Version of the whole generation algorithm, <see cref="Generate"/> (steps 1–9), not only of the terrain stage.
    /// Stored in saves (<see cref="Save.SaveFormat.WorldGenVersion"/>), which rebuild the world from the seed with it.
    /// Bump it on any change that alters the <see cref="WorldMap"/> of some seed once that version has been released,
    /// and regenerate the golden terrain and world hashes. The terrain hash includes the version, so a bump also changes
    /// every terrain golden.
    /// </summary>
    /// <remarks>
    /// v1 is the first generator that ships (M1) and already includes the placement steps 5–9: the terrain-only
    /// development state was never released or committed, and no save holds a world from it (M0 saves carry
    /// <see cref="Save.SaveFormat.NoWorldGenVersion"/>), so adding the placement steps did not need a bump.
    /// </remarks>
    public const int Version = 1;

    /// <summary>True when this build can reproduce worlds of generator <paramref name="version"/>.</summary>
    public static bool IsSupportedVersion(int version) => version == Version;

    /// <summary>
    /// Terrain stage, ADR 0006 steps 1–4: heightfield, climate, biomes, rivers. Later stages (resources, points of
    /// interest, start, settlements) take the returned map as input.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid request.</exception>
    /// <exception cref="NotSupportedException">Unsupported generator version.</exception>
    public static TerrainMap GenerateTerrain(WorldGenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var map = new TerrainMap(request.Seed, request.Size, request.Version);
        HeightStep.Run(map, request.Seed, request.Params.Height);
        ClimateStep.Run(map, request.Seed, request.Params.Climate);
        BiomeStep.Run(map, request.Params.Biomes);
        RiverStep.Run(map, request.Params.Rivers);
        return map;
    }

    /// <summary>
    /// The whole pipeline, ADR 0006 steps 1–9: the terrain (<see cref="GenerateTerrain"/>, unchanged), then resources,
    /// points of interest, the start and the settlement sites. Hash the result with <see cref="WorldMap.ComputeHash"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid request or placement parameters (unknown names).</exception>
    /// <exception cref="NotSupportedException">Unsupported generator version.</exception>
    public static WorldMap Generate(WorldGenRequest request)
    {
        TerrainMap terrain = GenerateTerrain(request);
        WorldGenParamsDef p = request.Params;
        var world = new WorldMap(terrain);
        bool[] buildable = PlacementOps.Buildable(terrain);
        ResourceStep.Run(world, p.Resources);
        PoiStep.Run(world, p.PointsOfInterest, buildable);
        var settlements = new SettlementStep(world, p.Settlements, buildable);
        StartStep.Run(world, p.Start, buildable, settlements);
        settlements.Run(world.Start);
        return world;
    }
}
