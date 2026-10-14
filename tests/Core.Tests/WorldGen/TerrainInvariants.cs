using Core.WorldGen;
using Core.WorldGen.Terrain;

namespace Core.Tests.WorldGen;

/// <summary>Structural rules every generated terrain must satisfy, whatever the seed and size.</summary>
internal static class TerrainInvariants
{
    public static void AssertAll(TerrainMap map)
    {
        AssertTileLayersConsistent(map);
        AssertBorderIsOcean(map);
        AssertLakesAreSeparateFromOcean(map);
        AssertRiversFlowDownhillToWater(map);
    }

    public static void AssertTileLayersConsistent(TerrainMap map)
    {
        for (int i = 0; i < map.TileCount; i++)
        {
            int height = map.Heights[i];
            Biome biome = map.Biomes[i];
            TileFeatures features = map.Features[i];
            bool water = (features & TileFeatures.Water) != 0;
            bool ocean = (features & TileFeatures.Ocean) != 0;
            bool lake = (features & TileFeatures.Lake) != 0;
            bool river = (features & TileFeatures.River) != 0;

            Assert.InRange(height, 0, TerrainMap.MaxHeight);
            Assert.Contains(biome, BiomeNames.Generated);
            Assert.Equal(water, biome == Biome.Water);
            Assert.Equal(water, ocean ^ lake);
            Assert.False(ocean && lake);
            Assert.False(water && river, $"Tile {i} is water and river.");
            Assert.Equal(river, map.RiverDirections[i] != TerrainMap.NoDirection);
            if (water)
            {
                Assert.Equal(0, height);
                Assert.Equal(0, map.Moistures[i]);
                Assert.Equal(0, map.Temperatures[i]);
            }
            else
            {
                Assert.InRange(map.Moistures[i], 0, 999);
                Assert.InRange(map.Temperatures[i], 0, 999);
            }
        }
    }

    public static void AssertBorderIsOcean(TerrainMap map)
    {
        for (int k = 0; k < map.Size; k++)
        {
            Assert.True((map.GetFeatures(k, 0) & TileFeatures.Ocean) != 0);
            Assert.True((map.GetFeatures(k, map.Size - 1) & TileFeatures.Ocean) != 0);
            Assert.True((map.GetFeatures(0, k) & TileFeatures.Ocean) != 0);
            Assert.True((map.GetFeatures(map.Size - 1, k) & TileFeatures.Ocean) != 0);
        }
    }

    // Lakes and the ocean are different 4-connected water bodies, so they never touch orthogonally.
    public static void AssertLakesAreSeparateFromOcean(TerrainMap map)
    {
        for (int y = 0; y < map.Size; y++)
        {
            for (int x = 0; x < map.Size - 1; x++)
            {
                TileFeatures a = map.GetFeatures(x, y);
                TileFeatures b = map.GetFeatures(x + 1, y);
                TileFeatures c = map.GetFeatures(y, x);
                TileFeatures d = map.GetFeatures(y, x + 1);
                Assert.False(((a | b) & (TileFeatures.Ocean | TileFeatures.Lake)) == (TileFeatures.Ocean | TileFeatures.Lake)
                    && (a & b & TileFeatures.Water) != 0, $"Ocean touches lake at ({x}, {y}).");
                Assert.False(((c | d) & (TileFeatures.Ocean | TileFeatures.Lake)) == (TileFeatures.Ocean | TileFeatures.Lake)
                    && (c & d & TileFeatures.Water) != 0, $"Ocean touches lake at ({y}, {x}).");
            }
        }
    }

    /// <summary>
    /// Every river tile has a path along its flow directions over river tiles to standing water, strictly descending
    /// on the priority-flooded surface, and every river starts (has no upstream river tile) in the mountains.
    /// </summary>
    public static void AssertRiversFlowDownhillToWater(TerrainMap map)
    {
        int[] filled = RiverStep.PriorityFlood(map, WorldGenTestData.Params.Rivers.MeanderJitter);
        bool[] hasUpstream = new bool[map.TileCount];
        for (int i = 0; i < map.TileCount; i++)
        {
            if ((map.Features[i] & TileFeatures.River) == 0)
            {
                continue;
            }

            int next = Next(map, i);
            Assert.True(filled[next] < filled[i], $"River at tile {i} flows uphill.");
            Assert.True((map.Features[next] & (TileFeatures.River | TileFeatures.Water)) != 0, $"River at tile {i} flows onto dry land.");
            hasUpstream[next] = true;

            // Walk to water; strictly descending heights also rule out loops.
            int steps = 0;
            int current = i;
            while ((map.Features[current] & TileFeatures.Water) == 0)
            {
                current = Next(map, current);
                Assert.True(++steps <= map.TileCount, $"River from tile {i} never reaches water.");
            }
        }

        for (int i = 0; i < map.TileCount; i++)
        {
            if ((map.Features[i] & TileFeatures.River) != 0 && !hasUpstream[i])
            {
                Assert.True(map.Biomes[i] == Biome.Mountain, $"River source at tile {i} is in {map.Biomes[i]}, not in the mountains.");
            }
        }
    }

    /// <summary>Land tiles whose biome no orthogonal land neighbour shares (checkerboard residue).</summary>
    public static int CountIsolatedBiomeTiles(TerrainMap map)
    {
        int isolated = 0;
        for (int y = 0; y < map.Size; y++)
        {
            for (int x = 0; x < map.Size; x++)
            {
                if (map.IsWater(x, y))
                {
                    continue;
                }

                int neighbours = 0;
                int same = 0;
                foreach ((int dx, int dy) in TerrainMap.Directions.Take(4))
                {
                    if (map.Contains(x + dx, y + dy) && !map.IsWater(x + dx, y + dy))
                    {
                        neighbours++;
                        same += map.GetBiome(x + dx, y + dy) == map.GetBiome(x, y) ? 1 : 0;
                    }
                }

                isolated += neighbours > 0 && same == 0 ? 1 : 0;
            }
        }

        return isolated;
    }

    public static int CountLand(TerrainMap map) => map.TileCount - map.CountBiomes()[(int)Biome.Water];

    private static int Next(TerrainMap map, int i)
    {
        int direction = map.RiverDirections[i];
        Assert.InRange(direction, 0, 7);
        (int dx, int dy) = TerrainMap.Directions[direction];
        int x = (i % map.Size) + dx;
        int y = (i / map.Size) + dy;
        Assert.True(map.Contains(x, y), $"River at tile {i} flows off the map.");
        return map.Index(x, y);
    }
}
