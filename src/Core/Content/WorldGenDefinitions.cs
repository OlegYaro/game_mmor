using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Core.WorldGen;

namespace Core.Content;

/// <summary>
/// Marker for a nested object inside a definition. The catalog validates the DataAnnotations of sections (and of
/// lists of sections) recursively, reporting JSON paths such as <c>$.items[0].height.octaves</c>.
/// </summary>
public interface IContentSection
{
}

/// <summary>
/// Parameters of the world generator (ADR 0006), <c>data/worldgen/*.json</c>. Exactly one definition must exist.
/// All numbers are integers: lengths in tiles, shares and weights in permille (thousandths). The generator turns
/// them into noise settings itself.
/// </summary>
/// <param name="Id">Definition id.</param>
/// <param name="Height">Step 1: heightfield.</param>
/// <param name="Climate">Step 2: moisture and temperature.</param>
/// <param name="Biomes">Step 3: biome table and smoothing.</param>
/// <param name="Rivers">Step 4: rivers.</param>
/// <param name="Resources">Step 5: resource nodes (trees, stone, ore, berry bushes).</param>
/// <param name="PointsOfInterest">Step 6: points of interest.</param>
/// <param name="Start">Step 7: the start location (B1).</param>
/// <param name="Settlements">Step 8: faction settlement sites.</param>
public sealed record WorldGenParamsDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    HeightParams Height,
    ClimateParams Climate,
    BiomeParams Biomes,
    RiverParams Rivers,
    ResourceParams Resources,
    PoiParams PointsOfInterest,
    StartParams Start,
    SettlementParams Settlements) : IContentDef
{
    /// <summary>
    /// Cross-field checks that DataAnnotations cannot express (run in the catalog's reference pass): increasing height
    /// quantiles, ordered rule ranges, a catch-all last biome rule so every tile gets a biome, ordered stock ranges,
    /// unique names in the placement lists, and settlement sites that cannot overlap the start zone.
    /// </summary>
    /// <returns>(JSON path below the definition, message) per problem, in a fixed order.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency() =>
        CheckTerrain().Concat(CheckPlacement());

    private IEnumerable<(string Path, string Message)> CheckPlacement()
    {
        foreach ((string path, int min, int max) in new[]
        {
            ("resources.trees", Resources.Trees.StockMin, Resources.Trees.StockMax),
            ("resources.stone", Resources.Stone.StockMin, Resources.Stone.StockMax),
            ("resources.ore", Resources.Ore.StockMin, Resources.Ore.StockMax),
            ("resources.berries", Resources.Berries.StockMin, Resources.Berries.StockMax),
        })
        {
            if (min > max)
            {
                yield return (path, "stockMin must not exceed stockMax.");
            }
        }

        foreach ((string path, IEnumerable<string> names) in new[]
        {
            ("resources.trees.biomes", Resources.Trees.Biomes.Select(b => b.Biome)),
            ("resources.stone.biomes", Resources.Stone.Biomes.Select(b => b.Biome)),
            ("resources.ore.biomes", Resources.Ore.Biomes.Select(b => b.Biome)),
            ("resources.berries.biomes", Resources.Berries.Biomes.Select(b => b.Biome)),
            ("pointsOfInterest.kinds", PointsOfInterest.Kinds.Select(k => k.Kind)),
            ("settlements.factions", Settlements.Factions.Select(f => f.Faction)),
        })
        {
            List<string> list = names.ToList();
            if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
            {
                yield return (path, "Every name may appear only once.");
            }
        }

        if (PointsOfInterest.Kinds.Count == 0)
        {
            yield return ("pointsOfInterest.kinds", "At least one point of interest kind is required.");
        }

        if (Settlements.SiteSize % 2 == 0)
        {
            yield return ("settlements.siteSize", "The site footprint must have an odd side, so it has a centre tile.");
        }

        for (int i = 0; i < Settlements.Factions.Count; i++)
        {
            if (Settlements.Factions[i].MinStartDistance < Start.ZoneRadius + Settlements.SiteSize)
            {
                yield return ($"settlements.factions[{i}].minStartDistance",
                    "Must be at least start.zoneRadius + settlements.siteSize, so no site overlaps the start zone (FR-WORLD-08).");
            }
        }
    }

    private IEnumerable<(string Path, string Message)> CheckTerrain()
    {
        if (!(Height.WaterPermille < Height.Step1Permille && Height.Step1Permille < Height.Step2Permille
            && Height.Step2Permille < Height.Step3Permille))
        {
            yield return ("height", "Height quantiles must increase: waterPermille < step1Permille < step2Permille < step3Permille.");
        }

        for (int i = 0; i < Biomes.Rules.Count; i++)
        {
            BiomeRuleDef rule = Biomes.Rules[i];
            if (rule.MinHeight > rule.MaxHeight || rule.MinMoisture > rule.MaxMoisture || rule.MinTemperature > rule.MaxTemperature)
            {
                yield return ($"biomes.rules[{i}]", "Every min* must not exceed the matching max*.");
            }
        }

        if (Biomes.Rules.Count == 0 || !Biomes.Rules[^1].IsCatchAll)
        {
            yield return ("biomes.rules", "The last biome rule must match every tile (full height, moisture and temperature ranges).");
        }
    }
}

