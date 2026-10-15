using Core.Content;
using Core.Determinism;
using Core.Entities;

namespace Core.Heroes;

/// <summary>What the hero is doing. Values are part of the state hash; save names are in the save mapper. Append only.</summary>
public enum HeroActivity : byte
{
    /// <summary>Standing (possibly finishing the step it was on when stopped).</summary>
    Idle = 0,

    /// <summary>Walking to the goal of its <see cref="Mover"/>.</summary>
    Walking = 1,

    /// <summary>Gathering the node at (<see cref="Hero.TargetX"/>, <see cref="Hero.TargetY"/>): walking up to it first, then working.</summary>
    Gathering = 2,
}

/// <summary>Why the hero's last activity ended (shown by the game). Append only.</summary>
public enum HeroStopReason : byte
{
    /// <summary>Nothing has ended yet.</summary>
    None = 0,

    /// <summary>Reached the walking goal.</summary>
    Arrived = 1,

    /// <summary>Stopped by a <see cref="Sim.StopHeroCommand"/>. A new order that replaces the activity is not a stop.</summary>
    Stopped = 2,

    /// <summary>The way became blocked and no new path was found.</summary>
    PathBlocked = 3,

    /// <summary>Gathering stopped: no room in the backpack for the item.</summary>
    BackpackFull = 4,

    /// <summary>Gathering stopped: the node was used up (it disappeared from the map).</summary>
    Depleted = 5,

    /// <summary>Gathering stopped: the target tile has no node the hero can gather.</summary>
    NoResource = 6,
}

/// <summary>
/// Outcome of the hero's most recent order (<see cref="Sim.MoveHeroCommand"/>, <see cref="Sim.GatherCommand"/>,
/// <see cref="Sim.StopHeroCommand"/>). Rejected orders change nothing else. Append only.
/// </summary>
public enum HeroOrderResult : byte
{
    /// <summary>No order yet.</summary>
    None = 0,

    /// <summary>The order was accepted.</summary>
    Accepted = 1,

    /// <summary>Rejected: the target is outside the map.</summary>
    OutsideMap = 2,

    /// <summary>Rejected: the target tile is still unknown in the fog of war.</summary>
    Unexplored = 3,

    /// <summary>Rejected: the target tile cannot be stood on (water, river, an occupied tile).</summary>
    Impassable = 4,

    /// <summary>Rejected: no path leads there (or to a tile next to the node).</summary>
    Unreachable = 5,

    /// <summary>Rejected: the target tile has no node the hero can gather.</summary>
    NoResource = 6,
}

/// <summary>
/// The hero (FR-HERO-01): characteristics, health, stamina and the current activity. Exactly one entity has it in M2
/// (<see cref="Sim.GameWorld.HeroEntity"/>). Maximum health and stamina come from <see cref="HeroBalanceDef"/>.
/// </summary>
/// <param name="Attack">Attack.</param>
/// <param name="Defense">Defense.</param>
/// <param name="SpellPower">Spell Power.</param>
/// <param name="Knowledge">Knowledge.</param>
/// <param name="Health">Current health, 0 to <see cref="HeroBalanceDef.Health"/>.</param>
/// <param name="Stamina">Current stamina, 0 to <see cref="HeroBalanceDef.Stamina"/>; sprinting spends it.</param>
/// <param name="Activity">What the hero is doing.</param>
/// <param name="TargetX">Column of the node being gathered; -1 when not gathering.</param>
/// <param name="TargetY">Row of the node being gathered; -1 when not gathering.</param>
/// <param name="ActionTicks">Ticks spent on the current gathering action (0 to <see cref="GatherDef.TicksPerAction"/> − 1).</param>
/// <param name="LastStop">Why the last activity ended.</param>
/// <param name="LastOrder">Outcome of the most recent order.</param>
public readonly record struct Hero(
    int Attack,
    int Defense,
    int SpellPower,
    int Knowledge,
    int Health,
    int Stamina,
    HeroActivity Activity,
    int TargetX,
    int TargetY,
    int ActionTicks,
    HeroStopReason LastStop,
    HeroOrderResult LastOrder) : IComponent
{
    /// <summary>A fresh hero from the balance: start characteristics, full health and stamina, idle.</summary>
    public static Hero FromBalance(HeroBalanceDef balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        return new Hero(balance.Attack, balance.Defense, balance.SpellPower, balance.Knowledge, balance.Health, balance.Stamina,
            HeroActivity.Idle, -1, -1, 0, HeroStopReason.None, HeroOrderResult.None);
    }

    /// <summary>The same hero, idle, with <paramref name="reason"/> as the last stop and no gathering target.</summary>
    public Hero Stop(HeroStopReason reason) => this with
    {
        Activity = HeroActivity.Idle,
        TargetX = -1,
        TargetY = -1,
        ActionTicks = 0,
        LastStop = reason,
    };

    /// <summary>Writes every field in declaration order (Int32, enums as one byte).</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(Attack);
        writer.WriteInt32(Defense);
        writer.WriteInt32(SpellPower);
        writer.WriteInt32(Knowledge);
        writer.WriteInt32(Health);
        writer.WriteInt32(Stamina);
        writer.WriteByte((byte)Activity);
        writer.WriteInt32(TargetX);
        writer.WriteInt32(TargetY);
        writer.WriteInt32(ActionTicks);
        writer.WriteByte((byte)LastStop);
        writer.WriteByte((byte)LastOrder);
    }
}
