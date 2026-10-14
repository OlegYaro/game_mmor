using Core.Logging;

namespace Core.Sim;

/// <summary>
/// First system: moves <see cref="Simulation.Now"/> to the current tick and logs dawn, nightfall and new days,
/// seasons and years (FR-TIME-01 groundwork).
/// </summary>
internal sealed class ClockSystem : ISimSystem
{
    public const string SystemName = "clock";
    private const string Category = "calendar";

    public string Name => SystemName;

    public void Update(Simulation sim)
    {
        CalendarDate previous = sim.Now;
        CalendarDate now = sim.Calendar.At(sim.Tick);
        sim.Now = now;

        SimLog log = sim.Log;
        if (now.Day != previous.Day)
        {
            if (now.Year != previous.Year)
            {
                log.Write(sim.Tick, LogLevel.Info, Category, "Year {year} begins", LogField.Of("year", now.Year));
            }

            if (now.Season != previous.Season)
            {
                log.Write(sim.Tick, LogLevel.Info, Category, "Season {season} begins", LogField.Of("season", now.Season));
            }

            log.Write(sim.Tick, LogLevel.Info, Category, "Day {day} begins", LogField.Of("day", now.Day));
        }

        if (now.IsNight != previous.IsNight)
        {
            log.Write(sim.Tick, LogLevel.Info, Category, now.IsNight ? "Night falls" : "Dawn breaks");
        }
    }
}

/// <summary>Second system: executes the queued commands due in the current tick, in (tick, sequence) order.</summary>
internal sealed class CommandSystem : ISimSystem
{
    public const string SystemName = "commands";

    public string Name => SystemName;

    public void Update(Simulation sim) => sim.ExecuteDueCommands();
}
