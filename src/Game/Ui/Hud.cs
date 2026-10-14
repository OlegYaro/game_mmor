using System.Globalization;
using Core.Sim;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// Placeholder HUD: day, clock, day/night, season and speed. All text comes from translation keys
/// (<c>locale/strings.csv</c>). F2 switches between Russian and English.
/// </summary>
public partial class Hud : Control
{
    private static readonly string[] _seasonKeys =
        ["ui.hud.season.spring", "ui.hud.season.summer", "ui.hud.season.autumn", "ui.hud.season.winter"];

    private static readonly string[] _locales = ["ru", "en"];

    private SimulationHost? _host;
    private Label _label = null!;

    public override void _Ready()
    {
        _label = new Label { Position = new Vector2(16, 12) };
        _label.AddThemeFontSizeOverride("font_size", 22);
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

        CalendarDate d = _host.Simulation.Now;
        TickScheduler scheduler = _host.Scheduler;
        string season = d.Season < _seasonKeys.Length
            ? Tr(_seasonKeys[d.Season])
            : string.Create(CultureInfo.InvariantCulture, $"#{d.Season + 1}");
        string phase = Tr(d.IsNight ? "ui.hud.night" : "ui.hud.daylight");
        string speed = scheduler.IsPaused
            ? Tr("ui.speed.paused")
            : string.Create(CultureInfo.InvariantCulture, $"x{(int)scheduler.Speed}");

        _label.Text = string.Join('\n',
            string.Create(CultureInfo.InvariantCulture, $"{Tr("ui.hud.day")} {d.Day + 1}  {d.Hour:D2}:{d.Minute:D2}  {phase}"),
            string.Create(CultureInfo.InvariantCulture, $"{season}, {Tr("ui.hud.day")} {d.DayOfSeason + 1}"),
            string.Create(CultureInfo.InvariantCulture, $"{Tr("ui.speed.label")}: {speed}  ({Tr("ui.hud.tick")} {d.Tick})"),
            Tr("ui.hud.lang_hint"));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F2 })
        {
            ToggleLanguage();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Switches between Russian and English.</summary>
    public static void ToggleLanguage()
    {
        string current = TranslationServer.GetLocale();
        string next = current.StartsWith("ru", StringComparison.Ordinal) ? _locales[1] : _locales[0];
        TranslationServer.SetLocale(next);
    }
}
