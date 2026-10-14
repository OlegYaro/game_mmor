using Core.Content;
using Core.Determinism;

namespace Core.WorldGen.Placement;

/// <summary>
/// ADR 0006 step 6: points of interest (FR-WORLD-07, B7). Poisson-disk style dart throwing: buildable tiles away from
/// the edge are shuffled with the step's seeded stream and accepted when no accepted point lies closer than the
/// minimum spacing, until the target count (land tiles / tiles per point) is reached. The point's tile loses its
/// resource node.
/// </summary>
internal static class PoiStep
{
    public static void Run(WorldMap world, PoiParams p, bool[] buildable)
    {
        TerrainMap map = world.Terrain;
        int size = map.Size;
        (PoiKind Kind, int Weight)[] kinds = ParseKinds(p);
        int totalWeight = kinds.Sum(k => k.Weight);

        long land = 0;
        foreach (TileFeatures f in map.FeatureArray)
        {
            land += (f & TileFeatures.Water) == 0 ? 1 : 0;
        }

        int target = (int)((land + (p.TilesPerPoi / 2)) / p.TilesPerPoi);
        List<int> candidates = [];
        for (int y = p.EdgeMargin; y < size - p.EdgeMargin; y++)
        {
            for (int x = p.EdgeMargin; x < size - p.EdgeMargin; x++)
            {
                if (buildable[(y * size) + x])
                {
                    candidates.Add((y * size) + x);
                }
            }
        }

        Xoshiro256StarStar rng = WorldGenSeeds.CreateRng(map.Seed, WorldGenStep.PointsOfInterest);
        PlacementOps.Shuffle(candidates, rng);
        var grid = new SpacingGrid(size, p.MinSpacing);
        List<PointOfInterest> placed = [];
        foreach (int i in candidates)
        {
            if (placed.Count >= target)
            {
                break;
            }

            int x = i % size;
            int y = i / size;
            if (!grid.IsFar(x, y, p.MinSpacing))
            {
                continue;
            }

            grid.Add(x, y);
            placed.Add(new PointOfInterest(PickKind(kinds, rng.NextInt(totalWeight)), x, y));
            world.ResourceKindArray[i] = ResourceKind.None;
            world.ResourceAmountArray[i] = 0;
        }

        if (placed.Count < target)
        {
            world.WarningList.Add($"Points of interest: placed {placed.Count} of {target} (not enough flat land at spacing {p.MinSpacing}).");
        }

        placed.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        world.PointsOfInterest = placed;
    }

    private static (PoiKind, int)[] ParseKinds(PoiParams p)
    {
        if (p.Kinds.Count == 0)
        {
            throw new ArgumentException("At least one point of interest kind is required.", nameof(p));
        }

        return p.Kinds
            .Select(k => WorldNames.TryParse(k.Kind, out PoiKind kind) && k.Weight > 0
                ? (kind, k.Weight)
                : throw new ArgumentException($"Point of interest kind '{k.Kind}' with weight {k.Weight} is not valid.", nameof(p)))
            .ToArray();
    }

    private static PoiKind PickKind((PoiKind Kind, int Weight)[] kinds, int roll)
    {
        int k = 0;
        while (k < kinds.Length - 1 && roll >= kinds[k].Weight)
        {
            roll -= kinds[k].Weight;
            k++;
        }

        return kinds[k].Kind;
    }
}
