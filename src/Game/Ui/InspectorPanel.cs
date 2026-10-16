using System.Globalization;
using System.Text;
using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Sim;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// Panel of the selected building, right above the minimap (FR-BUILD-09, FR-BUILD-12, FR-BUILD-13): name, level, state
/// with progress, a storage's stock, and the orders upgrade, move, demolish (with a confirmation that shows the refund)
/// and cancel. Whether an order is possible is answered by Core (<see cref="Construction.CheckUpgrade"/>,
/// <see cref="Construction.CheckDemolish"/>); this panel only shows the answer and sends the command through
/// <see cref="SimulationHost.Send"/>. Refusals of the commands themselves come back as toasts (<see cref="BuildingMessages"/>).
/// </summary>
public partial class InspectorPanel : PanelContainer
{
    private const int FramesPerRefresh = 3;
    private const int PanelWidth = 320;
    private const int PermilleOfWhole = 1000;

    private readonly StringBuilder _text = new();
    private SimulationHost? _host;
    private Label _title = null!;
    private Label _info = null!;
    private Label _progress = null!;
    private Label _stock = null!;
    private Label _upgradeInfo = null!;
    private Label _demolishInfo = null!;
    private Label _cancelInfo = null!;
    private int _frame;

    /// <summary>The player asked to move the building (the owner starts the build mode in move mode).</summary>
    public event Action<EntityId>? MoveRequested;

    /// <summary>The shown building, or <see cref="EntityId.None"/> when the panel is closed.</summary>
    public EntityId Selected { get; private set; }

    /// <summary>The "Upgrade" button.</summary>
    public Button UpgradeButton { get; private set; } = null!;

    /// <summary>The "Move" button.</summary>
    public Button MoveButton { get; private set; } = null!;

    /// <summary>The "Demolish" button.</summary>
    public Button DemolishButton { get; private set; } = null!;

    /// <summary>The "Cancel" button (construction, upgrade, move).</summary>
    public Button CancelButton { get; private set; } = null!;

    /// <summary>The translated title (building name).</summary>
    public string TitleText => _title.Text;

    /// <summary>The line under the upgrade button: the extra cost or the reason it is grey.</summary>
    public string UpgradeInfoText => _upgradeInfo.Text;

