using Core.Content;
using Core.Determinism;

namespace Core.Sim;

/// <summary>
/// Calendar position of one tick. All counters are 0-based; Game adds 1 for display.
/// </summary>
/// <param name="Tick">The tick.</param>
/// <param name="Day">Days since the start of the game.</param>
/// <param name="TickOfDay">Ticks since the start of <paramref name="Day"/>.</param>
/// <param name="Hour">Hour of the day, 0 to <see cref="Calendar.HoursPerDay"/> - 1. Hour 0 is dawn.</param>
/// <param name="Minute">Minute of the hour, 0 to <see cref="Calendar.MinutesPerHour"/> - 1.</param>
/// <param name="Week">Weeks since the start of the game (weeks run on independently of seasons).</param>
/// <param name="DayOfWeek">Day of the week.</param>
/// <param name="Year">Years since the start of the game.</param>
/// <param name="Season">Season of the year, 0 to SeasonsPerYear - 1 (in the default data: spring, summer, autumn, winter).</param>
/// <param name="DayOfSeason">Day of the season.</param>
/// <param name="IsNight">True during the night hours (see <see cref="Calendar.NightStartHour"/>).</param>
public readonly record struct CalendarDate(
    long Tick,
    long Day,
    int TickOfDay,
    int Hour,
    int Minute,
    long Week,
    int DayOfWeek,
    long Year,
    int Season,
    int DayOfSeason,
    bool IsNight) : IStateHashable
{
    /// <summary>Writes every field in declaration order.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt64(Tick);
        writer.WriteInt64(Day);
        writer.WriteInt32(TickOfDay);
        writer.WriteInt32(Hour);
        writer.WriteInt32(Minute);
        writer.WriteInt64(Week);
        writer.WriteInt32(DayOfWeek);
        writer.WriteInt64(Year);
        writer.WriteInt32(Season);
        writer.WriteInt32(DayOfSeason);
        writer.WriteBool(IsNight);
    }
}

/// <summary>
/// Converts tick numbers to calendar dates (FR-TIME-01, GDD §14). Pure integer math; every balance number comes
/// from <see cref="TimeBalanceDef"/>.
/// </summary>
/// <remarks>
/// A day lasts <c>TicksPerSecond * 60 * RealMinutesPerGameDay</c> ticks and is split into 24 clock hours.
/// Tick 0 is hour 0 (dawn) of day 0. The day starts with daylight; the night is the last
/// <see cref="NightHours"/> hours of the day, where <c>NightHours = round(24 * NightPermille / 1000)</c>
/// (half rounds up). With the default 333 permille that is 8 hours: hours 0-15 are day, hours 16-23 are night.
/// Night is aligned to whole hours so the HUD clock and the day/night flag always agree.
/// </remarks>
public sealed class Calendar
{
    /// <summary>Clock hours per game day (clock-face convention, not a balance value).</summary>
    public const int HoursPerDay = 24;

    /// <summary>Clock minutes per hour (clock-face convention, not a balance value).</summary>
    public const int MinutesPerHour = 60;

    private const int SecondsPerMinute = 60;
    private const int PermilleScale = 1000;

    /// <summary>Creates a calendar from the time balance.</summary>
    public Calendar(TimeBalanceDef balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        Balance = balance;
        TicksPerDay = checked(balance.TicksPerSecond * SecondsPerMinute * balance.RealMinutesPerGameDay);
        DaysPerYear = checked(balance.DaysPerSeason * balance.SeasonsPerYear);
        NightHours = ((HoursPerDay * balance.NightPermille) + (PermilleScale / 2)) / PermilleScale;
        NightStartHour = HoursPerDay - NightHours;
    }

    /// <summary>The time balance this calendar was built from.</summary>
    public TimeBalanceDef Balance { get; }

    /// <summary>Ticks in one game day.</summary>
    public int TicksPerDay { get; }

    /// <summary>Days in one year.</summary>
    public int DaysPerYear { get; }

    /// <summary>Number of night hours at the end of each day.</summary>
    public int NightHours { get; }

    /// <summary>First night hour; night lasts from here to the end of the day. Equals <see cref="HoursPerDay"/> when there is no night.</summary>
    public int NightStartHour { get; }

    /// <summary>True when <paramref name="hour"/> (0-23) is a night hour.</summary>
    public bool IsNightHour(int hour) => hour >= NightStartHour;

    /// <summary>First tick of <paramref name="hour"/> on <paramref name="day"/>.</summary>
    public long FirstTickOf(long day, int hour)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(day);
        ArgumentOutOfRangeException.ThrowIfNegative(hour);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour, HoursPerDay);

        // Tick t is in hour h when h <= t * 24 / TicksPerDay, so the first one is ceil(h * TicksPerDay / 24).
        long offset = (((long)hour * TicksPerDay) + HoursPerDay - 1) / HoursPerDay;
        return checked((day * TicksPerDay) + offset);
    }

    /// <summary>Calendar position of <paramref name="tick"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick"/> is negative.</exception>
    public CalendarDate At(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        long day = tick / TicksPerDay;
        int tickOfDay = (int)(tick % TicksPerDay);
        int minuteOfDay = (int)((long)tickOfDay * HoursPerDay * MinutesPerHour / TicksPerDay);
        int hour = minuteOfDay / MinutesPerHour;
        long dayOfYear = day % DaysPerYear;

        return new CalendarDate(
            Tick: tick,
            Day: day,
            TickOfDay: tickOfDay,
            Hour: hour,
            Minute: minuteOfDay % MinutesPerHour,
            Week: day / Balance.DaysPerWeek,
            DayOfWeek: (int)(day % Balance.DaysPerWeek),
            Year: day / DaysPerYear,
            Season: (int)(dayOfYear / Balance.DaysPerSeason),
            DayOfSeason: (int)(dayOfYear % Balance.DaysPerSeason),
            IsNight: IsNightHour(hour));
    }
}
