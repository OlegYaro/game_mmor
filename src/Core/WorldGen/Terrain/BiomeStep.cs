using System.Runtime.InteropServices;
using Core.Content;

namespace Core.WorldGen.Terrain;

/// <summary>
/// ADR 0006 step 3: biome per land tile from the data table over (height step, moisture, temperature), first match
/// wins; water tiles get <see cref="Biome.Water"/>. Then majority-filter passes remove checkerboard borders.
/// </summary>
internal static class BiomeStep
{
    public static void Run(TerrainMap map, BiomeParams p)
    {
        Biome[] ruleBiomes = new Biome[p.Rules.Count];
        for (int r = 0; r < ruleBiomes.Length; r++)
        {
            if (!BiomeNames.TryParse(p.Rules[r].Biome, out ruleBiomes[r]) || ruleBiomes[r] == Biome.Water || ruleBiomes[r] == Biome.None)
            {
                throw new ArgumentException($"Biome rule {r} names '{p.Rules[r].Biome}', which is not a land biome.", nameof(p));
            }
        }

        int count = map.TileCount;
        bool[] land = new bool[count];
        Biome[] biomes = map.BiomeArray;
        for (int i = 0; i < count; i++)
        {
            land[i] = (map.FeatureArray[i] & TileFeatures.Water) == 0;
            biomes[i] = land[i]
                ? Classify(p.Rules, ruleBiomes, map.HeightArray[i], map.MoistureArray[i], map.TemperatureArray[i])
                : Biome.Water;
        }

        GridOps.MajorityFilter(MemoryMarshal.AsBytes(biomes.AsSpan()), land, map.Size, p.SmoothingPasses, p.MajorityThreshold,
            categories: BiomeNames.Generated.Count + 1);
    }

    private static Biome Classify(IReadOnlyList<BiomeRuleDef> rules, Biome[] ruleBiomes, int height, int moisture, int temperature)
    {
        for (int r = 0; r < ruleBiomes.Length; r++)
        {
            if (rules[r].Matches(height, moisture, temperature))
            {
                return ruleBiomes[r];
            }
        }

        throw new ArgumentException($"No biome rule matches height {height}, moisture {moisture}, temperature {temperature}; the last rule must match everything.", nameof(rules));
    }
}
