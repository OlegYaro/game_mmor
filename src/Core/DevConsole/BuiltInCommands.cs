using System.Globalization;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Logging;
using Core.Save;
using Core.Sim;

namespace Core.DevConsole;

/// <summary>The M0 console commands: help, tick, speed, pause, resume, seed, hash, spawn, save, load, log.</summary>
internal static class BuiltInCommands
{
    private const string SaveExtension = ".sav";
    private const int MaxLogLines = 500;

    internal static void RegisterAll(DevConsole console)
    {
        console.Register("help", "help [command]", "List commands, or show help for one.", (_, args) => Help(console, args));
        console.Register("tick", "tick", "Print the current tick and calendar date.", Tick);
        console.Register("speed", "speed <1|2|4|20>", "Set or show the game speed (20 needs the dev flag).", Speed);
        console.Register("pause", "pause", "Pause the simulation.", (c, args) => SetPaused(c, args, true));
        console.Register("resume", "resume", "Resume the simulation.", (c, args) => SetPaused(c, args, false));
        console.Register("seed", "seed", "Print the world seed.", Seed);
        console.Register("hash", "hash", "Print the combined state hash and the hash of each subsystem.", Hash);
        console.Register("spawn", "spawn marker <x> <y>", "Spawn a marker entity at a tile (runs next tick).", Spawn);
        console.Register("save", "save <name>", "Save the game to the save directory.", Save);
        console.Register("load", "load <name>", "Load a save from the save directory.", Load);
        console.Register("log", "log <trace|debug|info|warn|error> [n]", "Set log verbosity, or print the last n events at that level or above.", Log);
    }

    private static ConsoleResult Usage(DevConsole console, string name) =>
        ConsoleResult.Fail($"Usage: {console.Commands.First(c => c.Name == name).Usage}");

    private static ConsoleResult Help(DevConsole console, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return ConsoleResult.Ok(console.Commands.Select(c => $"{c.Usage} - {c.Help}"));
        }

        if (args.Count > 1)
        {
            return Usage(console, "help");
        }

