using Core.Content;
using Core.Determinism;

namespace Core.WorldGen.Terrain;

/// <summary>
/// ADR 0006 step 4: rivers. Priority-flood with epsilon (Barnes et al. 2014) over the continuous height, seeded from
/// the ocean, gives a depression-free surface where every land tile has a strictly lower 8-neighbour. D8 flow follows
/// the lowest neighbour. Sources are mountain tiles picked in a seeded random order with a minimum spacing; each
/// river is traced downstream until it reaches standing water or joins an earlier river.
/// </summary>
/// <remarks>
/// Guarantee: following <see cref="TerrainMap.RiverDirections"/> from any river tile visits only river tiles and ends
/// on a water tile, strictly descending on the filled surface (so it cannot loop). Lakes are flooded like land, so a
/// river may end in a lake.
/// </remarks>
internal static class RiverStep
{
    public static void Run(TerrainMap map, RiverParams p)
    {
        int size = map.Size;
        int count = map.TileCount;
        TileFeatures[] features = map.FeatureArray;
        int[] filled = PriorityFlood(map, p.MeanderJitter);

        int target = p.SourcesPerMillionTiles == 0 ? 0 : (int)Math.Max(1L, (long)p.SourcesPerMillionTiles * count / 1_000_000);
        if (target == 0)
        {
            return;
        }

        List<int> candidates = [];
        for (int i = 0; i < count; i++)
        {
            if (map.BiomeArray[i] == Biome.Mountain && (features[i] & TileFeatures.Water) == 0)
            {
                candidates.Add(i);
            }
        }

        Xoshiro256StarStar rng = WorldGenSeeds.CreateRng(map.Seed, WorldGenStep.Rivers);
        for (int k = candidates.Count - 1; k > 0; k--)
        {
            int j = rng.NextInt(k + 1);
            (candidates[k], candidates[j]) = (candidates[j], candidates[k]);
        }

        List<int> sources = [];
        long spacingSquared = (long)p.MinSourceSpacing * p.MinSourceSpacing;
        foreach (int source in candidates)
        {
            if (sources.Count >= target)
            {
                break;
            }

            if ((features[source] & TileFeatures.River) != 0 || !FarFromAll(source, sources, size, spacingSquared)
                || PathLength(map, filled, source) < p.MinLength)
            {
                continue;
            }

            sources.Add(source);
            Trace(map, filled, source);
        }
    }

    // Filled height per tile: max(elevation, spill height of the flood neighbour + 1 + jitter). Ocean tiles keep their
    // elevation. The jitter is a hash of (seed, tile), so the result does not depend on the queue order.
    internal static int[] PriorityFlood(TerrainMap map, int jitter)
    {
        ulong salt = WorldGenSeeds.Derive(map.Seed, WorldGenStep.Rivers, 0, -2);
        int size = map.Size;
        int count = map.TileCount;
        TileFeatures[] features = map.FeatureArray;
        ushort[] elevation = map.ElevationArray;
        int[] filled = new int[count];
        bool[] done = new bool[count];
        var open = new PriorityQueue<int, long>();
        for (int i = 0; i < count; i++)
        {
            if ((features[i] & TileFeatures.Ocean) != 0)
            {
                filled[i] = elevation[i];
                done[i] = true;
            }
        }

        // Only ocean tiles next to non-ocean ones can spill inland.
        for (int i = 0; i < count; i++)
        {
            if (done[i] && HasNeighbour(i, size, j => !done[j]))
            {
                open.Enqueue(i, Key(filled[i], i));
            }
        }

        while (open.TryDequeue(out int i, out _))
        {
            int x = i % size;
            int y = i / size;
            foreach ((int dx, int dy) in TerrainMap.Directions)
            {
                int nx = x + dx;
                int ny = y + dy;
                if ((uint)nx >= (uint)size || (uint)ny >= (uint)size)
                {
                    continue;
                }

                int j = (ny * size) + nx;
                if (done[j])
                {
                    continue;
                }

                done[j] = true;
                int rise = 1 + (int)(SplitMix64.Mix(salt ^ (ulong)j) % (ulong)(jitter + 1));
                filled[j] = Math.Max(elevation[j], filled[i] + rise);
                open.Enqueue(j, Key(filled[j], j));
            }
        }

        return filled;
    }

    // Lowest 8-neighbour on the filled surface (first in Directions order on ties), as a direction index; -1 if none is lower.
    internal static int Downhill(int i, int size, int[] filled)
    {
        int x = i % size;
        int y = i / size;
        int best = -1;
        int bestHeight = filled[i];
        for (int d = 0; d < TerrainMap.Directions.Count; d++)
        {
            (int dx, int dy) = TerrainMap.Directions[d];
            int nx = x + dx;
            int ny = y + dy;
            if ((uint)nx < (uint)size && (uint)ny < (uint)size && filled[(ny * size) + nx] < bestHeight)
            {
                bestHeight = filled[(ny * size) + nx];
                best = d;
            }
        }

        return best;
    }

    private static int Step(int i, int size, int direction)
    {
        (int dx, int dy) = TerrainMap.Directions[direction];
        return i + (dy * size) + dx;
    }

    private static int PathLength(TerrainMap map, int[] filled, int source)
    {
        int length = 0;
        int i = source;
        while ((map.FeatureArray[i] & TileFeatures.Water) == 0)
        {
            length++;
            i = Step(i, map.Size, NextDirection(i, map.Size, filled));
        }

        return length;
    }

    // Marks tiles from a (non-river) source down to water, stopping where it joins an earlier river.
    private static void Trace(TerrainMap map, int[] filled, int source)
    {
        int i = source;
        while ((map.FeatureArray[i] & (TileFeatures.Water | TileFeatures.River)) == 0)
        {
            int direction = NextDirection(i, map.Size, filled);
            map.FeatureArray[i] |= TileFeatures.River;
            map.RiverDirectionArray[i] = (byte)direction;
            i = Step(i, map.Size, direction);
        }
    }

    private static int NextDirection(int i, int size, int[] filled)
    {
        int direction = Downhill(i, size, filled);
        return direction >= 0
            ? direction
            : throw new InvalidOperationException($"Tile {i} has no lower neighbour after priority-flood; the flood did not reach it.");
    }

    private static bool FarFromAll(int tile, List<int> sources, int size, long spacingSquared)
    {
        int x = tile % size;
        int y = tile / size;
        foreach (int other in sources)
        {
            long dx = x - (other % size);
            long dy = y - (other / size);
            if ((dx * dx) + (dy * dy) < spacingSquared)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNeighbour(int i, int size, Func<int, bool> predicate)
    {
        int x = i % size;
        int y = i / size;
        foreach ((int dx, int dy) in TerrainMap.Directions)
        {
            int nx = x + dx;
            int ny = y + dy;
            if ((uint)nx < (uint)size && (uint)ny < (uint)size && predicate((ny * size) + nx))
            {
                return true;
            }
        }

        return false;
    }

    // Total order: filled height first, tile index breaks ties, so dequeue order never depends on heap internals.
    private static long Key(int height, int index) => ((long)height << 32) | (uint)index;
}
