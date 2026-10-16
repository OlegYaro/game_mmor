using System.Globalization;
using Core.Tests.Replay;

namespace Core.Tests.Sim;

/// <summary>
/// The bot simulation (docs/research/economy-six-chains.md, FR-ECO-01, FR-ECO-06, FR-ECO-10, FR-NPC-06, NFR-04): the
/// scenario <c>bot_scenario.json</c> with 5, 12 and 40 residents runs 100 game days without orders; the daily metrics
/// must match the golden CSV (<c>UPDATE_GOLDEN=1</c> rewrites it), a second run in the same process gives the same CSV,
/// and the invariants hold every day (<see cref="BotSimulation.CheckInvariants"/>). The three runs take seconds, so the
/// full hundred days are the regular run (no shorter CI variant is needed). Category <c>Simulation</c>: excluded from the
/// coverage run, run on its own (<c>tools/test.sh --filter Category=Simulation</c>).
/// </summary>
public sealed class BotSimulationTests(ITestOutputHelper output)
{
    public static TheoryData<int> Sizes => [.. BotScenario.Load().Residents];

    [Theory]
    [Trait("Category", "Simulation")]
    [Trait("Req", "FR-ECO-10")]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "NFR-04")]
    [MemberData(nameof(Sizes))]
    public void TheSettlementRunsOnItsOwnAndMatchesItsGolden(int residents)
    {
        BotScenario scenario = BotScenario.Load();
        BotRun run = BotSimulation.Run(scenario, residents, scenario.Days);
        Report(residents, scenario.Days, run);
        string name = BotScenario.CsvName(residents);
        if (GoldenFiles.Updating)
        {
            GoldenFiles.WriteText(name, run.Csv);
        }

        BotRun again = BotSimulation.Run(scenario, residents, scenario.Days);
        Assert.Equal(run.Csv, again.Csv);
        Assert.Equal(scenario.Days, run.Days.Count);
        Assert.Equal(GoldenFiles.ReadText(name), run.Csv);
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
