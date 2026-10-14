using Core.Content;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>Shared generator inputs and cached maps (generating 512² takes about half a second in Debug; do it once per run).</summary>
/// <remarks>
/// Sizes are on the 32×16 tile grid (P-07): 512² covers the area of the former 256² map on the 64×32 grid, 1024² the
/// former 512² MVP map, 2048² (the game's default) four times that.
/// </remarks>
internal static class WorldGenTestData
{
    /// <summary>Map side of the golden maps.</summary>
    public const int GoldenSize = 512;

    /// <summary>
    /// Map side of the many-seed invariant runs: the smallest standard size. Smaller maps cannot hold the scaled start
    /// zone (radius 80, edge margin 112) and the evil distance (240) at the same time, so every start would be relaxed.
    /// </summary>
    public const int InvariantSize = 512;

    /// <summary>Map side of the larger scale checks (the former 512² MVP geography).</summary>
    public const int LargeSize = 1024;

    /// <summary>Map side of the default game world (Benchmark-tagged checks only: about 7 s per map in Debug).</summary>
    public const int HugeSize = 2048;

    /// <summary>Seeds with committed golden hashes.</summary>
    public static readonly ulong[] GoldenSeeds = [1, 2, 3];

    /// <summary>Seeds of the many-seed invariant runs.</summary>
    public const int InvariantSeedCount = 20;

    private static readonly Lazy<WorldMap>[] _golden = GoldenSeeds
        .Select(seed => new Lazy<WorldMap>(() => GenerateWorld(seed, GoldenSize)))
        .ToArray();

    // Golden and invariant maps have the same size (512), so seeds 1..3 share the golden cache.
    private static readonly Lazy<WorldMap>[] _invariant = Enumerable.Range(1, InvariantSeedCount)
        .Select(seed => seed <= GoldenSeeds.Length
            ? _golden[seed - 1]
            : new Lazy<WorldMap>(() => GenerateWorld((ulong)seed, InvariantSize)))
        .ToArray();

    private static readonly Lazy<WorldMap>[] _large = GoldenSeeds
        .Select(seed => new Lazy<WorldMap>(() => GenerateWorld(seed, LargeSize)))
        .ToArray();

    private static readonly Lazy<WorldMap>[] _huge = [new(() => GenerateWorld(1, HugeSize)), new(() => GenerateWorld(2, HugeSize))];

    /// <summary>Generator parameters from the real data/ folder.</summary>
    public static WorldGenParamsDef Params => SimTestData.Real.WorldGen;

    /// <summary>The terrain of the cached 512² world of golden seed index 0..2 (<see cref="WorldGenerator.Generate"/> keeps it unchanged).</summary>
    public static TerrainMap Golden(int index) => _golden[index].Value.Terrain;

    public static IEnumerable<TerrainMap> AllGolden => _golden.Select(g => g.Value.Terrain);

    /// <summary>The cached 512² world of golden seed index 0..2.</summary>
    public static WorldMap GoldenWorld(int index) => _golden[index].Value;

    public static IEnumerable<WorldMap> AllGoldenWorlds => _golden.Select(g => g.Value);

    /// <summary>The cached 512² world of seed 1..20.</summary>
    public static WorldMap InvariantWorld(int seed) => _invariant[seed - 1].Value;

    /// <summary>The cached 1024² world of seed 1..3.</summary>
    public static WorldMap LargeWorld(int seed) => _large[seed - 1].Value;

    /// <summary>The cached 2048² world of seed 1 or 2 (Benchmark-tagged tests only).</summary>
    public static WorldMap HugeWorld(int seed) => _huge[seed - 1].Value;

    public static TerrainMap Generate(ulong seed, int size) =>
        WorldGenerator.GenerateTerrain(new WorldGenRequest(seed, size, Params));

    public static WorldMap GenerateWorld(ulong seed, int size, WorldGenParamsDef? parameters = null) =>
        WorldGenerator.Generate(new WorldGenRequest(seed, size, parameters ?? Params));

    /// <summary>xUnit data: golden seed indices.</summary>
    public static TheoryData<int> GoldenIndices => [0, 1, 2];

    /// <summary>xUnit data: invariant seeds 1..20.</summary>
    public static TheoryData<int> InvariantSeeds => [.. Enumerable.Range(1, InvariantSeedCount)];
}
