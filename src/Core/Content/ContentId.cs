namespace Core.Content;

/// <summary>Rules for content identifiers: <c>snake_case</c> with an optional <c>modid:</c> prefix (ADR 0008).</summary>
public static partial class ContentId
{
    /// <summary>Regular expression (also used in the generated JSON schemas) that every content id must match.</summary>
    public const string Pattern = "^([a-z][a-z0-9_]*:)?[a-z][a-z0-9_]*$";

    /// <summary>Returns true when <paramref name="id"/> is a valid content id.</summary>
    public static bool IsValid(string? id) => id is not null && IdRegex().IsMatch(id);

    [System.Text.RegularExpressions.GeneratedRegex(Pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex IdRegex();
}
