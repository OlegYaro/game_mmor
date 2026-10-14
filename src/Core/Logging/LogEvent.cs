using System.Globalization;
using System.Text;

namespace Core.Logging;

/// <summary>Severity of a log event, from most to least verbose.</summary>
public enum LogLevel
{
    /// <summary>Very detailed tracing, off by default.</summary>
    Trace = 0,

    /// <summary>Diagnostic detail.</summary>
    Debug = 1,

    /// <summary>Notable simulation events.</summary>
    Info = 2,

    /// <summary>Something unexpected that the simulation recovered from.</summary>
    Warning = 3,

    /// <summary>A failure that broke an operation.</summary>
    Error = 4,
}

/// <summary>One named value of a log event: either a string or a 64-bit integer (never a float, ADR 0005).</summary>
public readonly record struct LogField
{
    private LogField(string key, string? text, long number)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Key = key;
        Text = text;
        Number = number;
    }

    /// <summary>Field name, used as <c>{Key}</c> in templates.</summary>
    public string Key { get; }

    /// <summary>String value, or null for a numeric field.</summary>
    public string? Text { get; }

    /// <summary>Numeric value (0 for a string field).</summary>
    public long Number { get; }

    /// <summary>True when the field holds <see cref="Number"/>.</summary>
    public bool IsNumber => Text is null;

    /// <summary>The value as text, numbers in invariant culture.</summary>
    public string ValueText => Text ?? Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>A numeric field.</summary>
    public static LogField Of(string key, long value) => new(key, null, value);

    /// <summary>A string field.</summary>
    public static LogField Of(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LogField(key, value, 0);
    }
}

/// <summary>
/// A structured simulation log event (NFR-08). Time is the tick number only, never wall-clock time.
/// </summary>
/// <param name="Tick">Tick the event was written in.</param>
/// <param name="Level">Severity.</param>
/// <param name="Category">Short subsystem tag, for example <c>calendar</c>.</param>
/// <param name="Template">Message template with <c>{Key}</c> placeholders for <paramref name="Fields"/>.</param>
/// <param name="Fields">Values in the order they were given.</param>
public sealed record LogEvent(long Tick, LogLevel Level, string Category, string Template, IReadOnlyList<LogField> Fields)
{
    /// <summary>Value of the first field named <paramref name="key"/>, or null.</summary>
    public LogField? Find(string key)
    {
        foreach (LogField field in Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal))
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>The template with each <c>{Key}</c> replaced by its value; unknown placeholders stay as written.</summary>
    public string Render()
    {
        var builder = new StringBuilder(Template.Length + 16);
        int i = 0;
        while (i < Template.Length)
        {
            int open = Template.IndexOf('{', i);
            int close = open < 0 ? -1 : Template.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(Template, i, Template.Length - i);
                break;
            }

            builder.Append(Template, i, open - i);
            string key = Template.Substring(open + 1, close - open - 1);
            LogField? field = Find(key);
            if (field is { } found)
            {
                builder.Append(found.ValueText);
            }
            else
            {
                builder.Append(Template, open, close - open + 1);
            }

            i = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>For example <c>[1200] Info calendar: Day 2 begins</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Tick}] {Level} {Category}: {Render()}");
}
