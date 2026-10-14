using System.Globalization;
using Core.Content;
using Core.Tests.Replay;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// Golden hashes of seed 1 at the player sizes (FR-WORLD-01, FR-WORLD-02): 1024² in every run, 2048² (the default
/// size, about 7 s in Debug) as a Benchmark-tagged test. Besides the terrain and full map hashes each file records the
/// tile count per biome, so an intended change shows how the world moved.
/// </summary>
/// <remarks>Regenerate deliberately with <c>UPDATE_GOLDEN=1</c> (see <see cref="GoldenFiles"/>) and review the diff.</remarks>
public sealed class LargeWorldGoldenTests
{
    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    [Trait("Req", "FR-WORLD-02")]
    public void World1024MatchesGolden() => AssertGolden(WorldGenTestData.LargeWorld(1), "world_1024.hashes.txt");

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    [Trait("Req", "FR-WORLD-02")]
    [Trait("Category", "Benchmark")]
    public void World2048MatchesGolden() => AssertGolden(WorldGenTestData.HugeWorld(1), "world_2048.hashes.txt");

    private static void AssertGolden(WorldMap world, string fileName)
    {
        string actual = Describe(world);
        if (GoldenFiles.Updating)
        {
            GoldenFiles.WriteText(fileName,
                "# Generator version, seed, size, terrain hash, full map hash, tiles per biome (Biome order).\n" +
                "# Regenerate deliberately with UPDATE_GOLDEN=1 tools/test.sh.\n" + actual + "\n");
        }

        string expected = GoldenFiles.ReadText(fileName).Split('\n').Single(l => l.Length > 0 && !l.StartsWith('#'));
        Assert.True(expected == actual,
            $"World of seed {world.Seed} at {world.Size}² is\n  {actual}\ngolden is\n  {expected}\n" +
            "If the generator or data/worldgen changed on purpose, regenerate with UPDATE_GOLDEN=1 tools/test.sh (and bump WorldGenerator.Version if released worlds change).");
    }

    private static string Describe(WorldMap world)
    {
        int[] counts = world.Terrain.CountBiomes();
        string biomes = string.Join(' ', Enum.GetValues<Biome>().Where(b => b != Biome.None)
            .Select(b => string.Create(CultureInfo.InvariantCulture, $"{BiomeNames.ToName(b)}={counts[(int)b]}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"{world.Version} {world.Seed} {world.Size} {ContentHash.Format(world.Terrain.ComputeHash())} {ContentHash.Format(world.ComputeHash())} {biomes}");
    }
}
