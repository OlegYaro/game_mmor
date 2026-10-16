namespace Core.Economy;

/// <summary>What happened in the settlement's economy (<see cref="EconomyEvent"/>). Append only.</summary>
public enum EconomyEventKind : byte
{
    /// <summary>
    /// The day's meal was complete, but the stored food lasts at most <see cref="Content.EconomyBalanceDef.WarnDaysBeforeEmpty"/>
    /// more days: <see cref="EconomyEvent.DaysLeft"/> says how many; <see cref="EconomyEvent.Amount"/> is the food eaten.
    /// </summary>
    FoodLow = 0,

    /// <summary>
    /// The storages did not hold enough food for the day: <see cref="EconomyEvent.Amount"/> food units were eaten,
    /// <see cref="EconomyEvent.Lost"/> were missing; <see cref="EconomyEvent.DaysLeft"/> is the days without food so far.
    /// </summary>
    FoodShortage = 1,

    /// <summary>
    /// The daily tax: <see cref="EconomyEvent.Amount"/> of <see cref="EconomyEvent.Item"/> went into the storages,
    /// <see cref="EconomyEvent.Lost"/> did not fit and were lost.
    /// </summary>
    Tax = 2,

    /// <summary>
    /// The hero handed the backpack in at a storage: <see cref="EconomyEvent.Amount"/> items went in,
    /// <see cref="EconomyEvent.Lost"/> stayed in the backpack (no room, or the filter refuses them).
    /// </summary>
    HeroDeposit = 3,
}

/// <summary>
/// An economy event for the game's event panel ("food runs out in 2 days", "the tax brought 10 gold"). Presentation
/// only: not part of the state, not saved, not hashed (like the log); <see cref="Sim.GameWorld.EconomyEvents"/> keeps the
/// latest ones. Residents leaving because of hunger are resident events (<see cref="Residents.ResidentEventKind.LeftHungry"/>).
/// </summary>
/// <param name="Sequence">Running number (1, 2, ...) since the simulation object was created.</param>
/// <param name="Tick">Tick it happened in.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Item">The item id of a tax; empty otherwise.</param>
/// <param name="Amount">See <see cref="EconomyEventKind"/>.</param>
/// <param name="Lost">See <see cref="EconomyEventKind"/>.</param>
/// <param name="DaysLeft">See <see cref="EconomyEventKind"/>; 0 where it has no meaning.</param>
public sealed record EconomyEvent(long Sequence, long Tick, EconomyEventKind Kind, string Item, int Amount, int Lost, int DaysLeft);
