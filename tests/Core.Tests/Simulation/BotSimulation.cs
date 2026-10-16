using System.Globalization;
using System.Text;
using System.Text.Json;
using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Heroes;
using Core.Jobs;
using Core.Residents;
using Core.Sim;
using Core.Tests.Replay;
using Core.Tiles;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Sim;

/// <summary>The bot scenario file <c>tests/Core.Tests/Golden/bot_scenario.json</c> (docs/research/economy-six-chains.md).</summary>
/// <param name="Seed">World seed.</param>
/// <param name="MapSize">Side of the map.</param>
/// <param name="Days">Game days without orders; one CSV row each.</param>
/// <param name="Houses">Level-1 houses built at the start.</param>
/// <param name="Fields">Fields built at the start.</param>
/// <param name="Give">Items given to the hero at the start (building materials, a first food stock).</param>
/// <param name="Residents">Settlement sizes: residents spawned at the start, one run each.</param>
/// <param name="Build">The building demand variant (runs <c>bot_build_&lt;residents&gt;</c>), or null.</param>
internal sealed record BotScenario(ulong Seed, int MapSize, int Days, int Houses, int Fields,
    IReadOnlyList<(string Item, int Count)> Give, IReadOnlyList<int> Residents, BotBuildDemand? Build = null)
{
    public const string FileName = "bot_scenario.json";

    public static BotScenario Load()
    {
        using JsonDocument doc = JsonDocument.Parse(GoldenFiles.ReadText(FileName));
        JsonElement root = doc.RootElement;
        return new BotScenario(
            root.GetProperty("seed").GetUInt64(),
            root.GetProperty("mapSize").GetInt32(),
            root.GetProperty("days").GetInt32(),
            root.GetProperty("houses").GetInt32(),
            root.GetProperty("fields").GetInt32(),
            [.. root.GetProperty("give").EnumerateArray().Select(g => (g.GetProperty("item").GetString()!, g.GetProperty("count").GetInt32()))],
            [.. root.GetProperty("residents").EnumerateArray().Select(r => r.GetInt32())],
            root.TryGetProperty("build", out JsonElement build)
                ? new BotBuildDemand(
                    build.GetProperty("building").GetString()!,
                    build.GetProperty("everyTicks").GetInt32(),
                    build.GetProperty("upgradeTo").GetInt32(),
                    build.GetProperty("keep").GetInt32(),
                    [.. build.GetProperty("residents").EnumerateArray().Select(r => r.GetInt32())])
                : null);
    }

    /// <summary>File name of the golden CSV of the run with <paramref name="residents"/> residents (with the building demand when <paramref name="build"/>).</summary>
    public static string CsvName(int residents, bool build = false) =>
        string.Create(CultureInfo.InvariantCulture, $"bot_{(build ? "build_" : string.Empty)}{residents}.csv");
}

/// <summary>
/// The building demand of the scenario (section <c>build</c>): M3a has no consumers of wood and stone yet, so without it
/// the stocks fill up in a few days and the residents stand idle. Every <paramref name="EveryTicks"/> ticks the bot gives
/// orders from the state alone (deterministic): every finished building of its own below <paramref name="UpgradeTo"/> is
/// upgraded, one new <paramref name="Building"/> is placed near the center while fewer than <paramref name="Keep"/> of its
/// own would stand, and every finished one at <paramref name="UpgradeTo"/> is demolished (spending first, so the refund
/// finds room in the storages). A demolition refunds only part of the cost, so every building passing through costs wood
/// and stone; orders spread over the day let the residents refill the stock in between. An order that cannot be paid or
/// placed is simply refused.
/// </summary>
/// <param name="Building">Building the bot places, upgrades and demolishes.</param>
/// <param name="EveryTicks">Ticks between two rounds of orders.</param>
/// <param name="UpgradeTo">Level a building is upgraded to before it is demolished.</param>
/// <param name="Keep">Buildings of the bot standing at once at most.</param>
/// <param name="Residents">Settlement sizes of this variant, one run each.</param>
internal sealed record BotBuildDemand(string Building, int EveryTicks, int UpgradeTo, int Keep, IReadOnlyList<int> Residents);

