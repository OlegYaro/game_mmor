using System.Globalization;
using Core.Tests.Replay;

namespace Core.Tests.Sim;

/// <summary>
/// The bot simulation (docs/research/economy-six-chains.md, FR-ECO-01, FR-ECO-06, FR-ECO-10, FR-NPC-06, NFR-04): the
/// scenario <c>bot_scenario.json</c> with 5, 12 and 40 residents runs 100 game days without orders, and with 12 residents
/// once more with the building orders of its <c>build</c> section (<see cref="BotBuildDemand"/>: wood and stone are
/// spent, so felling, planting, regrowth and roads go on over the whole run); the daily metrics must match the golden
/// CSV (<c>UPDATE_GOLDEN=1</c> rewrites it), a second run in the same process gives the same CSV, and the invariants
/// hold every day (<see cref="BotSimulation.CheckInvariants"/>). The runs take seconds, so the full hundred days are the
/// regular run (no shorter CI variant is needed). Category <c>Simulation</c>: excluded from the coverage run, run on its
/// own (<c>tools/test.sh --filter Category=Simulation</c>).
/// </summary>
public sealed class BotSimulationTests(ITestOutputHelper output)
{
    public static TheoryData<int> Sizes => [.. BotScenario.Load().Residents];

    public static TheoryData<int> BuildSizes => [.. BotScenario.Load().Build!.Residents];

    [Theory]
    [Trait("Category", "Simulation")]
    [Trait("Req", "FR-ECO-10")]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "NFR-04")]
    [MemberData(nameof(Sizes))]
    public void TheSettlementRunsOnItsOwnAndMatchesItsGolden(int residents)
    {
        BotScenario scenario = BotScenario.Load();
        RunAndCompare(scenario, residents, build: null);
    }

    [Theory]
    [Trait("Category", "Simulation")]
    [Trait("Req", "FR-ECO-10")]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-WORLD-15")]
    [Trait("Req", "NFR-04")]
    [MemberData(nameof(BuildSizes))]
    public void TheSettlementWithABuildingDemandKeepsGatheringAndMatchesItsGolden(int residents)
    {
        BotScenario scenario = BotScenario.Load();
        BotRun run = RunAndCompare(scenario, residents, scenario.Build);

        // The demand lasts: the last third of the run still spends, fells and plants, and stumps grow back.
        IReadOnlyList<BotDay> late = [.. run.Days.Skip(run.Days.Count * 2 / 3)];
        Assert.True(late.Sum(d => d.SpentWood) > 0 && late.Sum(d => d.SpentStone) > 0, "Nothing was spent late in the run.");
        Assert.True(late.Sum(d => d.GatheredWood) > 0 && late.Sum(d => d.GatheredStone) > 0, "Nothing was gathered late in the run.");
        Assert.True(run.Days.Sum(d => d.RegrownStumps) > 0, "No stump grew back.");
        Assert.True(late.Max(d => d.Saplings) > 0, "Nothing was planted late in the run.");
    }

    private BotRun RunAndCompare(BotScenario scenario, int residents, BotBuildDemand? build)
    {
        BotRun run = BotSimulation.Run(scenario, residents, scenario.Days, build);
        Report(residents, scenario.Days, run);
        string name = BotScenario.CsvName(residents, build is not null);
        if (GoldenFiles.Updating)
        {
            GoldenFiles.WriteText(name, run.Csv);
        }

        BotRun again = BotSimulation.Run(scenario, residents, scenario.Days, build);
        Assert.Equal(run.Csv, again.Csv);
        Assert.Equal(scenario.Days, run.Days.Count);
        Assert.Equal(GoldenFiles.ReadText(name), run.Csv);
        return run;
    }

    private void Report(int residents, int days, BotRun run)
    {
        BotDay last = run.Days[^1];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{residents} residents, {days} days: {run.Seconds:F1} s, setup {run.SetupTicks} ticks; at the end population {last.Population}, food {last.Food}, "
            + $"days with a shortage {last.ZeroFoodDays}, gold {last.Gold}, wood {last.Wood}, stone {last.Stone}, trails {last.Trails}, roads {last.Roads}"));
        output.WriteLine(run.Csv);
    }
}
