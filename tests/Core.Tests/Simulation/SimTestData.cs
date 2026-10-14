using Core.Content;
using Core.Tests.Content;

namespace Core.Tests.Sim;

/// <summary>Content for simulation tests.</summary>
internal static class SimTestData
{
    private static readonly Lazy<ContentCatalog> _real = new(() => ContentCatalog.Load(TestPaths.LoadDataSource()).Catalog
        ?? throw new InvalidOperationException("data/ failed to load."));

    /// <summary>The real data/ folder.</summary>
    public static ContentCatalog Real => _real.Value;

    /// <summary>A time balance with the GDD §14 values, independent of later tuning of data/.</summary>
    public static TimeBalanceDef GddTime { get; } = new("time", TicksPerSecond: 10, RealMinutesPerGameDay: 15,
        NightPermille: 333, DaysPerWeek: 7, DaysPerSeason: 15, SeasonsPerYear: 4);
}
