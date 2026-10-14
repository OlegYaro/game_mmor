using System.Text;

namespace Core.DevConsole;

/// <summary>
/// Splits a console line into words. Words are separated by whitespace; double quotes group words into one
/// argument (<c>save "my game"</c>), and inside quotes <c>\"</c> and <c>\\</c> are escapes. Empty quotes give an
/// empty argument.
/// </summary>
public static class CommandLineParser
{
    /// <summary>Parses <paramref name="line"/>; on failure returns false with an English message in <paramref name="error"/>.</summary>
    public static bool TryParse(string? line, out IReadOnlyList<string> words, out string error)
    {
        List<string> result = [];
        words = result;
        error = string.Empty;
        if (line is null)
        {
            return true;
        }

        var current = new StringBuilder();
        bool inWord = false;
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
                {
                    current.Append(line[++i]);
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }

        if (inQuotes)
        {
            error = "Unterminated quote.";
            return false;
        }

        if (inWord)
        {
            result.Add(current.ToString());
        }

        return true;
    }
}
