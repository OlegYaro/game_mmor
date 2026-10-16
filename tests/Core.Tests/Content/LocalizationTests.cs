using Core.Content;

namespace Core.Tests.Content;

public sealed class LocalizationTests
{
    // Godot translation CSV: first column "keys", then one column per locale code (docs: localization_using_spreadsheets).
    // Values may be quoted (commas and line breaks inside quotes, "" for a quote).
    internal static Dictionary<string, string[]> ReadCsv(out string[] header)
    {
        List<string[]> records = ParseCsv(File.ReadAllText(Path.Combine(TestPaths.Locale, "strings.csv")));
        header = records[0];
        Dictionary<string, string[]> rows = new(StringComparer.Ordinal);
        foreach (string[] cells in records.Skip(1))
        {
            Assert.True(rows.TryAdd(cells[0], cells), "Duplicate key " + cells[0]);
        }

        return rows;
    }

    private static List<string[]> ParseCsv(string text)
    {
        List<string[]> records = [];
        List<string> cells = [];
        System.Text.StringBuilder cell = new();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n')
            {
                cells.Add(cell.ToString());
                cell.Clear();
                if (cells.Count > 1 || cells[0].Length > 0)
                {
                    records.Add([.. cells]);
                }

                cells.Clear();
            }
            else if (c != '\r')
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            records.Add([.. cells]);
        }

        return records;
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
