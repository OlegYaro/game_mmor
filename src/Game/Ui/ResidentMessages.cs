using System.Globalization;
using Core.Economy;
using Core.Residents;
using Core.Sim;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// Turns resident and economy events (<see cref="GameWorld.ResidentEvents"/>, <see cref="GameWorld.EconomyEvents"/>) into toasts:
/// a newcomer, a resident who left hungry, food running low or out, the daily tax. Events are not saved, so the node reads them
/// by <c>Sequence</c> and shows each one once; after the simulation was replaced it starts from the current sequence, so a loaded
/// game does not replay old events. The toast has one line, so messages wait in a short queue and are shown one after another
/// <see cref="GapSeconds"/> apart (the oldest is dropped when more than <see cref="QueueLimit"/> wait, a message already waiting
/// is not queued twice), and the tax is shown at most once a game day.
/// </summary>
public partial class ResidentMessages : Node
{
    /// <summary>Key of "a resident arrived".</summary>
    public const string ArrivedKey = "ui.residents.toast.arrived";

    /// <summary>Key of "a resident left hungry".</summary>
    public const string LeftHungryKey = "ui.residents.toast.left_hungry";

    /// <summary>Key of "food lasts N more days" (<c>{0}</c> is N).</summary>
    public const string FoodLowKey = "ui.residents.toast.food_low";

    /// <summary>Key of "food lasts less than a day".</summary>
    public const string FoodLastDayKey = "ui.residents.toast.food_last_day";

    /// <summary>Key of "food ran out: work slows down".</summary>
    public const string FoodShortageKey = "ui.residents.toast.food_shortage";

    /// <summary>Key of "tax: +N gold" (<c>{0}</c> is N).</summary>
    public const string TaxKey = "ui.residents.toast.tax";

    /// <summary>Real seconds between two toasts.</summary>
    public const double GapSeconds = 2.0;

    /// <summary>Most messages that wait for their turn.</summary>
    public const int QueueLimit = 6;

    // Far in the past, but far enough from the type's minimum for "tick - value" not to overflow.
    private const long NoTaxToast = -1_000_000_000_000L;

    private readonly List<string> _queue = [];
    private SimulationHost? _host;
    private double _cooldown;
    private Simulation? _seen;
    private long _lastResident;
    private long _lastEconomy;
    private long _lastTaxTick = NoTaxToast;

    /// <summary>Connects the node to the host; messages are shown with <see cref="SimulationHost.ShowToast"/>.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        _seen = null;
    }

    public override void _Process(double delta) => Poll(delta);

    /// <summary>
    /// Queues the messages of new events and shows the next waiting one when <see cref="GapSeconds"/> have passed since the
    /// last toast (called every frame with the frame time).
    /// </summary>
    public void Poll(double delta = 0)
    {
        if (_host is not { IsRunning: true } || _host.Simulation.World is not { } world)
        {
            return;
        }

        _cooldown -= delta;
        Collect(_host.Simulation, world);
        if (_queue.Count > 0 && _cooldown <= 0)
        {
            _host.ShowToast(_queue[0]);
            _queue.RemoveAt(0);
            _cooldown = GapSeconds;
        }
    }

    // Reads the events newer than the last seen sequence into the queue.
    private void Collect(Simulation sim, GameWorld world)
    {
        if (!ReferenceEquals(sim, _seen))
        {
            _seen = sim;
            _lastResident = world.LastResidentEventSequence;
            _lastEconomy = world.LastEconomyEventSequence;
            _lastTaxTick = NoTaxToast;
            _queue.Clear();
            _cooldown = 0;
            return;
        }

        if (world.LastResidentEventSequence == _lastResident && world.LastEconomyEventSequence == _lastEconomy)
        {
            return;
        }

        foreach (ResidentEvent entry in world.ResidentEvents)
        {
            if (entry.Sequence <= _lastResident)
            {
                continue;
            }

            if (entry.Kind == ResidentEventKind.LeftHungry)
            {
                Enqueue(LeftHungryKey);
            }
            else if (entry.Kind is ResidentEventKind.Arrived or ResidentEventKind.Spawned)
            {
                Enqueue(ArrivedKey);
            }
        }

        foreach (EconomyEvent entry in world.EconomyEvents)
        {
            if (entry.Sequence <= _lastEconomy)
            {
                continue;
            }

            switch (entry.Kind)
            {
                case EconomyEventKind.FoodShortage:
                    Enqueue(FoodShortageKey);
                    break;
                case EconomyEventKind.FoodLow:
                    Enqueue(entry.DaysLeft > 0 ? Format(FoodLowKey, entry.DaysLeft) : FoodLastDayKey);
                    break;
                case EconomyEventKind.Tax when entry.Amount > 0 && entry.Tick - _lastTaxTick >= TicksPerDay(sim):
                    _lastTaxTick = entry.Tick;
                    Enqueue(Format(TaxKey, entry.Amount));
                    break;
            }
        }

        _lastResident = world.LastResidentEventSequence;
        _lastEconomy = world.LastEconomyEventSequence;
    }

    // Ticks in one game day, from the time balance.
    private static long TicksPerDay(Simulation sim) => (long)sim.Content.Time.TicksPerSecond * sim.Content.Time.RealMinutesPerGameDay * 60;

    // The toast host translates what it is given; a text with a number is translated and filled in here (it is a few seconds old at most).
    private string Format(string key, int value) => string.Format(CultureInfo.InvariantCulture, Tr(key), value);

    private void Enqueue(string message)
    {
        if (_queue.Contains(message))
        {
            return;
        }

        if (_queue.Count == QueueLimit)
        {
            _queue.RemoveAt(0);
        }

        _queue.Add(message);
    }
}
