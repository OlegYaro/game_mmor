using Core.Content;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Sim;

namespace Core.Buildings;

/// <summary>
/// Read-only building rules shared by the Core commands and the game's previews (FR-BUILD-01, 02, 06, 09, 12, 13,
/// ADR 0012): the placement check, the territory and the era, materials, refunds and construction progress.
/// </summary>
/// <remarks>
/// <para>Renderer notes: buildings are the <see cref="GameWorld.Buildings"/> store (state, level, position, rotation);
/// <see cref="ProgressPermille"/> picks the stage frame (below 250, 500, 800 ‰: s0, s1, s2; from 800 ‰ the finished
/// frame). A storage's stock is its <see cref="Inventory"/> in <see cref="GameWorld.Inventories"/>. Orders are
/// <see cref="PlaceBuildingCommand"/>, <see cref="CancelBuildingCommand"/>, <see cref="UpgradeBuildingCommand"/>,
/// <see cref="MoveBuildingCommand"/> and <see cref="DemolishBuildingCommand"/>; their outcomes, refusals included,
/// appear in <see cref="GameWorld.BuildingEvents"/>.</para>
/// <para>Materials (A2): a cost is taken at once from the hero's backpack first, then from the available storages in
/// ascending entity order. Refunds go to the available storages first, then to the backpack; what still does not fit
/// is lost and reported (E6). A storage is available only while <see cref="BuildingState.Active"/>.</para>
/// </remarks>
public static class Construction
{
    /// <summary>Building category of the settlement center (the campfire).</summary>
    public const string CenterCategory = "settlement_center";

    /// <summary>Building category of storages.</summary>
    public const string StorageCategory = "storage";

    /// <summary>Largest rotation (quarter turns).</summary>
    public const int MaxRotation = 3;

    /// <summary>Requirement kind: the settlement's era must be at least the given one.</summary>
    public const string EraRequirement = "era";

    private static readonly string[] _resultNames =
    [
        "none", "ok", "no_world", "unknown_building", "invalid_rotation", "out_of_map", "not_explored", "water", "not_flat",
        "resource_in_the_way", "occupied", "unit_in_the_way", "no_center", "center_exists", "outside_territory",
        "requirement_not_met", "not_enough_materials", "not_a_building", "busy", "max_level", "nothing_to_cancel",
        "center_not_removable", "last_storage", "center_in_use", "buildings_outside_territory", "same_place",
    ];

    private static readonly string[] _stateNames = ["constructing", "active", "upgrading", "moving"];

    /// <summary>Stable snake_case name of a result (console, logs, game messages).</summary>
    public static string NameOf(BuildResult result) =>
        (int)result < _resultNames.Length ? _resultNames[(int)result] : throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown result.");

    /// <summary>Stable snake_case name of a state; also its save name.</summary>
    public static string NameOf(BuildingState state) =>
        (int)state < _stateNames.Length ? _stateNames[(int)state] : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state.");

    /// <summary>All state names, indexed by <see cref="BuildingState"/> value.</summary>
    public static IReadOnlyList<string> StateNames => _stateNames;

    /// <summary>Footprint size in tiles for <paramref name="rotation"/>: odd quarter turns swap width and height (E7: the same for every level).</summary>
    public static (int Width, int Height) FootprintOf(BuildingDef def, int rotation)
    {
        ArgumentNullException.ThrowIfNull(def);
        return (rotation & 1) == 0 ? (def.Footprint.Width, def.Footprint.Height) : (def.Footprint.Height, def.Footprint.Width);
    }

    /// <summary>True for the settlement center type.</summary>
    public static bool IsCenter(BuildingDef def) => string.Equals(def?.Category, CenterCategory, StringComparison.Ordinal);

    /// <summary>True for a storage type.</summary>
    public static bool IsStorage(BuildingDef def) => string.Equals(def?.Category, StorageCategory, StringComparison.Ordinal);

