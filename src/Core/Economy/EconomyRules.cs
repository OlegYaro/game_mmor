using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Logging;
using Core.Residents;
using Core.Sim;

namespace Core.Economy;

/// <summary>
/// The settlement's economy rules that change the state (FR-ECO-01, FR-ECO-06, FR-ECO-10, A5): the daily meal and the
/// tax, the food shortage and hungry residents leaving, storage filters, the hero's deposit, loading the saved economy.
/// Read-only numbers are in <see cref="FoodSupply"/> and <see cref="Storages"/>.
/// </summary>
/// <remarks>
/// <para>At the first tick of every game day (not tick 0): every resident but the leaving ones eats
/// <see cref="EconomyBalanceDef.FoodPerResidentPerDay"/> food units from the available storages. Food items (food value
/// above 0) are eaten in item id order, each from the storages in entity order; an item worth more than the units still
/// missing is eaten whole. When the food ran short, <see cref="GameWorld.DaysWithoutFood"/> grows (work slows down,
/// <see cref="Jobs.JobBoard.WorkSpeedPermille"/>); from <see cref="EconomyBalanceDef.DaysWithoutFoodUntilFirstLeaves"/> days
/// on the resident with the lowest id leaves, and another one every <see cref="EconomyBalanceDef.LeaveIntervalDays"/>
/// days while the shortage lasts. A complete meal ends the shortage; when the rest lasts at most
/// <see cref="EconomyBalanceDef.WarnDaysBeforeEmpty"/> days the player is warned. Then the tax
/// (<see cref="FoodSupply.TaxPerDay"/>) goes into the storages that take <see cref="FoodSupply.TaxItem"/>; what does not
/// fit is lost.</para>
/// </remarks>
internal static class EconomyRules
{
    /// <summary>Log category of economy events.</summary>
    public const string LogCategory = "economy";

    /// <summary>Economy system body: at the start of a day the meal, a shortage's consequences and the tax.</summary>
    public static void Update(Simulation sim)
    {
        if (sim.World is not { } world || sim.Tick == 0 || sim.Now.TickOfDay != 0)
        {
            return;
        }

        Eat(sim, world);
        CollectTax(sim, world);
    }

    /// <summary>Executes <see cref="SetStorageFilterCommand"/>.</summary>
    public static void OrderSetFilter(Simulation sim, EntityId storage, int categories)
    {
        if (sim.World is not { } world || !world.Buildings.TryGet(storage, out Building building) || !Construction.IsStorage(Construction.DefOf(sim.Content, building))
            || building.Settlement != Building.PlayerSettlement)
        {
            Log(sim, LogLevel.Warning, $"{SetStorageFilterCommand.KindName} ignored: {storage} is not a storage of the settlement");
            return;
        }

        if ((categories & ~ItemCategories.AllMask) != 0)
        {
            Log(sim, LogLevel.Warning, $"{SetStorageFilterCommand.KindName} ignored: mask {categories} has bits of no category");
            return;
        }

        if (categories == ItemCategories.AllMask)
        {
            world.StorageFilters.Remove(storage);
        }
        else
        {
            world.StorageFilters.Set(storage, new StorageFilter(categories));
        }

        Log(sim, LogLevel.Info, $"{storage} takes {DescribeCategories(categories)}");
    }

