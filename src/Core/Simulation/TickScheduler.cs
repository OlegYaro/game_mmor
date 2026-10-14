namespace Core.Sim;

/// <summary>Simulation speed multipliers (FR-TIME-02). The numeric value is the multiplier.</summary>
public enum GameSpeed
{
    /// <summary>Normal speed.</summary>
    X1 = 1,

    /// <summary>Double speed.</summary>
    X2 = 2,

    /// <summary>Quadruple speed.</summary>
    X4 = 4,

    /// <summary>Developer speed, only allowed when <see cref="TickScheduler.DevSpeedsEnabled"/> is set.</summary>
    X20 = 20,
}

/// <summary>
/// Decides how many fixed ticks the host runs per rendered frame (ADR 0005): an integer time accumulator,
/// pause, speed multiplier and a per-frame cap. Pure logic, no clock of its own: the host passes elapsed time.
/// </summary>
/// <remarks>
/// The accumulator counts millionths of a tick, so <c>elapsed us * speed * ticksPerSecond</c> adds up exactly and
/// never drifts. When more than <see cref="MaxTicksPerFrame"/> whole ticks are due (a hitch, or a slow machine),
/// the extra whole ticks are dropped and counted in <see cref="DroppedTicks"/> while the fractional remainder is
/// kept: the game slows down instead of spiraling into ever longer frames. Pausing freezes the accumulator.
/// Input is microseconds because truncating every frame to whole milliseconds loses up to 1 ms per frame
/// (about 7 % at 144 fps); <see cref="AdvanceMilliseconds"/> is there for coarse callers and tests.
/// </remarks>
public sealed class TickScheduler
{
    /// <summary>Maximum ticks per frame from ADR 0005.</summary>
    public const int DefaultMaxTicksPerFrame = 4;

    private const long UnitsPerTick = 1_000_000;
    private const long UnitsPerPermille = UnitsPerTick / 1000;
    private const long MicrosecondsPerMillisecond = 1000;

    // Elapsed time is clamped so the accumulator math cannot overflow; anything this long is dropped anyway.
    private readonly long _maxElapsedMicroseconds;
    private long _accumulator;
    private bool _devSpeedsEnabled;

    /// <summary>Creates a scheduler at ×1, not paused.</summary>
    /// <param name="ticksPerSecond">Ticks per second at ×1 (TimeBalanceDef.TicksPerSecond).</param>
    /// <param name="devSpeedsEnabled">Whether ×20 is allowed (developer console only).</param>
    /// <param name="maxTicksPerFrame">Per-frame cap.</param>
    public TickScheduler(int ticksPerSecond, bool devSpeedsEnabled = false, int maxTicksPerFrame = DefaultMaxTicksPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTicksPerFrame);
        TicksPerSecond = ticksPerSecond;
        MaxTicksPerFrame = maxTicksPerFrame;
        _devSpeedsEnabled = devSpeedsEnabled;
        _maxElapsedMicroseconds = long.MaxValue / 2 / ((long)GameSpeed.X20 * ticksPerSecond);
    }

    /// <summary>Ticks per second at ×1.</summary>
    public int TicksPerSecond { get; }

    /// <summary>Maximum ticks returned by one <see cref="Advance"/> call.</summary>
    public int MaxTicksPerFrame { get; }

    /// <summary>Current speed (kept while paused).</summary>
    public GameSpeed Speed { get; private set; } = GameSpeed.X1;

    /// <summary>When true, no ticks run and the accumulator is frozen.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Whole ticks discarded by the per-frame cap since creation.</summary>
    public long DroppedTicks { get; private set; }

    /// <summary>
    /// Progress from the last run tick to the next one in permille (0-999), for interpolating rendering
    /// between the previous and the current snapshot.
    /// </summary>
    public int AlphaPermille => (int)(_accumulator / UnitsPerPermille);

    /// <summary>
    /// Whether ×20 is allowed. Turning it off while running at ×20 falls back to ×4.
    /// </summary>
    public bool DevSpeedsEnabled
    {
        get => _devSpeedsEnabled;
        set
        {
            _devSpeedsEnabled = value;
            if (!value && Speed == GameSpeed.X20)
            {
                Speed = GameSpeed.X4;
            }
        }
    }

    /// <summary>True when <paramref name="speed"/> may be selected right now.</summary>
    public bool IsSpeedAllowed(GameSpeed speed) => speed switch
    {
        GameSpeed.X1 or GameSpeed.X2 or GameSpeed.X4 => true,
        GameSpeed.X20 => _devSpeedsEnabled,
        _ => false,
    };

    /// <summary>Changes the speed; returns false (and keeps the old one) for ×20 without the dev flag or unknown values.</summary>
    public bool TrySetSpeed(GameSpeed speed)
    {
        if (!IsSpeedAllowed(speed))
        {
            return false;
        }

        Speed = speed;
        return true;
    }

    /// <summary>Flips <see cref="IsPaused"/>.</summary>
    public void TogglePause() => IsPaused = !IsPaused;

    /// <summary>Adds a frame's elapsed real time and returns how many ticks to run now (0 to <see cref="MaxTicksPerFrame"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsedMicroseconds"/> is negative.</exception>
    public int Advance(long elapsedMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMicroseconds);
        if (IsPaused)
        {
            return 0;
        }

        long elapsed = Math.Min(elapsedMicroseconds, _maxElapsedMicroseconds);
        _accumulator += elapsed * (int)Speed * TicksPerSecond;

        long due = _accumulator / UnitsPerTick;
        _accumulator -= due * UnitsPerTick;
        if (due <= MaxTicksPerFrame)
        {
            return (int)due;
        }

        DroppedTicks += due - MaxTicksPerFrame;
        return MaxTicksPerFrame;
    }

    /// <summary><see cref="Advance"/> with whole milliseconds.</summary>
    public int AdvanceMilliseconds(long elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        return Advance(Math.Min(elapsedMilliseconds, _maxElapsedMicroseconds / MicrosecondsPerMillisecond) * MicrosecondsPerMillisecond);
    }

    /// <summary>Drops any partial tick (for example after loading a save) without changing speed or pause.</summary>
    public void ResetAccumulator() => _accumulator = 0;
}
