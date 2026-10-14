using Core.Content;
using Core.WorldGen.Noise;

namespace Core.WorldGen.Terrain;

/// <summary>
/// ADR 0006 step 2: moisture and temperature as low-frequency FBm. Temperature is lowered by the continuous height.
/// Both are rank-normalized over land to permille, so the biome table's thresholds mean shares of land.
/// </summary>
internal static class ClimateStep
{
    public static void Run(TerrainMap map, ulong seed, ClimateParams p)
    {
        int count = map.TileCount;
        bool[] land = new bool[count];
        for (int i = 0; i < count; i++)
        {
            land[i] = (map.FeatureArray[i] & TileFeatures.Water) == 0;
        }

        FastNoiseLite moistureNoise = CreateFbm(WorldGenSeeds.NoiseSeed(seed, WorldGenStep.Climate, 0), p.MoistureWavelength, p.MoistureOctaves);
        FastNoiseLite temperatureNoise = CreateFbm(WorldGenSeeds.NoiseSeed(seed, WorldGenStep.Climate, 1), p.TemperatureWavelength, p.TemperatureOctaves);

        int size = map.Size;
        int[] moisture = new int[count];
        int[] temperature = new int[count];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size) + x;
                if (!land[i])
                {
                    continue;
                }

                moisture[i] = GridOps.UnitToFixed16((moistureNoise.GetNoise((double)x, y) * 0.5) + 0.5);
                int warmth = GridOps.UnitToFixed16((temperatureNoise.GetNoise((double)x, y) * 0.5) + 0.5);

                // Integer from here on: the lapse rate subtracts a share of the elevation.
                temperature[i] = warmth - (int)((long)map.ElevationArray[i] * p.TemperatureLapsePermille / 1000);
            }
        }

        GridOps.RankPermille(moisture, land, map.MoistureArray);
        GridOps.RankPermille(temperature, land, map.TemperatureArray);
    }

    private static FastNoiseLite CreateFbm(int noiseSeed, int wavelength, int octaves)
    {
        var noise = new FastNoiseLite(noiseSeed);
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(1f / wavelength);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(octaves);
        return noise;
    }
}
