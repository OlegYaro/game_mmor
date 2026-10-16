using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Pathfinding;
using Core.Sim;
using Core.WorldGen;

namespace Core.Residents;

/// <summary>
/// The odds of a newcomer on one morning (<see cref="Arrivals.ComputeOdds"/>), every term of the formula for the game
/// and the console. Points are percent; the chance is in permille.
/// </summary>
/// <param name="HasCenter">True when the settlement has a built center (campfire); without one nobody comes.</param>
/// <param name="Population">Residents now.</param>
/// <param name="FreePlaces">Free places in active houses minus homeless residents (never below 0).</param>
/// <param name="SlotPoints">Points for free places: <see cref="EconomyBalanceDef.ArrivalFreeSlotPoints"/> each, at most <see cref="EconomyBalanceDef.ArrivalMaxSlots"/> places.</param>
/// <param name="Food">Food units available (Σ food value × count over the hero's backpack and the available storages).</param>
/// <param name="FoodNeeded">Food units for <see cref="EconomyBalanceDef.ArrivalFoodDays"/> days for everyone including the newcomer.</param>
/// <param name="FoodPoints"><see cref="EconomyBalanceDef.ArrivalFoodPoints"/> when <paramref name="Food"/> reaches <paramref name="FoodNeeded"/>, else 0.</param>
/// <param name="NoRaidPoints"><see cref="EconomyBalanceDef.ArrivalNoRaidPoints"/> when there was no raid for <see cref="EconomyBalanceDef.ArrivalNoRaidDays"/> days.</param>
/// <param name="FamePoints">The hero's fame divided by <see cref="EconomyBalanceDef.ArrivalFamePointsDivisor"/>.</param>
/// <param name="EraPoints">Points of the current era (<see cref="EconomyBalanceDef.ArrivalEraPoints"/>).</param>
/// <param name="ChancePermille">
/// The chance in permille: the points × 10, at most <see cref="EconomyBalanceDef.ArrivalChanceCapPermille"/>; 0 without a
/// built center, without free places, or at <see cref="EconomyBalanceDef.MaxResidents"/>.
/// </param>
public sealed record ArrivalOdds(
    bool HasCenter,
    int Population,
    int FreePlaces,
    int SlotPoints,
    long Food,
    long FoodNeeded,
    int FoodPoints,
    int NoRaidPoints,
    int FamePoints,
    int EraPoints,
    int ChancePermille)
{
    /// <summary>The sum of all points (percent) before the cap.</summary>
    public int Points => SlotPoints + FoodPoints + NoRaidPoints + FamePoints + EraPoints;
}

/// <summary>
/// Newcomers (FR-NPC-01, gdd-issues E2 recalculated for P-08 and P-10): once a game day at
/// <see cref="EconomyBalanceDef.ArrivalHour"/> the settlement may attract one wanderer. The odds come from free house
/// places, food for everyone, days without raids, the hero's fame and the era; the roll uses the
/// <see cref="Determinism.RngStreamId.Events"/> stream. The newcomer appears at the site of good people nearest to the
/// center (or, when there is none it can walk from, where the map edge nearest to the center meets reachable land) and
/// walks to the center (<see cref="FindSpawnTile"/>).
/// </summary>
public static class Arrivals
{
    /// <summary>
    /// The hero's fame. The hero earns no fame before quests and battles (M4), so it is 0 and the fame term adds nothing
    /// yet; the formula already divides it by <see cref="EconomyBalanceDef.ArrivalFamePointsDivisor"/>.
    /// </summary>
    public const int HeroFame = 0;

    /// <summary>
    /// Points (percent) to permille: the chance is points / 100, expressed in thousandths. A unit conversion, not a
    /// balance number.
    /// </summary>
    private const int PermillePerPoint = 10;

    /// <summary>True when <see cref="Simulation.Tick"/> is the first tick of the arrival hour of its day (once a day).</summary>
    public static bool IsArrivalTick(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        return sim.Tick == sim.Calendar.FirstTickOf(sim.Now.Day, sim.Content.Economy.ArrivalHour);
    }

