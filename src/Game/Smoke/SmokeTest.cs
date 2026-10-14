using Core;
using Core.Content;
using Core.DevConsole;
using Core.Entities;
using Core.Logging;
using Core.Save;
using Core.Sim;
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

        return CheckConsole(content, seed, saveDir);
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
