using Core.Content;

namespace Core.WorldGen.Placement;

/// <summary>
/// ADR 0006 step 7: the start (FR-WORLD-08, B1). Candidates lie on a grid away from the map edge and are tried in an
/// order shuffled by the step's seeded stream; the first that passes every check wins. Checks:
/// <list type="bullet">
/// <item>the tile: buildable (B2), of the start biome, without a resource node, no point of interest within the clearance;</item>
/// <item>building area: at least the required share of buildable tiles within the flat radius (B2);</item>
/// <item>start zone: enough tree, stone and water/river tiles within the zone radius;</item>
/// <item>room for every evil settlement site at its start distance (<see cref="SettlementStep.HasRoomForEvil"/>).</item>
/// </list>
/// When no candidate passes, the checks are relaxed in B1's order (water, then the evil room) and a warning is
/// recorded. Beyond B1, a fallback keeps generation total: the first candidate whose tile qualifies, else the land
/// tile nearest to the map centre.
/// </summary>
internal static class StartStep
{
    public static void Run(WorldMap world, StartParams p, bool[] buildable, SettlementStep settlements)
    {
        TerrainMap map = world.Terrain;
        int size = map.Size;
        if (!BiomeNames.TryParse(p.Biome, out Biome startBiome) || startBiome is Biome.None or Biome.Water)
        {
            throw new ArgumentException($"Start biome '{p.Biome}' is not a land biome.", nameof(p));
        }

        // Candidate tiles: grid points within the edge margin whose tile qualifies, in seeded order.
        bool[] poiMask = new bool[map.TileCount];
        foreach (PointOfInterest poi in world.PointsOfInterest)
        {
            poiMask[(poi.Y * size) + poi.X] = true;
        }

        var pois = new DiscCounter(poiMask, size);
        int[] clearance = PlacementOps.DiscHalfWidths(p.PoiClearance);
        List<int> candidates = [];
        int offset = p.CandidateSpacing / 2;
        for (int y = p.EdgeMargin + offset; y < size - p.EdgeMargin; y += p.CandidateSpacing)
        {
            for (int x = p.EdgeMargin + offset; x < size - p.EdgeMargin; x += p.CandidateSpacing)
            {
                int i = (y * size) + x;
                if (buildable[i] && map.BiomeArray[i] == startBiome && world.ResourceKindArray[i] == ResourceKind.None
                    && pois.Count(x, y, clearance) == 0)
                {
                    candidates.Add(i);
                }
            }
        }

        PlacementOps.Shuffle(candidates, WorldGenSeeds.CreateRng(map.Seed, WorldGenStep.Start));

        int[] flatDisc = PlacementOps.DiscHalfWidths(p.FlatRadius);
        int[] zone = PlacementOps.DiscHalfWidths(p.ZoneRadius);
        var flat = new DiscCounter(buildable, size);
        var trees = new DiscCounter(Mask(world, i => world.ResourceKindArray[i] == ResourceKind.Tree), size);
        var stone = new DiscCounter(Mask(world, i => world.ResourceKindArray[i] == ResourceKind.Stone), size);
        var water = new DiscCounter(Mask(world, i => (map.FeatureArray[i] & (TileFeatures.Water | TileFeatures.River)) != 0), size);

        // Per candidate: 0 = fails the hard checks, 1 = passes all but water, 2 = passes all including water.
        int[] grade = new int[candidates.Count];
        for (int k = 0; k < candidates.Count; k++)
        {
            int x = candidates[k] % size;
            int y = candidates[k] / size;
            bool hard = flat.Count(x, y, flatDisc) * 1000L >= (long)p.MinFlatPermille * flat.Area(x, y, flatDisc)
                && trees.Count(x, y, zone) >= p.MinTreeTiles
                && stone.Count(x, y, zone) >= p.MinStoneTiles;
            grade[k] = !hard ? 0 : water.Count(x, y, zone) >= p.MinWaterTiles ? 2 : 1;
        }

        StartRelaxation[] levels = [StartRelaxation.None, StartRelaxation.Water, StartRelaxation.EvilDistance];
        foreach (StartRelaxation level in levels)
        {
            int needGrade = level == StartRelaxation.None ? 2 : 1;
            for (int k = 0; k < candidates.Count; k++)
            {
                if (grade[k] < needGrade)
                {
                    continue;
                }

                var start = new StartPoint(candidates[k] % size, candidates[k] / size);
                if (level == StartRelaxation.EvilDistance || settlements.HasRoomForEvil(start))
                {
                    Choose(world, start, level, p);
                    return;
                }
            }
        }

        if (candidates.Count > 0)
        {
            Choose(world, new StartPoint(candidates[0] % size, candidates[0] / size), StartRelaxation.Fallback, p);
            return;
        }

        Choose(world, NearestLandToCentre(map), StartRelaxation.Fallback, p);
    }

    private static void Choose(WorldMap world, StartPoint start, StartRelaxation level, StartParams p)
    {
        world.Start = start;
        world.StartRelaxation = level;
        string? warning = level switch
        {
            StartRelaxation.Water =>
                $"Start: no candidate has {p.MinWaterTiles}+ water tiles within {p.ZoneRadius}; the water check was relaxed (B1).",
            StartRelaxation.EvilDistance =>
                $"Start: no candidate leaves room for every evil settlement; the water and evil-room checks were relaxed (B1).",
            StartRelaxation.Fallback =>
                $"Start: no candidate passes the B1 checks even relaxed; fell back to ({start.X}, {start.Y}).",
            _ => null,
        };
        if (warning is not null)
        {
            world.WarningList.Add(warning);
        }
    }

    private static bool[] Mask(WorldMap world, Func<int, bool> predicate)
    {
        bool[] mask = new bool[world.TileCount];
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = predicate(i);
        }

        return mask;
    }

    // Land (not water, not river) tile nearest to the centre, lowest index on ties; the centre itself on an all-water map.
    private static StartPoint NearestLandToCentre(TerrainMap map)
    {
        int size = map.Size;
        int c = size / 2;
        long best = long.MaxValue;
        var result = new StartPoint(c, c);
        for (int i = 0; i < map.TileCount; i++)
        {
            long d = PlacementOps.DistanceSquared(i % size, i / size, c, c);
            if ((map.FeatureArray[i] & (TileFeatures.Water | TileFeatures.River)) == 0 && d < best)
            {
                best = d;
                result = new StartPoint(i % size, i / size);
            }
        }

        return result;
    }
}
