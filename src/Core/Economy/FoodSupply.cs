using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Residents;
using Core.Sim;

namespace Core.Economy;

/// <summary>
/// Read-only food and tax numbers for the game and the console (FR-ECO-10, FR-ECO-06, MVP "Food" and "Gold"). Food is
/// eaten from the available storages only, once a day; the hero's backpack is the hero's own.
/// </summary>
public static class FoodSupply
{
    /// <summary>Id of the item the tax is paid in.</summary>
    public const string TaxItem = "gold";

    /// <summary>Residents who eat and pay tax: everyone but those leaving.</summary>
    public static int Eaters(GameWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = 0;
        foreach (Resident resident in world.Residents.Values)
        {
            count += resident.State == ResidentState.Leaving ? 0 : 1;
        }

        return count;
    }

    /// <summary>Food units the settlement eats per day: <see cref="EconomyBalanceDef.FoodPerResidentPerDay"/> × <see cref="Eaters"/>.</summary>
    public static long DailyNeed(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        return sim.World is { } world ? (long)sim.Content.Economy.FoodPerResidentPerDay * Eaters(world) : 0;
    }

    /// <summary>Food units in the available storages: Σ <see cref="ItemDef.FoodValue"/> × count.</summary>
    public static long Stored(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world)
        {
            return 0;
        }

        long food = 0;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (Construction.IsAvailableStorage(world, owners[i], buildings[i]))
            {
                foreach (ItemSlot slot in world.Inventories.Get(owners[i]).Backpack)
                {
                    food += slot.IsEmpty ? 0 : (long)sim.Content.Get<ItemDef>(slot.Item).FoodValue * slot.Count;
                }
            }
        }

        return food;
    }

    /// <summary>Whole days the stored food lasts at today's need; -1 when nobody eats.</summary>
    public static long DaysLeft(Simulation sim)
    {
        long need = DailyNeed(sim);
        return need == 0 ? -1 : Stored(sim) / need;
    }

    /// <summary>
    /// Level of the settlement center for the tax: its level while active or moving, the previous level while upgrading,
    /// 0 while under construction or without a center.
    /// </summary>
    public static int CenterLevel(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        EntityId center = Construction.FindCenter(sim);
        if (center == EntityId.None)
        {
            return 0;
        }

        Building building = sim.World!.Buildings.Get(center);
        return building.State switch
        {
            BuildingState.Constructing => 0,
            BuildingState.Upgrading => building.Level - 1,
            _ => building.Level,
        };
    }

    /// <summary>The daily tax in <see cref="TaxItem"/>: <see cref="EconomyBalanceDef.TaxPerResidentPerDay"/> × <see cref="Eaters"/> × <see cref="CenterLevel"/>.</summary>
    public static int TaxPerDay(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        return sim.World is { } world ? sim.Content.Economy.TaxPerResidentPerDay * Eaters(world) * CenterLevel(sim) : 0;
    }
}
