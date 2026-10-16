using Core.Buildings;

namespace Core.Tests.Content;

public sealed class BuildModeLocalizationTests
{
    // Rows waiting to be merged into strings.csv: "key,ru,en", no commas inside the texts.
    private static Dictionary<string, string[]> ReadPart()
    {
        string[] lines = File.ReadAllLines(Path.Combine(TestPaths.Locale, "build_mode_strings.csv.part"));
        Dictionary<string, string[]> rows = new(StringComparer.Ordinal);
        foreach (string line in lines.Where(l => l.Length > 0))
        {
            string[] cells = line.Split(',');
            Assert.Equal(3, cells.Length);
            Assert.False(string.IsNullOrWhiteSpace(cells[1]), cells[0] + " has no ru");
            Assert.False(string.IsNullOrWhiteSpace(cells[2]), cells[0] + " has no en");
            Assert.True(rows.TryAdd(cells[0], cells), "Duplicate key " + cells[0]);
        }

        return rows;
    }

    [Fact]
    [Trait("Req", "FR-BUILD-01")]
    public void EveryRefusalReasonHasATooltipKey()
    {
        Dictionary<string, string[]> rows = ReadPart();

        foreach (BuildResult result in Enum.GetValues<BuildResult>().Where(r => r is not (BuildResult.None or BuildResult.Ok)))
        {
            Assert.Contains("build.reason." + Construction.NameOf(result), rows.Keys);
        }

        Assert.Contains("build.hint.place", rows.Keys);
        Assert.Contains("build.hint.place_rotate", rows.Keys);
        Assert.Contains("build.hint.move", rows.Keys);
    }
}
