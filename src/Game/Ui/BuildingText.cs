using System.Globalization;
using System.Text;
using Core.Buildings;
using Core.Content;
using Godot;

namespace Game.Ui;

/// <summary>Text helpers shared by the building panels: localization keys of buildings and results, item lists.</summary>
public static class BuildingText
{
    /// <summary>Key of the building name, <c>building.{id}.name</c>.</summary>
    public static string NameKey(string buildingId) => "building." + buildingId.Replace(':', '.') + ".name";

    /// <summary>Key of the build menu category, <c>ui.build.category.{category}</c>.</summary>
    public static string CategoryKey(string category) => "ui.build.category." + category;

    /// <summary>Key of the message for a building order outcome, <c>ui.build.result.{name}</c>.</summary>
    public static string ResultKey(BuildResult result) => "ui.build.result." + Construction.NameOf(result);

    /// <summary>Key of the name of a building state, <c>ui.inspector.state.{name}</c>.</summary>
    public static string StateKey(BuildingState state) => "ui.inspector.state." + Construction.NameOf(state);

    /// <summary>The translated name of an item, falling back to its id when the item is unknown.</summary>
    public static string ItemName(Node owner, ContentCatalog content, string itemId) =>
        content.TryGet(itemId, out ItemDef item) ? owner.Tr(item.EffectiveNameKey) : itemId;

    /// <summary>Appends "Wood 20, Stone 10" for <paramref name="items"/> to <paramref name="text"/>; "-" when empty.</summary>
    public static void AppendItems(StringBuilder text, Node owner, ContentCatalog content, IReadOnlyList<ItemAmountDef> items)
    {
        if (items.Count == 0)
        {
            text.Append('-');
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            text.Append(ItemName(owner, content, items[i].Item)).Append(' ').Append(items[i].Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>"Wood 20, Stone 10" for <paramref name="items"/>.</summary>
    public static string FormatItems(Node owner, ContentCatalog content, IReadOnlyList<ItemAmountDef> items)
    {
        var text = new StringBuilder();
        AppendItems(text, owner, content, items);
        return text.ToString();
    }

    /// <summary>Seconds a number of ticks takes at the content's tick rate, rounded up.</summary>
    public static int Seconds(ContentCatalog content, int ticks) => (ticks + content.Time.TicksPerSecond - 1) / content.Time.TicksPerSecond;

    /// <summary>Translates <paramref name="key"/> and fills its <c>{0}</c>, <c>{1}</c> placeholders.</summary>
    public static string Format(Node owner, string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, owner.Tr(key), args);
}