/// <summary>Heightfield: OpenSimplex2 FBm on domain-warped coordinates, ocean falloff at the map edge, quantile steps.</summary>
/// <param name="Wavelength">Base feature size in tiles (noise frequency = 1 / wavelength).</param>
/// <param name="Octaves">FBm octaves.</param>
/// <param name="LacunarityPermille">Frequency multiplier per octave, in permille (2000 = x2).</param>
/// <param name="GainPermille">Amplitude multiplier per octave, in permille.</param>
/// <param name="WarpWavelength">Feature size of the domain warp noise in tiles.</param>
/// <param name="WarpAmplitude">Largest warp offset in tiles; 0 disables warping.</param>
/// <param name="WarpOctaves">Octaves of the (progressive) domain warp.</param>
/// <param name="OceanMargin">Width in tiles of the falloff that pulls the land down to the sea at the map edge.</param>
/// <param name="WaterPermille">Share of tiles below the water level (quantile of the continuous height).</param>
/// <param name="Step1Permille">Cumulative quantile from which land is step 1.</param>
/// <param name="Step2Permille">Cumulative quantile from which land is step 2.</param>
/// <param name="Step3Permille">Cumulative quantile from which land is step 3.</param>
/// <param name="MinLakeTiles">Inland water bodies smaller than this become land.</param>
/// <param name="MinIslandTiles">Land masses smaller than this become water.</param>
/// <param name="SmoothingPasses">Majority-filter passes over the land steps (removes one-tile bumps).</param>
/// <param name="MajorityThreshold">Votes (of the 3x3 land block, self included) the winning step needs to replace a tile.</param>
public sealed record HeightParams(
    [property: Range(16, 4096)] int Wavelength,
    [property: Range(1, 8)] int Octaves,
    [property: Range(1000, 4000)] int LacunarityPermille,
    [property: Range(100, 900)] int GainPermille,
    [property: Range(16, 4096)] int WarpWavelength,
    [property: Range(0, 512)] int WarpAmplitude,
    [property: Range(1, 6)] int WarpOctaves,
    [property: Range(1, 512)] int OceanMargin,
    [property: Range(1, 900)] int WaterPermille,
    [property: Range(1, 999)] int Step1Permille,
    [property: Range(1, 999)] int Step2Permille,
    [property: Range(1, 999)] int Step3Permille,
    [property: Range(1, 100000)] int MinLakeTiles,
    [property: Range(1, 100000)] int MinIslandTiles,
    [property: Range(0, 4)] int SmoothingPasses,
    [property: Range(5, 9)] int MajorityThreshold) : IContentSection;

/// <summary>Moisture and temperature fields (both rank-normalized to permille over land).</summary>
/// <param name="MoistureWavelength">Feature size of the moisture noise in tiles.</param>
/// <param name="MoistureOctaves">FBm octaves of the moisture noise.</param>
/// <param name="TemperatureWavelength">Feature size of the temperature noise in tiles.</param>
/// <param name="TemperatureOctaves">FBm octaves of the temperature noise.</param>
/// <param name="TemperatureLapsePermille">
/// How strongly height lowers temperature: the full continuous height range subtracts this share of the full
/// temperature-noise range before normalization.
/// </param>
public sealed record ClimateParams(
    [property: Range(16, 4096)] int MoistureWavelength,
    [property: Range(1, 8)] int MoistureOctaves,
    [property: Range(16, 4096)] int TemperatureWavelength,
    [property: Range(1, 8)] int TemperatureOctaves,
    [property: Range(0, 4000)] int TemperatureLapsePermille) : IContentSection;