    /// <summary>The type of <paramref name="building"/>.</summary>
    public static BuildingDef DefOf(ContentCatalog content, Building building)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.Get<BuildingDef>(building.Def);
    }

    /// <summary>The data of the building's current <see cref="Building.Level"/>.</summary>
    public static BuildingLevelDef LevelOf(ContentCatalog content, Building building) => DefOf(content, building).Levels[building.Level - 1];

    /// <summary>
    /// Ticks the current construction, upgrade or move takes in total (the level's build or move ticks); 0 when active.
    /// </summary>
    public static int TotalTicks(ContentCatalog content, Building building) => building.State switch
    {
        BuildingState.Constructing or BuildingState.Upgrading => LevelOf(content, building).BuildTicks,
        BuildingState.Moving => LevelOf(content, building).MoveTicks,
        _ => 0,
    };

    /// <summary>
    /// Progress of the current construction, upgrade or move in permille, <c>(total − left) × 1000 / total</c>
    /// rounded down; 1000 when active. The game picks the stage frame from it.
    /// </summary>
    public static int ProgressPermille(ContentCatalog content, Building building)
    {
        int total = TotalTicks(content, building);
        return total == 0 ? 1000 : (int)((long)(total - building.TicksLeft) * 1000 / total);
    }

    /// <summary>
    /// Number of residents of the player's settlement. M2 has no residents, so this is always 0; M3 counts them here and
    /// the era (and with it the territory radius) follows.
    /// </summary>
    public static int SettlementPopulation(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        return 0;
    }

    /// <summary>
    /// The era for <paramref name="population"/> residents: the era whose population range holds it. Below the first
    /// era's minimum (no residents yet) it is the first era.
    /// </summary>
    public static EraDef EraFor(ContentCatalog content, int population)
    {
        ArgumentNullException.ThrowIfNull(content);
        IReadOnlyList<EraDef> eras = content.Eras;
        foreach (EraDef era in eras)
        {
            if (population >= era.MinPopulation && (era.MaxPopulation is not { } max || population <= max))
            {
                return era;
            }
        }

        return eras[0];
    }

    /// <summary>The era of the player's settlement (from <see cref="SettlementPopulation"/>).</summary>
    public static EraDef CurrentEra(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        return EraFor(sim.Content, SettlementPopulation(sim));
    }

    /// <summary>The player's settlement center (any state), or <see cref="EntityId.None"/> when there is none.</summary>
    public static EntityId FindCenter(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world)
        {
            return EntityId.None;
        }

        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (buildings[i].Settlement == Building.PlayerSettlement && IsCenter(DefOf(sim.Content, buildings[i])))
            {
                return owners[i];
            }
        }

        return EntityId.None;
    }

    /// <summary>
    /// The territory around the center's current place, or null without a center. While the center is moving the
    /// territory stays here until the move ends; placements then must also fit the territory at the new place
    /// (<see cref="IsInTerritory"/>).
    /// </summary>
    public static Territory? GetTerritory(Simulation sim)
    {
        EntityId center = FindCenter(sim);
        if (center == EntityId.None)
        {
            return null;
        }

        Building building = sim.World!.Buildings.Get(center);
        return TerritoryAt(sim, building.Def, building.X, building.Y, building.Rotation);
    }

    /// <summary>
    /// True when the whole <paramref name="width"/> × <paramref name="height"/> area at (<paramref name="x"/>,
    /// <paramref name="y"/>) is inside the territory; while the center is moving, inside both the current and the new
    /// territory. False without a center.
    /// </summary>
    public static bool IsInTerritory(Simulation sim, int x, int y, int width, int height)
    {
        EntityId center = FindCenter(sim);
        if (center == EntityId.None)
        {
            return false;
        }

        Building building = sim.World!.Buildings.Get(center);
        bool inside = TerritoryAt(sim, building.Def, building.X, building.Y, building.Rotation).ContainsArea(x, y, width, height);
        return inside && (building.State != BuildingState.Moving
            || TerritoryAt(sim, building.Def, building.TargetX, building.TargetY, building.TargetRotation).ContainsArea(x, y, width, height));
    }

    /// <summary>True when <paramref name="building"/> of <paramref name="entity"/> is a storage whose stock can be used (built and active).</summary>
    public static bool IsAvailableStorage(GameWorld world, EntityId entity, Building building)
    {
        ArgumentNullException.ThrowIfNull(world);
        return building.State == BuildingState.Active && building.Settlement == Building.PlayerSettlement && world.Inventories.Has(entity);
    }

    /// <summary>How many <paramref name="item"/> the player can spend: the hero's backpack plus the available storages.</summary>
    public static int CountAvailable(Simulation sim, DefId item)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world)
        {
            return 0;
        }

        long total = 0;
        EntityId hero = world.HeroEntity;
        if (hero != EntityId.None && world.Inventories.TryGet(hero, out Inventory backpack))
        {
            total += backpack.Count(item);
        }

        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (IsAvailableStorage(world, owners[i], buildings[i]))
            {
                total += world.Inventories.Get(owners[i]).Count(item);
            }
        }

        return (int)Math.Min(int.MaxValue, total);
    }

    /// <summary>
    /// True when the player can pay <paramref name="cost"/> (<see cref="CountAvailable"/>). Entries of the same item
    /// are added up: data never repeats an item in one cost (the content check rejects it), but a cost built in code
    /// may, and paying takes the entries one after another.
    /// </summary>
    public static bool CanPay(Simulation sim, IReadOnlyList<ItemAmountDef> cost)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(cost);
        for (int i = 0; i < cost.Count; i++)
        {
            string id = cost[i].Item;
            if (IndexOfItem(cost, id, 0, i) >= 0)
            {
                continue; // the first entry of this item checked the sum
            }

            long needed = cost[i].Count;
            for (int j = IndexOfItem(cost, id, i + 1, cost.Count); j >= 0; j = IndexOfItem(cost, id, j + 1, cost.Count))
            {
                needed += cost[j].Count;
            }

            if (!sim.Content.TryGetId(id, out DefId item) || CountAvailable(sim, item) < needed)
            {
                return false;
            }
        }

        return true;
    }

    // Index of the first entry of `item` in cost[from..to), or -1.
    private static int IndexOfItem(IReadOnlyList<ItemAmountDef> cost, string item, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (string.Equals(cost[i].Item, item, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when the settlement meets every requirement of <paramref name="level"/>: an <c>era</c> requirement needs the
    /// current era's order to be at least the required era's; a <c>specialist</c> requirement is never met in M2 (there
    /// are no residents yet).
    /// </summary>
    public static bool MeetsRequirements(Simulation sim, BuildingLevelDef level)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(level);
        int era = CurrentEra(sim).Order;
        foreach (RequirementDef requirement in level.Requirements)
        {
            bool met = string.Equals(requirement.Kind, EraRequirement, StringComparison.Ordinal)
                && sim.Content.TryGet(requirement.Value, out EraDef required) && era >= required.Order;
            if (!met)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What an upgrade order for <paramref name="entity"/> would answer right now (the order uses this check): not an
    /// active building, last level, requirements of the next level, materials. <see cref="BuildResult.Ok"/> when it would be accepted.
    /// </summary>
    public static BuildResult CheckUpgrade(Simulation sim, EntityId entity)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world || !world.Buildings.TryGet(entity, out Building building))
        {
            return BuildResult.NotABuilding;
        }

        BuildingDef def = DefOf(sim.Content, building);
        BuildingLevelDef? next = building.Level < def.Levels.Count ? def.Levels[building.Level] : null;
        if (building.State != BuildingState.Active)
        {
            return BuildResult.Busy;
        }

        if (next is null)
        {
            return BuildResult.MaxLevel;
        }

        if (!MeetsRequirements(sim, next))
        {
            return BuildResult.RequirementNotMet;
        }

        return CanPay(sim, next.Cost) ? BuildResult.Ok : BuildResult.NotEnoughMaterials;
    }

    /// <summary>
    /// What a demolish order for <paramref name="entity"/> would answer right now (the order uses this check): busy,
    /// the center (E4), the last built storage (E4). <see cref="BuildResult.Ok"/> when it would be accepted.
    /// </summary>
    public static BuildResult CheckDemolish(Simulation sim, EntityId entity)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world || !world.Buildings.TryGet(entity, out Building building))
        {
            return BuildResult.NotABuilding;
        }

        BuildingDef def = DefOf(sim.Content, building);
        if (building.State != BuildingState.Active)
        {
            return BuildResult.Busy;
        }

        if (IsCenter(def))
        {
            return BuildResult.CenterNotRemovable;
        }

        return IsStorage(def) && !BuildingRules.HasOtherBuiltStorage(sim, world, entity) ? BuildResult.LastStorage : BuildResult.Ok;
    }

    /// <summary>
    /// The refund of levels <paramref name="fromLevel"/>..<paramref name="toLevel"/> (1-based, inclusive) of
    /// <paramref name="def"/> at <paramref name="permille"/>: the costs of those levels are summed per item, then each sum
    /// is multiplied by <paramref name="permille"/> / 1000 and rounded down (E5). Items keep the order of their first
    /// appearance (level by level, cost order); items that round to 0 are left out.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The level range or the permille is invalid.</exception>
    public static IReadOnlyList<ItemAmountDef> Refund(BuildingDef def, int fromLevel, int toLevel, int permille)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromLevel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(toLevel, def.Levels.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fromLevel, toLevel);
        ArgumentOutOfRangeException.ThrowIfNegative(permille);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permille, 1000);

        var items = new List<string>();
        var sums = new List<long>();
        for (int level = fromLevel; level <= toLevel; level++)
        {
            foreach (ItemAmountDef entry in def.Levels[level - 1].Cost)
            {
                int index = items.IndexOf(entry.Item);
                if (index < 0)
                {
                    items.Add(entry.Item);
                    sums.Add(entry.Count);
                }
                else
                {
                    sums[index] += entry.Count;
                }
            }
        }

        var refund = new List<ItemAmountDef>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            long count = sums[i] * permille / 1000;
            if (count > 0)
            {
                refund.Add(new ItemAmountDef(items[i], (int)count));
            }
        }

        return refund;
    }

    /// <summary>
    /// The one placement check (FR-BUILD-01): for a new building of type <paramref name="buildingId"/>, or, with
    /// <paramref name="moving"/>, for moving that existing building, with the footprint's top-left tile at
    /// (<paramref name="x"/>, <paramref name="y"/>) and <paramref name="rotation"/> quarter turns. The commands use it,
    /// and the game calls it for the preview every frame (it is cheap: the footprint's tiles and one pass over the
    /// buildings and the walkers).
    /// </summary>
    /// <remarks>
    /// <para>Checks, in this order (the first failure is the result): world, type, (move: the entity is that building
    /// and is active), rotation, (move: not the same place and rotation), map
    /// bounds, fog (every tile explored), center and territory (a center only when there is none, outside the territory
    /// allowed; other buildings need a center and, unless the type may stand outside, the whole footprint inside the
    /// territory; moving the center must keep every other building inside the new territory), water and rivers, flat
    /// ground (one height), resource nodes (none under the footprint), occupancy (other buildings; a moving building's
    /// own tiles are free), units (the hero's tile and the tile it is stepping onto), and for a new building the
    /// requirements and the materials of level 1.</para>
    /// <para>Resource nodes block placement (<see cref="BuildResult.ResourceInTheWay"/>) instead of being cleared:
    /// a node is stock on the map (trees, rocks, ore veins for later mines); removing it silently would destroy
    /// resources the player did not gather, and gathering first is the hero's job. The same rule holds for a move target.</para>
    /// </remarks>
    public static PlacementResult CheckPlacement(Simulation sim, string buildingId, int x, int y, int rotation, EntityId moving = default)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world)
        {
            return PlacementResult.Refuse(BuildResult.NoWorld);
        }

        if (buildingId is null || !sim.Content.TryGet(buildingId, out BuildingDef def))
        {
            return PlacementResult.Refuse(BuildResult.UnknownBuilding);
        }

        Building self = default;
        bool isMove = moving != EntityId.None;
        if (isMove && (!world.Buildings.TryGet(moving, out self) || !string.Equals(DefOf(sim.Content, self).Id, def.Id, StringComparison.Ordinal)))
        {
            return PlacementResult.Refuse(BuildResult.NotABuilding);
        }

        if (isMove && self.State != BuildingState.Active)
        {
            return PlacementResult.Refuse(BuildResult.Busy);
        }

        if (rotation < 0 || rotation > MaxRotation || (!def.Rotatable && rotation != 0))
        {
            return PlacementResult.Refuse(BuildResult.InvalidRotation);
        }

        if (isMove && self.X == x && self.Y == y && self.Rotation == rotation)
        {
            return PlacementResult.Refuse(BuildResult.SamePlace);
        }

        (int width, int height) = FootprintOf(def, rotation);
        int size = world.Size;
        if (x < 0 || y < 0 || (long)x + width > size || (long)y + height > size)
        {
            return new PlacementResult(BuildResult.OutOfMap, x, y);
        }

        PlacementResult tiles = CheckTiles(world, sim.Positions, x, y, width, height, moving);
        if (tiles.Reason == BuildResult.NotExplored)
        {
            return tiles;
        }

        PlacementResult territory = CheckSettlement(sim, def, x, y, width, height, moving);
        if (!territory.IsOk)
        {
            return territory;
        }

        if (!tiles.IsOk)
        {
            return tiles;
        }

        if (!isMove)
        {
            BuildingLevelDef first = def.Levels[0];
            if (!MeetsRequirements(sim, first))
            {
                return PlacementResult.Refuse(BuildResult.RequirementNotMet);
            }

            if (!CanPay(sim, first.Cost))
            {
                return PlacementResult.Refuse(BuildResult.NotEnoughMaterials);
            }
        }

        return PlacementResult.Valid;
    }

    /// <summary>The territory around a center of type <paramref name="def"/> placed at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static Territory TerritoryAt(Simulation sim, DefId def, int x, int y, int rotation)
    {
        (int width, int height) = FootprintOf(sim.Content.Get<BuildingDef>(def), rotation);
        return new Territory(x + ((width - 1) / 2), y + ((height - 1) / 2), CurrentEra(sim).TerritoryRadius);
    }

    // Terrain, fog, nodes, occupancy and units, each checked over the whole footprint before the next, so the reason
    // is the most basic one. NotExplored comes back first so the caller can rank it before the settlement rules.
    private static PlacementResult CheckTiles(GameWorld world, ComponentStore<Position> positions, int x, int y, int width, int height, EntityId moving)
    {
        for (int pass = 0; pass < 5; pass++)
        {
            int baseHeight = world.Map.Terrain.GetHeight(x, y);
            for (int ty = y; ty < y + height; ty++)
            {
                for (int tx = x; tx < x + width; tx++)
                {
                    BuildResult reason = pass switch
                    {
                        0 => world.Fog.GetState(tx, ty) == FogState.Unknown ? BuildResult.NotExplored : BuildResult.Ok,
                        1 => world.Map.Terrain.IsWater(tx, ty) || world.Map.Terrain.IsRiver(tx, ty) ? BuildResult.Water : BuildResult.Ok,
                        2 => world.Map.Terrain.GetHeight(tx, ty) != baseHeight ? BuildResult.NotFlat : BuildResult.Ok,
                        3 => world.Tiles.GetEffectiveResource(tx, ty) != WorldGen.ResourceKind.None ? BuildResult.ResourceInTheWay : BuildResult.Ok,
                        _ => world.Tiles.GetOccupant(tx, ty) != EntityId.None && world.Tiles.GetOccupant(tx, ty) != moving ? BuildResult.Occupied : BuildResult.Ok,
                    };
                    if (reason != BuildResult.Ok)
                    {
                        return new PlacementResult(reason, tx, ty);
                    }
                }
            }
        }

        return CheckUnits(world, positions, x, y, width, height);
    }

    // A walker standing on a footprint tile, or stepping onto one, would be shut in.
    private static PlacementResult CheckUnits(GameWorld world, ComponentStore<Position> positions, int x, int y, int width, int height)
    {
        ReadOnlySpan<EntityId> owners = world.Movers.Entities;
        Span<Mover> movers = world.Movers.Values;
        int size = world.Size;
        for (int i = 0; i < owners.Length; i++)
        {
            int standing = movers[i].IsMoving ? movers[i].CurrentTile
                : positions.TryGet(owners[i], out Position position) && world.Map.Terrain.Contains(position.TileX, position.TileY)
                    ? (position.TileY * size) + position.TileX
                    : -1;
            int next = movers[i].NextTile;
            if (standing >= 0 && Inside(standing % size, standing / size, x, y, width, height))
            {
                return new PlacementResult(BuildResult.UnitInTheWay, standing % size, standing / size);
            }

            if (next >= 0 && Inside(next % size, next / size, x, y, width, height))
            {
                return new PlacementResult(BuildResult.UnitInTheWay, next % size, next / size);
            }
        }

        return PlacementResult.Valid;
    }

    private static PlacementResult CheckSettlement(Simulation sim, BuildingDef def, int x, int y, int width, int height, EntityId moving)
    {
        EntityId center = FindCenter(sim);
        if (IsCenter(def))
        {
            if (moving == EntityId.None)
            {
                return center == EntityId.None ? PlacementResult.Valid : PlacementResult.Refuse(BuildResult.CenterExists);
            }

            return KeepsBuildingsInside(sim, new Territory(x + ((width - 1) / 2), y + ((height - 1) / 2), CurrentEra(sim).TerritoryRadius), moving);
        }

        if (center == EntityId.None)
        {
            return PlacementResult.Refuse(BuildResult.NoCenter);
        }

        if (def.OutsideTerritory || IsInTerritory(sim, x, y, width, height))
        {
            return PlacementResult.Valid;
        }

        for (int ty = y; ty < y + height; ty++)
        {
            for (int tx = x; tx < x + width; tx++)
            {
                if (!IsInTerritory(sim, tx, ty, 1, 1))
                {
                    return new PlacementResult(BuildResult.OutsideTerritory, tx, ty);
                }
            }
        }

        return PlacementResult.Refuse(BuildResult.OutsideTerritory);
    }

    // Moving the center: every other building (and the target of a building being moved) must stay in the new territory.
    private static PlacementResult KeepsBuildingsInside(Simulation sim, Territory next, EntityId center)
    {
        GameWorld world = sim.World!;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            Building other = buildings[i];
            BuildingDef def = DefOf(sim.Content, other);
            if (owners[i] == center || other.Settlement != Building.PlayerSettlement || def.OutsideTerritory)
            {
                continue;
            }

            (int width, int height) = FootprintOf(def, other.Rotation);
            bool inside = next.ContainsArea(other.X, other.Y, width, height);
            if (inside && other.State == BuildingState.Moving)
            {
                (int targetWidth, int targetHeight) = FootprintOf(def, other.TargetRotation);
                inside = next.ContainsArea(other.TargetX, other.TargetY, targetWidth, targetHeight);
            }

            if (!inside)
            {
                return new PlacementResult(BuildResult.BuildingsOutsideTerritory, other.X, other.Y);
            }
        }

        return PlacementResult.Valid;
    }

    private static bool Inside(int tx, int ty, int x, int y, int width, int height) =>
        tx >= x && tx < x + width && ty >= y && ty < y + height;
}
