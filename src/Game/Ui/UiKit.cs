using Godot;

namespace Game.Ui;

/// <summary>Small helpers that build the menu controls. Texts are translation keys: controls translate them on their own.</summary>
public static class UiKit
{
    /// <summary>Width of menu columns in pixels.</summary>
    public const float ColumnWidth = 560;

    /// <summary>A button whose text is the translation key <paramref name="key"/>.</summary>
    public static Button Button(string key, Action onPressed)
    {
        // No keyboard focus: a focused button would take Space (pause) as ui_accept and press itself again.
        var button = new Button { Text = key, CustomMinimumSize = new Vector2(0, 44), FocusMode = Control.FocusModeEnum.None };
        button.Pressed += onPressed;
        return button;
    }

    /// <summary>A label whose text is the translation key <paramref name="key"/>.</summary>
    public static Label Label(string key, int fontSize = 0, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = key, HorizontalAlignment = align };
        if (fontSize > 0)
        {
            label.AddThemeFontSizeOverride("font_size", fontSize);
        }

        return label;
    }

    /// <summary>A vertical column of the standard menu width.</summary>
    public static VBoxContainer Column(int separation = 10)
    {
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(ColumnWidth, 0) };
        column.AddThemeConstantOverride("separation", separation);
        return column;
    }

    /// <summary>A panel with margins that holds <paramref name="content"/>, centred on screen inside a full-rect container.</summary>
    public static CenterContainer CenteredPanel(Control content)
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer();
        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 24);
        }

        margin.AddChild(content);
        panel.AddChild(margin);
        center.AddChild(panel);
        return center;
    }

    /// <summary>Shows a one-shot confirmation dialog with the translated question; runs <paramref name="onYes"/> when confirmed.</summary>
    public static void Ask(Node owner, string questionKey, Action onYes)
    {
        var dialog = new ConfirmationDialog
        {
            Exclusive = true,
            DialogText = owner.Tr(questionKey),
            OkButtonText = owner.Tr("ui.confirm.yes"),
            CancelButtonText = owner.Tr("ui.confirm.no"),
        };
        dialog.Confirmed += onYes;
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        owner.AddChild(dialog);
        dialog.PopupCentered();
    }
}
