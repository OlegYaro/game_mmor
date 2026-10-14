using Core.Content;
using Core.DevConsole;
using Core.Logging;
using Core.Sim;
using Godot;
using CoreDevConsole = Core.DevConsole.DevConsole;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Sim;

/// <summary>
/// Owns the Core simulation and drives it from the frame clock (ADR 0005): converts frame time to microseconds,
/// asks <see cref="TickScheduler"/> how many ticks to run and steps the simulation. Handles the speed keys
/// (Space = pause, 1/2/3 = x1/x2/x4). Created empty; <see cref="Initialize"/> loads content and starts the run.
/// </summary>
public partial class SimulationHost : Node
{
    private const double MicrosecondsPerSecond = 1_000_000.0;

    /// <summary>Holder of the current simulation (the console's <c>load</c> replaces it).</summary>
    public SimulationHolder Holder { get; private set; } = null!;

    /// <summary>Speed and pause.</summary>
    public TickScheduler Scheduler { get; private set; } = null!;

    /// <summary>Log shared by every simulation of this run.</summary>
    public SimLog Log { get; private set; } = null!;

    /// <summary>Developer console bound to this run.</summary>
    public CoreDevConsole Console { get; private set; } = null!;

    /// <summary>True after a successful <see cref="Initialize"/>.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The current simulation.</summary>
    public CoreSimulation Simulation => Holder.Current;

    /// <summary>
    /// Loads content and creates the simulation. On failure it reports the error with <see cref="GD.PushError(string)"/>
    /// and quits with exit code 1: the game cannot run without content.
    /// </summary>
    public bool Initialize()
    {
        if (!GameContent.TryLoad(out ContentCatalog content, out string error))
        {
            GD.PushError($"Cannot start: {error}");
            GetTree().Quit(1);
            return false;
        }

        Log = new SimLog();
        var simulation = new CoreSimulation(GameSettings.ResolveSeed(), content, Log);
        Holder = new SimulationHolder(simulation);
        Scheduler = new TickScheduler(content.Time.TicksPerSecond, devSpeedsEnabled: OS.IsDebugBuild());
        Console = new CoreDevConsole(new DevConsoleContext(Holder, Scheduler, Log, content, GameSettings.SaveDirectory));
        IsRunning = true;
        GD.Print($"Simulation started: seed {simulation.Seed}, content {ContentHash.Format(content.ContentHash)}");
        return true;
    }

    public override void _Process(double delta)
    {
        if (!IsRunning)
        {
            return;
        }

        int ticks = Scheduler.Advance((long)(delta * MicrosecondsPerSecond));
        for (int i = 0; i < ticks; i++)
        {
            Holder.Current.Step();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsRunning || @event is not InputEventKey { Pressed: true, Echo: false } key)
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
}
