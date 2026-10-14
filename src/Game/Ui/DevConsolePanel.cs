using Core.Logging;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// Developer console panel (NFR-07): toggled with the grave/tilde key, runs lines through the Core console and
/// prints new simulation log events at Warning level or above.
/// </summary>
public partial class DevConsolePanel : PanelContainer
{
    private const int MaxHistory = 50;

    private readonly List<string> _history = [];
    private int _historyIndex;
    private SimulationHost? _host;
    private IDisposable? _logSubscription;
    private RichTextLabel _output = null!;
    private LineEdit _input = null!;
    private Label _title = null!;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsPreset(LayoutPreset.TopWide);
        CustomMinimumSize = new Vector2(0, 360);

        var box = new VBoxContainer();
        AddChild(box);
        _title = new Label();
        box.AddChild(_title);
        _output = new RichTextLabel { ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill, FitContent = false, SelectionEnabled = true };
        box.AddChild(_output);
        _input = new LineEdit();
        _input.TextSubmitted += OnSubmitted;
        box.AddChild(_input);
        ApplyTranslations();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && _title is not null)
        {
            ApplyTranslations();
        }
    }

    public override void _ExitTree()
    {
        _logSubscription?.Dispose();
        _logSubscription = null;
    }

    /// <summary>A console command (<c>goto</c>) asked the camera to centre on a tile.</summary>
    public event Action<int, int>? CameraRequested;

    /// <summary>Connects the panel to the run it controls and starts listening to its log.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        _logSubscription?.Dispose();
        _logSubscription = host.Log.Subscribe(OnLogEvent);
    }

    // The grave key is read in _Input (before GUI focus handling) so it also closes the console while the
    // LineEdit has focus. PhysicalKeycode keeps it working on non-US layouts.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.PhysicalKeycode == Key.Quoteleft)
            {
                SetOpen(!Visible);
                GetViewport().SetInputAsHandled();
            }
            else if (Visible && _input.HasFocus() && key.Keycode is Key.Up or Key.Down)
            {
                BrowseHistory(key.Keycode == Key.Up ? -1 : 1);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void ApplyTranslations()
    {
        _title.Text = Tr("ui.console.title");
        _input.PlaceholderText = Tr("ui.console.placeholder");
    }

    private void SetOpen(bool open)
    {
        Visible = open;
        if (open)
        {
            _input.GrabFocus();
        }
        else
        {
            _input.ReleaseFocus();
        }
    }

    private void OnSubmitted(string line)
    {
        _input.Clear();
        if (string.IsNullOrWhiteSpace(line) || _host is not { IsRunning: true })
        {
            return;
        }

        _history.Add(line);
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }

        _historyIndex = _history.Count;
        Print($"> {line}", null);
        Core.DevConsole.ConsoleResult result = _host.Console.Execute(line);
        foreach (string text in result.Lines)
        {
            Print(text, result.Success ? null : "red");
        }

        if (result.CameraRequest is { } camera)
        {
            CameraRequested?.Invoke(camera.TileX, camera.TileY);
        }
    }

    private void BrowseHistory(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        _input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
        _input.CaretColumn = _input.Text.Length;
    }

    private void OnLogEvent(LogEvent logEvent)
    {
        if (logEvent.Level >= LogLevel.Warning)
        {
            Print(logEvent.ToString(), logEvent.Level == LogLevel.Error ? "red" : "yellow");
        }
    }

    private void Print(string text, string? color)
    {
        string safe = text.Replace("[", "[lb]", StringComparison.Ordinal);
        _output.AppendText(color is null ? safe + "\n" : $"[color={color}]{safe}[/color]\n");
    }
}