    /// <summary>Executes <see cref="DepositHeroItemsCommand"/>.</summary>
    public static void OrderDeposit(Simulation sim, EntityId storage)
    {
        GameWorld? world = sim.World;
        EntityId hero = world?.HeroEntity ?? EntityId.None;
        if (world is null || hero == EntityId.None || !world.Inventories.Has(hero) || !sim.Positions.TryGet(hero, out Position position))
        {
            Log(sim, LogLevel.Warning, $"{DepositHeroItemsCommand.KindName} ignored: there is no hero");
            return;
        }

        if (!world.Buildings.TryGet(storage, out Building building) || !Construction.IsAvailableStorage(world, storage, building)
            || !Construction.IsStorage(Construction.DefOf(sim.Content, building)))
        {
            Log(sim, LogLevel.Warning, $"{DepositHeroItemsCommand.KindName} ignored: {storage} is not an available storage");
            return;
        }

        (int nx, int ny) = Storages.NearestTile(sim.Content, building, position.TileX, position.TileY);
        if (AgentMovement.Distance(position.TileX, position.TileY, nx, ny) > sim.Content.Hero.InteractionRange)
        {
            Log(sim, LogLevel.Warning, $"{DepositHeroItemsCommand.KindName} ignored: the hero is too far from {storage}");
            return;
        }

        int moved = 0;
        int kept = 0;
        ref Inventory backpack = ref world.Inventories.Get(hero);
        foreach (ItemSlot slot in backpack.Backpack.ToArray())
        {
            if (slot.IsEmpty)
            {
                continue;
            }

            int added = Storages.Deposit(sim, storage, slot.Item, slot.Count);
            backpack = backpack.Remove(slot.Item, added, out _);
            moved += added;
            kept += slot.Count - added;
        }

        world.AddEconomyEvent(sim.Tick, EconomyEventKind.HeroDeposit, string.Empty, moved, kept, 0);
        Log(sim, LogLevel.Info, $"The hero stored {moved} items in {storage}; {kept} stay in the backpack");
    }

    /// <summary>Loads the work outputs, storage filters and food state after the buildings and residents, validating them.</summary>
    /// <exception cref="ArgumentException">An entry is inconsistent.</exception>
    public static void Restore(Simulation sim, WorldState state)
    {
        GameWorld world = sim.World!;
        foreach (ComponentEntry<WorkOutput> entry in state.WorkOutputs)
        {
            if (!world.Buildings.TryGet(entry.Entity, out Building building) || building.State == BuildingState.Constructing
                || Construction.LevelOf(sim.Content, building).Work is not { } work || entry.Value.Count < 0
                || !sim.Content.TryGet(work.Item, out ItemDef item) || entry.Value.Count > item.StackSize)
            {
                throw new ArgumentException($"Work output of {entry.Entity} is invalid: {entry.Value}.", nameof(state));
            }

            world.WorkOutputs.Add(entry.Entity, entry.Value);
        }

        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            bool works = buildings[i].State != BuildingState.Constructing && Construction.LevelOf(sim.Content, buildings[i]).Work is not null;
            if (works != world.WorkOutputs.Has(owners[i]))
            {
                throw new ArgumentException($"Building {owners[i]} must {(works ? string.Empty : "not ")}have a work output.", nameof(state));
            }
        }

        foreach (ComponentEntry<StorageFilter> entry in state.StorageFilters)
        {
            if (!world.Buildings.TryGet(entry.Entity, out Building building) || !Construction.IsStorage(Construction.DefOf(sim.Content, building))
                || entry.Value.Categories < 0 || entry.Value.Categories >= ItemCategories.AllMask)
            {
                throw new ArgumentException($"Storage filter of {entry.Entity} is invalid: {entry.Value}.", nameof(state));
            }

            world.StorageFilters.Add(entry.Entity, entry.Value);
        }

        bool leaveOk = state.LastLeaveDay == GameWorld.NoLeaveDay || (state.DaysWithoutFood > 0 && state.LastLeaveDay >= 0 && state.LastLeaveDay <= sim.Now.Day);
        if (state.DaysWithoutFood < 0 || !leaveOk)
        {
            throw new ArgumentException($"Food state {state.DaysWithoutFood} days without food, last leave day {state.LastLeaveDay} is invalid.", nameof(state));
        }

