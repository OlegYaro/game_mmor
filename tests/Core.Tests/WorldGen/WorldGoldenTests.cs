using System.Globalization;
using System.Text;
using Core.Content;
using Core.Tests.Replay;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>
/// Same seed → same world, byte for byte, on every platform (FR-WORLD-01, ADR 0006 step 9). The golden file pins the
/// full map hash (<see cref="WorldMap.ComputeHash"/>: terrain hash, resource layers, points of interest, start,
/// settlement sites) of seeds 1, 2, 3 at 512²; CI checks it on macOS and Windows.
/// </summary>
/// <remarks>
/// After an intended change to a placement step or to <c>data/worldgen</c>, bump <see cref="WorldGenerator.Version"/>
/// if the change alters worlds of a released version, regenerate with <c>UPDATE_GOLDEN=1 tools/test.sh</c> (see
/// <see cref="GoldenFiles"/>) and review the diff.
/// </remarks>
public sealed class WorldGoldenTests
{
    private const string GoldenFile = "world_512.hashes.txt";

    private static readonly Lazy<IReadOnlyList<(int Version, ulong Seed, int Size, ulong Hash)>> _expected = new(LoadOrUpdate);

    [Theory]
    [Trait("Req", "FR-WORLD-01")]
    [Trait("Req", "NFR-04")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void WorldHashMatchesGolden(int index)
    {
        WorldMap world = WorldGenTestData.GoldenWorld(index);
        (int version, ulong seed, int size, ulong hash) = _expected.Value.Single(e => e.Seed == world.Seed);

        Assert.Equal(WorldGenerator.Version, version);
        Assert.Equal(WorldGenTestData.GoldenSize, size);
        Assert.True(hash == world.ComputeHash(),
            $"World of seed {seed} hashes to {ContentHash.Format(world.ComputeHash())}, golden is {ContentHash.Format(hash)}. " +
            "If a placement step or data/worldgen changed on purpose, regenerate with UPDATE_GOLDEN=1 tools/test.sh (and bump WorldGenerator.Version if released worlds change).");
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
            text.Append("# Full map hashes (WorldMap.ComputeHash): generator version, seed, size, hash.\n");
            text.Append("# Regenerate deliberately with UPDATE_GOLDEN=1 tools/test.sh.\n");
            foreach (WorldMap world in WorldGenTestData.AllGoldenWorlds)
            {
                text.Append(CultureInfo.InvariantCulture, $"{world.Version} {world.Seed} {world.Size} {ContentHash.Format(world.ComputeHash())}\n");
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
