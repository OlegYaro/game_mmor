namespace Core.Content;

/// <summary>Kind of a content problem.</summary>
public enum ContentErrorKind
{
    /// <summary>Malformed JSON, unknown or missing members, wrong value types.</summary>
    Parse,

    /// <summary>A value violates a constraint (range, id format).</summary>
    Validation,

    /// <summary>The same id is defined twice.</summary>
    DuplicateId,

    /// <summary>A reference points to a missing or wrong-typed definition.</summary>
    Reference,

    /// <summary>A required definition is absent.</summary>
    Missing,
}

/// <summary>One problem found while loading content.</summary>
/// <param name="Kind">Problem kind.</param>
/// <param name="File">Relative file path (empty for catalog-wide problems).</param>
/// <param name="JsonPath">JSON path inside the file, e.g. <c>$.items[1].stackSize</c>.</param>
/// <param name="Message">Description.</param>
public sealed record ContentError(ContentErrorKind Kind, string File, string JsonPath, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Kind}: {File} {JsonPath}: {Message}";
}
