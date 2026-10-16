using System.Globalization;
using System.Text;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Jobs;
using Core.Residents;
using Core.Sim;
using Game.Sim;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Ui;

/// <summary>
/// Window "Residents" (J; FR-UI-04, FR-NPC-05, FR-NPC-06): one row per resident with the name, the profession (a drop-down
/// of the ordinary professions; specialists are only shown), what the resident is doing, the home, and a priority button
/// 0 to 4 for every job type (a click goes to the next value). A summary row shows residents and places, food days and the
/// daily tax. The window only shows <see cref="GameWorld.Residents"/> and sends <see cref="SetProfessionCommand"/> and
/// <see cref="SetWorkPriorityCommand"/>; Core decides. Rows are pooled and a row's text is set only when its values change.
/// </summary>
public partial class ResidentsWindow : ToggleWindow
{
    private const int NameWidth = 120;
    private const int ProfessionWidth = 170;
    private const int StateWidth = 270;
    private const int HomeWidth = 100;
    private const int PriorityWidth = 72;
    private const int RowHeight = 36;
    private const int Unset = -1;
    private const string OrdinaryKind = "ordinary";
    private const string NameListId = "human_names";

    // An optimistic value (a command sent, Core has not stepped yet, e.g. on pause) is shown for at most this long.
    private const ulong PendingMsec = 3000;

    private readonly List<Row> _rows = [];
    private readonly StringBuilder _text = new();
    private readonly List<string> _ordinary = [];
    private IReadOnlyList<string> _names = [];
    private VBoxContainer _list = null!;
    private ScrollContainer _scroll = null!;
    private Label _summary = null!;
    private Label _empty = null!;
    private string _summaryShown = string.Empty;
    private EntityId _focus = EntityId.None;
    private bool _scrollToFocus;
    private bool _languageDirty = true;

    /// <summary>Number of rows currently shown (for the smoke test).</summary>
    public int ShownRows { get; private set; }

    /// <summary>The summary line under the table.</summary>
    public string SummaryText => _summary.Text;

    protected override Key ToggleKey => Key.J;

    protected override Vector2 WindowSize => new(1300, 800);

    /// <summary>Opens the window and highlights the row of <paramref name="resident"/>, scrolling to it.</summary>
    public void Focus(EntityId resident)
    {
        _focus = resident;
        _scrollToFocus = resident != EntityId.None;
        Open();
        if (IsOpen)
        {
            Refresh();
        }
    }

