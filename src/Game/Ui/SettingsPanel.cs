using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>Settings page: language and master volume (stored in <c>user://settings.cfg</c>). Raises <see cref="BackPressed"/>.</summary>
public partial class SettingsPanel : VBoxContainer
{
    private static readonly string[] _languageNames = ["Русский", "English"];

    private OptionButton _language = null!;
    private HSlider _volume = null!;
    private bool _refreshing;

    /// <summary>The player pressed Back.</summary>
    public event Action? BackPressed;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(UiKit.ColumnWidth, 0);
        AddThemeConstantOverride("separation", 12);
        AddChild(UiKit.Label("ui.settings.title", 28, HorizontalAlignment.Center));

        AddChild(UiKit.Label("ui.settings.language"));
        _language = new OptionButton();
        foreach (string name in _languageNames)
        {
            _language.AddItem(name);
        }

        _language.ItemSelected += index =>
        {
            if (!_refreshing)
            {
                UserSettings.SetLanguage(UserSettings.Languages[(int)index]);
            }
        };
        AddChild(_language);

        AddChild(UiKit.Label("ui.settings.volume"));
        _volume = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01 };
        _volume.ValueChanged += value =>
        {
            if (!_refreshing)
            {
                UserSettings.SetVolume((float)value);
            }
        };
        AddChild(_volume);

        AddChild(UiKit.Button("ui.menu.back", () => BackPressed?.Invoke()));
        Refresh();
    }

    /// <summary>Shows the stored values (call when the page is opened; F2 can change the language meanwhile).</summary>
    public void Refresh()
    {
        _refreshing = true;
        _language.Selected = Math.Max(0, Array.IndexOf(UserSettings.Languages, UserSettings.Language));
        _volume.Value = UserSettings.MasterVolume;
        _refreshing = false;
    }
}
