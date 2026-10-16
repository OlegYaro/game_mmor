using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Core.Content;

/// <summary>
/// A workshop recipe, <c>data/recipes/*.json</c> (ADR 0008, ADR 0012): a resident at <paramref name="Building"/> turns
/// <paramref name="Inputs"/> into <paramref name="Outputs"/> in one cycle. M3a only loads and checks recipes; the
/// workshop logic that runs them is M3b. Numbers come from docs/research/economy-six-chains.md and are provisional.
/// </summary>
/// <param name="Id">Definition id; ids share one namespace with items and buildings, so a recipe id is never an item id (e.g. <c>recipe_lumber</c>).</param>
/// <param name="Building">Id of the building whose residents work this recipe.</param>
/// <param name="Inputs">Items taken per cycle, in a fixed order; at least one, no item twice.</param>
/// <param name="Outputs">Items made per cycle, in a fixed order; at least one, no item twice.</param>
/// <param name="CycleTicks">Ticks of one cycle at base speed (60 seconds are 600 ticks at 10 Hz).</param>
/// <param name="Skill">The skill that speeds the work up, one of <see cref="SkillNames"/>.</param>
/// <param name="Provisional">True while the numbers are placeholders to be tuned by the bot simulation.</param>
/// <param name="Specialist">Optional id of a specialist profession the recipe needs; null when any worker may do it.</param>
public sealed record RecipeDef(
    [property: RegularExpression(ContentId.Pattern)] string Id,
    [property: RegularExpression(ContentId.Pattern)]
    [property: Description("Id of the building whose residents work this recipe.")]
    string Building,
    [property: Description("Items taken per cycle; at least one entry, no item twice.")]
    IReadOnlyList<ItemAmountDef> Inputs,
    [property: Description("Items made per cycle; at least one entry, no item twice.")]
    IReadOnlyList<ItemAmountDef> Outputs,
    [property: Range(1, 1000000)]
    [property: Description("Ticks of one cycle at base speed; 600 ticks are 60 seconds at 10 Hz (provisional).")]
    int CycleTicks,
    [property: RegularExpression(SkillNames.Pattern)]
    [property: Description("The skill that speeds the work up.")]
    string Skill,
    [property: Description("True while the inputs, outputs and cycle are placeholders; balance is tuned by the bot simulation (docs/research/economy-six-chains.md).")]
    bool Provisional,
    [property: RegularExpression(ContentId.Pattern)]
    [property: Description("Optional id of a specialist profession the recipe needs; omitted when any worker may do it.")]
    string? Specialist = null) : IContentDef
{
    /// <summary>Checks that both lists are non-empty and name each item at most once.</summary>
    /// <returns>(JSON path below the definition, message) per problem.</returns>
    public IEnumerable<(string Path, string Message)> CheckConsistency()
    {
        foreach ((string name, IReadOnlyList<ItemAmountDef> list) in new[] { ("inputs", Inputs), ("outputs", Outputs) })
        {
            if (list.Count == 0)
            {
                yield return (name, $"Recipe '{Id}' needs at least one entry in {name}.");
            }

            for (int i = 0; i < list.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(list[j].Item, list[i].Item, StringComparison.Ordinal))
                    {
                        // A workshop takes and gives each entry on its own, like a building cost; one entry per item keeps the total in one place.
                        yield return ($"{name}[{i}].item", $"Recipe '{Id}' lists item '{list[i].Item}' twice in {name} (entries {j} and {i}); put the total in one entry.");
                        break;
                    }
                }
            }
        }
    }
}

/// <summary>File envelope for <c>data/recipes/*.json</c>.</summary>
/// <param name="Items">Definitions in the file.</param>
/// <param name="Schema">Optional <c>$schema</c> reference.</param>
public sealed record RecipeFile(
    IReadOnlyList<RecipeDef> Items,
    [property: JsonPropertyName("$schema")] string? Schema = null);
