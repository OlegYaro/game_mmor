using Core.Content;

namespace Core.Tests.Content;

public sealed class LocalizationTests
{
    // Godot translation CSV: first column "keys", then one column per locale code (docs: localization_using_spreadsheets).
    private static Dictionary<string, string[]> ReadCsv(out string[] header)
    {
        string[] lines = File.ReadAllLines(Path.Combine(TestPaths.Locale, "strings.csv"));
        header = lines[0].Split(',');
        Dictionary<string, string[]> rows = new(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
        {
            string[] cells = line.Split(',');
            Assert.True(rows.TryAdd(cells[0], cells), "Duplicate key " + cells[0]);
        }

        return rows;
    }

    [Fact]
    [Trait("Req", "FR-UI-09")]
    public void EveryKeyHasRussianAndEnglish()
    {
        Dictionary<string, string[]> rows = ReadCsv(out string[] header);

        Assert.Equal(["keys", "ru", "en"], header);
        Assert.NotEmpty(rows);
        foreach ((string key, string[] cells) in rows)
        {
            Assert.Equal(3, cells.Length);
            Assert.False(string.IsNullOrWhiteSpace(cells[1]), key + " has no ru");
            Assert.False(string.IsNullOrWhiteSpace(cells[2]), key + " has no en");
        }

        Assert.Contains("ui.hud.day", rows.Keys);
        Assert.Contains("ui.speed.paused", rows.Keys);
    }

    [Fact]
    [Trait("Req", "FR-UI-09")]
    public void EveryContentItemHasANameKey()
    {
        Dictionary<string, string[]> rows = ReadCsv(out _);
        ContentCatalog catalog = ContentCatalog.Load(TestPaths.LoadDataSource()).Catalog!;

        foreach (ItemDef item in catalog.Items)
        {
            Assert.Contains(item.EffectiveNameKey, rows.Keys);
        }
    }
}