        return console.TryGetCommand(args[0], out ConsoleCommand command)
            ? ConsoleResult.Ok($"{command.Usage} - {command.Help}")
            : ConsoleResult.Fail($"Unknown command '{args[0]}'. Type 'help' for a list.");
    }

    private static ConsoleResult Tick(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: tick");
        }

        Simulation sim = context.Simulation.Current;
        CalendarDate d = sim.Now;
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"tick {sim.Tick} | day {d.Day + 1} {d.Hour:D2}:{d.Minute:D2} | week {d.Week + 1} | season {d.Season + 1} day {d.DayOfSeason + 1} | year {d.Year + 1} | {(d.IsNight ? "night" : "day")}"));
    }

    private static ConsoleResult Speed(DevConsoleContext context, IReadOnlyList<string> args)
    {
        TickScheduler scheduler = context.Scheduler;
        if (args.Count == 0)
        {
            return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
                $"speed x{(int)scheduler.Speed}{(scheduler.IsPaused ? " (paused)" : string.Empty)}"));
        }

        if (args.Count != 1 || !TryParseInt(args[0], out int value) || !Enum.IsDefined((GameSpeed)value))
        {
            return ConsoleResult.Fail("Usage: speed <1|2|4|20>");
        }

        if (!scheduler.TrySetSpeed((GameSpeed)value))
        {
            return ConsoleResult.Fail("Speed x20 is only available with the dev speed flag.");
        }

        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture, $"speed x{value}"));
    }

    private static ConsoleResult SetPaused(DevConsoleContext context, IReadOnlyList<string> args, bool paused)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail($"Usage: {(paused ? "pause" : "resume")}");
        }

        context.Scheduler.IsPaused = paused;
        return ConsoleResult.Ok(paused ? "paused" : "resumed");
    }

    private static ConsoleResult Seed(DevConsoleContext context, IReadOnlyList<string> args) =>
        args.Count != 0
            ? ConsoleResult.Fail("Usage: seed")
            : ConsoleResult.Ok(context.Simulation.Current.Seed.ToString(CultureInfo.InvariantCulture));

    private static ConsoleResult Hash(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: hash");
        }

        SubsystemHashes hashes = context.Simulation.Current.ComputeHashes();
        List<string> lines = [$"combined {ContentHash.Format(hashes.Combined)}"];
        lines.AddRange(hashes.Entries.Select(e => $"{e.Name} {ContentHash.Format(e.Hash)}"));
        return ConsoleResult.Ok(lines);
    }

    private static ConsoleResult Spawn(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: spawn marker <x> <y> (tile coordinates)";
        if (args.Count != 3 || !string.Equals(args[0], "marker", StringComparison.OrdinalIgnoreCase))
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryParseInt(args[1], out int x) || !TryParseInt(args[2], out int y))
        {
            return ConsoleResult.Fail($"Coordinates must be whole numbers. {UsageText}");
        }

        if (Math.Abs((long)x) > int.MaxValue / Position.UnitsPerTile || Math.Abs((long)y) > int.MaxValue / Position.UnitsPerTile)
        {
            return ConsoleResult.Fail("Coordinates are out of range.");
        }

        Simulation sim = context.Simulation.Current;
        PendingCommand pending = sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(x, y)));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture, $"marker queued at {x},{y} for tick {pending.ExecuteTick}"));
    }

    private static bool TryValidName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    private static ConsoleResult Save(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return ConsoleResult.Fail("Usage: save <name>");
        }

        if (!TryValidName(args[0]))
        {
            return ConsoleResult.Fail("Save names use letters, digits, '_' and '-' only (max 64).");
        }

        string path = Path.Combine(context.SaveDirectory, args[0] + SaveExtension);
        try
        {
            SaveGame.WriteFile(context.Simulation.Current, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConsoleResult.Fail($"Cannot write save '{args[0]}': {ex.Message}");
        }

        return ConsoleResult.Ok($"saved '{args[0]}' at tick {context.Simulation.Current.Tick.ToString(CultureInfo.InvariantCulture)}");
    }

    private static ConsoleResult Load(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return ConsoleResult.Fail("Usage: load <name>");
        }

        if (!TryValidName(args[0]))
        {
            return ConsoleResult.Fail("Save names use letters, digits, '_' and '-' only (max 64).");
        }

        string path = Path.Combine(context.SaveDirectory, args[0] + SaveExtension);
        SaveLoadResult result = SaveGame.ReadFile(path, context.Content, context.Log);
        if (result.Simulation is null)
        {
            return ConsoleResult.Fail($"Cannot load '{args[0]}': {result.Error?.Message ?? "unknown error"}");
        }

        context.Simulation.Current = result.Simulation;
        context.Scheduler.ResetAccumulator();
        return ConsoleResult.Ok($"loaded '{args[0]}' at tick {result.Simulation.Tick.ToString(CultureInfo.InvariantCulture)}");
    }

    private static ConsoleResult Log(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: log <trace|debug|info|warn|error> [n]";
        if (args.Count is < 1 or > 2 || !TryParseLevel(args[0], out LogLevel level))
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (args.Count == 1)
        {
            context.Log.MinimumLevel = level;
            return ConsoleResult.Ok($"log level {level}");
        }

        if (!TryParseInt(args[1], out int n) || n < 1 || n > MaxLogLines)
        {
            return ConsoleResult.Fail($"n must be a whole number from 1 to {MaxLogLines.ToString(CultureInfo.InvariantCulture)}. {UsageText}");
        }

        List<LogEvent> events = [.. context.Log.GetEvents().Where(e => e.Level >= level)];
        return ConsoleResult.Ok(events.Skip(Math.Max(0, events.Count - n)).Select(e => e.ToString()));
    }

    private static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text.ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info": level = LogLevel.Info; return true;
            case "warn" or "warning": level = LogLevel.Warning; return true;
            case "error": level = LogLevel.Error; return true;
            default: level = default; return false;
        }
    }

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