/// <summary>Biome lookup table over (height step, moisture, temperature) and the smoothing applied afterwards.</summary>
/// <param name="SmoothingPasses">Majority-filter passes over land biomes (against checkerboard borders).</param>
/// <param name="MajorityThreshold">Votes (of the 3x3 land block, self included) the winning biome needs to replace a tile.</param>
/// <param name="Rules">Rules in priority order; the first match wins and the last one must match everything.</param>
public sealed record BiomeParams(
    [property: Range(0, 4)] int SmoothingPasses,
    [property: Range(5, 9)] int MajorityThreshold,
    IReadOnlyList<BiomeRuleDef> Rules) : IContentSection;

/// <summary>One row of the biome table. Ranges are inclusive; omitted bounds cover the full range.</summary>
/// <param name="Biome">Land biome name: <c>meadow</c>, <c>forest</c>, <c>mountain</c> or <c>wasteland</c>.</param>
/// <param name="MinHeight">Lowest height step (0–3).</param>
/// <param name="MaxHeight">Highest height step (0–3).</param>
/// <param name="MinMoisture">Lowest moisture, permille.</param>
/// <param name="MaxMoisture">Highest moisture, permille.</param>
/// <param name="MinTemperature">Lowest temperature, permille.</param>
/// <param name="MaxTemperature">Highest temperature, permille.</param>
public sealed record BiomeRuleDef(
    [property: RegularExpression(BiomeNames.LandPattern)] string Biome,
    [property: Range(0, 3)] int MinHeight = 0,
    [property: Range(0, 3)] int MaxHeight = 3,
    [property: Range(0, 1000)] int MinMoisture = 0,
    [property: Range(0, 1000)] int MaxMoisture = 1000,
    [property: Range(0, 1000)] int MinTemperature = 0,
    [property: Range(0, 1000)] int MaxTemperature = 1000) : IContentSection
{
    /// <summary>True when the rule covers every height, moisture and temperature.</summary>
    [JsonIgnore]
    public bool IsCatchAll =>
        MinHeight == 0 && MaxHeight == 3 && MinMoisture == 0 && MaxMoisture == 1000 && MinTemperature == 0 && MaxTemperature == 1000;

    /// <summary>True when the rule matches a tile.</summary>
    public bool Matches(int height, int moisture, int temperature) =>
        height >= MinHeight && height <= MaxHeight
        && moisture >= MinMoisture && moisture <= MaxMoisture
        && temperature >= MinTemperature && temperature <= MaxTemperature;
}

/// <summary>Rivers: priority-flood + D8 flow, sources in the mountains.</summary>
/// <param name="SourcesPerMillionTiles">Target number of river sources per 1 000 000 tiles (at least one on any map).</param>
/// <param name="MinSourceSpacing">Smallest distance in tiles between two river sources.</param>
/// <param name="MinLength">A source is used only when its path to water is at least this many tiles long.</param>
/// <param name="MeanderJitter">
/// Largest random extra rise (in 1/65535 height units) per tile when the flood fills depressions and flats; without
/// it rivers cross filled areas in straight lines.
/// </param>
public sealed record RiverParams(
    [property: Range(0, 1000)] int SourcesPerMillionTiles,
    [property: Range(1, 1024)] int MinSourceSpacing,
    [property: Range(1, 4096)] int MinLength,
    [property: Range(0, 1024)] int MeanderJitter) : IContentSection;

/// <summary>
/// Step 5: resource nodes, one per tile at most, each with a stock (B5). Applied in this order, a later kind never
/// replacing an earlier one: ore, stone, trees, berry bushes. Nothing is placed on water or river tiles. Animals are
/// not placed by the generator (they spawn, M4).
/// </summary>
/// <param name="Trees">Forest and grove density by a cellular automaton over smoothed noise.</param>
/// <param name="Stone">Stone clusters.</param>
/// <param name="Ore">Ore clusters (rarer, higher up).</param>
/// <param name="Berries">Berry bushes along the borders between the listed biomes.</param>
public sealed record ResourceParams(
    TreeParams Trees,
    ClusterResourceParams Stone,
    ClusterResourceParams Ore,
    BerryParams Berries) : IContentSection;

