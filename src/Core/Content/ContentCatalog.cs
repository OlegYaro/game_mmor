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
        Fog = defs.OfType<FogBalanceDef>().Single();
        Saves = defs.OfType<SaveBalanceDef>().Single();
        WorldGen = defs.OfType<WorldGenParamsDef>().Single();
        Items = defs.OfType<ItemDef>().ToArray();
    }

    /// <summary>
    /// <see cref="Content.ContentHash"/> of the files this catalog was loaded from. Replays record it so a replay is
    /// never compared against different content.
    /// </summary>
    public ulong ContentHash { get; }

    /// <summary>Time constants (data/balance/time.json).</summary>
    public TimeBalanceDef Time { get; }

    /// <summary>Fog of war sight radii (data/balance/fog.json).</summary>
    public FogBalanceDef Fog { get; }

    /// <summary>Save slot numbers (data/balance/saves.json).</summary>
    public SaveBalanceDef Saves { get; }

    /// <summary>World generator parameters (data/worldgen/*.json, ADR 0006).</summary>
    public WorldGenParamsDef WorldGen { get; }

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

        RequireExactlyOne<TimeBalanceDef>(loaded, errors, "data/balance/time.json");
        RequireExactlyOne<FogBalanceDef>(loaded, errors, "data/balance/fog.json");
        RequireExactlyOne<SaveBalanceDef>(loaded, errors, "data/balance/saves.json");
        RequireExactlyOne<WorldGenParamsDef>(loaded, errors, "data/worldgen/*.json");

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

    private static void RequireExactlyOne<T>(List<(string File, int Index, IContentDef Def)> loaded, List<ContentError> errors, string where)
        where T : IContentDef
    {
        int count = loaded.Count(l => l.Def is T);
        if (count == 0)
        {
            errors.Add(new ContentError(ContentErrorKind.Missing, string.Empty, string.Empty, $"No {typeof(T).Name} found ({where})."));
        }
        else if (count > 1)
        {
            errors.Add(new ContentError(ContentErrorKind.Validation, string.Empty, string.Empty, $"More than one {typeof(T).Name} defined."));
        }
    }

    private static void ParseFile(ContentFile file, List<(string File, int Index, IContentDef Def)> loaded, List<ContentError> errors)
    {
        int slash = file.Path.IndexOf('/', StringComparison.Ordinal);
        string folder = slash < 0 ? string.Empty : file.Path[..slash];
        if (ContentCategories.NonDefinitionFolders.Contains(folder) && file.Path.EndsWith(".json", StringComparison.Ordinal))
        {
            return;
        }

        ContentCategory? category = ContentCategories.Find(file.Path);
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
                FogBalanceFile f => f.Items,
                SaveBalanceFile f => f.Items,
                ItemFile f => f.Items,
                WorldGenFile f => f.Items,
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
                Validate(file.Path, $"$.items[{index}]", def, errors);
                loaded.Add((file.Path, index, def));
            }

            index++;
        }
    }

    // Validates the DataAnnotations of `obj`, then recurses into IContentSection properties and lists of them
    // (Validator itself does not descend into nested objects). Properties are visited in declaration order.
    private static void Validate(string file, string path, object obj, List<ContentError> errors)
    {
        List<ValidationResult> results = [];
        if (!Validator.TryValidateObject(obj, new ValidationContext(obj), results, validateAllProperties: true))
        {
            foreach (ValidationResult result in results)
            {
                string member = result.MemberNames.FirstOrDefault() ?? "id";
                errors.Add(new ContentError(ContentErrorKind.Validation, file, $"{path}.{CamelCase(member)}", result.ErrorMessage ?? "Invalid value."));
            }
        }

        foreach (System.Reflection.PropertyInfo property in obj.GetType().GetProperties().OrderBy(p => p.MetadataToken))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            string childPath = $"{path}.{CamelCase(property.Name)}";
            switch (property.GetValue(obj))
            {
                case IContentSection section:
                    Validate(file, childPath, section, errors);
                    break;
                case IEnumerable<IContentSection?> sections:
                    int index = 0;
                    foreach (IContentSection? section in sections)
                    {
                        if (section is null)
                        {
                            errors.Add(new ContentError(ContentErrorKind.Parse, file, $"{childPath}[{index}]", "Entry must not be null."));
                        }
                        else
                        {
                            Validate(file, $"{childPath}[{index}]", section, errors);
                        }

                        index++;
                    }

                    break;
            }
        }
    }

    private static string CamelCase(string member) => char.ToLowerInvariant(member[0]) + member[1..];

    private static void CheckReferences(List<(string File, int Index, IContentDef Def)> loaded, List<ContentError> errors)
    {
        Dictionary<string, IContentDef> byId = loaded.ToDictionary(l => l.Def.Id, l => l.Def, StringComparer.Ordinal);
        foreach ((string file, int index, IContentDef def) in loaded)
        {
            if (def is WorldGenParamsDef worldGen)
            {
                foreach ((string path, string message) in worldGen.CheckConsistency())
                {
                    errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].{path}", message));
                }
            }

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
