using Core.Content;
using Core.WorldGen.Noise;

namespace Core.WorldGen.Terrain;

/// <summary>
/// ADR 0006 step 1: continuous height from domain-warped OpenSimplex2 FBm with an ocean falloff at the map edge,
/// then water by quantile, small lakes and islands removed, land quantized into steps 0–3 by quantiles and smoothed.
/// </summary>
internal static class HeightStep
{
    public static void Run(TerrainMap map, ulong seed, HeightParams p)
    {
        int size = map.Size;
        int count = map.TileCount;
        ushort[] elevation = map.ElevationArray;
        SampleElevation(seed, p, size, elevation);

        // Quantile levels over all tiles (exact integer histogram).
        int[] histogram = new int[GridOps.FixedOne + 1];
        foreach (ushort e in elevation)
        {
            histogram[e]++;
        }

        int waterLevel = GridOps.LevelForPermille(histogram, count, p.WaterPermille);
        int step1 = GridOps.LevelForPermille(histogram, count, p.Step1Permille);
        int step2 = GridOps.LevelForPermille(histogram, count, p.Step2Permille);
        int step3 = GridOps.LevelForPermille(histogram, count, p.Step3Permille);

        // Water: below the water level, plus the whole border ring so every map is surrounded by ocean.
        bool[] water = new bool[count];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size) + x;
                water[i] = elevation[i] < waterLevel || x == 0 || y == 0 || x == size - 1 || y == size - 1;
            }
        }

        List<int> sizes = [];
        List<bool> edge = [];

        // Small islands sink.
        bool[] land = Invert(water);
        int[] labels = GridOps.LabelRegions(land, size, sizes, edge);
        for (int i = 0; i < count; i++)
        {
            if (labels[i] >= 0 && sizes[labels[i]] < p.MinIslandTiles)
            {
                water[i] = true;
            }
        }

        // Water bodies: touching the edge = ocean; small inland ones dry up; the rest are lakes.
        labels = GridOps.LabelRegions(water, size, sizes, edge);
        TileFeatures[] features = map.FeatureArray;
        for (int i = 0; i < count; i++)
        {
            int label = labels[i];
            if (label < 0)
            {
                continue;
            }

            if (edge[label])
            {
                features[i] = TileFeatures.Water | TileFeatures.Ocean;
            }
            else if (sizes[label] >= p.MinLakeTiles)
            {
                features[i] = TileFeatures.Water | TileFeatures.Lake;
            }
            else
            {
                water[i] = false;
            }
        }

        byte[] heights = map.HeightArray;
        for (int i = 0; i < count; i++)
        {
            int e = elevation[i];
            heights[i] = water[i] ? (byte)0 : e < step1 ? (byte)0 : e < step2 ? (byte)1 : e < step3 ? (byte)2 : (byte)3;
        }

        GridOps.MajorityFilter(heights, Invert(water), size, p.SmoothingPasses, p.MajorityThreshold, categories: TerrainMap.MaxHeight + 1);
    }

    // The only floating-point part of the step: noise samples and the falloff, using + - * / only.
    private static void SampleElevation(ulong seed, HeightParams p, int size, ushort[] elevation)
    {
        var noise = new FastNoiseLite(WorldGenSeeds.NoiseSeed(seed, WorldGenStep.Height, 0));
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(1f / p.Wavelength);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(p.Octaves);
        noise.SetFractalLacunarity(p.LacunarityPermille / 1000f);
        noise.SetFractalGain(p.GainPermille / 1000f);

        var warp = new FastNoiseLite(WorldGenSeeds.NoiseSeed(seed, WorldGenStep.Height, 1));
        warp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        warp.SetDomainWarpAmp(p.WarpAmplitude);
        warp.SetFrequency(1f / p.WarpWavelength);
        warp.SetFractalType(FastNoiseLite.FractalType.DomainWarpProgressive);
        warp.SetFractalOctaves(p.WarpOctaves);

        int margin = p.OceanMargin;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double fx = x;
                double fy = y;
                if (p.WarpAmplitude > 0)
                {
                    warp.DomainWarp(ref fx, ref fy);
                }

                double value = (noise.GetNoise(fx, fy) * 0.5) + 0.5;

                // Ocean falloff by the distance of the warped position to the map edge: an organic coast. The
                // border ring itself is forced to water later, whatever the warp does.
                double edgeDistance = Math.Min(Math.Min(fx, fy), Math.Min(size - 1 - fx, size - 1 - fy));
                if (edgeDistance < margin)
                {
                    double t = edgeDistance > 0.0 ? edgeDistance / margin : 0.0;
                    value *= t * t * (3.0 - (2.0 * t));
                }

                elevation[(y * size) + x] = GridOps.UnitToFixed16(value);
            }
        }
    }

    private static bool[] Invert(bool[] source)
    {
        bool[] result = new bool[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = !source[i];
        }

        return result;
    }
}