/// <summary>
/// Trees: every eligible tile gets a value mixing OpenSimplex2 FBm with per-tile white noise; tiles whose rank among
/// eligible tiles is below their biome's share start as trees. Then a cellular automaton (B/S rule over the 8
/// neighbours) turns that into dense forests with clearings and small groves.
/// </summary>
/// <param name="NoiseWavelength">Feature size of the density noise in tiles.</param>
/// <param name="NoiseOctaves">FBm octaves of the density noise.</param>
/// <param name="NoisePermille">Weight of the smooth noise against per-tile white noise (1000 = noise only).</param>
/// <param name="AutomatonPasses">Cellular automaton passes.</param>
/// <param name="BirthNeighbours">A treeless tile grows a tree with at least this many tree neighbours (9 = never).</param>
/// <param name="SurviveNeighbours">A tree stays with at least this many tree neighbours (0 = always).</param>
/// <param name="StockMin">Smallest wood stock of a tree.</param>
/// <param name="StockMax">Largest wood stock of a tree.</param>
/// <param name="Biomes">Biomes that grow trees and the initial tree share in each, permille.</param>
public sealed record TreeParams(
    [property: Range(4, 1024)] int NoiseWavelength,
    [property: Range(1, 8)] int NoiseOctaves,
    [property: Range(0, 1000)] int NoisePermille,
    [property: Range(0, 16)] int AutomatonPasses,
    [property: Range(0, 9)] int BirthNeighbours,
    [property: Range(0, 9)] int SurviveNeighbours,
    [property: Range(1, 65535)] int StockMin,
    [property: Range(1, 65535)] int StockMax,
    IReadOnlyList<BiomeShareDef> Biomes) : IContentSection;

/// <summary>A land biome with a share or chance in permille.</summary>
/// <param name="Biome">Land biome name.</param>
/// <param name="Permille">Share or chance, permille.</param>
public sealed record BiomeShareDef(
    [property: RegularExpression(BiomeNames.LandPattern)] string Biome,
    [property: Range(0, 1000)] int Permille) : IContentSection;

/// <summary>
/// Clustered resources (cellular noise): the map is cut into square cells; each cell has one random feature point,
/// and around it a cluster of three overlapping discs. A tile belongs to a cluster when it lies in a disc of a cell
/// whose roll is below the chance of the tile's biome, using that biome's radius.
/// </summary>
/// <param name="CellSize">Side of a cell in tiles: on average at most one cluster per cell.</param>
/// <param name="MinHeight">Lowest height step (0–3) that can hold the resource.</param>
/// <param name="StockMin">Smallest stock of a node.</param>
/// <param name="StockMax">Largest stock of a node.</param>
/// <param name="Biomes">Biomes that can hold clusters; others never do.</param>
public sealed record ClusterResourceParams(
    [property: Range(4, 256)] int CellSize,
    [property: Range(0, 3)] int MinHeight,
    [property: Range(1, 65535)] int StockMin,
    [property: Range(1, 65535)] int StockMax,
    IReadOnlyList<ClusterBiomeDef> Biomes) : IContentSection;

/// <summary>How a clustered resource appears in one biome.</summary>
/// <param name="Biome">Land biome name.</param>
/// <param name="ChancePermille">Chance that a cell holds a cluster, permille.</param>
/// <param name="Radius">Largest disc radius in tiles (each disc gets a random radius from half of it up to it).</param>
public sealed record ClusterBiomeDef(
    [property: RegularExpression(BiomeNames.LandPattern)] string Biome,
    [property: Range(0, 1000)] int ChancePermille,
    [property: Range(1, 64)] int Radius) : IContentSection;

/// <summary>Berry bushes: on tiles of a listed biome within <c>EdgeDistance</c> of a tile of another listed biome.</summary>
/// <param name="EdgeDistance">Largest Chebyshev distance in tiles to the other biome.</param>
/// <param name="StockMin">Smallest berry stock of a bush.</param>
/// <param name="StockMax">Largest berry stock of a bush.</param>
/// <param name="Biomes">Biomes whose borders hold bushes, with the chance per border tile, permille.</param>
public sealed record BerryParams(
    [property: Range(1, 16)] int EdgeDistance,
    [property: Range(1, 65535)] int StockMin,
    [property: Range(1, 65535)] int StockMax,
    IReadOnlyList<BiomeShareDef> Biomes) : IContentSection;

/// <summary>
/// Step 6: points of interest (FR-WORLD-07, B7), placed Poisson-disk style (dart throwing in seeded order with a
/// minimum spacing) on flat land until the target count is reached.
/// </summary>
/// <param name="TilesPerPoi">
/// Target density: one point per this many land tiles (6400 = one per 80x80, i.e. FR-WORLD-07's one per 40x40 tiles of
/// the former 64x32 grid, P-07).
/// </param>
/// <param name="MinSpacing">Smallest distance in tiles between two points.</param>
/// <param name="EdgeMargin">Points keep this many tiles from the map edge.</param>
/// <param name="Kinds">Kinds with their relative weights.</param>
public sealed record PoiParams(
    [property: Range(100, 1000000)] int TilesPerPoi,
    [property: Range(1, 512)] int MinSpacing,
    [property: Range(0, 256)] int EdgeMargin,
    IReadOnlyList<PoiKindDef> Kinds) : IContentSection;

