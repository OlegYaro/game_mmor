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
        Hero = defs.OfType<HeroBalanceDef>().Single();
        Construction = defs.OfType<ConstructionBalanceDef>().Single();
        WorldGen = defs.OfType<WorldGenParamsDef>().Single();
        Economy = defs.OfType<EconomyBalanceDef>().Single();
        Roads = defs.OfType<RoadsBalanceDef>().Single();
        Regrowth = defs.OfType<RegrowthBalanceDef>().Single();
        Names = defs.OfType<NameListDef>().ToArray();
        Items = defs.OfType<ItemDef>().ToArray();
        Eras = defs.OfType<EraDef>().OrderBy(e => e.Order).ToArray();
        Races = defs.OfType<RaceDef>().ToArray();
        Buildings = defs.OfType<BuildingDef>().ToArray();
        Professions = defs.OfType<ProfessionDef>().ToArray();
        RelationModifiers = defs.OfType<RelationModifierDef>().ToArray();
        Recipes = defs.OfType<RecipeDef>().ToArray();
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

    /// <summary>Hero balance (data/balance/hero.json).</summary>
    public HeroBalanceDef Hero { get; }

    /// <summary>Construction rules such as refunds (data/balance/construction.json).</summary>
    public ConstructionBalanceDef Construction { get; }

    /// <summary>Economy and resident numbers (data/balance/economy.json).</summary>
    public EconomyBalanceDef Economy { get; }

    /// <summary>Footpath and road numbers (data/balance/roads.json).</summary>
    public RoadsBalanceDef Roads { get; }

    /// <summary>Tree regrowth and planting numbers (data/balance/regrowth.json).</summary>
    public RegrowthBalanceDef Regrowth { get; }

    /// <summary>Resident name lists, ordered by id (data/names/*.json).</summary>
    public IReadOnlyList<NameListDef> Names { get; }

    /// <summary>World generator parameters (data/worldgen/*.json, ADR 0006).</summary>
    public WorldGenParamsDef WorldGen { get; }

    /// <summary>All item definitions, ordered by id.</summary>
    public IReadOnlyList<ItemDef> Items { get; }

    /// <summary>All eras, ordered by <see cref="EraDef.Order"/> (data/eras/*.json).</summary>
    public IReadOnlyList<EraDef> Eras { get; }

    /// <summary>All races, ordered by id (data/races/*.json).</summary>
    public IReadOnlyList<RaceDef> Races { get; }

    /// <summary>All building types, ordered by id (data/buildings/*.json).</summary>
    public IReadOnlyList<BuildingDef> Buildings { get; }

    /// <summary>All professions, ordered by id (data/professions/*.json).</summary>
    public IReadOnlyList<ProfessionDef> Professions { get; }

    /// <summary>All relation modifier reasons, ordered by id (data/balance/relations.json).</summary>
    public IReadOnlyList<RelationModifierDef> RelationModifiers { get; }

    /// <summary>All workshop recipes, ordered by id (data/recipes/*.json); loaded in M3a, run by workshops from M3b.</summary>
    public IReadOnlyList<RecipeDef> Recipes { get; }

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
        RequireExactlyOne<HeroBalanceDef>(loaded, errors, "data/balance/hero.json");
        RequireExactlyOne<ConstructionBalanceDef>(loaded, errors, "data/balance/construction.json");
        RequireExactlyOne<WorldGenParamsDef>(loaded, errors, "data/worldgen/*.json");
        RequireExactlyOne<EconomyBalanceDef>(loaded, errors, "data/balance/economy.json");
        RequireExactlyOne<RoadsBalanceDef>(loaded, errors, "data/balance/roads.json");
        RequireExactlyOne<RegrowthBalanceDef>(loaded, errors, "data/balance/regrowth.json");
        if (!loaded.Any(l => l.Def is NameListDef))
        {
            errors.Add(new ContentError(ContentErrorKind.Missing, string.Empty, string.Empty, "No NameListDef found (data/names/*.json)."));
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
                HeroBalanceFile f => f.Items,
                ConstructionBalanceFile f => f.Items,
                ItemFile f => f.Items,
                EraFile f => f.Items,
                RaceFile f => f.Items,
                BuildingFile f => f.Items,
                ProfessionFile f => f.Items,
                RelationModifierFile f => f.Items,
                WorldGenFile f => f.Items,
                EconomyBalanceFile f => f.Items,
                RoadsBalanceFile f => f.Items,
                RegrowthBalanceFile f => f.Items,
                NameListFile f => f.Items,
                RecipeFile f => f.Items,
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
        CheckEras(loaded, errors);
        foreach ((string file, int index, IContentDef def) in loaded)
        {
            if (def is WorldGenParamsDef worldGen)
            {
                foreach ((string path, string message) in worldGen.CheckConsistency())
                {
                    errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].{path}", message));
                }
            }

            if (def is RelationModifierDef relation)
            {
                foreach ((string path, string message) in relation.CheckConsistency())
                {
                    errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].{path}", message));
                }
            }

            if (def is RoadsBalanceDef roads)
            {
                AddConsistency(file, index, roads.CheckConsistency(), errors);
            }
            else if (def is RegrowthBalanceDef regrowth)
            {
                AddConsistency(file, index, regrowth.CheckConsistency(), errors);
            }
            else if (def is NameListDef names)
            {
                AddConsistency(file, index, names.CheckConsistency(), errors);
            }
            else if (def is ItemDef consistentItem)
            {
                AddConsistency(file, index, consistentItem.CheckConsistency(), errors);
            }
            else if (def is EconomyBalanceDef economy)
            {
                AddConsistency(file, index, economy.CheckConsistency(), errors);
                CheckEconomy(file, index, economy, loaded.Count(l => l.Def is EraDef), byId, errors);
            }
            else if (def is ProfessionDef profession)
            {
                CheckProfession(file, index, profession, errors);
            }
            else if (def is RecipeDef recipe)
            {
                AddConsistency(file, index, recipe.CheckConsistency(), errors);
                CheckRecipe(file, index, recipe, byId, errors);
            }

            if (def is RaceDef race)
            {
                CheckRace(file, index, race, byId, errors);
            }

            if (def is HeroBalanceDef hero)
            {
                foreach ((string path, string message) in hero.CheckConsistency())
                {
                    errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].{path}", message));
                }

                for (int i = 0; i < hero.Gathering.Count; i++)
                {
                    CheckTarget<ItemDef>(file, $"$.items[{index}].gathering[{i}].item", hero.Id, hero.Gathering[i].Item, "item", byId, errors);
                }

                for (int i = 0; i < hero.StartItems.Count; i++)
                {
                    CheckTarget<ItemDef>(file, $"$.items[{index}].startItems[{i}].item", hero.Id, hero.StartItems[i].Item, "item", byId, errors);
                }
            }

            if (def is BuildingDef building)
            {
                CheckBuilding(file, index, building, byId, errors);
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

    private static void AddConsistency(string file, int index, IEnumerable<(string Path, string Message)> problems, List<ContentError> errors)
    {
        foreach ((string path, string message) in problems)
        {
            errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].{path}", message));
        }
    }

    private static void CheckProfession(string file, int index, ProfessionDef profession, List<ContentError> errors)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < profession.JobTypes.Count; i++)
        {
            string job = profession.JobTypes[i];
            string path = $"$.items[{index}].jobTypes[{i}]";
            if (!JobTypes.IsValid(job))
            {
                errors.Add(new ContentError(ContentErrorKind.Validation, file, path, $"Profession '{profession.Id}' has unknown job type '{job}'."));
            }
            else if (!seen.Add(job))
            {
                errors.Add(new ContentError(ContentErrorKind.Validation, file, path, $"Profession '{profession.Id}' lists job type '{job}' twice."));
            }
        }
    }

    private static void CheckEconomy(string file, int index, EconomyBalanceDef economy, int eraCount, Dictionary<string, IContentDef> byId, List<ContentError> errors)
    {
        if (economy.ArrivalEraPoints.Count != eraCount)
        {
            errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].arrivalEraPoints",
                $"arrivalEraPoints has {economy.ArrivalEraPoints.Count} entries but there are {eraCount} eras."));
        }

        for (int i = 0; i < economy.AutoAssignPattern.Count; i++)
        {
            string path = $"$.items[{index}].autoAssignPattern[{i}]";
            string id = economy.AutoAssignPattern[i];
            if (byId.TryGetValue(id, out IContentDef? found) && found is ProfessionDef { Kind: not "ordinary" })
            {
                errors.Add(new ContentError(ContentErrorKind.Reference, file, path, $"'{economy.Id}' assigns '{id}', which is not an ordinary profession."));
            }
            else
            {
                CheckTarget<ProfessionDef>(file, path, economy.Id, id, "profession", byId, errors);
            }
        }
    }

    private static void CheckEras(List<(string File, int Index, IContentDef Def)> loaded, List<ContentError> errors)
    {
        List<(string File, int Index, EraDef Def)> eras = loaded.Where(l => l.Def is EraDef).Select(l => (l.File, l.Index, (EraDef)l.Def)).ToList();
        foreach ((int index, string path, string message) in EraDef.CheckCoverage(eras.Select(e => e.Def).ToList()))
        {
            errors.Add(new ContentError(ContentErrorKind.Validation, eras[index].File, $"$.items[{eras[index].Index}].{path}", message));
        }
    }

    private static void CheckRace(string file, int index, RaceDef race, Dictionary<string, IContentDef> byId, List<ContentError> errors)
    {
        foreach ((string name, IReadOnlyList<string> ids) in new[]
        {
            ("startTechnologies", race.StartTechnologies),
            ("army", race.Army),
        })
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (!ContentId.IsValid(ids[i]))
                {
                    errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].{name}[{i}]", $"Race '{race.Id}' has an invalid id '{ids[i]}'."));
                }
            }
        }

        for (int i = 0; i < race.SettlementBuildings.Count; i++)
        {
            CheckTarget<BuildingDef>(file, $"$.items[{index}].settlementBuildings[{i}]", race.Id, race.SettlementBuildings[i], "building", byId, errors);
        }
    }

    private static void CheckBuilding(string file, int index, BuildingDef building, Dictionary<string, IContentDef> byId, List<ContentError> errors)
    {
        if (building.Levels.Count == 0)
        {
            errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].levels", $"Building '{building.Id}' needs at least one level."));
        }

        for (int level = 0; level < building.Levels.Count; level++)
        {
            BuildingLevelDef levelDef = building.Levels[level];
            if (level > 0 && (levelDef.Storage < building.Levels[level - 1].Storage || levelDef.Capacity < building.Levels[level - 1].Capacity))
            {
                // An upgrade never shrinks a building: residents and stock always keep their place (ADR 0012).
                errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].levels[{level}]",
                    $"Building '{building.Id}' level {level + 1} has less storage or capacity than level {level}."));
            }

            for (int i = 0; i < levelDef.Cost.Count; i++)
            {
                string path = $"$.items[{index}].levels[{level}].cost[{i}].item";
                CheckTarget<ItemDef>(file, path, building.Id, levelDef.Cost[i].Item, "item", byId, errors);
                int first = FirstEntryOf(levelDef.Cost, levelDef.Cost[i].Item);
                if (first < i)
                {
                    // Paying checks and takes each entry on its own, so a repeated item would pass the check and fail the payment.
                    errors.Add(new ContentError(ContentErrorKind.Validation, file, path,
                        $"Building '{building.Id}' level {level + 1} lists item '{levelDef.Cost[i].Item}' twice in its cost (entries {first} and {i}); put the total in one entry."));
                }
            }

            if (levelDef.Work is { } work)
            {
                CheckTarget<ItemDef>(file, $"$.items[{index}].levels[{level}].work.item", building.Id, work.Item, "item", byId, errors);
            }

            if (level > 0 && !IsSameWork(building.Levels[0].Work, levelDef.Work))
            {
                // An upgrade keeps the building's work: its output exists while it is upgraded and the saved output and
                // jobs name the item of the current level (EconomyRules.Restore, review of M3a).
                errors.Add(new ContentError(ContentErrorKind.Validation, file, $"$.items[{index}].levels[{level}].work",
                    $"Building '{building.Id}' level {level + 1} changes its work; every level needs the same job and item, or no level has work."));
            }

            for (int i = 0; i < levelDef.Requirements.Count; i++)
            {
                RequirementDef requirement = levelDef.Requirements[i];
                string path = $"$.items[{index}].levels[{level}].requirements[{i}].value";
                if (requirement.Kind == "era")
                {
                    CheckTarget<EraDef>(file, path, building.Id, requirement.Value, "era", byId, errors);
                }
                else if (byId.TryGetValue(requirement.Value, out IContentDef? found) && found is ProfessionDef { Kind: not "specialist" })
                {
                    errors.Add(new ContentError(ContentErrorKind.Reference, file, path, $"'{building.Id}' requires '{requirement.Value}', which is not a specialist profession."));
                }
                else
                {
                    CheckTarget<ProfessionDef>(file, path, building.Id, requirement.Value, "profession", byId, errors);
                }
            }
        }
    }

    private static void CheckRecipe(string file, int index, RecipeDef recipe, Dictionary<string, IContentDef> byId, List<ContentError> errors)
    {
        CheckTarget<BuildingDef>(file, $"$.items[{index}].building", recipe.Id, recipe.Building, "building", byId, errors);
        foreach ((string name, IReadOnlyList<ItemAmountDef> list) in new[] { ("inputs", recipe.Inputs), ("outputs", recipe.Outputs) })
        {
            for (int i = 0; i < list.Count; i++)
            {
                CheckTarget<ItemDef>(file, $"$.items[{index}].{name}[{i}].item", recipe.Id, list[i].Item, "item", byId, errors);
            }
        }

        if (recipe.Specialist is not { } specialist)
        {
            return;
        }

        string path = $"$.items[{index}].specialist";
        if (byId.TryGetValue(specialist, out IContentDef? found) && found is ProfessionDef { Kind: not "specialist" })
        {
            errors.Add(new ContentError(ContentErrorKind.Reference, file, path, $"Recipe '{recipe.Id}' needs '{specialist}', which is not a specialist profession."));
        }
        else
        {
            CheckTarget<ProfessionDef>(file, path, recipe.Id, specialist, "profession", byId, errors);
        }
    }

    // Both levels without work, or both with the same job and item (slots, cycle and yield may differ).
    private static bool IsSameWork(WorkDef? first, WorkDef? other) =>
        first is null ? other is null
            : other is not null && string.Equals(first.Job, other.Job, StringComparison.Ordinal) && string.Equals(first.Item, other.Item, StringComparison.Ordinal);

    // Reports a Reference error when `target` is missing or is not a definition of type T.
    private static void CheckTarget<T>(string file, string path, string owner, string target, string what, Dictionary<string, IContentDef> byId, List<ContentError> errors)
        where T : IContentDef
    {
        if (!byId.TryGetValue(target, out IContentDef? found))
        {
            errors.Add(new ContentError(ContentErrorKind.Reference, file, path, $"'{owner}' references unknown id '{target}'."));
        }
        else if (found is not T)
        {
            errors.Add(new ContentError(ContentErrorKind.Reference, file, path, $"'{owner}' references '{target}', which is not a {what}."));
        }
    }

    // Index of the first entry of `cost` for `item` (ordinal comparison, like ids everywhere).
    private static int FirstEntryOf(IReadOnlyList<ItemAmountDef> cost, string item)
    {
        for (int i = 0; i < cost.Count; i++)
        {
            if (string.Equals(cost[i].Item, item, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
