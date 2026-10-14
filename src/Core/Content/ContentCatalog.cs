using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Core.Content;

/// <summary>Numeric id of a definition, assigned at load time by sorting string ids. 0 is never valid.</summary>
public readonly record struct DefId(int Value)
{
    /// <summary>True for ids that were assigned by a catalog.</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Outcome of loading content: a catalog when there are no errors, otherwise all collected errors.</summary>
/// <param name="Catalog">The catalog, or null when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Every problem found, in deterministic order.</param>
public sealed record ContentLoadResult(ContentCatalog? Catalog, IReadOnlyList<ContentError> Errors);

/// <summary>Immutable set of all definitions with deterministic numeric ids (FR-MOD-01).</summary>
public sealed class ContentCatalog
{
    private readonly IContentDef[] _defs; // index = DefId - 1, sorted by ordinal string id
    private readonly FrozenDictionary<string, DefId> _ids;

    private ContentCatalog(IContentDef[] defs, ulong contentHash)
    {
        _defs = defs;
        ContentHash = contentHash;
        _ids = defs.Select((d, i) => KeyValuePair.Create(d.Id, new DefId(i + 1))).ToFrozenDictionary(StringComparer.Ordinal);
        Time = defs.OfType<TimeBalanceDef>().Single();
        Items = defs.OfType<ItemDef>().ToArray();
    }

    /// <summary>
    /// <see cref="Content.ContentHash"/> of the files this catalog was loaded from. Replays record it so a replay is
    /// never compared against different content.
    /// </summary>
    public ulong ContentHash { get; }

    /// <summary>Time constants (data/balance/time.json).</summary>
    public TimeBalanceDef Time { get; }

    /// <summary>All item definitions, ordered by id.</summary>
    public IReadOnlyList<ItemDef> Items { get; }

    /// <summary>Number of definitions.</summary>
    public int Count => _defs.Length;

    /// <summary>All definitions ordered by <see cref="DefId"/>.</summary>
    public IReadOnlyList<IContentDef> All => _defs;

    /// <summary>Looks up the numeric id of a string id.</summary>
    public bool TryGetId(string id, out DefId defId) => _ids.TryGetValue(id, out defId);

    /// <summary>Returns the definition with the given numeric id; throws when it is not in this catalog.</summary>
    public IContentDef Get(DefId id) =>
        id.Value >= 1 && id.Value <= _defs.Length ? _defs[id.Value - 1] : throw new KeyNotFoundException($"Unknown DefId {id}.");

    /// <summary>Returns the definition of type <typeparamref name="T"/> with the given numeric id.</summary>
    public T Get<T>(DefId id) where T : class, IContentDef =>
        Get(id) as T ?? throw new InvalidCastException($"Definition {Get(id).Id} is not a {typeof(T).Name}.");

    /// <summary>Looks up a definition of type <typeparamref name="T"/> by string id.</summary>
    public bool TryGet<T>(string id, out T def) where T : class, IContentDef
    {
        if (_ids.TryGetValue(id, out DefId defId) && _defs[defId.Value - 1] is T typed)
        {
            def = typed;
            return true;
        }

        def = null!;
        return false;
    }

    /// <summary>
    /// Loads and validates all files of <paramref name="source"/>. Never throws for bad data: parse errors, constraint
    /// violations and duplicate ids are collected first, then (only if those passed) cross references are checked.
    /// </summary>
    public static ContentLoadResult Load(IContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<ContentError> errors = [];
        List<(string File, int Index, IContentDef Def)> loaded = [];
        IReadOnlyList<ContentFile> files = source.GetFiles();

        foreach (ContentFile file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ParseFile(file, loaded, errors);
        }

        // Duplicate ids (one namespace across all categories).
        foreach (IGrouping<string, (string File, int Index, IContentDef Def)> group in
            loaded.GroupBy(l => l.Def.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            foreach ((string file, int index, IContentDef def) in group.Skip(1))
            {
                (string firstFile, int firstIndex, _) = group.First();
                errors.Add(new ContentError(ContentErrorKind.DuplicateId, file, $"$.items[{index}].id",
                    $"Duplicate id '{def.Id}' (first defined in {firstFile} at index {firstIndex})."));
            }
        }

        if (!loaded.Any(l => l.Def is TimeBalanceDef))
        {
            errors.Add(new ContentError(ContentErrorKind.Missing, string.Empty, string.Empty, "No TimeBalanceDef found (data/balance/time.json)."));
        }

        if (loaded.Count(l => l.Def is TimeBalanceDef) > 1)
        {
            errors.Add(new ContentError(ContentErrorKind.Validation, string.Empty, string.Empty, "More than one TimeBalanceDef defined."));
        }

        // Cross-reference pass runs only on structurally valid data so it does not report follow-up noise.
        if (errors.Count == 0)
        {
            CheckReferences(loaded, errors);
        }

        if (errors.Count > 0)
        {
            return new ContentLoadResult(null, errors);
        }

        IContentDef[] defs = loaded.Select(l => l.Def).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        ulong contentHash;
        try
        {
            contentHash = Content.ContentHash.Compute(files);
        }
        catch (ArgumentException ex)
        {
            return new ContentLoadResult(null, [new ContentError(ContentErrorKind.Validation, string.Empty, "$", ex.Message)]);
        }

        return new ContentLoadResult(new ContentCatalog(defs, contentHash), errors);
    }

    private static void ParseFile(ContentFile file, List<(string File, int Index, IContentDef Def)> loaded, List<ContentError> errors)
    {
        int slash = file.Path.IndexOf('/', StringComparison.Ordinal);
        string folder = slash < 0 ? string.Empty : file.Path[..slash];
        ContentCategory? category = ContentCategories.All.FirstOrDefault(c => c.Folder == folder);
        if (category is null || !file.Path.EndsWith(".json", StringComparison.Ordinal))
        {
            errors.Add(new ContentError(ContentErrorKind.Parse, file.Path, "$", "File is not in a known content folder or is not a .json file."));
            return;
        }

        IEnumerable<IContentDef?> defs;
        try
        {
            object? envelope = JsonSerializer.Deserialize(file.Text, category.TypeInfo);
            defs = envelope switch
            {
                TimeBalanceFile f => f.Items,
                ItemFile f => f.Items,
                _ => throw new JsonException("Unexpected file content."),
            };
        }
        catch (JsonException ex)
        {
            errors.Add(new ContentError(ContentErrorKind.Parse, file.Path, ex.Path ?? "$", ex.Message));
            return;
        }

        int index = 0;
        foreach (IContentDef? def in defs)
        {
            if (def is null)
            {
                errors.Add(new ContentError(ContentErrorKind.Parse, file.Path, $"$.items[{index}]", "Definition must not be null."));
            }
            else
            {
                Validate(file.Path, index, def, errors);
                loaded.Add((file.Path, index, def));
            }

            index++;
        }
    }

    private static void Validate(string file, int index, IContentDef def, List<ContentError> errors)
    {
        List<ValidationResult> results = [];
        if (Validator.TryValidateObject(def, new ValidationContext(def), results, validateAllProperties: true))
        {
            return;
        }

        foreach (ValidationResult result in results)
        {
            string member = result.MemberNames.FirstOrDefault() ?? "id";
            string name = char.ToLowerInvariant(member[0]) + member[1..];
            errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].{name}", result.ErrorMessage ?? "Invalid value."));
        }
    }

    private static void CheckReferences(List<(string File, int Index, IContentDef Def)> loaded, List<ContentError> errors)
    {
        Dictionary<string, IContentDef> byId = loaded.ToDictionary(l => l.Def.Id, l => l.Def, StringComparer.Ordinal);
        foreach ((string file, int index, IContentDef def) in loaded)
        {
            if (def is ItemDef { SpoilsInto: { } target })
            {
                if (!byId.TryGetValue(target, out IContentDef? found))
                {
                    errors.Add(new ContentError(ContentErrorKind.Reference, file, $"$.items[{index}].spoilsInto", $"Item '{def.Id}' references unknown id '{target}'."));
                }
                else if (found is not ItemDef)
                {
                    errors.Add(new ContentError(ContentErrorKind.Reference, file, $"$.items[{index}].spoilsInto", $"Item '{def.Id}' references '{target}', which is not an item."));
                }
            }
        }
    }
}
