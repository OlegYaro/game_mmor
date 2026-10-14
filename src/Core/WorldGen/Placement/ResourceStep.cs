using Core.Content;
using Core.WorldGen.Noise;
using Core.WorldGen.Terrain;

namespace Core.WorldGen.Placement;

/// <summary>
/// ADR 0006 step 5: resource nodes as a tile layer with a stock (B5). Order: ore, stone, trees, berry bushes; a later
/// kind never replaces an earlier one. Only open land gets resources: never water, never a river.
/// </summary>
/// <remarks>
/// Ore and stone use integer cellular (Worley-style) clusters: one feature point per square cell, three overlapping
/// discs around it, a per-cell roll against the chance of the tile's biome. Trees use OpenSimplex2 FBm (the only
/// floating-point part, converted to 16-bit fixed point right after sampling) mixed with per-tile white noise,
/// rank-normalized, then a cellular automaton. Everything else is integer arithmetic.
/// </remarks>
internal static class ResourceStep
{
    private const int Lobes = 3;

    // Salt layers (chunk y = -3 keeps them apart from the noise seeds, which use chunk y = -1).
    private const int OreLayer = 0;
    private const int StoneLayer = 1;
    private const int TreeLayer = 2;
    private const int BerryLayer = 3;

    public static void Run(WorldMap world, ResourceParams p)
    {
        TerrainMap map = world.Terrain;
        int count = map.TileCount;
        bool[] open = new bool[count];
        for (int i = 0; i < count; i++)
        {
            open[i] = (map.FeatureArray[i] & (TileFeatures.Water | TileFeatures.River)) == 0;
        }

        PlaceClusters(world, open, p.Ore, ResourceKind.Ore, OreLayer);
        PlaceClusters(world, open, p.Stone, ResourceKind.Stone, StoneLayer);
        PlaceTrees(world, open, p.Trees);
        PlaceBerries(world, open, p.Berries);
    }

    private static ulong Salt(TerrainMap map, int layer) => WorldGenSeeds.Derive(map.Seed, WorldGenStep.Resources, layer, -3);

    private static void Put(WorldMap world, int i, ResourceKind kind, ulong stockHash, int stockMin, int stockMax)
    {
        world.ResourceKindArray[i] = kind;
        world.ResourceAmountArray[i] = (ushort)PlacementOps.InRange(stockHash, stockMin, stockMax);
    }

    // Per-biome lookup (indexed by Biome value) of a value from a named list; -1 for biomes not in the list.
    private static int[] BiomeTable<T>(IReadOnlyList<T> entries, Func<T, string> name, Func<T, int> value, string what)
    {
        int[] table = new int[BiomeNames.Generated.Count + 1];
        Array.Fill(table, -1);
        foreach (T entry in entries)
        {
            if (!BiomeNames.TryParse(name(entry), out Biome biome) || biome is Biome.None or Biome.Water)
            {
                throw new ArgumentException($"{what} lists '{name(entry)}', which is not a land biome.", nameof(entries));
            }

            table[(int)biome] = value(entry);
        }

        return table;
    }

    private static void PlaceClusters(WorldMap world, bool[] open, ClusterResourceParams p, ResourceKind kind, int layer)
    {
        TerrainMap map = world.Terrain;
        int size = map.Size;
        string what = $"Resource '{WorldNames.ToName(kind)}'";
        int[] chance = BiomeTable(p.Biomes, b => b.Biome, b => b.ChancePermille, what);
        int[] radius = BiomeTable(p.Biomes, b => b.Biome, b => b.Radius, what);
        ulong salt = Salt(map, layer);
        int cell = p.CellSize;
        int cells = (size + cell - 1) / cell;

        // Per cell: feature point, roll, and lobes as (offset x, offset y, radius) in permille of the biome radius.
        int[] featureX = new int[cells * cells];
        int[] featureY = new int[cells * cells];
        int[] roll = new int[cells * cells];
        int[] lobes = new int[cells * cells * Lobes * 3];
        for (int c = 0; c < cells * cells; c++)
        {
            ulong h = PlacementOps.Hash(salt, c);
            featureX[c] = ((c % cells) * cell) + (int)(h % (ulong)cell);
            featureY[c] = ((c / cells) * cell) + (int)((h >> 20) % (ulong)cell);
            roll[c] = PlacementOps.Permille(h);
            for (int l = 0; l < Lobes; l++)
            {
                ulong lh = PlacementOps.Hash(salt, ((long)c * Lobes) + l + (1L << 40));
                int o = ((c * Lobes) + l) * 3;
                lobes[o] = l == 0 ? 0 : PlacementOps.InRange(lh, -700, 700);
                lobes[o + 1] = l == 0 ? 0 : PlacementOps.InRange(lh >> 16, -700, 700);
                lobes[o + 2] = PlacementOps.InRange(lh >> 32, 500, 1000);
            }
        }

        ulong stockSalt = salt ^ 0x9E3779B97F4A7C15UL;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size) + x;
                int b = (int)map.BiomeArray[i];
                if (!open[i] || world.ResourceKindArray[i] != ResourceKind.None || chance[b] <= 0 || map.HeightArray[i] < p.MinHeight)
                {
                    continue;
                }