    /// <summary>The odds of a newcomer right now, with every term of the formula.</summary>
    public static ArrivalOdds ComputeOdds(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        EconomyBalanceDef economy = sim.Content.Economy;
        GameWorld? world = sim.World;
        EntityId center = Construction.FindCenter(sim);
        bool hasCenter = center != EntityId.None && world!.Buildings.Get(center).State != BuildingState.Constructing;
        int population = Construction.SettlementPopulation(sim);
        int freePlaces = world is null ? 0 : Math.Max(0, Housing.TotalFreePlaces(sim) - Housing.CountHomeless(world));
        int slotPoints = economy.ArrivalFreeSlotPoints * Math.Min(freePlaces, economy.ArrivalMaxSlots);
        long food = CountFood(sim);
        long foodNeeded = (long)economy.FoodPerResidentPerDay * (population + 1) * economy.ArrivalFoodDays;
        int foodPoints = food >= foodNeeded ? economy.ArrivalFoodPoints : 0;

        // Raids come with M4: until then no day had a raid, so the raid-free condition always holds.
        int noRaidPoints = economy.ArrivalNoRaidPoints;
        int famePoints = HeroFame / economy.ArrivalFamePointsDivisor;
        int eraOrder = Construction.CurrentEra(sim).Order;
        int eraPoints = economy.ArrivalEraPoints[Math.Min(eraOrder, economy.ArrivalEraPoints.Count) - 1];
        int points = slotPoints + foodPoints + noRaidPoints + famePoints + eraPoints;
        bool possible = hasCenter && freePlaces > 0 && population < economy.MaxResidents;
        int chance = possible ? Math.Min(points * PermillePerPoint, economy.ArrivalChanceCapPermille) : 0;
        return new ArrivalOdds(hasCenter, population, freePlaces, slotPoints, food, foodNeeded, foodPoints, noRaidPoints, famePoints, eraPoints, chance);
    }

    /// <summary>
    /// Food units the settlement can eat: Σ <see cref="ItemDef.FoodValue"/> × count over the hero's backpack and the
    /// available storages (the same places <see cref="Construction.CountAvailable"/> counts).
    /// </summary>
    public static long CountFood(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world)
        {
            return 0;
        }

