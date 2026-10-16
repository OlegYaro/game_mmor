using Core.Entities;

namespace Game.Gameplay;

/// <summary>
/// The building the player has selected (clicked on the map). Plain state with an event: <see cref="BuildMode"/> sets it
/// from map clicks, the inspector panel subscribes to <see cref="BuildingSelected"/> and the footprint overlay draws a
/// frame around <see cref="Selected"/>. Lives on <c>GameRoot</c>.
/// </summary>
public sealed class BuildingSelection
{
    /// <summary>The selected building, or <see cref="EntityId.None"/>.</summary>
    public EntityId Selected { get; private set; }

    /// <summary>Raised when the selection changes; the argument is the new building or <see cref="EntityId.None"/> when cleared.</summary>
    public event Action<EntityId>? BuildingSelected;

    /// <summary>Selects <paramref name="building"/>; <see cref="EntityId.None"/> clears. No event when nothing changes.</summary>
    public void Select(EntityId building)
    {
        if (building == Selected)
        {
            return;
        }

        Selected = building;
        BuildingSelected?.Invoke(building);
    }

    /// <summary>Clears the selection.</summary>
    public void Clear() => Select(EntityId.None);
}
