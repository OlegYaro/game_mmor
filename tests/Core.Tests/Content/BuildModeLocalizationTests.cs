using Core.Buildings;

namespace Core.Tests.Content;

public sealed class BuildModeLocalizationTests
{
    private static Dictionary<string, string[]> ReadPart() => LocalizationTests.ReadCsv(out _);

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
