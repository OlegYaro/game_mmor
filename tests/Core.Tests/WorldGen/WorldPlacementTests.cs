using Core.Content;
using Core.Save;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>ADR 0006 steps 5–9 on the real data: determinism, invariants over many seeds, B1 relaxation, map hash.</summary>
public sealed class WorldPlacementTests
{
    private static WorldGenParamsDef P => WorldGenTestData.Params;

    // ---- Determinism and the map hash (FR-WORLD-01) ----

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void SameRequestGivesTheSameWorld()
    {
        WorldMap a = WorldGenTestData.GenerateWorld(5, 128);
        WorldMap b = WorldGenTestData.GenerateWorld(5, 128);

        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.True(a.ResourceKinds.SequenceEqual(b.ResourceKinds));
        Assert.True(a.ResourceAmounts.SequenceEqual(b.ResourceAmounts));
        Assert.Equal(a.PointsOfInterest, b.PointsOfInterest);
        Assert.Equal(a.Start, b.Start);
        Assert.Equal(a.SettlementSites, b.SettlementSites);
        Assert.Equal(a.Warnings, b.Warnings);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void DifferentSeedsGiveDifferentWorlds() =>
        Assert.Equal(20, Enumerable.Range(1, 20).Select(s => WorldGenTestData.InvariantWorld(s).ComputeHash()).Distinct().Count());

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void GenerateKeepsTheTerrainStageUnchanged()
    {
        WorldMap world = WorldGenTestData.InvariantWorld(7);

        Assert.Equal(WorldGenTestData.Generate(7, WorldGenTestData.InvariantSize).ComputeHash(), world.Terrain.ComputeHash());
        Assert.Equal(7UL, world.Seed);
        Assert.Equal(WorldGenTestData.InvariantSize, world.Size);
        Assert.Equal(world.Size * world.Size, world.TileCount);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void MapHashCoversEveryLayerAndList()
    {
        WorldMap world = WorldGenTestData.GenerateWorld(9, 128);
        ulong original = world.ComputeHash();
        int tile = world.ResourceKinds.IndexOf(ResourceKind.Tree);
        List<ulong> hashes = [];

        void Mutate(Action change, Action undo)
        {
            change();
            hashes.Add(world.ComputeHash());
            undo();
            Assert.Equal(original, world.ComputeHash());
        }

        Mutate(() => world.ResourceKindArray[tile] = ResourceKind.Stone, () => world.ResourceKindArray[tile] = ResourceKind.Tree);
        Mutate(() => world.ResourceAmountArray[tile]++, () => world.ResourceAmountArray[tile]--);
        IReadOnlyList<PointOfInterest> pois = world.PointsOfInterest;
        Mutate(() => world.PointsOfInterest = [.. pois.Skip(1)], () => world.PointsOfInterest = pois);
        Mutate(() => world.PointsOfInterest = [pois[0] with { Kind = pois[0].Kind == PoiKind.Lair ? PoiKind.Chest : PoiKind.Lair }, .. pois.Skip(1)], () => world.PointsOfInterest = pois);
        StartPoint start = world.Start;
        Mutate(() => world.Start = start with { X = start.X + 1 }, () => world.Start = start);
        StartRelaxation relaxation = world.StartRelaxation;
        Mutate(() => world.StartRelaxation = relaxation == StartRelaxation.Water ? StartRelaxation.None : StartRelaxation.Water, () => world.StartRelaxation = relaxation);
        IReadOnlyList<SettlementSite> sites = world.SettlementSites;
        Mutate(() => world.SettlementSites = [.. sites, new SettlementSite(Faction.Human, 1, 1)], () => world.SettlementSites = sites);
        Mutate(() => world.Terrain.HeightArray[0] ^= 1, () => world.Terrain.HeightArray[0] ^= 1);

        Assert.DoesNotContain(original, hashes);
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
        Assert.Throws<ArgumentNullException>(() => world.WriteState(null!));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-01")]
    public void WorldVersionIsWhatSavesRecord()
    {
        WorldMap world = WorldGenTestData.InvariantWorld(1);

        // Saves store the generator version and rebuild the world from the seed with it (ADR 0006, ADR 0007).
        Assert.Equal(WorldGenerator.Version, world.Version);
        Assert.Equal(SaveFormat.WorldGenVersion, world.Version);
        Assert.True(SaveFormat.IsLoadableWorldGenVersion(world.Version));
        Assert.Throws<NotSupportedException>(() => WorldGenerator.Generate(new WorldGenRequest(1, 64, P, WorldGenerator.Version + 1)));
        Assert.Throws<ArgumentNullException>(() => WorldGenerator.Generate(null!));
    }

    // ---- Invariants ----

    [Theory]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-WORLD-07")]
    [Trait("Req", "FR-WORLD-08")]
    [Trait("Req", "FR-WORLD-09")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void GoldenWorldsSatisfyAllInvariants(int index) => WorldInvariants.AssertAll(WorldGenTestData.GoldenWorld(index), P);

    [Theory]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-WORLD-07")]
    [Trait("Req", "FR-WORLD-08")]
    [Trait("Req", "FR-WORLD-09")]
    [MemberData(nameof(WorldGenTestData.InvariantSeeds), MemberType = typeof(WorldGenTestData))]
    public void ManySeedsSatisfyAllInvariants(int seed) => WorldInvariants.AssertAll(WorldGenTestData.InvariantWorld(seed), P);

    [Theory]
    [Trait("Req", "FR-WORLD-07")]
    [MemberData(nameof(WorldGenTestData.InvariantSeeds), MemberType = typeof(WorldGenTestData))]
    public void PointOfInterestDensityIsAboutOnePer80x80LandTiles(int seed) => AssertPoiDensity(WorldGenTestData.InvariantWorld(seed));

    [Theory]
    [Trait("Req", "FR-WORLD-07")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void GoldenWorldsHaveAllThreePointKindsAtTheRightDensity(int index)
    {
        WorldMap world = WorldGenTestData.GoldenWorld(index);

        AssertPoiDensity(world);
        Assert.Equal(WorldNames.PoiKinds, world.PointsOfInterest.Select(p => p.Kind).Distinct().Order());
    }

    [Theory]
    [Trait("Req", "FR-WORLD-04")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void ResourcesFormBiomeClusters(int index)
    {
        WorldMap world = WorldGenTestData.GoldenWorld(index);
        int[] counts = world.CountResources();
        Assert.Equal(world.TileCount, counts.Sum());
        Assert.All(WorldNames.Resources, kind => Assert.True(counts[(int)kind] > 0, $"No {kind} on the map."));
        Assert.True(counts[(int)ResourceKind.Ore] < counts[(int)ResourceKind.Stone], "Ore must be rarer than stone.");

        long[] trees = new long[BiomeNames.Generated.Count + 1];
        long[] tiles = new long[BiomeNames.Generated.Count + 1];
        for (int i = 0; i < world.TileCount; i++)
        {
            Biome biome = world.Terrain.Biomes[i];
            tiles[(int)biome]++;
            trees[(int)biome] += world.ResourceKinds[i] == ResourceKind.Tree ? 1 : 0;

            // With the real data ore is a mountain resource only.
            Assert.True(world.ResourceKinds[i] != ResourceKind.Ore || biome == Biome.Mountain, $"Ore outside the mountains on tile {i}.");
        }

        // Forests are dense, meadows have only groves.
        Assert.InRange(trees[(int)Biome.Forest] * 1000 / tiles[(int)Biome.Forest], 450, 900);
        Assert.InRange(trees[(int)Biome.Meadow] * 1000 / tiles[(int)Biome.Meadow], 10, 200);
    }

    [Theory]
    [Trait("Req", "FR-WORLD-08")]
    [MemberData(nameof(WorldGenTestData.InvariantSeeds), MemberType = typeof(WorldGenTestData))]
    public void StartZoneHasForestStoneAndWaterOrAWarning(int seed)
    {
        WorldMap world = WorldGenTestData.InvariantWorld(seed);
        StartParams p = P.Start;
        StartPoint s = world.Start;
        bool forest = WorldInvariants.CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => world.ResourceKinds[i] == ResourceKind.Tree) >= p.MinTreeTiles;
        bool stone = WorldInvariants.CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => world.ResourceKinds[i] == ResourceKind.Stone) >= p.MinStoneTiles;
        bool water = WorldInvariants.CountInDisc(world, s.X, s.Y, p.ZoneRadius, i => !WorldInvariants.IsOpen(world, i)) >= p.MinWaterTiles;

        Assert.True((forest && stone && water) || world.Warnings.Any(w => w.StartsWith("Start:", StringComparison.Ordinal)));
        Assert.DoesNotContain(world.SettlementSites, site => WorldNames.IsEvil(site.Faction)
            && WorldInvariants.Distance2(s.X, s.Y, site.X, site.Y) < (long)WorldInvariants.EvilMinStartDistance * WorldInvariants.EvilMinStartDistance);
    }

    [Theory]
    [Trait("Req", "FR-WORLD-09")]
    [MemberData(nameof(WorldGenTestData.GoldenIndices), MemberType = typeof(WorldGenTestData))]
    public void GoldenWorldsGetEveryFactionsSitesWithoutRelaxation(int index)
    {
        WorldMap world = WorldGenTestData.GoldenWorld(index);

        Assert.Equal(StartRelaxation.None, world.StartRelaxation);
        Assert.Empty(world.Warnings);
        foreach (FactionSitesDef def in P.Settlements.Factions)
        {
            int target = (int)Math.Max(1L, (long)def.PerMillionTiles * world.TileCount / 1_000_000);
            Assert.Equal(target, world.SettlementSites.Count(s => WorldNames.ToName(s.Faction) == def.Faction));
        }
    }

    [Fact]
    [Trait("Req", "FR-WORLD-09")]
    public void RealDataKeepsEvilSitesAtLeast240TilesAway()
    {
        FactionSitesDef orc = P.Settlements.Factions.Single(f => f.Faction == "orc");

        // P-07: FR-WORLD-09's 120 tiles and the B1 zone radius of 40 were set on the 64x32 grid; lengths double on 32x16.
        Assert.True(orc.MinStartDistance >= WorldInvariants.EvilMinStartDistance);
        Assert.Equal(80, P.Start.ZoneRadius);
    }

    // ---- B1 relaxation and shortfalls ----

    [Fact]
    [Trait("Req", "FR-WORLD-08")]
    public void MissingWaterRelaxesTheWaterCheckFirst()
    {
        WorldGenParamsDef p = P with { Start = P.Start with { MinWaterTiles = 100000 } };
        WorldMap world = WorldGenTestData.GenerateWorld(4, WorldGenTestData.InvariantSize, p);

        Assert.Equal(StartRelaxation.Water, world.StartRelaxation);
        Assert.Contains(world.Warnings, w => w.Contains("water check was relaxed", StringComparison.Ordinal));
        WorldInvariants.AssertAll(world, p);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-08")]
    [Trait("Req", "FR-WORLD-09")]
    public void NoRoomForEvilSitesRelaxesThatNextButNeverTheDistance()
    {
        WorldGenParamsDef p = P with
        {
            Settlements = P.Settlements with
            {
                Factions = [.. P.Settlements.Factions.Select(f => f.Faction == "orc" ? f with { PerMillionTiles = 1000 } : f)],
            },
        };
        WorldMap world = WorldGenTestData.GenerateWorld(4, WorldGenTestData.InvariantSize, p);

        Assert.Equal(StartRelaxation.EvilDistance, world.StartRelaxation);
        Assert.Contains(world.Warnings, w => w.Contains("evil-room", StringComparison.Ordinal));
        Assert.Contains(world.Warnings, w => w.StartsWith("Settlements: placed", StringComparison.Ordinal) && w.Contains(" orc sites", StringComparison.Ordinal));
        Assert.Contains(world.SettlementSites, s => s.Faction == Faction.Orc);
        WorldInvariants.AssertAll(world, p);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-08")]
    public void ImpossibleZoneFallsBackToTheFirstQualifyingCandidate()
    {
        WorldGenParamsDef p = P with { Start = P.Start with { MinTreeTiles = 100000 } };
        WorldMap world = WorldGenTestData.GenerateWorld(4, WorldGenTestData.InvariantSize, p);

        Assert.Equal(StartRelaxation.Fallback, world.StartRelaxation);
        Assert.Contains(world.Warnings, w => w.Contains("fell back", StringComparison.Ordinal));
        Assert.True(WorldInvariants.IsBuildable(world, world.Start.X, world.Start.Y));
        Assert.Equal(Biome.Meadow, world.Terrain.GetBiome(world.Start.X, world.Start.Y));
        WorldInvariants.AssertAll(world, p);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-08")]
    public void NoCandidateAtAllFallsBackToLandNearestTheCentre()
    {
        WorldGenParamsDef p = P with { Start = P.Start with { EdgeMargin = 1024 } };
        WorldMap world = WorldGenTestData.GenerateWorld(4, 128, p);
        StartPoint s = world.Start;
        long best = long.MaxValue;
        for (int i = 0; i < world.TileCount; i++)
        {
            if (WorldInvariants.IsOpen(world, i))
            {
                best = Math.Min(best, WorldInvariants.Distance2(i % 128, i / 128, 64, 64));
            }
        }

        Assert.Equal(StartRelaxation.Fallback, world.StartRelaxation);
        Assert.Equal(best, WorldInvariants.Distance2(s.X, s.Y, 64, 64));
        WorldInvariants.AssertAll(world, p);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-07")]
    public void TooWideSpacingPlacesFewerPointsAndWarns()
    {
        // 400 tiles on a 512² map, as 200 tiles were on the former 256² map (P-07): only a few points fit.
        WorldGenParamsDef p = P with { PointsOfInterest = P.PointsOfInterest with { MinSpacing = 400 } };
        WorldMap world = WorldGenTestData.GenerateWorld(4, WorldGenTestData.InvariantSize, p);

        Assert.InRange(world.PointsOfInterest.Count, 1, 4);
        Assert.Contains(world.Warnings, w => w.StartsWith("Points of interest: placed", StringComparison.Ordinal));
        WorldInvariants.AssertAll(world, p);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-09")]
    public void FactionWithoutSitesGetsNoneAndNoWarning()
    {
        WorldGenParamsDef p = P with
        {
            Settlements = P.Settlements with { Factions = [.. P.Settlements.Factions.Select(f => f with { PerMillionTiles = 0 })] },
        };
        WorldMap world = WorldGenTestData.GenerateWorld(4, 128, p);

        Assert.Empty(world.SettlementSites);
        Assert.DoesNotContain(world.Warnings, w => w.StartsWith("Settlements", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    public void DisabledClustersAndBerriesLeaveOnlyTrees()
    {
        WorldGenParamsDef p = P with
        {
            Resources = P.Resources with
            {
                Stone = P.Resources.Stone with { Biomes = [] },
                Ore = P.Resources.Ore with { Biomes = [new ClusterBiomeDef("mountain", 0, 3)] },
                Berries = P.Resources.Berries with { Biomes = [new BiomeShareDef("meadow", 1000)] },
            },
            Start = P.Start with { MinStoneTiles = 0 },
        };
        int[] counts = WorldGenTestData.GenerateWorld(4, 128, p).CountResources();

        Assert.Equal(0, counts[(int)ResourceKind.Stone]);
        Assert.Equal(0, counts[(int)ResourceKind.Ore]);
        Assert.Equal(0, counts[(int)ResourceKind.BerryBush]); // a lone listed biome has no border with another listed one
        Assert.True(counts[(int)ResourceKind.Tree] > 0);
    }

    // ---- Unknown names in the parameters fail loudly (the generator can run on unvalidated data) ----

    public static TheoryData<string> BadParameterCases => ["resourceBiome", "poiKind", "noPoiKinds", "faction", "preferredBiome", "startBiome"];

    [Theory]
    [Trait("Req", "FR-MOD-01")]
    [MemberData(nameof(BadParameterCases))]
    public void UnknownNamesInParametersFailLoudly(string field)
    {
        WorldGenParamsDef p = field switch
        {
            "resourceBiome" => P with { Resources = P.Resources with { Ore = P.Resources.Ore with { Biomes = [new ClusterBiomeDef("water", 100, 3)] } } },
            "poiKind" => P with { PointsOfInterest = P.PointsOfInterest with { Kinds = [new PoiKindDef("portal", 1)] } },
            "noPoiKinds" => P with { PointsOfInterest = P.PointsOfInterest with { Kinds = [] } },
            "faction" => P with { Settlements = P.Settlements with { Factions = [new FactionSitesDef("elf", 10, 200, "forest")] } },
            "preferredBiome" => P with { Settlements = P.Settlements with { Factions = [new FactionSitesDef("orc", 10, 200, "lava")] } },
            "startBiome" => P with { Start = P.Start with { Biome = "water" } },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Throws<ArgumentException>(() => WorldGenTestData.GenerateWorld(1, 64, p));
    }

    private static void AssertPoiDensity(WorldMap world)
    {
        int land = 0;
        for (int i = 0; i < world.TileCount; i++)
        {
            land += WorldInvariants.IsLand(world, i) ? 1 : 0;
        }

        // ±50 % around one point per 6400 land tiles (FR-WORLD-07: about one per 40x40 tiles of the former 64x32 grid,
        // which is one per 80x80 tiles of the 32x16 grid, P-07).
        long expected = land / 6400;
        Assert.InRange(world.PointsOfInterest.Count, expected / 2, expected * 3 / 2);
        Assert.Equal(6400, P.PointsOfInterest.TilesPerPoi);
    }
}
