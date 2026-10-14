using Core.Sim;
using Godot;

namespace Game.World;

/// <summary>
/// Day/night colour of the world (FR-TIME-01), smooth over the hour: warm dawn at hour 0, plain daylight, an orange
/// dusk before the night, a dark bluish night (<see cref="Calendar.NightStartHour"/> to the end of the day).
/// </summary>
public static class DayNightTint
{
    /// <summary>Night colour.</summary>
    public static readonly Color Night = new(0.42f, 0.48f, 0.74f);

    private static readonly Color _dawn = new(0.97f, 0.88f, 0.80f);
    private static readonly Color _day = new(1f, 1f, 1f);
    private static readonly Color _dusk = new(0.98f, 0.72f, 0.56f);

    /// <summary>Tint at the simulation's current tick.</summary>
    public static Color At(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        double hour = (double)sim.Now.TickOfDay * Calendar.HoursPerDay / sim.Calendar.TicksPerDay;
        return At(hour, sim.Calendar.NightStartHour);
    }

    /// <summary>Tint at fractional <paramref name="hour"/> (0..24) of a day whose night starts at <paramref name="nightStartHour"/>.</summary>
    public static Color At(double hour, int nightStartHour)
    {
        double n = nightStartHour;
        double day = Calendar.HoursPerDay;
        (double At, Color Color)[] keys =
        [
            (0, _dawn),
            (1.0, _day),
            (n - 2.0, _day),
            (n - 0.25, _dusk),
            (n + 1.0, Night),
            (day - 1.0, Night),
            (day, _dawn),
        ];
        hour = Math.Clamp(hour, 0, day);
        for (int i = 1; i < keys.Length; i++)
        {
            if (hour <= keys[i].At)
            {
                double span = keys[i].At - keys[i - 1].At;
                float t = span <= 0 ? 1f : (float)((hour - keys[i - 1].At) / span);
                return keys[i - 1].Color.Lerp(keys[i].Color, t);
            }
        }

        return _dawn;
    }
}