/// <summary>A point of interest kind and its weight.</summary>
/// <param name="Kind">Kind name: <c>chest</c>, <c>ruins</c> or <c>lair</c>.</param>
/// <param name="Weight">Relative weight.</param>
public sealed record PoiKindDef(
    [property: RegularExpression(WorldNames.PoiKindPattern)] string Kind,
    [property: Range(1, 1000)] int Weight) : IContentSection;

/// <summary>
/// Step 7: the start location (FR-WORLD-08, B1). Candidates on a grid are tried in seeded order; the first one that
/// passes every check wins. When none does, the checks are relaxed step by step (water first, then the room for evil
/// settlements) and the result carries a warning.
/// </summary>
/// <param name="Biome">Land biome the start tile must have.</param>
/// <param name="ZoneRadius">Radius of the start zone in tiles.</param>
/// <param name="MinTreeTiles">Tree tiles the zone needs.</param>
/// <param name="MinStoneTiles">Stone tiles the zone needs.</param>
/// <param name="MinWaterTiles">Water or river tiles the zone needs.</param>
/// <param name="FlatRadius">Radius of the building area around the start (B2).</param>
/// <param name="MinFlatPermille">Share of buildable tiles the building area needs, permille.</param>
/// <param name="PoiClearance">No point of interest may lie closer to the start than this.</param>
/// <param name="EdgeMargin">The start keeps this many tiles from the map edge.</param>
/// <param name="CandidateSpacing">Grid step of the start candidates in tiles.</param>
public sealed record StartParams(
    [property: RegularExpression(BiomeNames.LandPattern)] string Biome,
    [property: Range(1, 256)] int ZoneRadius,
    [property: Range(0, 100000)] int MinTreeTiles,
    [property: Range(0, 100000)] int MinStoneTiles,
    [property: Range(0, 100000)] int MinWaterTiles,
    [property: Range(1, 64)] int FlatRadius,
    [property: Range(0, 1000)] int MinFlatPermille,
    [property: Range(0, 256)] int PoiClearance,
    [property: Range(0, 1024)] int EdgeMargin,
    [property: Range(1, 64)] int CandidateSpacing) : IContentSection;

/// <summary>
/// Step 8: reserved settlement sites of factions (FR-WORLD-09): square clearings of flat land, no river, no point of
/// interest. Evil factions are placed first and only on clearings outside their start distance, so the distance is
/// guaranteed by construction. Resources inside a placed footprint are removed.
/// </summary>
/// <param name="SiteSize">Side of the square footprint in tiles (odd).</param>
/// <param name="MinSpacing">Smallest distance in tiles between the centres of two sites.</param>
/// <param name="EdgeMargin">Footprints keep this many tiles from the map edge.</param>
/// <param name="PoiClearance">No point of interest within this many tiles of a footprint.</param>
/// <param name="Factions">Factions with sites.</param>
public sealed record SettlementParams(
    [property: Range(3, 31)] int SiteSize,
    [property: Range(1, 1024)] int MinSpacing,
    [property: Range(0, 256)] int EdgeMargin,
    [property: Range(0, 256)] int PoiClearance,
    IReadOnlyList<FactionSitesDef> Factions) : IContentSection;

/// <summary>Sites of one faction.</summary>
/// <param name="Faction">Faction name: <c>human</c> or <c>orc</c>.</param>
/// <param name="PerMillionTiles">Target number of sites per 1 000 000 map tiles (at least one on any map when positive).</param>
/// <param name="MinStartDistance">
/// Smallest distance in tiles from the start to a site centre (at least 240 for evil factions: FR-WORLD-09's 120 tiles of
/// the former 64x32 grid, P-07).
/// </param>
/// <param name="PreferredBiome">Clearings in this biome are tried first.</param>
public sealed record FactionSitesDef(
    [property: RegularExpression(WorldNames.FactionPattern)] string Faction,
    [property: Range(0, 1000)] int PerMillionTiles,
    [property: Range(0, 4096)] int MinStartDistance,
    [property: RegularExpression(BiomeNames.LandPattern)] string PreferredBiome) : IContentSection;

/// <summary>File envelope for <c>data/worldgen/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record WorldGenFile(
    IReadOnlyList<WorldGenParamsDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
