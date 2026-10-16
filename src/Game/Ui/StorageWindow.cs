using System.Globalization;
using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Heroes;
using Core.Sim;
using Game.Sim;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Ui;

/// <summary>
/// Window "Storage" (K; FR-UI-04, FR-ECO-01): the stock of every item (hero's backpack | storages), the list of storages
/// (name, level, stacks used of capacity, state) and, for the chosen storage, a switch for every item category it takes
/// (<see cref="SetStorageFilterCommand"/>), the button that puts the hero's backpack into it (<see cref="DepositHeroItemsCommand"/>)
/// and the food supply in days. The window only shows Core state and sends commands; Core decides what is allowed.
/// </summary>
public partial class StorageWindow : ToggleWindow
{
    private const int ItemNameWidth = 200;
    private const int NumberWidth = 120;
    private const int Unset = -1;

    // A filter just sent is shown for at most this long while Core has not stepped (e.g. on pause).
    private const ulong PendingMsec = 3000;

    private readonly List<StorageRow> _storageRows = [];
    private readonly List<Button> _categoryButtons = [];
    private readonly List<ItemRow> _itemRows = [];
    private GridContainer _totals = null!;
    private VBoxContainer _storageList = null!;
    private Label _storageHeader = null!;
    private Label _noStorages = null!;
    private Label _filterTitle = null!;
    private HBoxContainer _filterRow = null!;
    private Label _food = null!;
    private Button _deposit = null!;
    private string _foodShown = string.Empty;
    private bool _languageDirty = true;
    private EntityId _pendingStorage = EntityId.None;
    private int _pendingMask;
    private ulong _pendingUntil;

    /// <summary>The storage whose filter is shown, or none.</summary>
    public EntityId Selected { get; private set; }

    /// <summary>Number of storages listed (for the smoke test).</summary>
    public int ShownStorages { get; private set; }

    /// <summary>The "give the backpack" button.</summary>
    public Button DepositButton => _deposit;

    /// <summary>The category switches in <see cref="ItemCategories.All"/> order.</summary>
    public IReadOnlyList<Button> CategoryButtons => _categoryButtons;

    /// <summary>The food line.</summary>
    public string FoodText => _food.Text;

    protected override Key ToggleKey => Key.K;

    protected override Vector2 WindowSize => new(960, 760);

    /// <summary>Chooses the storage whose filter is shown.</summary>
    public void Select(EntityId storage)
    {
        Selected = storage;
        if (IsOpen)
        {
            Refresh();
        }
    }

