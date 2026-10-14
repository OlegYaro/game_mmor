using Core.Content;
using Core.DevConsole;
using Core.Logging;
using Core.Save;
using Core.Sim;
using Godot;
using CoreDevConsole = Core.DevConsole.DevConsole;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Sim;

/// <summary>
/// Owns the Core simulation and drives it from the frame clock (ADR 0005): converts frame time to microseconds,
/// asks <see cref="TickScheduler"/> how many ticks to run and steps the simulation. Starts games
/// (<see cref="StartNew"/>, <see cref="LoadSlot"/>), manages save slots and the daily autosave, and handles the speed
/// keys (Space = pause, 1/2/3 = x1/x2/x4). <see cref="Initialize"/> loads content; no game runs until one is started.
/// Failures of saving or loading are returned as localization keys or raised through <see cref="Toast"/>, never thrown.
/// </summary>
public partial class SimulationHost : Node
{
    private const double MicrosecondsPerSecond = 1_000_000.0;

    private CoreSimulation? _observed;

    /// <summary>Holder of the current simulation (the console's <c>load</c> replaces its content).</summary>
    public SimulationHolder Holder { get; private set; } = null!;

    /// <summary>Speed and pause.</summary>
    public TickScheduler Scheduler { get; private set; } = null!;

    /// <summary>Log shared by every simulation of this run.</summary>
    public SimLog Log { get; private set; } = null!;

    /// <summary>Developer console bound to the current game.</summary>
    public CoreDevConsole Console { get; private set; } = null!;

    /// <summary>Loaded content.</summary>
    public ContentCatalog Content { get; private set; } = null!;

    /// <summary>Save slots in the save directory.</summary>
    public SaveSlots Slots { get; private set; } = null!;

    /// <summary>Daily autosave trigger of the current game.</summary>
    public AutosavePolicy Autosave { get; private set; } = new(0);

    /// <summary>True after a successful <see cref="Initialize"/>: content is loaded and games can be started.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>True while a game is running (between a start/load and <see cref="Stop"/>).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The current simulation (valid while <see cref="IsRunning"/>).</summary>
    public CoreSimulation Simulation => Holder.Current;

    /// <summary>When true the speed keys are ignored (a menu is open).</summary>
    public bool InputBlocked { get; set; }

    /// <summary>Raised after the simulation instance was replaced (new game, load, console <c>load</c>). Views rebind here.</summary>
    public event Action? SimulationChanged;

    /// <summary>Raised with a localization key for a short non-fatal message (a failed autosave, for example).</summary>
    public event Action<string>? Toast;

    /// <summary>Directory the slots live in; <see cref="GameSettings.SaveDirectory"/> unless a test overrides it.</summary>
    public string SaveDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// Loads content and prepares the scheduler, log and slots. On failure it reports the error with
    /// <see cref="GD.PushError(string)"/> and quits with exit code 1: the game cannot run without content.
    /// </summary>
    public bool Initialize(string? saveDirectory = null)
    {
        if (!GameContent.TryLoad(out ContentCatalog content, out string error))
        {
            GD.PushError($"Cannot start: {error}");
            GetTree().Quit(1);
            return false;
        }

        Content = content;
        Log = new SimLog();
        Scheduler = new TickScheduler(content.Time.TicksPerSecond, devSpeedsEnabled: OS.IsDebugBuild());
        SaveDirectory = saveDirectory ?? GameSettings.SaveDirectory;
        Slots = new SaveSlots(SaveDirectory, content.Saves, content.Time);
        IsInitialized = true;
        GD.Print($"Content loaded: {content.Count} definitions, hash {ContentHash.Format(content.ContentHash)}");
        return true;
    }

    /// <summary>
    /// Starts a new game (same path for the menu, <c>--autostart</c> and the smoke test). Returns null on success or a
    /// localization key of the problem.
    /// </summary>
    public string? StartNew(NewGameOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CoreSimulation simulation;
        try
        {
            simulation = CoreSimulation.CreateNewGame(Content, options, Log);
        }
        catch (ArgumentException ex)
        {
            GD.PushWarning($"Cannot start a new game: {ex.Message}");
            return "ui.toast.start_failed";
        }

        Replace(simulation);
        GD.Print($"Game started: seed {simulation.Seed}, map {options.MapSize}, difficulty {DifficultyNames.ToName(options.Difficulty)}");
        return null;
    }

