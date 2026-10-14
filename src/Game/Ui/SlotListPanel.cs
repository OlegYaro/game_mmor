using System.Globalization;
using Core.Save;
using Core.Sim;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// List of save slots. In <see cref="SlotListMode.Load"/> it shows the existing autosaves and manual slots with day,
/// seed, difficulty and file time, each with Load and Delete; unreadable files are shown as such and can be deleted.
/// In <see cref="SlotListMode.Save"/> it shows every manual slot (empty ones too) with Save, asking before an overwrite.
/// </summary>
public partial class SlotListPanel : VBoxContainer
{
    private readonly SlotListMode _mode;
    private SimulationHost? _host;
    private VBoxContainer _rows = null!;

    /// <summary>Creates the panel for a mode.</summary>
    public SlotListPanel(SlotListMode mode)
    {
        _mode = mode;
    }

    /// <summary>Godot needs a parameterless constructor for scene instancing; the panel is always created in code.</summary>
    public SlotListPanel()
        : this(SlotListMode.Load)
    {
    }

    /// <summary>The player chose a slot to load (Load mode) or to save into (Save mode, after the overwrite question).</summary>
    public event Action<SaveSlotId>? SlotChosen;

    /// <summary>The player pressed Back.</summary>
    public event Action? BackPressed;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(UiKit.ColumnWidth, 0);
        AddThemeConstantOverride("separation", 10);
        AddChild(UiKit.Label(_mode == SlotListMode.Load ? "ui.load.title" : "ui.save.title", 28, HorizontalAlignment.Center));
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 6);
        AddChild(_rows);
        AddChild(UiKit.Button("ui.menu.back", () => BackPressed?.Invoke()));
        Refresh();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && _rows is not null && Visible)
        {
            Refresh();
        }
    }

    /// <summary>Connects the panel to the host whose slots it lists.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        Refresh();
    }

    /// <summary>Re-reads the slot directory and rebuilds the rows.</summary>
    public void Refresh()
    {
        if (_rows is null)
        {
            return;
        }

        foreach (Node child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        if (_host is not { IsInitialized: true })
        {
            return;
        }

        IReadOnlyList<SaveSlotEntry> entries = _host.Slots.List();
        if (_mode == SlotListMode.Save)
        {
            for (int i = 1; i <= _host.Slots.Balance.ManualSlots; i++)
            {
                var id = new SaveSlotId(SaveSlotKind.Manual, i);
                AddRow(id, entries.FirstOrDefault(e => e.Id == id));
            }

            return;
        }

        foreach (SaveSlotEntry entry in entries)
        {
            AddRow(entry.Id, entry);
        }

        if (entries.Count == 0)
        {
            _rows.AddChild(UiKit.Label("ui.menu.no_saves", 0, HorizontalAlignment.Center));
        }
    }

    private void AddRow(SaveSlotId id, SaveSlotEntry? entry)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        string name = Tr(id.Kind == SaveSlotKind.Manual ? "ui.slot.manual" : "ui.slot.autosave");
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddChild(new Label { Text = string.Format(CultureInfo.InvariantCulture, name, id.Index) });
        var detail = new Label { Text = Describe(entry) };
        detail.AddThemeFontSizeOverride("font_size", 14);
        detail.Modulate = new Color(1, 1, 1, 0.7f);
        text.AddChild(detail);
        row.AddChild(text);

        bool usable = _mode == SlotListMode.Save || entry is { IsReadable: true };
        if (usable)
        {
            Button action = UiKit.Button(_mode == SlotListMode.Load ? "ui.slot.load" : "ui.slot.save", () => OnChosen(id, entry));
            action.CustomMinimumSize = new Vector2(120, 44);
            row.AddChild(action);
        }

        if (_mode == SlotListMode.Load && entry is not null)
        {
            Button delete = UiKit.Button("ui.slot.delete", () => OnDelete(id));
            delete.CustomMinimumSize = new Vector2(120, 44);
            row.AddChild(delete);
        }

        _rows.AddChild(row);
    }

    private string Describe(SaveSlotEntry? entry)
    {
        if (entry is null)
        {
            return Tr("ui.slot.empty");
        }

        if (entry.Info is null)
        {
            return Tr("ui.slot.unreadable");
        }

        SaveInfo info = entry.Info;
        string difficulty = Tr("ui.difficulty." + DifficultyNames.ToName(info.Difficulty));
        string time = entry.FileTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.InvariantCulture, Tr("ui.slot.info"), info.Day + 1, info.Seed, difficulty, time);
    }

    private void OnChosen(SaveSlotId id, SaveSlotEntry? entry)
    {
        if (_mode == SlotListMode.Save && entry is not null)
        {
            UiKit.Ask(this, "ui.confirm.overwrite", () => SlotChosen?.Invoke(id));
        }
        else
        {
            SlotChosen?.Invoke(id);
        }
    }

    private void OnDelete(SaveSlotId id) =>
        UiKit.Ask(this, "ui.confirm.delete", () =>
        {
            SaveSlotResult result = _host!.Slots.Delete(id);
            if (!result.Succeeded)
            {
                GD.PushWarning($"Cannot delete {id}: {result.Error}");
            }

            Refresh();
        });
}

/// <summary>What a <see cref="SlotListPanel"/> is for.</summary>
public enum SlotListMode
{
    /// <summary>Pick a save to load or delete.</summary>
    Load,

    /// <summary>Pick a manual slot to save into.</summary>
    Save,
}
