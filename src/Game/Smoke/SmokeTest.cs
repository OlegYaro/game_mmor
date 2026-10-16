using Core;
using Core.Content;
using Core.DevConsole;
using Core.Entities;
using Core.Logging;
using Core.Save;
using Core.Sim;
using Game.Gameplay;
using Game.Menu;
using Game.Sim;
using Godot;
using CoreDevConsole = Core.DevConsole.DevConsole;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Smoke;

/// <summary>
/// Headless smoke test (ADR 0011). Launched via <c>Main</c> with <c>-- --smoke</c>: loads content, runs the
/// simulation twice for <see cref="TicksToRun"/> ticks and compares the hashes, round-trips a save through
/// <c>user://</c>, drives the developer console, then prints <see cref="OkMarker"/> and quits with exit code 0.
/// Any failed check prints <see cref="FailMarker"/> with the reason and quits with exit code 1.
/// With <c>--smoke-throw</c> it throws inside <c>_Process</c> to prove that <see cref="CrashGuard"/>
/// turns an exception into <see cref="CrashGuard.Marker"/> and exit code 1.
/// </summary>
public partial class SmokeTest : Node
{
    /// <summary>Marker the smoke script looks for in the log.</summary>
    public const string OkMarker = "SMOKE_OK";

    /// <summary>Printed (with the reason) when a check fails.</summary>
    public const string FailMarker = "SMOKE_FAIL";

    private const string ThrowArg = "--smoke-throw";
    private const int FramesToRun = 10;
    private const int ThrowAtFrame = 3;
    private const int TicksToRun = 600;
    private const int ExtraTicks = 50;
    private const int FailureExitCode = 1;
    private const string SaveFileName = "smoke_test.sav";
    private const int SmokeMapSize = 64;
    private const string SmokeSaveFolder = "user://smoke_saves";
    private const int SmokeDays = 5;
    private const int LargeMapSize = 1024;
    private const int LargeMapTicks = 20;
    private const string SmokeLargeFolder = "user://smoke_large";

    private int _frames;
    private bool _throwOnPurpose;

    public override void _Ready()
    {
        _throwOnPurpose = OS.GetCmdlineUserArgs().Contains(ThrowArg);
        GD.Print($"SMOKE_START core={CoreInfo.Name} {CoreInfo.Version}");

        string? failure = RunChecks();
        if (failure is not null)
        {
            Fail(failure);
        }
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_throwOnPurpose && _frames == ThrowAtFrame)
        {
            throw new InvalidOperationException($"Deliberate smoke-test exception ({ThrowArg}).");
        }

        if (_frames < FramesToRun || CrashGuard.HasFailed)
        {
            return;
        }

