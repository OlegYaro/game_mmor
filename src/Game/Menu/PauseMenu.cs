using Core.Save;
using Game.Sim;
using Game.Ui;
using Godot;

namespace Game.Menu;

/// <summary>
/// In-game menu opened with Esc: resume, save to a slot, load, settings, back to the main menu. While open it pauses the
/// simulation and restores the previous pause state on close.
/// </summary>
public partial class PauseMenu : Control
{
    private SimulationHost? _host;
    private VBoxContainer _mainPage = null!;
    private SlotListPanel _save = null!;
    private SlotListPanel _load = null!;
    private SettingsPanel _settings = null!;
    private readonly Dictionary<Control, CenterContainer> _wrappers = [];
    private Control[] _pages = null!;
    private bool _wasPaused;

    /// <summary>The player chose "to main menu".</summary>
    public event Action? ExitToMenuRequested;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);
        ProcessMode = ProcessModeEnum.Always;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f) };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        _mainPage = UiKit.Column(12);
        _mainPage.AddChild(UiKit.Label("ui.pause.title", 32, HorizontalAlignment.Center));
        _mainPage.AddChild(UiKit.Button("ui.pause.resume", Close));
        _mainPage.AddChild(UiKit.Button("ui.pause.save", () => ShowPage(_save)));
        _mainPage.AddChild(UiKit.Button("ui.pause.load", () => ShowPage(_load)));
        _mainPage.AddChild(UiKit.Button("ui.pause.settings", () => ShowPage(_settings)));
        _mainPage.AddChild(UiKit.Button("ui.pause.to_menu", () =>
        {
            Close();
            ExitToMenuRequested?.Invoke();
        }));

        _save = new SlotListPanel(SlotListMode.Save);
        _save.SlotChosen += OnSaveChosen;
        _save.BackPressed += () => ShowPage(_mainPage);
        _load = new SlotListPanel(SlotListMode.Load);
        _load.SlotChosen += OnLoadChosen;
        _load.BackPressed += () => ShowPage(_mainPage);
        _settings = new SettingsPanel();
        _settings.BackPressed += () => ShowPage(_mainPage);

        _pages = [_mainPage, _save, _load, _settings];
        foreach (Control page in _pages)
        {
            CenterContainer wrapper = UiKit.CenteredPanel(page);
            _wrappers[page] = wrapper;
            AddChild(wrapper);
        }

        ShowPage(_mainPage);
    }

    /// <summary>True while the menu is shown.</summary>
    public bool IsOpen => Visible;

    /// <summary>Connects the menu to the host it saves and loads through.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        _save.Bind(host);
        _load.Bind(host);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            return;
        }

        if (!Visible)
        {
            Open();
        }
        else if (_wrappers[_mainPage].Visible)
        {
            Close();
        }
        else
        {
            ShowPage(_mainPage);
        }

        GetViewport().SetInputAsHandled();
    }

    /// <summary>Opens the menu and pauses the game.</summary>
    public void Open()
    {
        if (Visible || _host is not { IsRunning: true })
        {
            return;
        }

        _wasPaused = _host.Scheduler.IsPaused;
        _host.Scheduler.IsPaused = true;
        _host.InputBlocked = true;
        ShowPage(_mainPage);
        Visible = true;
    }

    /// <summary>Closes the menu and restores the pause state from before it opened.</summary>
    public void Close()
    {
        if (!Visible)
        {
            return;
        }

        Visible = false;
        if (_host is not null)
        {
            _host.InputBlocked = false;
            _host.Scheduler.IsPaused = _wasPaused;
        }
    }

    private void ShowPage(Control page)
    {
        foreach (Control candidate in _pages)
        {
            _wrappers[candidate].Visible = candidate == page;
        }

        if (page == _save)
        {
            _save.Refresh();
        }
        else if (page == _load)
        {
            _load.Refresh();
        }
        else if (page == _settings)
        {
            _settings.Refresh();
        }
    }

    private void OnSaveChosen(SaveSlotId id)
    {
        string? error = _host?.SaveToSlot(id);
        _host?.ShowToast(error ?? "ui.toast.saved");
        ShowPage(_save);
    }

    private void OnLoadChosen(SaveSlotId id)
    {
        string? error = _host?.LoadSlot(id);
        if (error is not null)
        {
            _host?.ShowToast(error);
            return;
        }

        Visible = false;
        _host!.InputBlocked = false;
    }
}
