using Core.Content;
using Core.Determinism;

namespace Core.WorldGen.Placement;

/// <summary>
/// Ore and coal veins (P-21), run after the resource nodes (step 5) and before the points of interest (step 6). Like
/// <see cref="PoiStep"/>: one candidate per square cell (a jittered grid point from a per-cell hash) whose tile lies in
/// the biome mask and wins the biome's chance roll; the candidates are shuffled with the step's seeded stream and
/// accepted greedily when no accepted vein lies closer than the minimum spacing and the vein's disc holds enough mask
/// tiles. A vein replaces the resource nodes on its tiles.
/// </summary>
/// <remarks>
/// Everything about a vein except whether it is accepted (kind, radius, stock multiplier, ragged edge) comes from
/// hashes of its cell and tiles, so rejecting one candidate never changes another vein. The vein's whole stock is the
/// average node stock of its resource times the multiplier (<see cref="VeinParams.TotalStock"/>), split over its tiles
/// in row-major order: every tile gets the quotient, the first <c>stock % tiles</c> tiles one more.
/// </remarks>
internal static class VeinStep
{
    // Chunk y of the hash salts (the resource step uses -3 and the noise seeds -1).
    private const int SaltRow = -4;
    private const int CellLayer = 0;
    private const int VeinLayer = 1;
    private const int EdgeLayer = 2;

