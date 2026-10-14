using System.Globalization;
using System.Text;
using Core.Content;
using Core.Tests.Replay;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// Same seed → same terrain, byte for byte, on every platform (FR-WORLD-01, ADR 0006). The golden file pins the
/// terrain hash of seeds 1, 2, 3 at 512²; CI checks it on macOS and Windows.
/// </summary>
/// <remarks>
/// The hashes depend on the generator code, <see cref="WorldGenerator.Version"/> and <c>data/worldgen</c>. After an
/// intended change, bump the version if the change alters existing worlds, then regenerate with
/// <c>UPDATE_GOLDEN=1 tools/test.sh</c> (see <see cref="GoldenFiles"/>) and review the diff.
/// </remarks>
public sealed class TerrainGoldenTests
{
    private const string GoldenFile = "terrain_512.hashes.txt";

    private static readonly Lazy<IReadOnlyList<(int Version, ulong Seed, int Size, ulong Hash)>> _expected = new(LoadOrUpdate);

    [Theory]
    [Trait("Req", "FR-WORLD-01")]
    [Trait("Req", "NFR-04")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void TerrainHashMatchesGolden(int index)
    {
        TerrainMap map = WorldGenTestData.Golden(index);
        (int version, ulong seed, int size, ulong hash) = _expected.Value.Single(e => e.Seed == map.Seed);

        Assert.Equal(WorldGenerator.Version, version);
        Assert.Equal(WorldGenTestData.GoldenSize, size);
        Assert.True(hash == map.ComputeHash(),
            $"Terrain of seed {seed} hashes to {ContentHash.Format(map.ComputeHash())}, golden is {ContentHash.Format(hash)}. " +
            "If the generator or data/worldgen changed on purpose, regenerate with UPDATE_GOLDEN=1 tools/test.sh (and bump WorldGenerator.Version if existing worlds change).");
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void GoldenFileCoversExactlyTheGoldenSeeds() =>
        Assert.Equal(WorldGenTestData.GoldenSeeds, _expected.Value.Select(e => e.Seed));

    private static List<(int Version, ulong Seed, int Size, ulong Hash)> LoadOrUpdate()
    {
        if (GoldenFiles.Updating)
        {
            var text = new StringBuilder();
            text.Append("# Terrain hashes (TerrainMap.ComputeHash): generator version, seed, size, hash.\n");
            text.Append("# Regenerate deliberately with UPDATE_GOLDEN=1 tools/test.sh.\n");
            foreach (TerrainMap map in WorldGenTestData.AllGolden)
            {
                text.Append(CultureInfo.InvariantCulture, $"{map.Version} {map.Seed} {map.Size} {ContentHash.Format(map.ComputeHash())}\n");
            }

            GoldenFiles.WriteText(GoldenFile, text.ToString());
        }

        List<(int, ulong, int, ulong)> entries = [];
        foreach (string line in GoldenFiles.ReadText(GoldenFile).Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split(' ');
            ulong hash = 0;
            Assert.True(parts.Length == 4 && ContentHash.TryParse(parts[3], out hash), $"Bad line in {GoldenFile}: '{line}'.");
            entries.Add((int.Parse(parts[0], CultureInfo.InvariantCulture), ulong.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture), hash));
        }

        return entries;
    }
}