        world.DaysWithoutFood = state.DaysWithoutFood;
        world.LastLeaveDay = state.LastLeaveDay;
    }

    // The daily meal (see the class remarks).
    private static void Eat(Simulation sim, GameWorld world)
    {
        EconomyBalanceDef economy = sim.Content.Economy;
        long need = FoodSupply.DailyNeed(sim);
        if (need == 0)
        {
            world.DaysWithoutFood = 0;
            world.LastLeaveDay = GameWorld.NoLeaveDay;
            return;
        }

        long missing = need;
        IReadOnlyList<IContentDef> defs = sim.Content.All;
        for (int d = 0; d < defs.Count && missing > 0; d++)
        {
            if (defs[d] is not ItemDef { FoodValue: > 0 } food)
            {
                continue;
            }

            var item = new DefId(d + 1);
            ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
            Span<Building> buildings = world.Buildings.Values;
            for (int i = 0; i < owners.Length && missing > 0; i++)
            {
                if (!Construction.IsAvailableStorage(world, owners[i], buildings[i]))
                {
                    continue;
                }

                ref Inventory stock = ref world.Inventories.Get(owners[i]);
                long wanted = (missing + food.FoodValue - 1) / food.FoodValue;
                stock = stock.Remove(item, (int)Math.Min(wanted, stock.Count(item)), out int eaten);
                world.Ledger.Add(LedgerFlow.Eaten, item, eaten);
                missing -= (long)eaten * food.FoodValue;
            }
        }

        int eatenUnits = (int)Math.Min(int.MaxValue, need - Math.Max(0, missing));
        long day = sim.Now.Day;
        if (missing > 0)
        {
            world.DaysWithoutFood++;
            world.AddEconomyEvent(sim.Tick, EconomyEventKind.FoodShortage, string.Empty, eatenUnits, (int)missing, world.DaysWithoutFood);
            Log(sim, LogLevel.Warning, $"Day {day}: only {eatenUnits} of {need} food units eaten; {world.DaysWithoutFood} days without food");
            bool due = world.LastLeaveDay == GameWorld.NoLeaveDay || day - world.LastLeaveDay >= economy.LeaveIntervalDays;
            if (world.DaysWithoutFood >= economy.DaysWithoutFoodUntilFirstLeaves && due && FirstStaying(world) is { } leaving)
            {
                ResidentRules.Leave(sim, leaving);
                world.LastLeaveDay = day;
            }

            return;
        }

        world.DaysWithoutFood = 0;
        world.LastLeaveDay = GameWorld.NoLeaveDay;
        long daysLeft = FoodSupply.Stored(sim) / need;
        if (daysLeft <= economy.WarnDaysBeforeEmpty)
        {
            world.AddEconomyEvent(sim.Tick, EconomyEventKind.FoodLow, string.Empty, eatenUnits, 0, (int)daysLeft);
        }

        Log(sim, daysLeft <= economy.WarnDaysBeforeEmpty ? LogLevel.Warning : LogLevel.Info,
            $"Day {day}: {eatenUnits} food units eaten; the rest lasts {daysLeft} days");
    }

    // The daily tax into the storages that take gold (see the class remarks).
    private static void CollectTax(Simulation sim, GameWorld world)
    {
        int gold = FoodSupply.TaxPerDay(sim);
        if (gold == 0)
        {
            return;
        }

        DefId item = sim.Content.TryGetId(FoodSupply.TaxItem, out DefId id) && sim.Content.Get(id) is ItemDef
            ? id
            : throw new InvalidOperationException($"The tax item '{FoodSupply.TaxItem}' is not in the content.");
        int added = Storages.DeliverToStorages(sim, item, gold);
        world.Ledger.Add(LedgerFlow.Taxed, item, added);
        world.AddEconomyEvent(sim.Tick, EconomyEventKind.Tax, FoodSupply.TaxItem, added, gold - added, 0);
        Log(sim, added == gold ? LogLevel.Info : LogLevel.Warning, $"Tax: {added} {FoodSupply.TaxItem} stored{(added == gold ? string.Empty : $", {gold - added} lost (no room)")}");
    }

    // The resident with the lowest id that is not leaving yet.
    private static EntityId? FirstStaying(GameWorld world)
    {
        ReadOnlySpan<EntityId> owners = world.Residents.Entities;
        Span<Resident> residents = world.Residents.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (residents[i].State != ResidentState.Leaving)
            {
                return owners[i];
            }
        }

        return null;
    }

    private static string DescribeCategories(int categories)
    {
        var names = new List<string>();
        for (int i = 0; i < ItemCategories.All.Count; i++)
        {
            if ((categories & (1 << i)) != 0)
            {
                names.Add(ItemCategories.All[i]);
            }
        }

        return names.Count == 0 ? "nothing" : string.Join(", ", names);
    }

    private static void Log(Simulation sim, LogLevel level, string message) =>
        sim.Log.Write(sim.Tick, level, LogCategory, "{message}", LogField.Of("message", message));
}
