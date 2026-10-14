using System.Globalization;
using Core.Logging;

namespace Core.DevConsole;

/// <summary>Code of one console command. <paramref name="args"/> excludes the command name.</summary>
public delegate ConsoleResult ConsoleHandler(DevConsoleContext context, IReadOnlyList<string> args);

/// <summary>
/// Developer console (NFR-07): a registry of named commands and a line executor. Output is deterministic (invariant
/// culture, commands listed in ordinal order). <see cref="Execute"/> never throws: bad input gives a failed
/// <see cref="ConsoleResult"/> with a friendly message.
/// </summary>
public sealed class DevConsole
{
    private readonly SortedDictionary<string, ConsoleCommand> _commands = new(StringComparer.Ordinal);

    /// <summary>Creates a console with the M0 commands (<see cref="BuiltInCommands"/>) registered.</summary>
    public DevConsole(DevConsoleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        BuiltInCommands.RegisterAll(this);
    }

    /// <summary>What commands operate on.</summary>
    public DevConsoleContext Context { get; }

    /// <summary>Registered commands in ordinal name order.</summary>
    public IReadOnlyCollection<ConsoleCommand> Commands => _commands.Values;

    /// <summary>Registers a command. Names are lower-case words without spaces.</summary>
    /// <exception cref="ArgumentException">The name is invalid or already taken.</exception>
    public void Register(string name, string usage, string help, ConsoleHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(handler);
        if (name.Any(c => char.IsWhiteSpace(c) || c == '"') || name.Any(char.IsUpper))
        {
            throw new ArgumentException($"Invalid command name '{name}'.", nameof(name));
        }

        if (!_commands.TryAdd(name, new ConsoleCommand(name, usage, help, handler)))
        {
            throw new ArgumentException($"Command '{name}' is already registered.", nameof(name));
        }
    }

    /// <summary>Finds a command by name (case-insensitive).</summary>
    public bool TryGetCommand(string name, out ConsoleCommand command)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _commands.TryGetValue(name.ToLowerInvariant(), out command!);
    }

    /// <summary>Parses and runs one line. A blank line is a successful no-op.</summary>
    public ConsoleResult Execute(string? line)
    {
        if (!CommandLineParser.TryParse(line, out IReadOnlyList<string> words, out string error))
        {
            return ConsoleResult.Fail(error);
        }

        if (words.Count == 0)
        {
            return ConsoleResult.Ok();
        }

        if (!TryGetCommand(words[0], out ConsoleCommand command))
        {
            return ConsoleResult.Fail($"Unknown command '{words[0]}'. Type 'help' for a list.");
        }

        try
        {
            return command.Handler(Context, [.. words.Skip(1)]);
        }
#pragma warning disable CA1031 // The console must never throw into the host; the failure is logged and reported.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Context.Log.Write(Context.Simulation.Current.Tick, LogLevel.Error, "console", "Command {name} failed: {error}",
                LogField.Of("name", command.Name), LogField.Of("error", ex.Message));
            return ConsoleResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Command '{command.Name}' failed: {ex.Message}"));
        }
    }
}

/// <summary>A registered command.</summary>
/// <param name="Name">Lower-case name.</param>
/// <param name="Usage">Argument synopsis, for example <c>speed &lt;1|2|4&gt;</c>.</param>
/// <param name="Help">One-line description.</param>
/// <param name="Handler">The code.</param>
public sealed record ConsoleCommand(string Name, string Usage, string Help, ConsoleHandler Handler);