    /// <summary>The row of a listed storage, or null.</summary>
    public Button? ButtonOf(EntityId storage)
    {
        for (int i = 0; i < ShownStorages; i++)
        {
            if (_storageRows[i].Entity == storage)
            {
                return _storageRows[i].Button;
            }
        }

        return null;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged)
        {
            _languageDirty = true;
        }
    }

    protected override void BuildContent(VBoxContainer column)
    {
        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var title = UiKit.Label("ui.storage.title", 26);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(title);
        top.AddChild(UiKit.Button("ui.window.close", Close));
        column.AddChild(top);

        column.AddChild(UiKit.Label("ui.storage.totals", 18));
        _totals = new GridContainer { Columns = 3 };
        _totals.AddThemeConstantOverride("h_separation", 12);
        _totals.AddThemeConstantOverride("v_separation", 2);
        AddCell(_totals, "ui.storage.col.item", ItemNameWidth, HorizontalAlignment.Left);
        AddCell(_totals, "ui.storage.col.backpack", NumberWidth, HorizontalAlignment.Right);
        AddCell(_totals, "ui.storage.col.storages", NumberWidth, HorizontalAlignment.Right);
        column.AddChild(_totals);

        _storageHeader = UiKit.Label("ui.storage.list", 18);
        column.AddChild(_storageHeader);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _storageList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _storageList.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_storageList);
        column.AddChild(scroll);
        _noStorages = UiKit.Label("ui.storage.none");
        column.AddChild(_noStorages);

        _filterTitle = UiKit.Label("ui.storage.filter", 18);
        column.AddChild(_filterTitle);
        _filterRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _filterRow.AddThemeConstantOverride("separation", 8);
        for (int i = 0; i < ItemCategories.All.Count; i++)
        {
            int bit = ItemCategories.BitOf(ItemCategories.All[i]);
            var button = new Button
            {
                Text = "ui.storage.category." + ItemCategories.All[i],
                ToggleMode = true,
                CustomMinimumSize = new Vector2(150, 40),
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "ui.storage.filter.tooltip",
            };
            button.Pressed += () => OnCategoryPressed(bit);
            _categoryButtons.Add(button);
            _filterRow.AddChild(button);
        }

        column.AddChild(_filterRow);
        _deposit = UiKit.Button("ui.storage.deposit", OnDepositPressed);
        _deposit.TooltipText = "ui.storage.deposit.tooltip";
        column.AddChild(_deposit);
        _food = UiKit.Label(string.Empty, 18);
        column.AddChild(_food);
    }

    protected override void OnBound()
    {
        foreach (ItemRow row in _itemRows)
        {
            row.Name.QueueFree();
            row.Backpack.QueueFree();
            row.Storages.QueueFree();
        }

        _itemRows.Clear();
        foreach (StorageRow row in _storageRows)
        {
            row.Button.QueueFree();
        }

        _storageRows.Clear();
        ShownStorages = 0;
        Selected = EntityId.None;
        _languageDirty = true;
    }

    protected override void Update(SimulationHost host)
    {
        CoreSimulation sim = host.Simulation;
        if (sim.World is not { } world)
        {
            return;
        }

        UpdateTotals(sim, world);
        UpdateStorages(sim, world);
        UpdateFilter(sim, world);
        UpdateFood(sim);
        _languageDirty = false;
    }

    private void UpdateTotals(CoreSimulation sim, GameWorld world)
    {
        ContentCatalog content = sim.Content;
        if (_itemRows.Count == 0)
        {
            foreach (ItemDef item in content.Items)
            {
                if (!content.TryGetId(item.Id, out DefId id))
                {
                    continue;
                }

                var row = new ItemRow(id, item.EffectiveNameKey);
                _totals.AddChild(row.Name);
                _totals.AddChild(row.Backpack);
                _totals.AddChild(row.Storages);
                _itemRows.Add(row);
            }
        }

        EntityId hero = world.HeroEntity;
        Inventory backpack = default;
        bool hasBackpack = hero != EntityId.None && world.Inventories.TryGet(hero, out backpack);
        foreach (ItemRow row in _itemRows)
        {
            int inBackpack = hasBackpack ? backpack.Count(row.Item) : 0;
            int inStorages = Construction.CountAvailable(sim, row.Item) - inBackpack;
            row.Set(inBackpack, inStorages);
            if (_languageDirty)
            {
                row.Name.Text = Tr(row.NameKey);
            }
        }
    }

    private void UpdateStorages(CoreSimulation sim, GameWorld world)
    {
        ContentCatalog content = sim.Content;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        int shown = 0;
        bool selectedFound = false;
        for (int i = 0; i < owners.Length; i++)
        {
            BuildingDef def = Construction.DefOf(content, buildings[i]);
            if (!Construction.IsStorage(def) || buildings[i].Settlement != Building.PlayerSettlement || !world.Inventories.Has(owners[i]))
            {
                continue;
            }

            if (shown == _storageRows.Count)
            {
                var row = new StorageRow(this);
                _storageRows.Add(row);
                _storageList.AddChild(row.Button);
            }

            StorageRow storageRow = _storageRows[shown];
            storageRow.Button.Visible = true;
            int used = world.Inventories.Get(owners[i]).UsedStacks;
            int capacity = Construction.LevelOf(content, buildings[i]).Storage;
            storageRow.Set(this, owners[i], def.Id, buildings[i].Level, used, capacity, buildings[i].State, _languageDirty);
            selectedFound |= owners[i] == Selected;
            shown++;
        }

        for (int i = shown; i < _storageRows.Count; i++)
        {
            _storageRows[i].Button.Visible = false;
        }

        ShownStorages = shown;
        if (!selectedFound)
        {
            Selected = shown > 0 ? _storageRows[0].Entity : EntityId.None;
        }

        for (int i = 0; i < shown; i++)
        {
            _storageRows[i].Button.SetPressedNoSignal(_storageRows[i].Entity == Selected);
        }

        _noStorages.Visible = shown == 0;
    }

    private void UpdateFilter(CoreSimulation sim, GameWorld world)
    {
        bool any = Selected != EntityId.None;
        _filterTitle.Visible = any;
        _filterRow.Visible = any;
        _deposit.Visible = any;
        if (!any)
        {
            return;
        }

        int mask = EffectiveMask(world, Selected);
        for (int i = 0; i < _categoryButtons.Count; i++)
        {
            _categoryButtons[i].SetPressedNoSignal((mask & ItemCategories.BitOf(ItemCategories.All[i])) != 0);
        }
    }

    private void UpdateFood(CoreSimulation sim)
    {
        long days = FoodSupply.DaysLeft(sim);
        string text = days < 0
            ? Tr("ui.storage.food_none")
            : BuildingText.Format(this, "ui.storage.food", days, FoodSupply.Stored(sim), FoodSupply.DailyNeed(sim));
        if (!string.Equals(text, _foodShown, StringComparison.Ordinal))
        {
            _foodShown = text;
            _food.Text = text;
        }
    }

    // Core's filter of the storage, or the filter just sent for it until Core shows it (or the time is up).
    private int EffectiveMask(GameWorld world, EntityId storage)
    {
        int mask = Storages.FilterOf(world, storage);
        if (_pendingStorage != EntityId.None)
        {
            if (_pendingStorage != storage || mask == _pendingMask || Time.GetTicksMsec() >= _pendingUntil)
            {
                _pendingStorage = EntityId.None;
            }
            else
            {
                mask = _pendingMask;
            }
        }

        return mask;
    }

    private void OnCategoryPressed(int bit)
    {
        if (Host is not { IsRunning: true } host || Selected == EntityId.None || host.Simulation.World is not { } world)
        {
            return;
        }

        // Toggle from the displayed filter, so quick clicks on several categories add up before Core has ticked.
        int mask = EffectiveMask(world, Selected) ^ bit;
        host.Send(new SetStorageFilterCommand(Selected, mask));
        _pendingStorage = Selected;
        _pendingMask = mask;
        _pendingUntil = Time.GetTicksMsec() + PendingMsec;
        Refresh();
    }

    private void OnDepositPressed()
    {
        if (Host is { IsRunning: true } host && Selected != EntityId.None)
        {
            host.Send(new DepositHeroItemsCommand(Selected));
        }
    }

    private static void AddCell(GridContainer grid, string key, int width, HorizontalAlignment align)
    {
        Label label = UiKit.Label(key, 16, align);
        label.CustomMinimumSize = new Vector2(width, 0);
        grid.AddChild(label);
    }

    private sealed class ItemRow
    {
        private int _backpack = Unset;
        private int _storages = Unset;

        public ItemRow(DefId item, string nameKey)
        {
            Item = item;
            NameKey = nameKey;
            Name = new Label { Text = nameKey, CustomMinimumSize = new Vector2(ItemNameWidth, 0) };
            Backpack = new Label { HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(NumberWidth, 0) };
            Storages = new Label { HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(NumberWidth, 0) };
        }

        public DefId Item { get; }

        public string NameKey { get; }

        public Label Name { get; }

        public Label Backpack { get; }

        public Label Storages { get; }

        public void Set(int backpack, int storages)
        {
            if (backpack != _backpack)
            {
                _backpack = backpack;
                Backpack.Text = backpack.ToString(CultureInfo.InvariantCulture);
            }

            if (storages != _storages)
            {
                _storages = storages;
                Storages.Text = storages.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private sealed class StorageRow
    {
        private int _level = Unset;
        private int _used = Unset;
        private int _capacity = Unset;
        private int _state = Unset;
        private EntityId _entity = EntityId.None;

        public StorageRow(StorageWindow window)
        {
            Button = new Button
            {
                ToggleMode = true,
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(0, 36),
                FocusMode = Control.FocusModeEnum.None,
            };
            Button.Pressed += () => window.Select(Entity);
        }

        public Button Button { get; }

        public EntityId Entity { get; private set; }

        public void Set(StorageWindow window, EntityId entity, string defId, int level, int used, int capacity, BuildingState state, bool languageDirty)
        {
            Entity = entity;
            if (!languageDirty && entity == _entity && level == _level && used == _used && capacity == _capacity && (int)state == _state)
            {
                return;
            }

            _entity = entity;
            _level = level;
            _used = used;
            _capacity = capacity;
            _state = (int)state;
            Button.Text = BuildingText.Format(window, "ui.storage.row", window.Tr(BuildingText.NameKey(defId)), level, used, capacity, window.Tr(BuildingText.StateKey(state)));
        }
    }
}
