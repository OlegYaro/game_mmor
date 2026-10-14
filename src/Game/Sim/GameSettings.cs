using System.Globalization;
using Godot;

namespace Game.Sim;

/// <summary>Run configuration from the command line and project settings.</summary>
public static class GameSettings
{
    /// <summary>World seed used when neither <c>--seed=N</c> nor the project setting is given.</summary>
    public const ulong DefaultSeed = 12345;

    /// <summary>Project setting that holds the default seed.</summary>
    public const string SeedSetting = "game/world_seed";

    private const string SeedArgPrefix = "--seed=";

    /// <summary>Real filesystem folder for saves (<c>user://saves</c>).</summary>
    public static string SaveDirectory => ProjectSettings.GlobalizePath("user://saves");

    /// <summary>Seed from <c>-- --seed=N</c>, else the <c>game/world_seed</c> project setting, else <see cref="DefaultSeed"/>.</summary>
    public static ulong ResolveSeed()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith(SeedArgPrefix, StringComparison.Ordinal)
                && ulong.TryParse(arg.AsSpan(SeedArgPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong cli))
            {
                return cli;
            }
        }

        if (ProjectSettings.HasSetting(SeedSetting)
            && ulong.TryParse(ProjectSettings.GetSetting(SeedSetting).AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong configured))
        {
            return configured;
        }

        return DefaultSeed;
    }
}