    /// <summary>The line under the demolish button: the refund or the reason it is grey.</summary>
    public string DemolishInfoText => _demolishInfo.Text;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        SetAnchorsPreset(LayoutPreset.BottomRight);
        OffsetLeft = -16 - PanelWidth;
        OffsetRight = -16;
        OffsetBottom = -288;
        OffsetTop = -288;
        GrowHorizontal = GrowDirection.Begin;
        GrowVertical = GrowDirection.Begin;
        CustomMinimumSize = new Vector2(PanelWidth, 0);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 10);
        }

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 4);
        _title = UiKit.Label(string.Empty, 20);
        _info = Wrapped();
        _progress = Wrapped();
        _stock = Wrapped();
        column.AddChild(_title);
        column.AddChild(_info);
        column.AddChild(_progress);
        column.AddChild(_stock);

        UpgradeButton = UiKit.Button("ui.inspector.upgrade", OnUpgrade);
        _upgradeInfo = Wrapped(small: true);
        MoveButton = UiKit.Button("ui.inspector.move", () => MoveRequested?.Invoke(Selected));
        DemolishButton = UiKit.Button("ui.inspector.demolish", OnDemolish);
        _demolishInfo = Wrapped(small: true);
        CancelButton = UiKit.Button("ui.inspector.cancel", OnCancel);
        _cancelInfo = Wrapped(small: true);
        column.AddChild(UpgradeButton);
        column.AddChild(_upgradeInfo);
        column.AddChild(MoveButton);
        column.AddChild(DemolishButton);
        column.AddChild(_demolishInfo);
        column.AddChild(CancelButton);
        column.AddChild(_cancelInfo);
        column.AddChild(UiKit.Button("ui.inspector.close", () => Hide()));
        margin.AddChild(column);
        AddChild(margin);
    }

    /// <summary>Connects the panel to the host. The panel closes when the simulation is replaced.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        host.SimulationChanged += () => Hide();
        Hide();
    }

    /// <summary>Opens the panel for <paramref name="building"/>. Does nothing for an entity that is not a building.</summary>
    public void Show(EntityId building)
    {
        if (_host is not { IsRunning: true } || _host.Simulation.World is not { } world || !world.Buildings.Has(building))
        {
            Hide();
            return;
        }

        Selected = building;
        Visible = true;
        Refresh();
    }

    /// <summary>Closes the panel and forgets the building.</summary>
    public new void Hide()
    {
        Selected = EntityId.None;
        Visible = false;
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

    /// <summary>Recomputes the whole panel from the simulation now.</summary>
    public void Refresh()
    {
        if (_host is not { IsRunning: true } || _host.Simulation.World is not { } world || !world.Buildings.TryGet(Selected, out Building building))
        {
            Hide();
            return;
        }

        Simulation sim = _host.Simulation;
        ContentCatalog content = sim.Content;
        BuildingDef def = Construction.DefOf(content, building);
        SetText(_title, Tr(BuildingText.NameKey(def.Id)));
        SetText(_info, BuildingText.Format(this, "ui.inspector.level", building.Level, def.Levels.Count) + "\n"
            + Tr("ui.inspector.state") + ": " + Tr(BuildingText.StateKey(building.State)));
        ShowProgress(content, building);
        ShowStock(world, content, def, building);

        bool constructing = building.State == BuildingState.Constructing;
        bool active = building.State == BuildingState.Active;
        ShowUpgrade(sim, def, building, constructing);
        MoveButton.Visible = !constructing;
        MoveButton.Disabled = !active;
        ShowDemolish(sim, def, building, constructing);
        ShowCancel(content, def, building, active);
    }

    private static Label Wrapped(bool small = false)
    {
        var label = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        label.CustomMinimumSize = new Vector2(PanelWidth - 20, 0);
        if (small)
        {
            label.AddThemeFontSizeOverride("font_size", 13);
        }

        return label;
    }

    private static void SetText(Label label, string text)
    {
        if (label.Text != text)
        {
            label.Text = text;
        }
    }

    private void ShowProgress(ContentCatalog content, Building building)
    {
        bool busy = building.State != BuildingState.Active;
        _progress.Visible = busy;
        if (!busy)
        {
            return;
        }

        int percent = Construction.ProgressPermille(content, building) / (PermilleOfWhole / 100);
        SetText(_progress, BuildingText.Format(this, "ui.inspector.progress", percent, BuildingText.Seconds(content, building.TicksLeft)));
    }

    private void ShowStock(GameWorld world, ContentCatalog content, BuildingDef def, Building building)
    {
        bool isStorage = Construction.IsStorage(def) && world.Inventories.Has(Selected);
        _stock.Visible = isStorage;
        if (!isStorage)
        {
            return;
        }

        Inventory stock = world.Inventories.Get(Selected);
        _text.Clear();
        _text.Append(Tr("ui.inspector.stock")).Append(": ");
        bool any = false;
        foreach (ItemDef item in content.Items)
        {
            if (content.TryGetId(item.Id, out DefId id) && stock.Count(id) is int count and > 0)
            {
                _text.Append(any ? ", " : string.Empty).Append(Tr(item.EffectiveNameKey)).Append(' ').Append(count.ToString(CultureInfo.InvariantCulture));
                any = true;
            }
        }

        if (!any)
        {
            _text.Append(Tr("ui.inspector.stock_empty"));
        }

        _text.Append('\n').Append(BuildingText.Format(this, "ui.inspector.stacks", stock.UsedStacks, def.Levels[building.Level - 1].Storage));
        SetText(_stock, _text.ToString());
    }

    private void ShowUpgrade(Simulation sim, BuildingDef def, Building building, bool constructing)
    {
        bool hasNext = building.Level < def.Levels.Count;
        UpgradeButton.Visible = hasNext && !constructing;
        _upgradeInfo.Visible = UpgradeButton.Visible;
        if (!UpgradeButton.Visible)
        {
            return;
        }

        BuildingLevelDef next = def.Levels[building.Level];
        BuildResult check = Construction.CheckUpgrade(sim, Selected);
        UpgradeButton.Disabled = check != BuildResult.Ok;
        string cost = BuildingText.Format(this, "ui.inspector.upgrade_cost", BuildingText.FormatItems(this, sim.Content, next.Cost), BuildingText.Seconds(sim.Content, next.BuildTicks));
        SetText(_upgradeInfo, check == BuildResult.Ok ? cost : cost + "\n" + ReasonText(sim, check, next.Cost));
    }

    private void ShowDemolish(Simulation sim, BuildingDef def, Building building, bool constructing)
    {
        DemolishButton.Visible = !constructing;
        _demolishInfo.Visible = DemolishButton.Visible;
        if (constructing)
        {
            return;
        }

        BuildResult check = Construction.CheckDemolish(sim, Selected);
        DemolishButton.Disabled = check != BuildResult.Ok;
        if (check == BuildResult.Ok)
        {
            SetText(_demolishInfo, BuildingText.Format(this, "ui.inspector.refund", BuildingText.FormatItems(this, sim.Content, DemolishRefund(sim.Content, def, building))));
        }
        else
        {
            SetText(_demolishInfo, ReasonText(sim, check, []));
        }
    }

    private void ShowCancel(ContentCatalog content, BuildingDef def, Building building, bool active)
    {
        CancelButton.Visible = !active;
        _cancelInfo.Visible = CancelButton.Visible;
        if (active)
        {
            return;
        }

        IReadOnlyList<ItemAmountDef> refund = building.State == BuildingState.Moving
            ? []
            : Construction.Refund(def, building.Level, building.Level, content.Construction.CancelRefundPermille);
        SetText(_cancelInfo, BuildingText.Format(this, "ui.inspector.refund", BuildingText.FormatItems(this, content, refund)));
    }

    private static IReadOnlyList<ItemAmountDef> DemolishRefund(ContentCatalog content, BuildingDef def, Building building) =>
        Construction.Refund(def, 1, building.Level, content.Construction.DemolishRefundPermille);

    // The localized reason for a refused check; for missing materials the missing amounts are listed.
    private string ReasonText(Simulation sim, BuildResult result, IReadOnlyList<ItemAmountDef> cost)
    {
        if (result != BuildResult.NotEnoughMaterials)
        {
            return Tr(BuildingText.ResultKey(result));
        }

        _text.Clear();
        foreach (ItemAmountDef entry in cost)
        {
            int have = sim.Content.TryGetId(entry.Item, out DefId item) ? Construction.CountAvailable(sim, item) : 0;
            if (have < entry.Count)
            {
                _text.Append(_text.Length > 0 ? ", " : string.Empty).Append(BuildingText.ItemName(this, sim.Content, entry.Item))
                    .Append(' ').Append((entry.Count - have).ToString(CultureInfo.InvariantCulture));
            }
        }

        return BuildingText.Format(this, "ui.build.reason.materials", _text.ToString());
    }

    private void OnUpgrade()
    {
        if (Selected != EntityId.None)
        {
            _host?.Send(new UpgradeBuildingCommand(Selected));
        }
    }

    private void OnCancel()
    {
        if (Selected != EntityId.None)
        {
            _host?.Send(new CancelBuildingCommand(Selected));
        }
    }

    private void OnDemolish()
    {
        if (_host is not { IsRunning: true } || Selected == EntityId.None || _host.Simulation.World is not { } world
            || !world.Buildings.TryGet(Selected, out Building building))
        {
            return;
        }

        ContentCatalog content = _host.Simulation.Content;
        BuildingDef def = Construction.DefOf(content, building);
        EntityId target = Selected;
        var dialog = new ConfirmationDialog
        {
            Exclusive = true,
            DialogText = BuildingText.Format(
                this,
                "ui.inspector.demolish_confirm",
                Tr(BuildingText.NameKey(def.Id)),
                BuildingText.FormatItems(this, content, DemolishRefund(content, def, building))),
            OkButtonText = Tr("ui.confirm.yes"),
            CancelButtonText = Tr("ui.confirm.no"),
        };
        dialog.Confirmed += () => _host?.Send(new DemolishBuildingCommand(target));
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }
}
