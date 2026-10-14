using Core.Save;
using Core.Sim;
using Game.Sim;
using Game.Ui;
using Godot;

namespace Game.Menu;

/// <summary>
/// Main menu: New Game (form), Continue (newest readable save), Load (slot list), Settings, Quit. It only raises events;
/// <c>Main</c> starts games and quits.
/// </summary>
public partial class MainMenu : Control
{
    private SimulationHost? _host;
    private VBoxContainer _mainPage = null!;
    private NewGamePanel _newGame = null!;
    private SlotListPanel _load = null!;
    private SettingsPanel _settings = null!;
    private Label _busy = null!;
    private Button _continue = null!;
    private readonly Dictionary<Control, CenterContainer> _wrappers = [];
    private Control[] _pages = null!;

    /// <summary>The player started a new game from the form.</summary>
    public event Action<NewGameOptions>? NewGameRequested;

    /// <summary>The player asked to load a slot (Continue picks the newest one).</summary>
    public event Action<SaveSlotId>? LoadRequested;

    /// <summary>The player chose Quit.</summary>
    public event Action? QuitRequested;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var backdrop = new ColorRect { Color = new Color(0.07f, 0.09f, 0.12f) };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        _mainPage = UiKit.Column(12);
        _mainPage.AddChild(UiKit.Label("ui.menu.title", 36, HorizontalAlignment.Center));
        _mainPage.AddChild(UiKit.Button("ui.menu.new_game", () => ShowPage(_newGame)));
        _continue = UiKit.Button("ui.menu.continue", OnContinue);
        _mainPage.AddChild(_continue);
        _mainPage.AddChild(UiKit.Button("ui.menu.load", () => ShowPage(_load)));
        _mainPage.AddChild(UiKit.Button("ui.menu.settings", () => ShowPage(_settings)));
        _mainPage.AddChild(UiKit.Button("ui.menu.quit", () => QuitRequested?.Invoke()));
        _busy = UiKit.Label("ui.newgame.generating", 20, HorizontalAlignment.Center);
        _busy.Visible = false;
        _mainPage.AddChild(_busy);

        _newGame = new NewGamePanel();
        _newGame.StartRequested += options => NewGameRequested?.Invoke(options);
        _newGame.BackPressed += () => ShowPage(_mainPage);
        _load = new SlotListPanel(SlotListMode.Load);
        _load.SlotChosen += id => LoadRequested?.Invoke(id);
        _load.BackPressed += () => ShowPage(_mainPage);
        _settings = new SettingsPanel();
        _settings.BackPressed += () => ShowPage(_mainPage);

        _pages = [_mainPage, _newGame, _load, _settings];
        foreach (Control page in _pages)
        {
            CenterContainer wrapper = UiKit.CenteredPanel(page);
            _wrappers[page] = wrapper;
            AddChild(wrapper);
        }

        ShowPage(_mainPage);
        if (_host is not null)
        {
            Bind(_host);
        }
    }

    /// <summary>Connects the menu to the host whose slots it shows.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        if (_load is not null)
        {
            _load.Bind(host);
            RefreshContinue();
        }
    }

    /// <summary>Shows or hides the "generating" text and blocks the buttons while a game is being created.</summary>
    public void SetBusy(bool busy)
    {
        _busy.Visible = busy;
        foreach (Control page in _pages)
        {
            _wrappers[page].Visible = !busy || page == _mainPage;
        }

        foreach (Node child in _mainPage.GetChildren())
        {
            if (child is Button button)
            {
                button.Disabled = busy;
            }
        }

        if (!busy)
        {
            ShowPage(_mainPage);
            RefreshContinue();
        }
    }

    private void ShowPage(Control page)
    {
        foreach (Control candidate in _pages)
        {
            _wrappers[candidate].Visible = candidate == page;
        }

        if (page == _load)
        {
            _load.Refresh();
        }
        else if (page == _settings)
        {
            _settings.Refresh();
        }
        else if (page == _newGame)
        {
            _newGame.FocusSeed();
        }
        else
        {
            RefreshContinue();
        }
    }

    private void RefreshContinue()
    {
        if (_continue is null || _host is not { IsInitialized: true })
        {
            return;
        }

        _continue.Disabled = SaveSlots.FindNewest(_host.Slots.List()) is null;
    }

    private void OnContinue()
    {
        if (_host is null)
        {
            return;
        }

        SaveSlotEntry? newest = SaveSlots.FindNewest(_host.Slots.List());
        if (newest is not null)
        {
            LoadRequested?.Invoke(newest.Id);
        }
    }
}
