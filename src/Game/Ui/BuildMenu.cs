using System.Text;
using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Sim;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// Bottom build panel (FR-BUILD-01, FR-UI-02): category tabs and one button per building type. A button is grey with the
/// reason written under it when the building cannot be started at all: no center yet, the center exists, a requirement is
/// missing, or the first level's materials are not there (the missing amounts are listed). Reasons that depend on the
/// place (territory, water, ...) come from the Core placement check while the player points at a tile, not from here.
/// The panel does not handle keys: <see cref="ToggleVisible"/> is called by the owner (key B). Choosing a building raises
/// <see cref="BuildingChosen"/> and closes the panel; the build mode takes over.
/// </summary>
public partial class BuildMenu : PanelContainer
{
    private const int FramesPerRefresh = 6;

    // Order of the tabs; a category without building types gets no tab.
    private static readonly string[] _categoryOrder = ["settlement_center", "housing", "storage", "production"];

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, Button> _tabs = [];
    private readonly StringBuilder _text = new();
    private SimulationHost? _host;
    private HBoxContainer _tabRow = null!;
    private HBoxContainer _buildingRow = null!;
    private string _category = string.Empty;
    private int _frame;

    /// <summary>The player chose a building type (its id). The owner starts the build mode with it.</summary>
    public event Action<string>? BuildingChosen;

    /// <summary>True while the panel is open.</summary>
    public bool IsOpen => Visible;