        GD.Print(OkMarker);
        GetTree().Quit(0);
        SetProcess(false);
    }

    private static CoreSimulation Run(ContentCatalog content, ulong seed, SimLog log)
    {
        var sim = new CoreSimulation(seed, content, log);
        sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(3, 4)));
        sim.Step(TicksToRun / 2);
        sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(-7, 9)));
        sim.Step(TicksToRun / 2);
        return sim;
    }

    // Returns null when everything passed, otherwise the reason. Exceptions are left to CrashGuard.
    private string? RunChecks()
    {
        if (!GameContent.TryLoad(out ContentCatalog content, out string error))
        {
            return "content: " + error;
        }

        string? translation = CheckTranslations();
        if (translation is not null)
        {
            return translation;
        }

        ulong seed = GameSettings.ResolveSeed();
        CoreSimulation first = Run(content, seed, new SimLog());
        CoreSimulation second = Run(content, seed, new SimLog());
        if (first.Tick != TicksToRun || first.Positions.Count != 2)
        {
            return $"unexpected state: tick {first.Tick}, markers {first.Positions.Count}";
        }

        ulong hash = first.ComputeHashes().Combined;
        if (hash != second.ComputeHashes().Combined)
        {
            return "two identical runs gave different hashes";
        }

        string saveDir = GameSettings.SaveDirectory;
        string path = System.IO.Path.Combine(saveDir, SaveFileName);
        SaveGame.WriteFile(first, path);
        SaveLoadResult loaded = SaveGame.ReadFile(path, content);
        System.IO.File.Delete(path);
        if (loaded.Simulation is null)
        {
            return "load failed: " + loaded.Error?.Message;
        }

        if (loaded.Simulation.ComputeHashes().Combined != hash)
        {
            return "hash changed after save and load";
        }

        first.Step(ExtraTicks);
        loaded.Simulation.Step(ExtraTicks);
        if (first.ComputeHashes().Combined != loaded.Simulation.ComputeHashes().Combined)
        {
            return "loaded simulation diverged from the original";
        }

        return CheckWorld(content, seed, saveDir) ?? CheckConsole(content, seed, saveDir) ?? CheckSeedText() ?? CheckSlots(content, seed) ?? CheckLargeMap(seed)
            ?? WorldRenderSmoke.Check(this, content, seed) ?? BuildingUiSmoke.Check(this, seed);
    }

    // A small new game (world + fog) survives a save and load through user:// with the same hashes.
    private static string? CheckWorld(ContentCatalog content, ulong seed, string saveDir)
    {
        CoreSimulation game = CoreSimulation.CreateNewGame(content, new NewGameOptions(seed, SmokeMapSize, Difficulty.Normal));
        game.Enqueue(new RevealAllCommand());
        game.Step(ExtraTicks);
        string path = System.IO.Path.Combine(saveDir, SaveFileName);
        SaveGame.WriteFile(game, path);
        SaveLoadResult loaded = SaveGame.ReadFile(path, content);
        System.IO.File.Delete(path);
        if (loaded.Simulation?.World is null)
        {
            return "world load failed: " + loaded.Error?.Message;
        }

        if (loaded.Simulation.ComputeHashes().Combined != game.ComputeHashes().Combined || loaded.Simulation.World.Fog.CountStates().Unknown != 0)
        {
            return "world changed after save and load";
        }

        GD.Print($"Smoke world: map {SmokeMapSize}, hash {ContentHash.Format(game.World!.MapHash)}");
        return null;
    }

    // The menu scenes build without errors, the pause menu pauses and restores the pause state, the busy state toggles.
    private string? CheckScreens(SimulationHost host)
    {
        var menu = GD.Load<PackedScene>("res://Menu/MainMenu.tscn").Instantiate<MainMenu>();
        AddChild(menu);
        menu.Bind(host);
        menu.SetBusy(true);
        menu.SetBusy(false);
        menu.QueueFree();

        var gameRoot = GD.Load<PackedScene>("res://Gameplay/GameRoot.tscn").Instantiate<GameRoot>();
        AddChild(gameRoot);
        gameRoot.Bind(host);
        var pause = gameRoot.GetNode<PauseMenu>("Ui/PauseMenu");
        host.Scheduler.IsPaused = false;
        pause.Open();
        bool openOk = pause.IsOpen && host.Scheduler.IsPaused && host.InputBlocked;
        pause.Close();
        bool closeOk = !pause.IsOpen && !host.Scheduler.IsPaused && !host.InputBlocked;
        gameRoot.QueueFree();
        return openOk && closeOk ? null : "pause menu did not pause and resume the game";
    }

    private static string? CheckSeedText() =>
        SeedText.TryParse("12345", out ulong number) && number == 12345 && SeedText.TryParse("hello", out ulong words) && words != 0 && !SeedText.TryParse(" ", out _)
            ? null
            : "seed text parsing";

    // Starts a 64x64 game through SimulationHost.StartNew (the path of the menu and --autostart), plays five days with the
    // daily autosave, expects only the last three autosaves, then loads the newest through LoadSlot and compares hashes.
    private string? CheckSlots(ContentCatalog content, ulong seed)
    {
        string dir = ProjectSettings.GlobalizePath(SmokeSaveFolder);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }

        var host = new SimulationHost();
        AddChild(host);
        host.SetProcess(false);
        host.SetProcessUnhandledInput(false);
        try
        {
            if (!host.Initialize(dir))
            {
                return "slots: host did not initialize";
            }

            string? startError = host.StartNew(new NewGameOptions(seed, SmokeMapSize, Difficulty.Hard));
            if (startError is not null || !host.IsRunning || host.Simulation.World?.Size != SmokeMapSize)
            {
                return "slots: new game did not start " + startError;
            }

            int ticksPerDay = content.Time.TicksPerSecond * 60 * content.Time.RealMinutesPerGameDay;
            for (int day = 1; day <= SmokeDays; day++)
            {
                host.Simulation.Step(ticksPerDay);
                host.AutosaveIfDue();
            }

            IReadOnlyList<SaveSlotEntry> list = host.Slots.List();
            string days = string.Join(",", list.Select(e => e.Info?.Day.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"));
            if (list.Count != content.Saves.AutosavesKept || days != "5,4,3" || list.Any(e => e.Id.Kind != SaveSlotKind.Auto))
            {
                return $"slots: expected autosave days 5,4,3 but found '{days}' ({list.Count} files)";
            }

            CoreSimulation before = host.Simulation;
            ulong hash = before.ComputeHashes().Combined;
            SaveSlotEntry? newest = SaveSlots.FindNewest(list);
            if (newest is null)
            {
                return "slots: no newest save";
            }

            string? loadError = host.LoadSlot(newest.Id);
            if (loadError is not null || ReferenceEquals(before, host.Simulation))
            {
                return "slots: load failed " + loadError;
            }

            if (host.Simulation.ComputeHashes().Combined != hash)
            {
                return "slots: hash changed after loading the newest autosave";
            }

            before.Step(ExtraTicks);
            host.Simulation.Step(ExtraTicks);
            if (before.ComputeHashes().Combined != host.Simulation.ComputeHashes().Combined)
            {
                return "slots: loaded game diverged from the original";
            }

            string? ui = CheckScreens(host);
            if (ui is not null)
            {
                return ui;
            }

            GD.Print($"Smoke slots: {list.Count} autosaves (days {days}), newest reloaded with hash {ContentHash.Format(hash)}");
            return null;
        }
        finally
        {
            host.QueueFree();
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }

    // Starts a 1024x1024 game through SimulationHost.StartNew, steps 20 ticks, saves a manual slot and loads it back:
    // the hashes must match (load regenerates the map). Prints the timings so growth shows up in the log.
    private string? CheckLargeMap(ulong seed)
    {
        string dir = ProjectSettings.GlobalizePath(SmokeLargeFolder);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }

        var host = new SimulationHost();
        AddChild(host);
        host.SetProcess(false);
        host.SetProcessUnhandledInput(false);
        try
        {
            if (!host.Initialize(dir))
            {
                return "large map: host did not initialize";
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            string? startError = host.StartNew(new NewGameOptions(seed, LargeMapSize, Difficulty.Normal));
            if (startError is not null || host.Simulation.World?.Size != LargeMapSize)
            {
                return "large map: new game did not start " + startError;
            }

            TimeSpan generate = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            host.Simulation.Step(LargeMapTicks);
            ulong hash = host.Simulation.ComputeHashes().Combined;
            var slot = new SaveSlotId(SaveSlotKind.Manual, 1);
            start = System.Diagnostics.Stopwatch.GetTimestamp();
            string? saveError = host.SaveToSlot(slot);
            TimeSpan save = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            start = System.Diagnostics.Stopwatch.GetTimestamp();
            string? loadError = host.LoadSlot(slot);
            TimeSpan load = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            if (saveError is not null || loadError is not null)
            {
                return $"large map: save/load failed {saveError} {loadError}";
            }

            if (host.Simulation.World?.Size != LargeMapSize || host.Simulation.ComputeHashes().Combined != hash)
            {
                return "large map: hash changed after save and load";
            }

            GD.Print($"Smoke large map: {LargeMapSize} generated in {generate.TotalMilliseconds:F0} ms, save {save.TotalMilliseconds:F0} ms, load {load.TotalMilliseconds:F0} ms");
            return null;
        }
        finally
        {
            host.QueueFree();
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }

    // Proves the CSV import and the project registration work: both languages resolve and differ.
    private static string? CheckTranslations()
    {
        const string key = "ui.hud.day";
        string previous = TranslationServer.GetLocale();
        try
        {
            TranslationServer.SetLocale("ru");
            string ru = TranslationServer.Translate(key).ToString();
            TranslationServer.SetLocale("en");
            string en = TranslationServer.Translate(key).ToString();
            bool ok = ru != key && en != key && ru != en;
            return ok ? null : $"translations: '{key}' gave ru='{ru}' en='{en}'";
        }
        finally
        {
            TranslationServer.SetLocale(previous);
        }
    }

    private static string? CheckConsole(ContentCatalog content, ulong seed, string saveDir)
    {
        var log = new SimLog();
        var holder = new SimulationHolder(new CoreSimulation(seed, content, log));
        var console = new CoreDevConsole(new DevConsoleContext(holder, new TickScheduler(content.Time.TicksPerSecond), log, content, saveDir));
        foreach (string line in new[] { "help", "tick", "seed", "hash", "speed 2", "pause", "resume", "spawn marker 1 1" })
        {
            ConsoleResult result = console.Execute(line);
            if (!result.Success)
            {
                return $"console '{line}' failed: {result.Text}";
            }
        }

        return console.Execute("no_such_command").Success ? "console accepted an unknown command" : null;
    }

    private void Fail(string reason)
    {
        GD.PushError($"{FailMarker}: {reason}");
        GD.Print($"{FailMarker}: {reason}");
        SetProcess(false);
        GetTree().Quit(FailureExitCode);
    }
}