/// <summary>The metrics of one game day of a bot run (one CSV row): the state at the first tick of the next day, after the meal.</summary>
internal sealed record BotDay(
    long Day,
    int Population,
    int Arrivals,
    int Departures,
    int Wood,
    int Stone,
    int Berries,
    int Vegetables,
    int Gold,
    long Food,
    long FoodNet,
    int IdlePermille,
    int ZeroFoodDays,
    int Busy,
    int FreeFieldSlots,
    int OutputWaiting,
    long GatheredWood,
    long GatheredStone,
    long GatheredBerries,
    long Harvested,
    long Eaten,
    long Lost,
    long SpentWood,
    long SpentStone,
    int Trails,
    int Roads,
    int Stumps,
    int Saplings,
    int GrownTrees,
    int RegrownStumps)
{
    // grown_trees counts the trees standing on tiles without a generated tree (flora); a stump on a map tree's tile grows
    // back into the map's tree and leaves the flora, so regrown_stumps counts the stumps that became trees that day.
    public const string Header =
        "day,population,arrivals,departures,wood,stone,berries,vegetables,gold,food,food_net,idle_permille,zero_food_days,busy,free_field_slots,"
        + "output_waiting,gathered_wood,gathered_stone,gathered_berries,harvested,eaten,lost,spent_wood,spent_stone,trails,roads,stumps,saplings,"
        + "grown_trees,regrown_stumps";

    public string ToCsv() => string.Join(',', new object[]
    {
        Day, Population, Arrivals, Departures, Wood, Stone, Berries, Vegetables, Gold, Food, FoodNet, IdlePermille, ZeroFoodDays, Busy,
        FreeFieldSlots, OutputWaiting, GatheredWood, GatheredStone, GatheredBerries, Harvested, Eaten, Lost, SpentWood, SpentStone, Trails, Roads,
        Stumps, Saplings, GrownTrees, RegrownStumps,
    }.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
}

/// <summary>The outcome of a bot run: one row per day and the CSV text.</summary>
internal sealed record BotRun(IReadOnlyList<BotDay> Days, string Csv, long SetupTicks, double Seconds);

/// <summary>
/// The bot simulation of docs/research/economy-six-chains.md: a new game, a settlement set up by orders at the start
/// (materials and food given, campfire, houses, storage, fields, the leftovers handed in at the storage, residents
/// spawned), then no order at all for the given days, or only the building orders of <see cref="BotBuildDemand"/>.
/// Every day the metrics go into a CSV row and the invariants are checked: no negative or overfull stock, every item
/// accounted for (held = gathered + harvested + taxed + given + refunded − eaten − spent − lost,
/// <see cref="EconomyLedger"/>), the population never 0.
/// </summary>
internal static class BotSimulation
{
    /// <summary>
    /// Plays the scenario with <paramref name="residents"/> residents for <paramref name="days"/> days, with the building
    /// orders of <paramref name="build"/> when given.
    /// </summary>
    public static BotRun Run(BotScenario scenario, int residents, int days, BotBuildDemand? build = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, new NewGameOptions(scenario.Seed, scenario.MapSize, Difficulty.Normal));
        Setup(sim, scenario, residents);
        long setupTicks = sim.Tick;
        GameWorld world = sim.World!;
        int ticksPerDay = sim.Calendar.TicksPerDay;
        var rows = new List<BotDay>();
        var ledger = new long[_ledgerColumns.Length];
        long food = FoodSupply.Stored(sim);
        int zeroFoodDays = 0;
        int arrivals = 0;
        int departures = 0;
        long residentTicks = 0;
        long idleTicks = 0;
        var stumps = new List<int>();
        int regrownStumps = 0;
        var built = new List<EntityId>();
        Assert.True(sim.Tick < ticksPerDay, $"The setup took {sim.Tick} ticks, more than the first day.");
        while (sim.Tick < (long)days * ticksPerDay)
        {
            // Flora grows only in the first tick of a day (TileSystem), before any resident or the hero works: the stumps
            // standing before that tick that are trees after it grew back.
            bool dayStarts = (sim.Tick + 1) % ticksPerDay == 0;
            if (dayStarts)
            {
                stumps.Clear();
                world.Tiles.CollectFlora(FloraState.Stump, stumps);
            }

            sim.Step();
            if (dayStarts)
            {
                regrownStumps += stumps.Count(t => world.Tiles.GetEffectiveResource(t % world.Size, t / world.Size) == Core.WorldGen.ResourceKind.Tree);
            }

            foreach (ResidentEvent e in world.ResidentEvents)
            {
                if (e.Tick == sim.Tick)
                {
                    arrivals += e.Kind == ResidentEventKind.Arrived ? 1 : 0;
                    departures += e.Kind == ResidentEventKind.LeftHungry ? 1 : 0;
                }
            }

            ReadOnlySpan<EntityId> owners = world.Residents.Entities;
            for (int i = 0; i < owners.Length; i++)
            {
                if (world.Residents.Values[i].State is not (ResidentState.Arriving or ResidentState.Leaving))
                {
                    residentTicks++;
                    idleTicks += world.Jobs.Get(owners[i]).Kind == JobKind.None ? 1 : 0;
                }
            }

            if (build is not null)
            {
                OrderBuildings(sim, build, built);
            }

            if (sim.Now.TickOfDay != 0)
            {
                continue;
            }

            CheckInvariants(sim);
            long stored = FoodSupply.Stored(sim);
            zeroFoodDays += world.DaysWithoutFood > 0 ? 1 : 0;
            long[] now = LedgerTotals(sim);
            rows.Add(new BotDay(
                sim.Now.Day,
                world.Residents.Count,
                arrivals,
                departures,
                Stored(sim, "wood"),
                Stored(sim, "stone"),
                Stored(sim, "berries"),
                Stored(sim, "vegetable"),
                Stored(sim, FoodSupply.TaxItem),
                stored,
                stored - food,
                residentTicks == 0 ? 0 : (int)(idleTicks * 1000 / residentTicks),
                zeroFoodDays,
                world.Jobs.Values.ToArray().Count(j => j.Kind != JobKind.None),
                FreeFieldSlots(sim),
                world.WorkOutputs.Values.ToArray().Sum(o => o.Count),
                now[0] - ledger[0],
                now[1] - ledger[1],
                now[2] - ledger[2],
                now[3] - ledger[3],
                now[4] - ledger[4],
                now[5] - ledger[5],
                now[6] - ledger[6],
                now[7] - ledger[7],
                world.Tiles.TrailTiles,
                world.Tiles.RoadTiles,
                CountFlora(world, FloraState.Stump),
                CountFlora(world, FloraState.Sapling),
                CountFlora(world, FloraState.Tree),
                regrownStumps));
            (ledger, food, arrivals, departures, residentTicks, idleTicks, regrownStumps) = (now, stored, 0, 0, 0, 0, 0);
            Assert.True(world.Residents.Count > 0, $"Day {sim.Now.Day}: the settlement is empty.");
        }

        var csv = new StringBuilder();
        csv.Append(BotDay.Header).Append('\n');
        foreach (BotDay row in rows)
        {
            csv.Append(row.ToCsv()).Append('\n');
        }

        return new BotRun(rows, csv.ToString(), setupTicks, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    /// <summary>
    /// Every item held anywhere: storages and the hero's backpack and equipment, residents' loads, building outputs; by
    /// item id value.
    /// </summary>
    public static long[] Census(Simulation sim)
    {
        GameWorld world = sim.World!;
        var held = new long[sim.Content.Count + 1];
        foreach (Inventory inventory in world.Inventories.Values)
        {
            foreach (ItemSlot slot in inventory.Backpack.Concat(inventory.Equipment))
            {
                held[slot.Item.Value] += slot.IsEmpty ? 0 : slot.Count;
            }
        }

        foreach (ResidentJob job in world.Jobs.Values)
        {
            held[job.CarryItem.Value] += job.CarryCount;
        }

        ReadOnlySpan<EntityId> outputs = world.WorkOutputs.Entities;
        for (int i = 0; i < outputs.Length; i++)
        {
            WorkDef work = Construction.LevelOf(sim.Content, world.Buildings.Get(outputs[i])).Work!;
            Assert.True(sim.Content.TryGetId(work.Item, out DefId item));
            held[item.Value] += world.WorkOutputs.Values[i].Count;
        }

        return held;
    }

    /// <summary>
    /// The daily checks: no slot negative or above its stack, no inventory with more stacks than it has, no storage with
    /// more of an item than its stacks per item, no load above the haul capacity, no output above a stack, and every item
    /// accounted for by the ledger.
    /// </summary>
    public static void CheckInvariants(Simulation sim)
    {
        GameWorld world = sim.World!;
        foreach (Inventory inventory in world.Inventories.Values)
        {
            foreach (ItemSlot slot in inventory.Backpack.Concat(inventory.Equipment))
            {
                Assert.True(slot.IsEmpty || (slot.Count > 0 && slot.Count <= sim.Content.Get<ItemDef>(slot.Item).StackSize),
                    $"Day {sim.Now.Day}: slot {slot} is negative or above its stack.");
            }

            Assert.InRange(inventory.UsedStacks, 0, inventory.Backpack.Count);
        }

        // No stock grows without a bound: a storage holds at most its stacks per item of anything.
        ReadOnlySpan<EntityId> buildings = world.Buildings.Entities;
        for (int i = 0; i < buildings.Length; i++)
        {
            if (Construction.IsStorage(Construction.DefOf(sim.Content, world.Buildings.Values[i])) && world.Inventories.TryGet(buildings[i], out Inventory stock))
            {
                foreach (ItemSlot slot in stock.Backpack.Where(s => !s.IsEmpty))
                {
                    Assert.InRange(stock.Count(slot.Item), 1, sim.Content.Economy.StorageStacksPerItem * sim.Content.Get<ItemDef>(slot.Item).StackSize);
                }
            }
        }

        foreach (ResidentJob job in world.Jobs.Values)
        {
            Assert.InRange(job.CarryCount, 0, sim.Content.Economy.HaulCapacity);
        }

        foreach (WorkOutput output in world.WorkOutputs.Values)
        {
            Assert.InRange(output.Count, 0, 100);
        }

        long[] held = Census(sim);
        for (int value = 1; value < held.Length; value++)
        {
            var item = new DefId(value);
            long expected = world.Ledger.Inflow(item) - world.Ledger.Outflow(item);
            Assert.True(held[value] == expected,
                $"Day {sim.Now.Day}: {sim.Content.Get(item).Id} held {held[value]}, but the ledger says {expected} (in {world.Ledger.Inflow(item)}, out {world.Ledger.Outflow(item)}).");
        }
    }

    private static readonly (LedgerFlow Flow, string Item)[] _ledgerColumns =
    [
        (LedgerFlow.Gathered, "wood"), (LedgerFlow.Gathered, "stone"), (LedgerFlow.Gathered, "berries"), (LedgerFlow.Harvested, "vegetable"),
        (LedgerFlow.Eaten, string.Empty), (LedgerFlow.Lost, string.Empty), (LedgerFlow.Spent, "wood"), (LedgerFlow.Spent, "stone"),
    ];

    // Cumulative ledger values of the CSV columns (an empty item sums the flow over every item).
    private static long[] LedgerTotals(Simulation sim)
    {
        EconomyLedger ledger = sim.World!.Ledger;
        var totals = new long[_ledgerColumns.Length];
        for (int c = 0; c < _ledgerColumns.Length; c++)
        {
            (LedgerFlow flow, string item) = _ledgerColumns[c];
            totals[c] = item.Length == 0
                ? Enumerable.Range(1, sim.Content.Count).Sum(v => ledger.Get(flow, new DefId(v)))
                : ledger.Get(flow, Jobs.JobTestWorld.Item(sim, item));
        }

        return totals;
    }

    private static int Stored(Simulation sim, string item) => Jobs.JobTestWorld.StoredEverywhere(sim, item) - HeroCount(sim, item);

    private static int HeroCount(Simulation sim, string item) =>
        sim.World!.Inventories.TryGet(sim.World.HeroEntity, out Inventory backpack) ? backpack.Count(Jobs.JobTestWorld.Item(sim, item)) : 0;

    private static int FreeFieldSlots(Simulation sim)
    {
        GameWorld world = sim.World!;
        int free = 0;
        ReadOnlySpan<EntityId> outputs = world.WorkOutputs.Entities;
        for (int i = 0; i < outputs.Length; i++)
        {
            WorkDef work = Construction.LevelOf(sim.Content, world.Buildings.Get(outputs[i])).Work!;
            free += work.Slots - world.Reservations.HolderCount(ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, outputs[i]));
        }

        return free;
    }

    private static int CountFlora(GameWorld world, FloraState state)
    {
        int count = 0;
        foreach (FloraChunkState chunk in world.Tiles.CaptureState().Flora)
        {
            count += chunk.States.Count(s => s == state);
        }

        return count;
    }

    // The bot's building orders after a tick (see BotBuildDemand); `built` holds its own buildings, oldest first. The
    // orders execute in the next tick, in the order given.
    private static void OrderBuildings(Simulation sim, BotBuildDemand build, List<EntityId> built)
    {
        GameWorld world = sim.World!;
        for (int i = world.BuildingEvents.Count - 1; i >= 0 && world.BuildingEvents[i].Tick == sim.Tick; i--)
        {
            BuildingEvent e = world.BuildingEvents[i];
            if (e.Command == PlaceBuildingCommand.KindName && e.Result == BuildResult.Ok)
            {
                built.Add(e.Building);
            }
        }

        built.RemoveAll(b => !world.Buildings.Has(b));
        if (sim.Tick % build.EveryTicks != 0)
        {
            return;
        }

        List<EntityId> demolish = [];
        foreach (EntityId b in built.Where(b => world.Buildings.Get(b).State == BuildingState.Active))
        {
            if (world.Buildings.Get(b).Level < build.UpgradeTo)
            {
                sim.Enqueue(new UpgradeBuildingCommand(b));
            }
            else
            {
                demolish.Add(b);
            }
        }

        if (built.Count - demolish.Count < build.Keep && TryFindSpot(sim, build.Building, Construction.FindCenter(sim)) is { } spot)
        {
            sim.Enqueue(new PlaceBuildingCommand(build.Building, spot.X, spot.Y));
        }

        foreach (EntityId b in demolish)
        {
            sim.Enqueue(new DemolishBuildingCommand(b));
        }
    }

    private static (int X, int Y)? TryFindSpot(Simulation sim, string id, EntityId center)
    {
        Building building = sim.World!.Buildings.Get(center);
        try
        {
            return FindSpot(sim, id, minDistance: 2, near: (building.X, building.Y));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The orders of the start: give, build, hand in, spawn; each waits for the previous to take effect.</summary>
    public static void Setup(Simulation sim, BotScenario scenario, int residents)
    {
        GameWorld world = sim.World!;
        foreach ((string item, int count) in scenario.Give)
        {
            sim.Enqueue(new GiveItemCommand(item, count));
        }

        sim.Step();
        Place(sim, "campfire", Heroes.HeroTestWorld.HeroTile(sim));
        Building center = world.Buildings.Get(Construction.FindCenter(sim));
        Place(sim, "storage", (center.X, center.Y));
        for (int i = 0; i < scenario.Houses; i++)
        {
            Place(sim, "house", (center.X, center.Y));
        }

        for (int i = 0; i < scenario.Fields; i++)
        {
            Place(sim, "field", (center.X, center.Y));
        }

        Heroes.HeroTestWorld.StepUntil(sim, () => world.Buildings.Values.ToArray().All(b => b.State == BuildingState.Active), limit: 2000);
        EntityId storage = M3aScenario.Find(sim, "storage");
        sim.Enqueue(M3aScenario.WalkNextTo(sim, storage));
        sim.Step();
        Heroes.HeroTestWorld.StepUntil(sim, () => !world.Movers.Get(world.HeroEntity).IsMoving, limit: 2000);
        sim.Enqueue(new DepositHeroItemsCommand(storage));
        for (int i = 0; i < residents; i++)
        {
            sim.Enqueue(new SpawnResidentCommand());
        }

        sim.Step();
        Assert.Equal(residents, world.Residents.Count);
    }

    private static void Place(Simulation sim, string id, (int X, int Y) near)
    {
        (int x, int y) = FindSpot(sim, id, minDistance: 2, near: near);
        sim.Enqueue(new PlaceBuildingCommand(id, x, y));
        sim.Step();
        Assert.Equal(BuildResult.Ok, sim.World!.BuildingEvents[^1].Result);
    }
}
