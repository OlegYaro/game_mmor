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

/// <summary>
/// Third system: walkers advance along their paths and heroes spend or regain stamina (FR-HERO-01). Runs after the
/// commands, so an order moves the hero in the tick it executes, and before the fog, so sight follows the new position.
/// Simulations without a world skip it.
/// </summary>
internal sealed class MovementSystem : ISimSystem
{
    public const string SystemName = "movement";

    public string Name => SystemName;

    public void Update(Simulation sim) => HeroRules.UpdateMovement(sim);
}

/// <summary>
/// Fourth system: heroes standing next to their node work on it and fill the backpack (FR-HERO-02, FR-WORLD-05). Runs
/// after the movement, so gathering starts in the tick the hero arrives. Simulations without a world skip it.
/// </summary>
internal sealed class GatherSystem : ISimSystem
{
    public const string SystemName = "gather";

    public string Name => SystemName;

    public void Update(Simulation sim) => HeroRules.UpdateGathering(sim);
}

/// <summary>
/// Last system: recomputes the visible fog layer from the revealers and the time of day (FR-WORLD-06). Runs after
/// the commands so circles reflect this tick's spawns and moves; cheap when nothing moved (one list compare).
/// Simulations without a world skip it.
/// </summary>
internal sealed class FogSystem : ISimSystem
{
    public const string SystemName = "fog";

    public string Name => SystemName;

    public void Update(Simulation sim) => sim.RefreshFog();
}
