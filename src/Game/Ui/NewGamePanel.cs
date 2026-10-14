using Core.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// New game form: seed (number or text, with a Random button), difficulty and the map size (512 or 1024). The random seed is only
/// the default in the text box; the seed that starts the game is the deterministic input (FR-SAVE-05).
/// </summary>
public partial class NewGamePanel : VBoxContainer
{
    private LineEdit _seed = null!;
    private OptionButton _difficulty = null!;
    private OptionButton _size = null!;

    /// <summary>Map sizes offered in the menu (2048 stays CLI-only until it is playable).</summary>
    public static readonly int[] MenuSizes = [512, 1024];

    /// <summary>The player pressed Start.</summary>
    public event Action<NewGameOptions>? StartRequested;

    /// <summary>The player pressed Back.</summary>
    public event Action? BackPressed;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(UiKit.ColumnWidth, 0);
        AddThemeConstantOverride("separation", 12);
        AddChild(UiKit.Label("ui.newgame.title", 28, HorizontalAlignment.Center));

        AddChild(UiKit.Label("ui.newgame.seed"));
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        _seed = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 40 };
        _seed.TextSubmitted += _ => Start();
        seedRow.AddChild(_seed);
        seedRow.AddChild(UiKit.Button("ui.newgame.random", Randomize));
        AddChild(seedRow);

        AddChild(UiKit.Label("ui.newgame.difficulty"));
        _difficulty = new OptionButton();
        foreach (Difficulty difficulty in DifficultyNames.All)
        {
            _difficulty.AddItem("ui.difficulty." + DifficultyNames.ToName(difficulty), (int)difficulty);
        }

        _difficulty.Selected = _difficulty.GetItemIndex((int)Difficulty.Normal);
        AddChild(_difficulty);

        AddChild(UiKit.Label("ui.newgame.map_size"));
        _size = new OptionButton();
        foreach (int size in MenuSizes)
        {
            _size.AddItem("ui.newgame.size." + size.ToString(System.Globalization.CultureInfo.InvariantCulture), size);
        }

        _size.Selected = _size.GetItemIndex(NewGameOptions.DefaultMapSize);
        AddChild(_size);

        AddChild(UiKit.Button("ui.newgame.start", Start));
        AddChild(UiKit.Button("ui.menu.back", () => BackPressed?.Invoke()));
        Randomize();
    }

    /// <summary>Puts a fresh random seed into the box (the UI default only).</summary>
    public void Randomize() =>
        _seed.Text = (((ulong)GD.Randi() << 32) | GD.Randi()).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Focuses the seed box when the page opens.</summary>
    public void FocusSeed() => _seed.GrabFocus();

    private void Start()
    {
        if (!SeedText.TryParse(_seed.Text, out ulong seed))
        {
            Randomize();
            SeedText.TryParse(_seed.Text, out seed);
        }

        var difficulty = (Difficulty)_difficulty.GetItemId(_difficulty.Selected);
        StartRequested?.Invoke(new NewGameOptions(seed, _size.GetItemId(_size.Selected), difficulty));
    }
}
