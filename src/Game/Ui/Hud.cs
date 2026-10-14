using System.Globalization;
using Core.Sim;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// HUD (FR-UI-01): date and clock, day or night, season, speed or pause with a text icon, difficulty and seed. All text
/// comes from translation keys (<c>locale/strings.csv</c>). F2 switches between Russian and English; the choice is saved.
/// </summary>
public partial class Hud : Control
{
    private static readonly string[] _seasonKeys =
        ["ui.hud.season.spring", "ui.hud.season.summer", "ui.hud.season.autumn", "ui.hud.season.winter"];

    private SimulationHost? _host;
    private Label _label = null!;

    public override void _Ready()
    {
        _label = new Label { Position = new Vector2(16, 12) };
        _label.AddThemeFontSizeOverride("font_size", 22);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 6);
        AddChild(_label);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Connects the HUD to the simulation it displays.</summary>
    public void Bind(SimulationHost host) => _host = host;

    public override void _Process(double delta)
    {
        if (_host is not { IsRunning: true })
        {
            return;
        }

        Core.Sim.Simulation sim = _host.Simulation;
        CalendarDate d = sim.Now;
        TickScheduler scheduler = _host.Scheduler;
        string season = d.Season < _seasonKeys.Length
            ? Tr(_seasonKeys[d.Season])
            : string.Create(CultureInfo.InvariantCulture, $"#{d.Season + 1}");
        string phase = Tr(d.IsNight ? "ui.hud.night" : "ui.hud.daylight");
        string speed = scheduler.IsPaused
            ? "|| " + Tr("ui.speed.paused")
            : string.Create(CultureInfo.InvariantCulture, $"{SpeedIcon(scheduler.Speed)} x{(int)scheduler.Speed}");
        string difficulty = Tr("ui.difficulty." + DifficultyNames.ToName(sim.Difficulty));

        _label.Text = string.Join('\n',
            string.Create(CultureInfo.InvariantCulture, $"{Tr("ui.hud.day")} {d.Day + 1}  {d.Hour:D2}:{d.Minute:D2}  {phase}"),
            string.Create(CultureInfo.InvariantCulture, $"{season}, {Tr("ui.hud.day")} {d.DayOfSeason + 1}"),
            string.Create(CultureInfo.InvariantCulture, $"{Tr("ui.speed.label")}: {speed}"),
            string.Create(CultureInfo.InvariantCulture, $"{Tr("ui.hud.difficulty")}: {difficulty}  {Tr("ui.hud.seed")}: {sim.Seed}"),
            $"{Tr("ui.hud.lang_hint")}  {Tr("ui.hud.esc_hint")}");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F2 })
        {
            UserSettings.ToggleLanguage();
            GetViewport().SetInputAsHandled();
        }
    }

    // Text stand-in for the speed icon (no icon art in the placeholder UI).
    private static string SpeedIcon(GameSpeed speed) => speed switch
    {
        GameSpeed.X1 => ">",
        GameSpeed.X2 => ">>",
        GameSpeed.X4 => ">>>",
        _ => ">>>>",
    };
}