    public static void Run(WorldMap world, ResourceParams p)
    {
        VeinParams v = p.Veins;
        VeinKind[] kinds = ParseKinds(p);
        int totalWeight = kinds.Sum(k => k.Weight);
        int[] chance = ResourceStep.BiomeTable(v.Biomes, b => b.Biome, b => b.ChancePermille, "Veins");
        int[] minHeight = ResourceStep.BiomeTable(v.Biomes, b => b.Biome, b => b.MinHeight, "Veins");
        if (kinds.Length == 0)
        {
            return;
        }

        TerrainMap map = world.Terrain;
        int size = map.Size;
        ulong cellSalt = WorldGenSeeds.Derive(map.Seed, WorldGenStep.Veins, CellLayer, SaltRow);
        ulong veinSalt = WorldGenSeeds.Derive(map.Seed, WorldGenStep.Veins, VeinLayer, SaltRow);
        ulong edgeSalt = WorldGenSeeds.Derive(map.Seed, WorldGenStep.Veins, EdgeLayer, SaltRow);

        // Candidates: one jittered point per cell, in the mask and below its biome's chance.
        int cell = v.CellSize;
        int cells = (size + cell - 1) / cell;
        List<int> candidates = [];
        for (int c = 0; c < cells * cells; c++)
        {
            ulong h = PlacementOps.Hash(cellSalt, c);
            int x = ((c % cells) * cell) + (int)(h % (ulong)cell);
            int y = ((c / cells) * cell) + (int)((h >> 20) % (ulong)cell);
            if (x < size && y < size && InMask((y * size) + x) && PlacementOps.Permille(h) < chance[(int)map.BiomeArray[(y * size) + x]])
            {
                candidates.Add(c);
            }
        }

        Xoshiro256StarStar rng = WorldGenSeeds.CreateRng(map.Seed, WorldGenStep.Veins);
        PlacementOps.Shuffle(candidates, rng);
        var grid = new SpacingGrid(size, v.MinSpacing);
        List<Vein> placed = [];
        List<int> tiles = [];
        foreach (int c in candidates)
        {
            ulong h = PlacementOps.Hash(cellSalt, c);
            int cx = ((c % cells) * cell) + (int)(h % (ulong)cell);
            int cy = ((c / cells) * cell) + (int)((h >> 20) % (ulong)cell);
            if (!grid.IsFar(cx, cy, v.MinSpacing))
            {
                continue;
            }

            ulong vh = PlacementOps.Hash(veinSalt, c);
            int radius = PlacementOps.InRange(vh >> 16, v.RadiusMin, v.RadiusMax);
            CollectTiles(cx, cy, radius);
            if (tiles.Count < v.MinTiles)
            {
                continue;
            }

            VeinKind kind = kinds[PickKind(kinds, PlacementOps.InRange(vh, 0, totalWeight - 1))];
            int multiplier = PlacementOps.InRange(vh >> 32, kind.MultiplierMin, kind.MultiplierMax);
            int stock = (int)VeinParams.TotalStock(kind.Node, multiplier);
            int share = stock / tiles.Count;
            int extra = stock % tiles.Count;
            for (int t = 0; t < tiles.Count; t++)
            {
                world.ResourceKindArray[tiles[t]] = kind.Kind;
                world.ResourceAmountArray[tiles[t]] = (ushort)(share + (t < extra ? 1 : 0));
            }

            grid.Add(cx, cy);
            placed.Add(new Vein(kind.Kind, cx, cy, radius, tiles.Count, stock));
        }

        placed.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        world.Veins = placed;

        // Open land of a listed biome at its height that is not already part of a vein.
        bool InMask(int i)
        {
            int b = (int)map.BiomeArray[i];
            return (map.FeatureArray[i] & (TileFeatures.Water | TileFeatures.River)) == 0
                && minHeight[b] >= 0 && map.HeightArray[i] >= minHeight[b]
                && !WorldNames.IsVein(world.ResourceKindArray[i]);
        }

        // Mask tiles of the disc in row-major order; tiles of the outer ring may be left out (ragged edge).
        void CollectTiles(int cx, int cy, int radius)
        {
            tiles.Clear();
            long inner2 = (long)(radius - 1) * (radius - 1);
            int[] halfWidths = PlacementOps.DiscHalfWidths(radius);
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = cy + dy;
                if ((uint)y >= (uint)size)
                {
                    continue;
                }

                int w = halfWidths[dy + radius];
                for (int x = Math.Max(0, cx - w); x <= Math.Min(size - 1, cx + w); x++)
                {
                    int i = (y * size) + x;
                    bool ragged = PlacementOps.DistanceSquared(cx, cy, x, y) > inner2
                        && PlacementOps.Permille(PlacementOps.Hash(edgeSalt, i)) < v.EdgeJitterPermille;
                    if (!ragged && InMask(i))
                    {
                        tiles.Add(i);
                    }
                }
            }
        }
    }

    // Fails loudly on data the content checks would reject: unknown kinds, or stocks that do not fit a vein's tiles.
    private static VeinKind[] ParseKinds(ResourceParams p)
    {
        int side = (2 * p.Veins.RadiusMax) + 1;
        return p.Veins.Kinds
            .Select(k =>
            {
                ClusterResourceParams? node = WorldNames.TryParseVein(k.Kind, out ResourceKind kind)
                    ? kind == ResourceKind.OreVein ? p.Ore : p.Coal
                    : null;
                return node is not null && k.Weight > 0 && k.StockMultiplierMinPermille <= k.StockMultiplierMaxPermille
                    && VeinParams.TotalStock(node, k.StockMultiplierMaxPermille) <= ushort.MaxValue
                    && VeinParams.TotalStock(node, k.StockMultiplierMinPermille) >= (long)side * side
                    ? new VeinKind(kind, k.Weight, k.StockMultiplierMinPermille, k.StockMultiplierMaxPermille, node)
                    : throw new ArgumentException(
                        $"Vein kind '{k.Kind}' with weight {k.Weight} and multipliers {k.StockMultiplierMinPermille}–{k.StockMultiplierMaxPermille} is not valid.", nameof(p));
            })
            .ToArray();
    }

    private static int PickKind(VeinKind[] kinds, int roll)
    {
        int k = 0;
        while (k < kinds.Length - 1 && roll >= kinds[k].Weight)
        {
            roll -= kinds[k].Weight;
            k++;
        }

        return k;
    }

    private readonly record struct VeinKind(ResourceKind Kind, int Weight, int MultiplierMin, int MultiplierMax, ClusterResourceParams Node);
}
