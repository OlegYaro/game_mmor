namespace Core;

/// <summary>
/// Identity of the Core simulation library. Lets Game and tests confirm which Core build they talk to.
/// </summary>
public static class CoreInfo
{
    /// <summary>Human-readable library name.</summary>
    public const string Name = "Core";

    /// <summary>Assembly version of Core, set once in Directory.Build.props.</summary>
    public static Version Version { get; } = typeof(CoreInfo).Assembly.GetName().Version ?? new Version(0, 0);
}