    /// <summary>Id of the shown category tab (empty before <see cref="Bind"/>).</summary>
    public string Category => _category;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        SetAnchorsPreset(LayoutPreset.BottomWide);
        OffsetLeft = 16;
        OffsetRight = -288;
        OffsetBottom = -16;
        OffsetTop = -16;
        GrowVertical = GrowDirection.Begin;
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 10);
        }

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 8);
        _tabRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _tabRow.AddThemeConstantOverride("separation", 6);
        _buildingRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _buildingRow.AddThemeConstantOverride("separation", 10);
        column.AddChild(UiKit.Label("ui.build.title", 18));
        column.AddChild(_tabRow);
        column.AddChild(_buildingRow);
        margin.AddChild(column);
        AddChild(margin);
    }

    /// <summary>Builds the tabs and buttons from the loaded content.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        foreach (Node child in _tabRow.GetChildren())
        {
            child.QueueFree();
        }

        foreach (Node child in _buildingRow.GetChildren())
        {
            child.QueueFree();
        }

        _entries.Clear();
        _tabs.Clear();
        IReadOnlyList<BuildingDef> defs = host.Content.Buildings;
        foreach (string category in _categoryOrder)
        {
            if (!defs.Any(d => d.Category == category))
            {
                continue;
            }

            string captured = category;
            var tab = new Button { Text = BuildingText.CategoryKey(category), ToggleMode = true, CustomMinimumSize = new Vector2(0, 36), FocusMode = FocusModeEnum.None };
            tab.Pressed += () => SelectCategory(captured);
            _tabRow.AddChild(tab);
            _tabs[category] = tab;
        }

        foreach (BuildingDef def in defs)
        {
            AddEntry(def);
        }

        SelectCategory(_tabs.Count > 0 ? _tabs.Keys.First() : string.Empty);
    }

    /// <summary>Opens the panel when closed, closes it when open (key B, wired by the owner).</summary>
    public void ToggleVisible()
    {
        if (Visible)
        {
            Visible = false;
            return;
        }

        if (_host is not { IsRunning: true })
        {
            return;
        }

        // Without a center only the center can be placed: open on its tab.
        if (Construction.FindCenter(_host.Simulation) == EntityId.None && _tabs.ContainsKey(Construction.CenterCategory))
        {
            SelectCategory(Construction.CenterCategory);
        }

        Visible = true;
        Refresh();
    }

    /// <summary>Shows the buildings of <paramref name="category"/>.</summary>
    public void SelectCategory(string category)
    {
        _category = category;
        foreach ((string id, Button tab) in _tabs)
        {
            tab.SetPressedNoSignal(id == category);
        }

        foreach (Entry entry in _entries.Values)
        {
            entry.Box.Visible = entry.Def.Category == category;
        }
    }

    /// <summary>The button of a building type, or null when the type is unknown.</summary>
    public Button? ButtonOf(string buildingId) => _entries.TryGetValue(buildingId, out Entry? entry) ? entry.Button : null;

    /// <summary>The text under the button of a building type (the reason it is grey); empty when it is available.</summary>
    public string ReasonOf(string buildingId) => _entries.TryGetValue(buildingId, out Entry? entry) ? entry.Reason.Text : string.Empty;

    /// <summary>Recomputes every button now.</summary>
    public void Refresh()
    {
        if (_host is not { IsRunning: true })
        {
            return;
        }

        Simulation sim = _host.Simulation;
        foreach (Entry entry in _entries.Values)
        {
            string? reason = ReasonFor(sim, entry.Def);
            bool available = reason is null;
            entry.Button.Disabled = !available;
            string reasonText = reason ?? string.Empty;
            if (entry.Reason.Text != reasonText)
            {
                entry.Reason.Text = reasonText;
            }

            entry.Reason.Visible = !available;
            string tip = TooltipFor(sim.Content, entry.Def);
            if (entry.Button.TooltipText != tip)
            {
                entry.Button.TooltipText = tip;
            }
        }
    }

    public override void _Process(double delta)
    {
        if (Visible && ++_frame % FramesPerRefresh == 0)
        {
            Refresh();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && Visible)
        {
            Refresh();
        }
    }

    private void AddEntry(BuildingDef def)
    {
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(190, 0) };
        var button = new Button { Text = BuildingText.NameKey(def.Id), CustomMinimumSize = new Vector2(190, 44), FocusMode = FocusModeEnum.None };
        string id = def.Id;
        button.Pressed += () =>
        {
            BuildingChosen?.Invoke(id);
            Visible = false;
        };
        var reason = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(190, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        reason.AddThemeFontSizeOverride("font_size", 13);
        reason.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.45f));
        box.AddChild(button);
        box.AddChild(reason);
        box.Visible = false;
        _buildingRow.AddChild(box);
        _entries[def.Id] = new Entry(def, box, button, reason);
    }

    // Null when the type can be started; otherwise the translated reason shown under the button.
    private string? ReasonFor(Simulation sim, BuildingDef def)
    {
        bool hasCenter = Construction.FindCenter(sim) != EntityId.None;
        if (Construction.IsCenter(def))
        {
            if (hasCenter)
            {
                return Tr("ui.build.reason.center_exists");
            }
        }
        else if (!hasCenter)
        {
            return Tr("ui.build.reason.no_center");
        }

        BuildingLevelDef first = def.Levels[0];
        if (!Construction.MeetsRequirements(sim, first))
        {
            return Tr("ui.build.reason.requirement");
        }

        if (Construction.CanPay(sim, first.Cost))
        {
            return null;
        }

        _text.Clear();
        foreach (ItemAmountDef entry in first.Cost)
        {
            int missing = entry.Count - (sim.Content.TryGetId(entry.Item, out DefId item) ? Construction.CountAvailable(sim, item) : 0);
            if (missing <= 0)
            {
                continue;
            }

            if (_text.Length > 0)
            {
                _text.Append(", ");
            }

            _text.Append(BuildingText.ItemName(this, sim.Content, entry.Item)).Append(' ').Append(missing.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return BuildingText.Format(this, "ui.build.reason.materials", _text.ToString());
    }

    private string TooltipFor(ContentCatalog content, BuildingDef def)
    {
        BuildingLevelDef first = def.Levels[0];
        return BuildingText.Format(
            this,
            "ui.build.tooltip",
            Tr(BuildingText.NameKey(def.Id)),
            BuildingText.FormatItems(this, content, first.Cost),
            BuildingText.Seconds(content, first.BuildTicks));
    }

    private sealed record Entry(BuildingDef Def, VBoxContainer Box, Button Button, Label Reason);
}