    /// <summary>Loads the game of a slot. Returns null on success or a localization key of the problem.</summary>
    public string? LoadSlot(SaveSlotId id)
    {
        SaveLoadResult result = Slots.Load(id, Content, Log);
        if (result.Simulation is null)
        {
            GD.PushWarning($"Cannot load {id}: {result.Error?.Message}");
            return result.Error?.Kind switch
            {
                SaveErrorKind.UnsupportedVersion => "ui.toast.load_version",
                SaveErrorKind.UnsupportedWorldGenVersion or SaveErrorKind.WorldGenMismatch => "ui.toast.load_worldgen",
                _ => "ui.toast.load_failed",
            };
        }

        Replace(result.Simulation);
        GD.Print($"Game loaded from {id}: tick {result.Simulation.Tick}");
        return null;
    }

    /// <summary>Saves the running game to a manual slot. Returns null on success or a localization key.</summary>
    public string? SaveToSlot(SaveSlotId id)
    {
        if (!IsRunning)
        {
            return "ui.toast.save_failed";
        }

        SaveSlotResult result = Slots.SaveManual(Simulation, id);
        if (!result.Succeeded)
        {
            GD.PushWarning($"Cannot save to {id}: {result.Error}");
            return "ui.toast.save_failed";
        }

        return null;
    }

    /// <summary>Writes the daily autosave when a new game day began since the last one; failures become a toast.</summary>
    public void AutosaveIfDue()
    {
        if (!IsRunning || !Autosave.Poll(Simulation.Now.Day))
        {
            return;
        }

        SaveSlotResult result = Slots.Autosave(Simulation);
        if (!result.Succeeded)
        {
            GD.PushWarning($"Autosave failed: {result.Error}");
            Toast?.Invoke("ui.toast.autosave_failed");
        }
    }

    /// <summary>Shows a toast with the translation <paramref name="key"/>.</summary>
    public void ShowToast(string key) => Toast?.Invoke(key);

    /// <summary>Ends the running game (back to the menu). The state is dropped; nothing is saved.</summary>
    public void Stop()
    {
        IsRunning = false;
        InputBlocked = false;
        _observed = null;
    }

    public override void _Process(double delta)
    {
        if (!IsRunning)
        {
            return;
        }

        // The console's "load" swaps the simulation inside the holder.
        if (!ReferenceEquals(_observed, Holder.Current))
        {
            Adopt();
        }

        int ticks = Scheduler.Advance((long)(delta * MicrosecondsPerSecond));
        for (int i = 0; i < ticks; i++)
        {
            Holder.Current.Step();
        }

        if (ticks > 0)
        {
            AutosaveIfDue();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsRunning || InputBlocked || @event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.Space:
                Scheduler.TogglePause();
                break;
            case Key.Key1:
                Scheduler.TrySetSpeed(GameSpeed.X1);
                break;
            case Key.Key2:
                Scheduler.TrySetSpeed(GameSpeed.X2);
                break;
            case Key.Key3:
                Scheduler.TrySetSpeed(GameSpeed.X4);
                break;
            default:
                return;
        }

        GetViewport().SetInputAsHandled();
    }

    private void Replace(CoreSimulation simulation)
    {
        if (Holder is null)
        {
            Holder = new SimulationHolder(simulation);
        }
        else
        {
            Holder.Current = simulation;
        }

        Console = new CoreDevConsole(new DevConsoleContext(Holder, Scheduler, Log, Content, SaveDirectory));
        Scheduler.IsPaused = false;
        Scheduler.TrySetSpeed(GameSpeed.X1);
        IsRunning = true;
        Adopt();
    }

    // Called whenever Holder.Current is a simulation this host has not seen yet.
    private void Adopt()
    {
        _observed = Holder.Current;
        Scheduler.ResetAccumulator();
        Autosave.Reset(_observed.Now.Day);
        SimulationChanged?.Invoke();
    }
}
