using Core.Content;
using Core.Determinism;

namespace Core.WorldGen.Placement;

/// <summary>
/// ADR 0006 step 8: reserved settlement sites (FR-WORLD-09). A clearing is a centre tile whose whole square footprint
/// is buildable (B2), keeps the edge margin and has no point of interest within the clearance. Clearings are shuffled
/// once with the step's seeded stream. Factions are placed greedily, evil ones first: each tries the clearings in its
/// preferred biome, then the rest, and takes those at least its start distance from the start (the mask that
/// guarantees the evil distance by construction) and the spacing from every placed site.
/// </summary>
/// <remarks>
/// The start step (7) asks <see cref="HasRoomForEvil"/> for its candidates. That runs exactly the evil part of
/// <see cref="Run"/>, so a start that passed the check gets every evil site.
/// </remarks>
internal sealed class SettlementStep
{
    private readonly WorldMap _world;
    private readonly SettlementParams _p;
    private readonly List<FactionPlan> _plans;

    public SettlementStep(WorldMap world, SettlementParams p, bool[] buildable)
    {
        _world = world;
        _p = p;
        TerrainMap map = world.Terrain;
        List<int> clearings = FindClearings(world, p, buildable);
        PlacementOps.Shuffle(clearings, WorldGenSeeds.CreateRng(map.Seed, WorldGenStep.Settlements));

        _plans = [];
        foreach (FactionSitesDef def in p.Factions)
        {
            if (!WorldNames.TryParse(def.Faction, out Faction faction))
            {
                throw new ArgumentException($"Unknown settlement faction '{def.Faction}'.", nameof(p));
            }

            if (!BiomeNames.TryParse(def.PreferredBiome, out Biome preferred))
            {
                throw new ArgumentException($"Faction '{def.Faction}' prefers unknown biome '{def.PreferredBiome}'.", nameof(p));
            }

            // Preferred biome first, both parts in the shuffled order.
            List<int> order = clearings.Where(i => map.BiomeArray[i] == preferred)
                .Concat(clearings.Where(i => map.BiomeArray[i] != preferred))
                .ToList();
            int target = def.PerMillionTiles == 0 ? 0 : (int)Math.Max(1L, (long)def.PerMillionTiles * map.TileCount / 1_000_000);
            _plans.Add(new FactionPlan(faction, target, def.MinStartDistance, order));
        }

        // Stable: evil factions first, each group in data order.
        _plans = [.. _plans.Where(f => WorldNames.IsEvil(f.Faction)), .. _plans.Where(f => !WorldNames.IsEvil(f.Faction))];
    }

    /// <summary>True when every evil faction gets its full number of sites with the start at <paramref name="start"/>.</summary>
    public bool HasRoomForEvil(StartPoint start)
    {
        List<SettlementSite> placed = [];
        foreach (FactionPlan plan in _plans.Where(f => WorldNames.IsEvil(f.Faction)))
        {
            if (Place(plan, start, placed) < plan.Target)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Places every faction's sites around the chosen start, clears their footprints and records shortfalls.</summary>
    public void Run(StartPoint start)
    {
        List<SettlementSite> placed = [];
        foreach (FactionPlan plan in _plans)
        {
            int got = Place(plan, start, placed);
            if (got < plan.Target)
            {
                _world.WarningList.Add(
                    $"Settlements: placed {got} of {plan.Target} {WorldNames.ToName(plan.Faction)} sites (no more clearings {plan.MinStartDistance}+ tiles from the start and {_p.MinSpacing}+ from other sites).");
            }
        }

        int size = _world.Size;
        int half = _p.SiteSize / 2;
        foreach (SettlementSite site in placed)
        {
            for (int y = site.Y - half; y <= site.Y + half; y++)
            {
                Array.Clear(_world.ResourceKindArray, (y * size) + site.X - half, _p.SiteSize);
                Array.Clear(_world.ResourceAmountArray, (y * size) + site.X - half, _p.SiteSize);
            }
        }

        placed.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        _world.SettlementSites = placed;
    }

    // Greedy placement of one faction; appends to `placed` and returns how many it added.
    private int Place(FactionPlan plan, StartPoint start, List<SettlementSite> placed)
    {
        int size = _world.Size;
        long startDistance2 = (long)plan.MinStartDistance * plan.MinStartDistance;
        long spacing2 = (long)_p.MinSpacing * _p.MinSpacing;
        int added = 0;
        foreach (int i in plan.Order)
        {
            if (added >= plan.Target)
            {
                break;
            }

            int x = i % size;
            int y = i / size;
            if (PlacementOps.DistanceSquared(x, y, start.X, start.Y) < startDistance2
                || placed.Exists(s => PlacementOps.DistanceSquared(x, y, s.X, s.Y) < spacing2))
            {
                continue;
            }

            placed.Add(new SettlementSite(plan.Faction, x, y));
            added++;
        }

        return added;
    }

    private static List<int> FindClearings(WorldMap world, SettlementParams p, bool[] buildable)
    {
        TerrainMap map = world.Terrain;
        int size = map.Size;
        int half = p.SiteSize / 2;
        int area = p.SiteSize * p.SiteSize;
        bool[] poi = new bool[map.TileCount];
        foreach (PointOfInterest point in world.PointsOfInterest)
        {
            poi[(point.Y * size) + point.X] = true;
        }

        var flat = new AreaSum(buildable, size);
        var points = new AreaSum(poi, size);
        int reach = half + p.PoiClearance;
        List<int> result = [];
        int lo = half + p.EdgeMargin;
        int hi = size - 1 - half - p.EdgeMargin;
        for (int y = lo; y <= hi; y++)
        {
            for (int x = lo; x <= hi; x++)
            {
                if (flat.Sum(x - half, y - half, x + half, y + half) == area
                    && points.Sum(x - reach, y - reach, x + reach, y + reach) == 0)
                {
                    result.Add((y * size) + x);
                }
            }
        }

        return result;
    }

    private sealed record FactionPlan(Faction Faction, int Target, int MinStartDistance, List<int> Order);
}

/// <summary>Summed-area table of a mask: set tiles in any axis-aligned rectangle in O(1).</summary>
internal sealed class AreaSum
{
    private readonly int _size;
    private readonly int[] _table; // (size + 1)^2, _table[(y + 1) * (size + 1) + x + 1] = set tiles in [0..x] x [0..y].

    public AreaSum(bool[] mask, int size)
    {
        _size = size;
        int stride = size + 1;
        _table = new int[stride * stride];
        for (int y = 0; y < size; y++)
        {
            int rowSum = 0;
            for (int x = 0; x < size; x++)
            {
                rowSum += mask[(y * size) + x] ? 1 : 0;
                _table[((y + 1) * stride) + x + 1] = _table[(y * stride) + x + 1] + rowSum;
            }
        }
    }

    /// <summary>Set tiles in the inclusive rectangle, clipped to the map.</summary>
    public int Sum(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(_size - 1, x1);
        y1 = Math.Min(_size - 1, y1);
        if (x0 > x1 || y0 > y1)
        {
            return 0;
        }

        int stride = _size + 1;
        return _table[((y1 + 1) * stride) + x1 + 1] - _table[(y0 * stride) + x1 + 1] - _table[((y1 + 1) * stride) + x0] + _table[(y0 * stride) + x0];
    }
}
