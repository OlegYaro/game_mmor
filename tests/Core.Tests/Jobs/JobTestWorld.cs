using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Heroes;
using Core.Jobs;
using Core.Pathfinding;
using Core.Residents;
using Core.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Jobs;

/// <summary>Helpers for job board, storage and food tests on the small real game of the building tests (64², the hero test seed).</summary>
internal static class JobTestWorld
{
    /// <summary>
    /// The real data without arrivals (no free-place points, no raid points: chance 0), so only spawned residents live in
    /// the test settlements.
    /// </summary>
    private static readonly Lazy<ContentCatalog> _quiet = new(() => Residents.ResidentTestWorld.WithEconomy(json => json
        .Replace("\"arrivalFreeSlotPoints\": 25", "\"arrivalFreeSlotPoints\": 0", StringComparison.Ordinal)
        .Replace("\"arrivalFoodPoints\": 25", "\"arrivalFoodPoints\": 0", StringComparison.Ordinal)
        .Replace("\"arrivalNoRaidPoints\": 20", "\"arrivalNoRaidPoints\": 0", StringComparison.Ordinal)
        .Replace("\"arrivalEraPoints\": [ 0, 10, 20, 30 ]", "\"arrivalEraPoints\": [ 0, 0, 0, 0 ]", StringComparison.Ordinal)));

    /// <summary>The real data, except that nobody arrives on their own.</summary>
    public static ContentCatalog Quiet => _quiet.Value;

    /// <summary>A new 64² game of the hero test seed with a built campfire and storage near the hero (no residents).</summary>
    public static (Simulation Sim, EntityId Storage) NewSettlement(ContentCatalog? content = null, bool storage = true)
    {
        Simulation sim = NewGame(content ?? Quiet);
        Build(sim, "campfire");
        return (sim, storage ? Build(sim, "storage") : EntityId.None);
    }

    /// <summary>Spawns a resident of <paramref name="profession"/> at the center and returns it.</summary>
    public static EntityId Spawn(Simulation sim, string profession) => Residents.ResidentTestWorld.Spawn(sim, profession);

    public static ResidentJob JobOf(Simulation sim, EntityId resident) => sim.World!.Jobs.Get(resident);

    public static DefId Item(Simulation sim, string id)
    {
        Assert.True(sim.Content.TryGetId(id, out DefId item));
        return item;
    }

    /// <summary>How many <paramref name="item"/> lie in <paramref name="storage"/>.</summary>
    public static int Stored(Simulation sim, EntityId storage, string item) => sim.World!.Inventories.Get(storage).Count(Item(sim, item));

    /// <summary>How many <paramref name="item"/> lie in every storage together.</summary>
    public static int StoredEverywhere(Simulation sim, string item)
    {
        GameWorld world = sim.World!;
        int total = 0;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            if (world.Inventories.TryGet(owners[i], out Inventory stock))
            {
                total += stock.Count(Item(sim, item));
            }
        }

        return total;
    }

    /// <summary>How many <paramref name="item"/> the residents carry.</summary>
    public static int Carried(Simulation sim, string item)
    {
        DefId id = Item(sim, item);
        return sim.World!.Jobs.Values.ToArray().Where(j => j.CarryItem == id).Sum(j => j.CarryCount);
    }

    /// <summary>Puts items straight into a storage (no command, no tick).</summary>
    public static void Fill(Simulation sim, EntityId storage, string item, int count)
    {
        DefId id = Item(sim, item);
        ref Inventory stock = ref sim.World!.Inventories.Get(storage);
        stock = stock.Add(id, count, sim.Content.Get<ItemDef>(id).StackSize, out int added);
        Assert.Equal(count, added);
    }

    /// <summary>Sets every priority of <paramref name="resident"/> at once (straight into the component).</summary>
    public static void SetPriorities(Simulation sim, EntityId resident, params (JobKind Kind, int Priority)[] priorities)
    {
        var set = default(WorkPriorities);
        foreach ((JobKind kind, int priority) in priorities)
        {
            set = set.With(kind, priority);
        }

        ref Resident r = ref sim.World!.Residents.Get(resident);
        r = r with { Priorities = set };
    }

    /// <summary>The tile index of (x, y).</summary>
    public static int TileIndex(Simulation sim, int x, int y) => (y * sim.World!.Size) + x;

    /// <summary>
    /// Every tile with a node of <paramref name="kind"/> that a resident standing on <paramref name="from"/> could work,
    /// in the job board's order: Chebyshev ring around it, then tile index (an independent scan of the whole map).
    /// </summary>
    public static List<int> NodesByRing(Simulation sim, ResourceKind kind, (int X, int Y) from)
    {
        GameWorld world = sim.World!;
        Pathfinder pathfinder = world.Pathfinder;
        Territory territory = Construction.GetTerritory(sim)!.Value;
        int radius = territory.Radius + sim.Content.Economy.JobSearchMarginTiles;
        int component = pathfinder.Connectivity.GetComponent(from.X, from.Y);
        var nodes = new List<(int Ring, int Index)>();
        for (int y = 0; y < world.Size; y++)
        {
            for (int x = 0; x < world.Size; x++)
            {
                long dx = x - territory.CenterX;
                long dy = y - territory.CenterY;
                bool near = Enumerable.Range(0, PassabilityGrid.DirectionCount).Any(d =>
                    pathfinder.Grid.Contains(x + PassabilityGrid.DirectionX(d), y + PassabilityGrid.DirectionY(d))
                    && pathfinder.Connectivity.GetComponent(x + PassabilityGrid.DirectionX(d), y + PassabilityGrid.DirectionY(d)) == component);
                if (world.Tiles.GetEffectiveResource(x, y) == kind && (dx * dx) + (dy * dy) <= (long)radius * radius && near)
                {
                    nodes.Add((AgentMovement.Distance(x, y, from.X, from.Y), TileIndex(sim, x, y)));
                }
            }
        }

        return nodes.OrderBy(n => n.Ring).ThenBy(n => n.Index).Select(n => n.Index).ToList();
    }

    /// <summary>Runs until <paramref name="done"/> holds; fails after <paramref name="limit"/> ticks.</summary>
    public static int StepUntil(Simulation sim, Func<bool> done, int limit = 20_000) => Heroes.HeroTestWorld.StepUntil(sim, done, limit);

    /// <summary>Steps to the first tick of <paramref name="day"/> (the daily meal runs in it).</summary>
    public static void StepToDay(Simulation sim, long day) => Residents.ResidentTestWorld.StepTo(sim, sim.Calendar.FirstTickOf(day, 0));
}