        long food = 0;
        EntityId hero = world.HeroEntity;
        if (hero != EntityId.None && world.Inventories.TryGet(hero, out Inventory backpack))
        {
            food += FoodIn(sim.Content, backpack);
        }

        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (Construction.IsAvailableStorage(world, owners[i], buildings[i]))
            {
                food += FoodIn(sim.Content, world.Inventories.Get(owners[i]));
            }
        }

        return food;
    }

    /// <summary>
    /// Passable tiles on the ring right around the settlement center's footprint, in tile index order: where residents
    /// gather. Empty without a center.
    /// </summary>
    public static IReadOnlyList<int> MeetingTiles(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        EntityId center = Construction.FindCenter(sim);
        if (center == EntityId.None)
        {
            return [];
        }

        GameWorld world = sim.World!;
        Building building = world.Buildings.Get(center);
        (int width, int height) = Construction.FootprintOf(Construction.DefOf(sim.Content, building), building.Rotation);
        PassabilityGrid grid = world.Pathfinder.Grid;
        var tiles = new List<int>();
        for (int y = building.Y - 1; y <= building.Y + height; y++)
        {
            for (int x = building.X - 1; x <= building.X + width; x++)
            {
                bool ring = x == building.X - 1 || x == building.X + width || y == building.Y - 1 || y == building.Y + height;
                if (ring && grid.IsPassable(x, y))
                {
                    tiles.Add(grid.Index(x, y));
                }
            }
        }

        return tiles;
    }

    /// <summary>
    /// Where a newcomer appears: a passable tile from which one of <paramref name="meeting"/> can be reached. At the site
    /// of good people nearest to the center (sites by squared distance, ties by their order in the map; within a site the
    /// tile nearest to its centre, ring by ring, row by row). Without such a site: the point of the map edge nearest to
    /// the center, or, when that is not reachable land (the map has an ocean border), the first reachable tile on the
    /// straight line from it to the center. -1 without meeting tiles.
    /// </summary>
    public static int FindSpawnTile(Simulation sim, IReadOnlyList<int> meeting)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(meeting);
        if (sim.World is not { } world || meeting.Count == 0)
        {
            return -1;
        }

        Pathfinder pathfinder = world.Pathfinder;
        PassabilityGrid grid = pathfinder.Grid;
        Building center = world.Buildings.Get(Construction.FindCenter(sim));
        (int width, int height) = Construction.FootprintOf(Construction.DefOf(sim.Content, center), center.Rotation);
        int cx = center.X + ((width - 1) / 2);
        int cy = center.Y + ((height - 1) / 2);
        int radius = sim.Content.WorldGen.Settlements.SiteSize / 2;
        foreach (SettlementSite site in GoodSitesByDistance(world.Map, cx, cy))
        {
            for (int ring = 0; ring <= radius; ring++)
            {
                for (int y = site.Y - ring; y <= site.Y + ring; y++)
                {
                    for (int x = site.X - ring; x <= site.X + ring; x++)
                    {
                        if (AgentMovement.Distance(x, y, site.X, site.Y) == ring && grid.IsPassable(x, y) && ReachesAny(pathfinder, grid.Index(x, y), meeting))
                        {
                            return grid.Index(x, y);
                        }
                    }
                }
            }
        }

        // No site to come from: the map edge nearest to the center (the foot of the perpendicular on the nearest side;
        // ties top, left, right, bottom), then straight towards the center until the first tile that reaches it: on a
        // map with an ocean border that is where the coast meets the line.
        int last = grid.Size - 1;
        (int ex, int ey) = (cx, 0);
        int nearest = cy;
        foreach ((int x, int y, int distance) in new[] { (0, cy, cx), (last, cy, last - cx), (cx, last, last - cy) })
        {
            if (distance < nearest)
            {
                (ex, ey, nearest) = (x, y, distance);
            }
        }

        int steps = AgentMovement.Distance(ex, ey, cx, cy);
        for (int step = 0; step <= steps; step++)
        {
            int x = ex + (int)((long)(cx - ex) * step / Math.Max(1, steps));
            int y = ey + (int)((long)(cy - ey) * step / Math.Max(1, steps));
            if (grid.IsPassable(x, y) && ReachesAny(pathfinder, grid.Index(x, y), meeting))
            {
                return grid.Index(x, y);
            }
        }

        return -1;
    }

    /// <summary>The tile of <paramref name="meeting"/> nearest to <paramref name="from"/> that can be reached from it (squared distance, ties by order); -1 when none.</summary>
    public static int NearestMeetingTile(GameWorld world, int from, IReadOnlyList<int> meeting)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(meeting);
        PassabilityGrid grid = world.Pathfinder.Grid;
        int best = -1;
        long bestDistance = long.MaxValue;
        if (!grid.ContainsIndex(from))
        {
            return best;
        }

        foreach (int tile in meeting)
        {
            long dx = grid.XOf(tile) - grid.XOf(from);
            long dy = grid.YOf(tile) - grid.YOf(from);
            long distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance && world.Pathfinder.IsReachable(from, tile))
            {
                best = tile;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static List<SettlementSite> GoodSitesByDistance(WorldMap map, int cx, int cy)
    {
        var sites = new List<(SettlementSite Site, long Distance, int Order)>();
        for (int i = 0; i < map.SettlementSites.Count; i++)
        {
            SettlementSite site = map.SettlementSites[i];
            if (!WorldNames.IsEvil(site.Faction))
            {
                long dx = site.X - cx;
                long dy = site.Y - cy;
                sites.Add((site, (dx * dx) + (dy * dy), i));
            }
        }

        // Full key (distance, then map order), so the unstable sort cannot reorder equal entries.
        sites.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Order.CompareTo(b.Order));
        return sites.ConvertAll(s => s.Site);
    }

    private static bool ReachesAny(Pathfinder pathfinder, int tile, IReadOnlyList<int> meeting)
    {
        foreach (int target in meeting)
        {
            if (pathfinder.IsReachable(tile, target))
            {
                return true;
            }
        }

        return false;
    }

    private static long FoodIn(ContentCatalog content, Inventory inventory)
    {
        long food = 0;
        foreach (ItemSlot slot in inventory.Backpack)
        {
            if (!slot.IsEmpty)
            {
                food += (long)content.Get<ItemDef>(slot.Item).FoodValue * slot.Count;
            }
        }

        return food;
    }
}