    /// <summary>The row of <paramref name="resident"/> among the shown ones, or null.</summary>
    public Row? RowOf(EntityId resident)
    {
        for (int i = 0; i < ShownRows; i++)
        {
            if (_rows[i].Entity == resident)
            {
                return _rows[i];
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
        var title = UiKit.Label("ui.residents.title", 26);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(title);
        top.AddChild(UiKit.Button("ui.window.close", Close));
        column.AddChild(top);

        var header = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        header.AddThemeConstantOverride("separation", 8);
        header.AddChild(Cell("ui.residents.col.name", NameWidth));
        header.AddChild(Cell("ui.residents.col.profession", ProfessionWidth));
        header.AddChild(Cell("ui.residents.col.state", StateWidth));
        header.AddChild(Cell("ui.residents.col.home", HomeWidth));
        for (int kind = 1; kind <= JobKinds.Count; kind++)
        {
            Label job = Cell("ui.residents.job." + JobKinds.Names[kind], PriorityWidth);
            job.TooltipText = "ui.residents.priority.tooltip";
            job.MouseFilter = MouseFilterEnum.Stop;
            header.AddChild(job);
        }

        column.AddChild(header);

        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 2);
        _scroll.AddChild(_list);
        column.AddChild(_scroll);
        _empty = UiKit.Label("ui.residents.empty");
        column.AddChild(_empty);
        _summary = UiKit.Label(string.Empty, 18);
        column.AddChild(_summary);
    }

    protected override void OnBound()
    {
        _ordinary.Clear();
        foreach (NameListDef list in Host!.Content.Names)
        {
            // The list residents draw their names from (the same id Core uses).
            if (string.Equals(list.Id, NameListId, StringComparison.Ordinal))
            {
                _names = list.Names;
            }
        }

        foreach (ProfessionDef profession in Host!.Content.Professions)
        {
            if (string.Equals(profession.Kind, OrdinaryKind, StringComparison.Ordinal))
            {
                _ordinary.Add(profession.Id);
            }
        }

        // The professions of an old pool no longer match the new content: drop the rows, they are rebuilt on demand.
        foreach (Row row in _rows)
        {
            row.Root.QueueFree();
        }

        _rows.Clear();
        ShownRows = 0;
        _languageDirty = true;
    }

    protected override void Update(SimulationHost host)
    {
        CoreSimulation sim = host.Simulation;
        if (sim.World is not { } world)
        {
            return;
        }

        ContentCatalog content = sim.Content;
        ReadOnlySpan<EntityId> owners = world.Residents.Entities;
        Span<Resident> residents = world.Residents.Values;
        while (_rows.Count < owners.Length)
        {
            Row row = new(this);
            _rows.Add(row);
            _list.AddChild(row.Root);
        }

        int housed = 0;
        for (int i = 0; i < owners.Length; i++)
        {
            Row row = _rows[i];
            row.Root.Visible = true;
            housed += residents[i].Home != EntityId.None ? 1 : 0;
            row.Show(this, content, _names, owners[i], residents[i], world.Jobs.Get(owners[i]), owners[i] == _focus, _languageDirty);
        }

        for (int i = owners.Length; i < _rows.Count; i++)
        {
            _rows[i].Root.Visible = false;
        }

        ShownRows = owners.Length;
        _empty.Visible = owners.Length == 0;
        _languageDirty = false;
        UpdateSummary(sim, world, housed);
        if (_scrollToFocus)
        {
            _scrollToFocus = false;
            Row? focused = RowOf(_focus);
            if (focused is not null)
            {
                Callable.From(() =>
                {
                    if (IsInstanceValid(_scroll) && IsInstanceValid(focused.Root))
                    {
                        _scroll.EnsureControlVisible(focused.Root);
                    }
                }).CallDeferred();
            }
        }
    }

    private void UpdateSummary(CoreSimulation sim, GameWorld world, int housed)
    {
        int places = housed + Housing.TotalFreePlaces(sim);
        long days = FoodSupply.DaysLeft(sim);
        _text.Clear();
        _text.Append(BuildingText.Format(this, "ui.residents.summary.population", world.Residents.Count, places)).Append("     ");
        _text.Append(days < 0
            ? Tr("ui.residents.summary.food_none")
            : BuildingText.Format(this, "ui.residents.summary.food", days)).Append("     ");
        _text.Append(BuildingText.Format(this, "ui.residents.summary.tax", FoodSupply.TaxPerDay(sim)));
        string text = _text.ToString();
        if (!string.Equals(text, _summaryShown, StringComparison.Ordinal))
        {
            _summaryShown = text;
            _summary.Text = text;
        }
    }

    private static Label Cell(string key, int width)
    {
        Label label = UiKit.Label(key, 16);
        label.CustomMinimumSize = new Vector2(width, 0);
        label.HorizontalAlignment = width == PriorityWidth ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        return label;
    }

    /// <summary>One pooled row of the table.</summary>
    public sealed class Row
    {
        private readonly ResidentsWindow _window;
        private readonly Label _name;
        private readonly Label _state;
        private readonly Label _home;
        private int _nameIndex = Unset;
        private int _stateKey = Unset;
        private int _homeKey = Unset;
        private bool _focused;
        // Priorities sent and not yet seen in Core (Unset when none), with the time (msec) after which they are dropped.
        private readonly int[] _pending;
        private readonly ulong[] _pendingUntil;
        private string? _professionShown;
        private string? _pendingProfession;
        private ulong _pendingProfessionUntil;
        private EntityId _popupEntity = EntityId.None;

        internal Row(ResidentsWindow window)
        {
            _window = window;
            Root = new HBoxContainer { CustomMinimumSize = new Vector2(0, RowHeight), MouseFilter = MouseFilterEnum.Ignore };
            Root.AddThemeConstantOverride("separation", 8);
            _name = Text(NameWidth);
            Profession = new MenuButton
            {
                CustomMinimumSize = new Vector2(ProfessionWidth, 0),
                FocusMode = Control.FocusModeEnum.None,
                Flat = false,
                TooltipText = "ui.residents.profession.tooltip",
            };
            Profession.AboutToPopup += () => _popupEntity = Entity;
            Profession.GetPopup().IdPressed += OnProfessionChosen;
            _state = Text(StateWidth);
            _home = Text(HomeWidth);
            Root.AddChild(_name);
            Root.AddChild(Profession);
            Root.AddChild(_state);
            Root.AddChild(_home);
            Priorities = new Button[JobKinds.Count];
            for (int i = 0; i < Priorities.Length; i++)
            {
                JobKind kind = (JobKind)(i + 1);
                var button = new Button { CustomMinimumSize = new Vector2(PriorityWidth, 0), FocusMode = Control.FocusModeEnum.None };
                button.Pressed += () => OnPriorityPressed(kind);
                Priorities[i] = button;
                Root.AddChild(button);
            }

            Shown = new int[JobKinds.Count];
            Array.Fill(Shown, Unset);
            _pending = new int[JobKinds.Count];
            _pendingUntil = new ulong[JobKinds.Count];
            Array.Fill(_pending, Unset);
        }

        /// <summary>The row container.</summary>
        public HBoxContainer Root { get; }

        /// <summary>The resident this row shows.</summary>
        public EntityId Entity { get; private set; }

        /// <summary>The profession drop-down.</summary>
        public MenuButton Profession { get; }

        /// <summary>The priority buttons in <see cref="JobKinds.Names"/> order (without "none").</summary>
        public Button[] Priorities { get; }

        /// <summary>The state text.</summary>
        public string StateText => _state.Text;

        /// <summary>The name text.</summary>
        public string NameText => _name.Text;

        /// <summary>The home text.</summary>
        public string HomeText => _home.Text;

        // Displayed priorities: Core's value, or the value just sent while Core has not confirmed it.
        private int[] Shown { get; }

        internal void Show(ResidentsWindow window, ContentCatalog content, IReadOnlyList<string> names, EntityId entity, Resident resident, ResidentJob job, bool focused, bool languageDirty)
        {
            if (entity != Entity)
            {
                Entity = entity;
                _nameIndex = Unset;
                _stateKey = Unset;
                _homeKey = Unset;
                _professionShown = null;
                _pendingProfession = null;
                Array.Fill(Shown, Unset);
                Array.Fill(_pending, Unset);
            }

            if (languageDirty)
            {
                _professionShown = null;
                _stateKey = Unset;
                _homeKey = Unset;
            }

            if (resident.Name != _nameIndex)
            {
                _nameIndex = resident.Name;
                _name.Text = names[resident.Name];
            }

            ShowProfession(window, content, resident);

            // One integer stands for the state, the job type and its phase: the text is rebuilt only when it changes.
            int stateKey = ((int)resident.State << 16) | ((int)job.Kind << 8) | (int)job.Phase;
            if (stateKey != _stateKey)
            {
                _stateKey = stateKey;
                _state.Text = DescribeState(window, resident.State, job);
            }

            int homeKey = resident.Home != EntityId.None ? 1 : 0;
            if (homeKey != _homeKey)
            {
                _homeKey = homeKey;
                _home.Text = window.Tr(homeKey == 1 ? "ui.residents.home.yes" : "ui.residents.home.no");
            }

            for (int i = 0; i < Priorities.Length; i++)
            {
                int priority = resident.Priorities.Get((JobKind)(i + 1));
                if (_pending[i] != Unset)
                {
                    // Drop the sent value once Core shows it, or when Core did not take it in time.
                    if (priority == _pending[i] || Time.GetTicksMsec() >= _pendingUntil[i])
                    {
                        _pending[i] = Unset;
                    }
                    else
                    {
                        priority = _pending[i];
                    }
                }

                if (priority != Shown[i])
                {
                    Shown[i] = priority;
                    Priorities[i].Text = priority == 0 ? "-" : priority.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (focused != _focused)
            {
                _focused = focused;
                Root.Modulate = focused ? new Color(1f, 0.9f, 0.45f) : Colors.White;
            }
        }

        private static string DescribeState(ResidentsWindow window, ResidentState state, ResidentJob job)
        {
            if (job.Kind != JobKind.None && state is ResidentState.Walking or ResidentState.Working)
            {
                return BuildingText.Format(window, "ui.residents.work_format",
                    window.Tr("ui.residents.work." + JobKinds.Names[(int)job.Kind]),
                    window.Tr("ui.residents.phase." + JobKinds.PhaseNames[(int)job.Phase]));
            }

            return window.Tr("ui.residents.state." + state.ToString().ToLowerInvariant());
        }

        // Shows the profession (the one just chosen while Core has not confirmed it) and rebuilds the drop-down on change.
        private void ShowProfession(ResidentsWindow window, ContentCatalog content, Resident resident)
        {
            var def = content.Get<ProfessionDef>(resident.Profession);
            if (_pendingProfession is not null
                && (string.Equals(def.Id, _pendingProfession, StringComparison.Ordinal) || Time.GetTicksMsec() >= _pendingProfessionUntil))
            {
                _pendingProfession = null;
            }

            string shownId = _pendingProfession ?? def.Id;
            PopupMenu popup = Profession.GetPopup();
            if (string.Equals(shownId, _professionShown, StringComparison.Ordinal) || popup.Visible)
            {
                // Unchanged, or the list is open: clearing it now would drop the item under the cursor (retried next refresh).
                return;
            }

            _professionShown = shownId;
            Profession.Text = window.Tr("profession." + shownId + ".name");
            bool ordinary = string.Equals(def.Kind, OrdinaryKind, StringComparison.Ordinal);
            Profession.Disabled = !ordinary;
            popup.Clear();
            if (!ordinary)
            {
                return;
            }

            for (int i = 0; i < window._ordinary.Count; i++)
            {
                popup.AddRadioCheckItem(window.Tr("profession." + window._ordinary[i] + ".name"), i);
                popup.SetItemChecked(i, string.Equals(window._ordinary[i], shownId, StringComparison.Ordinal));
            }
        }

        private Label Text(int width)
        {
            var label = new Label { CustomMinimumSize = new Vector2(width, 0), VerticalAlignment = VerticalAlignment.Center, ClipText = true };
            return label;
        }

        private void OnProfessionChosen(long index)
        {
            // The resident remembered when the list opened: rows are pooled and may show someone else by now.
            if (index >= 0 && index < _window._ordinary.Count && _popupEntity != EntityId.None && _window.Host is { } host)
            {
                string id = _window._ordinary[(int)index];
                host.Send(new SetProfessionCommand(_popupEntity, id));
                if (_popupEntity == Entity)
                {
                    _pendingProfession = id;
                    _pendingProfessionUntil = Time.GetTicksMsec() + PendingMsec;
                    _professionShown = null;
                    _window.Refresh();
                }
            }
        }

        private void OnPriorityPressed(JobKind kind)
        {
            if (_window.Host is not { } host)
            {
                return;
            }

            // Step from the displayed value, so two quick clicks give 0 to 1 to 2 even before Core has ticked.
            int slot = (int)kind - 1;
            int next = (Math.Max(Shown[slot], 0) + 1) % (WorkPriorities.Max + 1);
            host.Send(new SetWorkPriorityCommand(Entity, JobKinds.Names[(int)kind], next));
            _pending[slot] = next;
            _pendingUntil[slot] = Time.GetTicksMsec() + PendingMsec;
            Shown[slot] = next;
            Priorities[slot].Text = next == 0 ? "-" : next.ToString(CultureInfo.InvariantCulture);
        }
    }
}
