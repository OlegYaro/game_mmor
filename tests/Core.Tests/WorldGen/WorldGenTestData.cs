using Core.Content;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>Shared generator inputs and cached maps (generating 512² takes about a second; do it once per run).</summary>
internal static class WorldGenTestData
{
    /// <summary>Map side of the golden maps (MVP size, FR-WORLD-02).</summary>
    public const int GoldenSize = 512;

    /// <summary>Map side of the many-seed invariant runs.</summary>
    public const int InvariantSize = 256;

    /// <summary>Seeds with committed golden hashes.</summary>
    public static readonly ulong[] GoldenSeeds = [1, 2, 3];

    private static readonly Lazy<WorldMap>[] _golden = GoldenSeeds
        .Select(seed => new Lazy<WorldMap>(() => GenerateWorld(seed, GoldenSize)))
        .ToArray();

    private static readonly Lazy<WorldMap>[] _invariant = Enumerable.Range(1, 20)
        .Select(seed => new Lazy<WorldMap>(() => GenerateWorld((ulong)seed, InvariantSize)))
        .ToArray();

    /// <summary>Generator parameters from the real data/ folder.</summary>
    public static WorldGenParamsDef Params => SimTestData.Real.WorldGen;

    /// <summary>The terrain of the cached 512² world of golden seed index 0..2 (<see cref="WorldGenerator.Generate"/> keeps it unchanged).</summary>
    public static TerrainMap Golden(int index) => _golden[index].Value.Terrain;

    public static IEnumerable<TerrainMap> AllGolden => _golden.Select(g => g.Value.Terrain);

    /// <summary>The cached 512² world of golden seed index 0..2.</summary>
    public static WorldMap GoldenWorld(int index) => _golden[index].Value;

    public static IEnumerable<WorldMap> AllGoldenWorlds => _golden.Select(g => g.Value);

    /// <summary>The cached 256² world of seed 1..20.</summary>
    public static WorldMap InvariantWorld(int seed) => _invariant[seed - 1].Value;

    public static TerrainMap Generate(ulong seed, int size) =>
        WorldGenerator.GenerateTerrain(new WorldGenRequest(seed, size, Params));

    public static WorldMap GenerateWorld(ulong seed, int size, WorldGenParamsDef? parameters = null) =>
        WorldGenerator.Generate(new WorldGenRequest(seed, size, parameters ?? Params));

    /// <summary>xUnit data: golden seed indices.</summary>
    public static TheoryData<int> GoldenIndices => [0, 1, 2];

    /// <summary>xUnit data: invariant seeds 1..20.</summary>
    public static TheoryData<int> InvariantSeeds => [.. Enumerable.Range(1, 20)];
}
