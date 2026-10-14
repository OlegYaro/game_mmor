using Core.Content;
using Core.Sim;

namespace Core.Tests.Sim;

public sealed class CalendarTests
{
    private readonly Calendar _calendar = new(SimTestData.GddTime);

    [Fact]
    [Trait("Req", "FR-TIME-01")]
    public void DerivedConstantsFollowTheBalance()
    {
        Assert.Equal(9000, _calendar.TicksPerDay); // 10 ticks/s * 60 s * 15 min
        Assert.Equal(60, _calendar.DaysPerYear);
        Assert.Equal(8, _calendar.NightHours);
        Assert.Equal(16, _calendar.NightStartHour);
    }

    // GDD §14: a third of the day is night. Hours 0-15 are day, 16-23 night.
    [Fact]
    [Trait("Req", "FR-TIME-01")]
    public void RealDataGivesNightHoursSixteenToTwentyThree()
    {
        var calendar = new Calendar(SimTestData.Real.Time);

        Assert.Equal(Enumerable.Range(16, 8), Enumerable.Range(0, Calendar.HoursPerDay).Where(calendar.IsNightHour));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(6, 0, 0, 0, false)] // 6 ticks = 0.96 game minutes
    [InlineData(7, 0, 0, 1, false)]
    [InlineData(374, 0, 0, 59, false)]
    [InlineData(375, 0, 1, 0, false)]
    [InlineData(5999, 0, 15, 59, false)]
    [InlineData(6000, 0, 16, 0, true)]
    [InlineData(8999, 0, 23, 59, true)]
    [InlineData(9000, 1, 0, 0, false)]
    [InlineData(15000, 1, 16, 0, true)]
    [Trait("Req", "FR-TIME-01")]
    public void HourMinuteAndNightBoundaries(long tick, long day, int hour, int minute, bool night)
    {
        CalendarDate date = _calendar.At(tick);

        Assert.Equal(tick, date.Tick);
        Assert.Equal(day, date.Day);
        Assert.Equal(hour, date.Hour);
        Assert.Equal(minute, date.Minute);
        Assert.Equal(night, date.IsNight);
        Assert.Equal((int)(tick % 9000), date.TickOfDay);
    }

    [Theory]
    [InlineData(6, 0, 6, 0, 0, 6, 0)]
    [InlineData(7, 1, 0, 0, 0, 7, 0)]
    [InlineData(14, 2, 0, 0, 0, 14, 0)]
    [InlineData(15, 2, 1, 0, 1, 0, 0)]
    [InlineData(44, 6, 2, 0, 2, 14, 0)]
    [InlineData(45, 6, 3, 0, 3, 0, 0)]
    [InlineData(59, 8, 3, 0, 3, 14, 0)]
    [InlineData(60, 8, 4, 1, 0, 0, 0)]
    [InlineData(125, 17, 6, 2, 0, 5, 0)]
    [Trait("Req", "FR-TIME-01")]
    public void DayWeekSeasonAndYearRollover(long day, long week, int dayOfWeek, long year, int season, int dayOfSeason, int hour)
    {
        CalendarDate date = _calendar.At(_calendar.FirstTickOf(day, hour));

        Assert.Equal(day, date.Day);
        Assert.Equal(week, date.Week);
        Assert.Equal(dayOfWeek, date.DayOfWeek);
        Assert.Equal(year, date.Year);
        Assert.Equal(season, date.Season);
        Assert.Equal(dayOfSeason, date.DayOfSeason);

        // The tick before is still the previous day.
        Assert.Equal(day - 1, _calendar.At(date.Tick - 1).Day);
    }

    // 7 ticks/s * 60 s * 1 min = 420 ticks per day: 17.5 ticks per hour, so hours alternate 18 and 17 ticks.
    [Fact]
    [Trait("Req", "FR-TIME-01")]
    public void FirstTickOfMatchesAtWhenHoursAreNotWholeTicks()
    {
        var calendar = new Calendar(SimTestData.GddTime with { TicksPerSecond = 7, RealMinutesPerGameDay = 1 });
        Assert.Equal(420, calendar.TicksPerDay);

        for (long tick = 0; tick < 2 * calendar.TicksPerDay; tick++)
        {
            CalendarDate date = calendar.At(tick);
            Assert.InRange(tick, calendar.FirstTickOf(date.Day, date.Hour), calendar.FirstTickOf(date.Day, date.Hour + 1) - 1);
        }

        Assert.Equal(18, calendar.FirstTickOf(0, 1));
        Assert.Equal(35, calendar.FirstTickOf(0, 2));
        Assert.Equal(420, calendar.FirstTickOf(0, 24));
        Assert.Equal(420, calendar.FirstTickOf(1, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 0)] // 0.48 h rounds down
    [InlineData(21, 1)] // 0.504 h rounds up
    [InlineData(333, 8)]
    [InlineData(500, 12)]
    [InlineData(1000, 24)]
    [Trait("Req", "FR-TIME-01")]
    public void NightIsRoundedToWholeHoursAtTheEndOfTheDay(int nightPermille, int nightHours)
    {
        var calendar = new Calendar(SimTestData.GddTime with { NightPermille = nightPermille });

        Assert.Equal(nightHours, calendar.NightHours);
        Assert.Equal(Calendar.HoursPerDay - nightHours, calendar.NightStartHour);
        Assert.Equal(nightHours, Enumerable.Range(0, Calendar.HoursPerDay).Count(calendar.IsNightHour));
        Assert.Equal(nightHours == 24, calendar.At(0).IsNight);
        Assert.Equal(nightHours > 0, calendar.At(calendar.TicksPerDay - 1).IsNight);
    }

    [Fact]
    [Trait("Req", "FR-TIME-01")]
    public void InvalidInputIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _calendar.At(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _calendar.FirstTickOf(0, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => _calendar.FirstTickOf(-1, 0));
        Assert.Throws<ArgumentNullException>(() => new Calendar(null!));
    }

    [Fact]
    [Trait("Req", "FR-TIME-01")]
    public void BalanceIsKept()
    {
        TimeBalanceDef balance = SimTestData.GddTime;

        Assert.Same(balance, new Calendar(balance).Balance);
    }
}
