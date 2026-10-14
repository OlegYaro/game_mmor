namespace Core.Logging;

/// <summary>
/// Structured simulation log (NFR-08): a fixed-capacity ring buffer of <see cref="LogEvent"/>s with a minimum
/// level, plus subscribers (dev console, Game). Deterministic: no wall-clock time, events carry the tick number.
/// </summary>
/// <remarks>
/// The log is output only and is deliberately NOT part of the state hash or saves: its contents depend on
/// <see cref="MinimumLevel"/> and <see cref="Capacity"/>, which are viewer settings, and old events are dropped
/// when the ring is full. Including it would make the hash change when someone turns on debug logging.
/// For the same reason simulation logic must never read the log back. Tests can still assert on events.
/// </remarks>
public sealed class SimLog
{
    /// <summary>Default ring capacity.</summary>
    public const int DefaultCapacity = 1024;

    private readonly LogEvent?[] _ring;
    private int _start;
    private int _count;
    private Action<LogEvent>[] _subscribers = [];

    /// <summary>Creates a log keeping the last <paramref name="capacity"/> events at or above <paramref name="minimumLevel"/>.</summary>
    public SimLog(int capacity = DefaultCapacity, LogLevel minimumLevel = LogLevel.Info)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ring = new LogEvent?[capacity];
        MinimumLevel = minimumLevel;
    }

    /// <summary>Maximum number of events kept.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Number of events currently kept.</summary>
    public int Count => _count;

    /// <summary>Events below this level are discarded without being stored or sent to subscribers.</summary>
    public LogLevel MinimumLevel { get; set; }

    /// <summary>Events accepted since creation (including ones later overwritten).</summary>
    public long TotalWritten { get; private set; }

    /// <summary>Events pushed out of the ring because it was full.</summary>
    public long Overwritten { get; private set; }

    /// <summary>True when an event of <paramref name="level"/> would be kept. Check it before building costly fields.</summary>
    public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    /// <summary>Writes an event if its level is enabled; returns it, or null when it was filtered out.</summary>
    public LogEvent? Write(long tick, LogLevel level, string category, string template, params ReadOnlySpan<LogField> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        ArgumentNullException.ThrowIfNull(template);
        if (!IsEnabled(level))
        {
            return null;
        }

        var logEvent = new LogEvent(tick, level, category, template, fields.ToArray());
        if (_count == _ring.Length)
        {
            _ring[_start] = logEvent;
            _start = (_start + 1) % _ring.Length;
            Overwritten++;
        }
        else
        {
            _ring[(_start + _count) % _ring.Length] = logEvent;
            _count++;
        }

        TotalWritten++;
        foreach (Action<LogEvent> subscriber in _subscribers)
        {
            subscriber(logEvent);
        }

        return logEvent;
    }

    /// <summary>Kept events, oldest first.</summary>
    public IReadOnlyList<LogEvent> GetEvents()
    {
        var events = new LogEvent[_count];
        for (int i = 0; i < _count; i++)
        {
            events[i] = _ring[(_start + i) % _ring.Length]!;
        }

        return events;
    }

    /// <summary>Drops all kept events (counters and subscribers stay).</summary>
    public void Clear()
    {
        Array.Clear(_ring);
        _start = 0;
        _count = 0;
    }

    /// <summary>
    /// Calls <paramref name="handler"/> synchronously for every accepted event, in subscription order, until the
    /// returned handle is disposed. Handlers must not change simulation state.
    /// </summary>
    public IDisposable Subscribe(Action<LogEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Copy on write, so a handler may subscribe or unsubscribe while events are being delivered.
        _subscribers = [.. _subscribers, handler];
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<LogEvent> handler)
    {
        int index = Array.IndexOf(_subscribers, handler);
        if (index >= 0)
        {
            _subscribers = [.. _subscribers.AsSpan(0, index), .. _subscribers.AsSpan(index + 1)];
        }
    }

    private sealed class Subscription(SimLog log, Action<LogEvent> handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                log.Unsubscribe(handler);
            }
        }
    }
}
