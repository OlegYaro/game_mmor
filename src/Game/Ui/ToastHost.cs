using Godot;

namespace Game.Ui;

/// <summary>Shows short non-fatal messages at the bottom of the screen for a few seconds. Messages are translation keys.</summary>
public partial class ToastHost : Control
{
    private const double SecondsVisible = 4.0;

    private Label _label = null!;
    private Godot.Timer _timer = null!;
    private string _key = string.Empty;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _label = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _label.SetAnchorsPreset(LayoutPreset.BottomWide);
        _label.OffsetTop = -96;
        _label.OffsetBottom = -48;
        _label.AddThemeFontSizeOverride("font_size", 22);
        _label.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 6);
        AddChild(_label);
        _timer = new Godot.Timer { OneShot = true };
        _timer.Timeout += () => _label.Visible = false;
        AddChild(_timer);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && _label is not null && _key.Length > 0)
        {
            _label.Text = Tr(_key);
        }
    }

    /// <summary>Shows the translation <paramref name="key"/>.</summary>
    public void Show(string key)
    {
        _key = key;
        _label.Text = Tr(key);
        _label.Visible = true;
        _timer.Start(SecondsVisible);
    }
}
