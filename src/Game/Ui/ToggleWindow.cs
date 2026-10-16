using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// Base of the big information windows (residents, storages; FR-UI-04): a centred panel that stops the mouse, opens and
/// closes with one key (read by physical key code, so it works on every keyboard layout), closes with Esc, closes when the
/// simulation is replaced, and refreshes itself a few times per second while open. Content comes from the derived class.
/// </summary>
public abstract partial class ToggleWindow : PanelContainer
{
    private const int FramesPerRefresh = 6;
    private const int Margin = 12;

    private int _frame;
    private bool _built;

    /// <summary>True while the window is shown.</summary>
    public bool IsOpen => Visible;

    /// <summary>Raised when the window opens (the owner closes the other windows).</summary>
    public event Action? Opened;

    /// <summary>The host the window shows; null before <see cref="Bind"/>.</summary>
    protected SimulationHost? Host { get; private set; }

    /// <summary>The key that opens and closes the window.</summary>
    protected abstract Key ToggleKey { get; }

    /// <summary>Size of the window in pixels.</summary>
    protected abstract Vector2 WindowSize { get; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        SetAnchorsPreset(LayoutPreset.Center);
        OffsetLeft = -WindowSize.X / 2;
        OffsetRight = WindowSize.X / 2;
        OffsetTop = -WindowSize.Y / 2;
        OffsetBottom = WindowSize.Y / 2;
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        CustomMinimumSize = WindowSize;

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, Margin);
        }

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 6);
        margin.AddChild(column);
        AddChild(margin);
        BuildContent(column);
        _built = true;
    }

    /// <summary>Connects the window to the host; the window closes when the simulation is replaced.</summary>
    public void Bind(SimulationHost host)
    {
        if (Host is not null)
        {
            Host.SimulationChanged -= Close;
        }

        Host = host;
        host.SimulationChanged += Close;
        Close();
        OnBound();
    }

    public override void _ExitTree()
    {
        if (Host is not null)
        {
            Host.SimulationChanged -= Close;
        }
    }

    /// <summary>Opens the window (when a game is running) and fills it.</summary>
    public void Open()
    {
        if (Host is not { IsRunning: true } || !_built)
        {
            return;
        }

        Visible = true;
        Opened?.Invoke();
        Refresh();
    }

    /// <summary>Closes the window.</summary>
    public void Close() => Visible = false;

    /// <summary>Opens a closed window, closes an open one.</summary>
    public void Toggle()
    {
        if (Visible)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Recomputes what the window shows now.</summary>
    public void Refresh()
    {
        _frame = 0;
        if (Host is { IsRunning: true } && _built)
        {
            Update(Host);
        }
    }

    public override void _Process(double delta)
    {
        if (Visible && ++_frame >= FramesPerRefresh)
        {
            Refresh();
        }
    }

    // Esc is taken before the unhandled input, so the pause menu does not open on the same press.
    public override void _Input(InputEvent @event)
    {
        if (Visible && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is not { IsRunning: true, InputBlocked: false }
            || @event is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: var code, CtrlPressed: false, AltPressed: false, MetaPressed: false }
            || code != ToggleKey)
        {
            return;
        }

        Toggle();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Builds the controls into <paramref name="column"/>, once.</summary>
    protected abstract void BuildContent(VBoxContainer column);

    /// <summary>Called by <see cref="Bind"/> after the host changed.</summary>
    protected virtual void OnBound()
    {
    }

    /// <summary>Refreshes the shown values; called a few times per second while open. Must not allocate when nothing changed.</summary>
    protected abstract void Update(SimulationHost host);
}
