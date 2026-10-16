using Core.Content;

namespace Core.Economy;

/// <summary>A way items enter or leave the settlement's economy (<see cref="EconomyLedger"/>). Append only.</summary>
public enum LedgerFlow : byte
{
    /// <summary>In: taken from a resource node by a resident or the hero.</summary>
    Gathered = 0,

    /// <summary>In: the yield of a work cycle put into a building's output (a field's harvest).</summary>
    Harvested = 1,

    /// <summary>In: the daily tax that went into the storages.</summary>
    Taxed = 2,

    /// <summary>In: start items and console gifts that went into the hero's backpack.</summary>
    Given = 3,

    /// <summary>In: a refund of a demolished or cancelled building (also the part that then found no room, see <see cref="Lost"/>).</summary>
    Refunded = 4,

    /// <summary>Out: food eaten at the daily meal.</summary>
    Eaten = 5,

    /// <summary>Out: building costs paid.</summary>
    Spent = 6,

    /// <summary>Out: items that found no room and vanished (a refund, the load of a resident who left).</summary>
    Lost = 7,
}

/// <summary>
/// Cumulative item flows of the economy per item since the simulation object was created (FR-ECO-01, FR-ECO-10; the
/// "production per chain" metric of docs/research/economy-six-chains.md): what was gathered, harvested, taxed, given
/// and refunded, and what was eaten, spent and lost. Items only move between places otherwise (node to load, output to
/// load, load to storage, backpack to storage), so the items held anywhere (storages, the hero's backpack, residents'
/// loads, building outputs) always equal the stock at the start plus <see cref="Inflow"/> minus <see cref="Outflow"/>:
/// the conservation check of the bot simulation. Presentation only, like the events: not part of the state, not saved,
/// not hashed; after a load it starts from zero.
/// </summary>
public sealed class EconomyLedger
{
    private const int FlowCount = 8;

    private readonly long[] _amounts;
    private readonly int _items;

    /// <summary>Creates an empty ledger for item ids 1..<paramref name="contentCount"/>.</summary>
    public EconomyLedger(int contentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentCount);
        _items = contentCount + 1;
        _amounts = new long[FlowCount * _items];
    }

    /// <summary>How many <paramref name="item"/> moved by <paramref name="flow"/> so far.</summary>
    public long Get(LedgerFlow flow, DefId item) => (uint)item.Value < (uint)_items ? _amounts[((int)flow * _items) + item.Value] : 0;

    /// <summary>Everything that brought <paramref name="item"/> in: gathered, harvested, taxed, given, refunded.</summary>
    public long Inflow(DefId item) =>
        Get(LedgerFlow.Gathered, item) + Get(LedgerFlow.Harvested, item) + Get(LedgerFlow.Taxed, item) + Get(LedgerFlow.Given, item)
        + Get(LedgerFlow.Refunded, item);

    /// <summary>Everything that took <paramref name="item"/> out: eaten, spent, lost.</summary>
    public long Outflow(DefId item) => Get(LedgerFlow.Eaten, item) + Get(LedgerFlow.Spent, item) + Get(LedgerFlow.Lost, item);

    /// <summary>Counts <paramref name="amount"/> (0 or more) of <paramref name="item"/> moved by <paramref name="flow"/>.</summary>
    internal void Add(LedgerFlow flow, DefId item, int amount)
    {
        if (amount > 0)
        {
            _amounts[((int)flow * _items) + item.Value] += amount;
        }
    }
}
