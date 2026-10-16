using Core.Entities;

namespace Game.Gameplay;

/// <summary>
/// The resident the player has selected (clicked on the map). Plain state with an event, like <see cref="BuildingSelection"/>:
/// <see cref="ResidentPicker"/> sets it from map clicks, the residents window subscribes to <see cref="ResidentSelected"/>
/// and the resident layer draws a ring under <see cref="Selected"/>. Lives on <c>GameRoot</c>.
/// </summary>
public sealed class ResidentSelection
{
    /// <summary>The selected resident, or <see cref="EntityId.None"/>.</summary>
    public EntityId Selected { get; private set; }

    /// <summary>Raised when the selection changes; the argument is the new resident or <see cref="EntityId.None"/> when cleared.</summary>
    public event Action<EntityId>? ResidentSelected;

    /// <summary>Selects <paramref name="resident"/>; <see cref="EntityId.None"/> clears. No event when nothing changes.</summary>
    public void Select(EntityId resident)
    {
        if (resident == Selected)
        {
            return;
        }

        Selected = resident;
        ResidentSelected?.Invoke(resident);
    }

    /// <summary>Clears the selection.</summary>
    public void Clear() => Select(EntityId.None);
}
