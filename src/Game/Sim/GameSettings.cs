using System.Globalization;
using Core.Sim;
using Godot;

namespace Game.Sim;

/// <summary>Run configuration from the command line and project settings.</summary>
public static class GameSettings
{
    /// <summary>World seed used when neither <c>--seed=N</c> nor the project setting is given.</summary>
    public const ulong DefaultSeed = 12345;

    /// <summary>Project setting that holds the default seed.</summary>
    public const string SeedSetting = "game/world_seed";

    /// <summary>Command-line flag that skips the menu and starts a new game at once.</summary>
    public const string AutoStartArg = "--autostart";

    private const string SeedArgPrefix = "--seed=";
    private const string SizeArgPrefix = "--size=";
    private const string DifficultyArgPrefix = "--difficulty=";

    /// <summary>Real filesystem folder for saves (<c>user://saves</c>).</summary>
    public static string SaveDirectory => ProjectSettings.GlobalizePath("user://saves");

    /// <summary>True when launched with <c>-- --autostart</c>.</summary>
    public static bool AutoStart => OS.GetCmdlineUserArgs().Contains(AutoStartArg);

    /// <summary>Seed from <c>-- --seed=N</c> (a number or any text), else the <c>game/world_seed</c> project setting, else <see cref="DefaultSeed"/>.</summary>
    public static ulong ResolveSeed()
    {
        string? cli = ArgValue(SeedArgPrefix);
        if (cli is not null && SeedText.TryParse(cli, out ulong fromCli))
        {
            return fromCli;
        }

        if (ProjectSettings.HasSetting(SeedSetting)
            && ulong.TryParse(ProjectSettings.GetSetting(SeedSetting).AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong configured))
        {
            return configured;
        }

        return DefaultSeed;
    }

    /// <summary>New game parameters from <c>--seed</c>, <c>--size</c> (default 512) and <c>--difficulty</c> (default normal).</summary>
    public static NewGameOptions ResolveNewGameOptions()
    {
        int size = NewGameOptions.DefaultMapSize;
        string? sizeText = ArgValue(SizeArgPrefix);
        if (sizeText is not null && int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSize))
        {
            size = parsedSize;
        }

        Difficulty difficulty = Difficulty.Normal;
        string? difficultyText = ArgValue(DifficultyArgPrefix);
        if (difficultyText is not null && DifficultyNames.TryParse(difficultyText, out Difficulty parsed))
        {
            difficulty = parsed;
        }

        return new NewGameOptions(ResolveSeed(), size, difficulty);
    }

    private static string? ArgValue(string prefix)
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }
}
