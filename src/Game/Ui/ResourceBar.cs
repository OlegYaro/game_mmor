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
/// Top-right resource strip (FR-UI-01): for every item of the catalog "in the backpack | in the storages". The backpack
/// is the hero's, the storages are the built, available ones (<see cref="Construction.CountAvailable"/> minus the
/// backpack). Refreshed a few times per second, and the text is rebuilt only when a number or the language changed.
/// </summary>
public partial class ResourceBar : PanelContainer
{
    private const int FramesPerRefresh = 6;

    private readonly StringBuilder _text = new();
    private SimulationHost? _host;
    private Label _label = null!;
    private DefId[] _items = [];
    private int[] _backpack = [];
    private int[] _total = [];
    private int _frame;
    private bool _dirty = true;

    /// <summary>The text currently shown (for the smoke test).</summary>
    public string ShownText => _label.Text;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -16;
        OffsetRight = -16;
        OffsetTop = 12;
        OffsetBottom = 12;
        GrowHorizontal = GrowDirection.Begin;
        _label = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(_label);
        TooltipText = "ui.res.tooltip";
    }

    /// <summary>Connects the bar to the host; it follows simulation swaps (load) by itself.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        _items = [];
        _dirty = true;
    }

    /// <summary>Recomputes the numbers now instead of waiting for the next refresh frame.</summary>
    public void RefreshNow()
    {
        _frame = FramesPerRefresh - 1;
        _Process(0);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged)
        {
            _dirty = true;
        }
    }

    public override void _Process(double delta)
    {
        if (_host is not { IsRunning: true } || ++_frame % FramesPerRefresh != 0)
        {
            return;
        }

        Simulation sim = _host.Simulation;
        if (sim.World is not { } world)
        {
            return;
        }

        if (_items.Length != sim.Content.Items.Count)
        {
            Rebuild(sim.Content);
        }

        EntityId hero = world.HeroEntity;
        Inventory backpack = default;
        bool hasBackpack = hero != EntityId.None && world.Inventories.TryGet(hero, out backpack);
        for (int i = 0; i < _items.Length; i++)
        {
            int inBackpack = hasBackpack ? backpack.Count(_items[i]) : 0;
            int total = Construction.CountAvailable(sim, _items[i]);
            if (inBackpack != _backpack[i] || total != _total[i])
            {
                _backpack[i] = inBackpack;
                _total[i] = total;
                _dirty = true;
            }
        }

        if (_dirty)
        {
            UpdateText(sim.Content);
        }
    }

    private void Rebuild(ContentCatalog content)
    {
        var ids = new List<DefId>(content.Items.Count);
        foreach (ItemDef item in content.Items)
        {
            if (content.TryGetId(item.Id, out DefId id))
            {
                ids.Add(id);
            }
        }

        _items = [.. ids];
        _backpack = new int[_items.Length];
        _total = new int[_items.Length];
        Array.Fill(_backpack, -1);
        Array.Fill(_total, -1);
        _dirty = true;
    }

    private void UpdateText(ContentCatalog content)
    {
        _dirty = false;
        _text.Clear();
        for (int i = 0; i < _items.Length; i++)
        {
            if (i > 0)
            {
                _text.Append("   ");
            }

            ItemDef item = content.Get<ItemDef>(_items[i]);
            _text.Append(Tr(item.EffectiveNameKey)).Append(' ')
                .Append(_backpack[i].ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append((_total[i] - _backpack[i]).ToString(CultureInfo.InvariantCulture));
        }

        _label.Text = _text.ToString();
    }
}
