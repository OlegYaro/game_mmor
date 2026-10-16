using Godot;

namespace Game.Ui;

/// <summary>
/// The row of buttons that open the information windows (residents, storage), right under the resource bar in the top
/// right corner. The buttons only raise events; the owner opens the windows.
/// </summary>
public partial class WindowButtons : HBoxContainer
{
    private const int Top = 60;
    private const int Gutter = 16;

    /// <summary>The "Residents" button was pressed.</summary>
    public event Action? ResidentsRequested;

    /// <summary>The "Storage" button was pressed.</summary>
    public event Action? StorageRequested;

    /// <summary>The "Residents" button.</summary>
    public Button ResidentsButton { get; private set; } = null!;

    /// <summary>The "Storage" button.</summary>
    public Button StorageButton { get; private set; } = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -Gutter;
        OffsetRight = -Gutter;
        OffsetTop = Top;
        OffsetBottom = Top;
        GrowHorizontal = GrowDirection.Begin;
        AddThemeConstantOverride("separation", 8);
        ResidentsButton = UiKit.Button("ui.window.residents_button", () => ResidentsRequested?.Invoke());
        StorageButton = UiKit.Button("ui.window.storage_button", () => StorageRequested?.Invoke());
        AddChild(ResidentsButton);
        AddChild(StorageButton);
    }
}