                if (InCluster(x, y, radius[b], chance[b]))
                {
                    Put(world, i, kind, PlacementOps.Hash(stockSalt, i), p.StockMin, p.StockMax);
                }
            }
        }

        bool InCluster(int x, int y, int r, int chancePermille)
        {
            int reach = r + (r * 7 / 10) + 1;
            int gx0 = Math.Max(0, x - reach) / cell;
            int gx1 = Math.Min(size - 1, x + reach) / cell;
            int gy0 = Math.Max(0, y - reach) / cell;
            int gy1 = Math.Min(size - 1, y + reach) / cell;
            for (int gy = gy0; gy <= gy1; gy++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    int c = (gy * cells) + gx;
                    if (roll[c] >= chancePermille)
                    {
                        continue;
                    }

                    for (int l = 0; l < Lobes; l++)
                    {
                        int o = ((c * Lobes) + l) * 3;
                        int lx = featureX[c] + (lobes[o] * r / 1000);
                        int ly = featureY[c] + (lobes[o + 1] * r / 1000);
                        long lr = (long)lobes[o + 2] * r / 1000;
                        if (PlacementOps.DistanceSquared(x, y, lx, ly) <= lr * lr)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    private static void PlaceTrees(WorldMap world, bool[] open, TreeParams p)
    {
        TerrainMap map = world.Terrain;
        int size = map.Size;
        int count = map.TileCount;
        int[] share = BiomeTable(p.Biomes, b => b.Biome, b => b.Permille, "Trees");
        bool[] eligible = new bool[count];
        for (int i = 0; i < count; i++)
        {
            eligible[i] = open[i] && share[(int)map.BiomeArray[i]] >= 0;
        }

        // Initial density: smooth noise mixed with white noise, ranked among eligible tiles.
        var noise = new FastNoiseLite(WorldGenSeeds.NoiseSeed(map.Seed, WorldGenStep.Resources, TreeLayer));
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(1f / p.NoiseWavelength);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(p.NoiseOctaves);
        ulong salt = Salt(map, TreeLayer);
        int[] value = new int[count];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size) + x;
                if (eligible[i])
                {
                    int smooth = GridOps.UnitToFixed16((noise.GetNoise((double)x, y) * 0.5) + 0.5);
                    int white = (int)(PlacementOps.Hash(salt, i) & 0xFFFF);
                    value[i] = (smooth * p.NoisePermille) + (white * (1000 - p.NoisePermille));
                }
            }
        }

        ushort[] rank = new ushort[count];
        GridOps.RankPermille(value, eligible, rank);
        bool[] tree = new bool[count];
        for (int i = 0; i < count; i++)
        {
            tree[i] = eligible[i] && rank[i] < share[(int)map.BiomeArray[i]];
        }

        // Cellular automaton, double-buffered so the result does not depend on scan order.
        bool[] previous = new bool[count];
        for (int pass = 0; pass < p.AutomatonPasses; pass++)
        {
            Array.Copy(tree, previous, count);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size) + x;
                    if (!eligible[i])
                    {
                        continue;
                    }

                    int neighbours = CountNeighbours(previous, size, x, y);
                    tree[i] = previous[i] ? neighbours >= p.SurviveNeighbours : neighbours >= p.BirthNeighbours;
                }
            }
        }

        ulong stockSalt = salt ^ 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < count; i++)
        {
            if (tree[i] && world.ResourceKindArray[i] == ResourceKind.None)
            {
                Put(world, i, ResourceKind.Tree, PlacementOps.Hash(stockSalt, i), p.StockMin, p.StockMax);
            }
        }
    }

    private static int CountNeighbours(bool[] set, int size, int x, int y)
    {
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if ((uint)ny >= (uint)size)
            {
                continue;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx;
                if ((dx != 0 || dy != 0) && (uint)nx < (uint)size && set[(ny * size) + nx])
                {
                    n++;
                }
            }
        }

        return n;
    }

    private static void PlaceBerries(WorldMap world, bool[] open, BerryParams p)
    {
        TerrainMap map = world.Terrain;
        int size = map.Size;
        int[] chance = BiomeTable(p.Biomes, b => b.Biome, b => b.Permille, "Berries");
        ulong salt = Salt(map, BerryLayer);
        ulong stockSalt = salt ^ 0x9E3779B97F4A7C15UL;
        int d = p.EdgeDistance;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size) + x;
                Biome own = map.BiomeArray[i];
                if (!open[i] || world.ResourceKindArray[i] != ResourceKind.None || chance[(int)own] <= 0)
                {
                    continue;
                }

                ulong h = PlacementOps.Hash(salt, i);
                if (PlacementOps.Permille(h) < chance[(int)own] && NearOtherListedBiome(x, y, own))
                {
                    Put(world, i, ResourceKind.BerryBush, PlacementOps.Hash(stockSalt, i), p.StockMin, p.StockMax);
                }
            }
        }

        bool NearOtherListedBiome(int x, int y, Biome own)
        {
            for (int ny = Math.Max(0, y - d); ny <= Math.Min(size - 1, y + d); ny++)
            {
                for (int nx = Math.Max(0, x - d); nx <= Math.Min(size - 1, x + d); nx++)
                {
                    Biome other = map.BiomeArray[(ny * size) + nx];
                    if (other != own && other != Biome.Water && chance[(int)other] >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
