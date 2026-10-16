using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Sim;

namespace Core.Residents;

/// <summary>
/// Read-only housing rules (FR-NPC-01, P-08): places in houses, who lives where, and the house a resident moves into.
/// A house is a building whose current level has a <see cref="BuildingLevelDef.Capacity"/>; it offers places only
/// while <see cref="BuildingState.Active"/>. Residents keep their home while it is upgraded or moved (the entity stays
/// the same) and lose it when it is demolished.
/// </summary>
public static class Housing
{
    /// <summary>Resident places of <paramref name="building"/>'s current level (0 for anything but a house).</summary>
    public static int CapacityOf(ContentCatalog content, Building building) => Construction.LevelOf(content, building).Capacity;

    /// <summary>Number of residents whose home is <paramref name="house"/>.</summary>
    public static int CountOccupants(GameWorld world, EntityId house)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = 0;
        foreach (Resident resident in world.Residents.Values)
        {
            count += resident.Home == house ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// Free places in <paramref name="house"/>: capacity minus occupants while it is an active house of the player's
    /// settlement, otherwise 0 (under construction, upgrading, moving, or not a house).
    /// </summary>
    public static int FreePlaces(Simulation sim, EntityId house)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world || !world.Buildings.TryGet(house, out Building building) || !OffersPlaces(sim.Content, building))
        {
            return 0;
        }

        return Math.Max(0, CapacityOf(sim.Content, building) - CountOccupants(world, house));
    }

    /// <summary>Free places over all active houses (<see cref="FreePlaces"/>).</summary>
    public static int TotalFreePlaces(Simulation sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world)
        {
            return 0;
        }

        int total = 0;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            total += FreePlaces(sim, owners[i]);
        }

        return total;
    }

    /// <summary>Number of residents without a home, not counting those leaving the settlement (they want no place).</summary>
    public static int CountHomeless(GameWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = 0;
        foreach (Resident resident in world.Residents.Values)
        {
            count += resident.Home == EntityId.None && resident.State != ResidentState.Leaving ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// The active house with a free place nearest to tile (<paramref name="x"/>, <paramref name="y"/>) (squared distance
    /// to the centre of its footprint; ties go to the lower entity index), or <see cref="EntityId.None"/> when every house
    /// is full.
    /// </summary>
    public static EntityId FindHome(Simulation sim, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (sim.World is not { } world)
        {
            return EntityId.None;
        }

        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (FreePlaces(sim, owners[i]) == 0)
            {
                continue;
            }

            // Doubled coordinates keep the footprint centre whole: 2x + w - 1 is twice the centre of columns x..x+w-1.
            (int width, int height) = Construction.FootprintOf(Construction.DefOf(sim.Content, buildings[i]), buildings[i].Rotation);
            long dx = (2L * buildings[i].X) + width - 1 - (2L * x);
            long dy = (2L * buildings[i].Y) + height - 1 - (2L * y);
            long distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                best = owners[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    // An active house of the player's settlement.
    private static bool OffersPlaces(ContentCatalog content, Building building) =>
        building.State == BuildingState.Active && building.Settlement == Building.PlayerSettlement && CapacityOf(content, building) > 0;
}
