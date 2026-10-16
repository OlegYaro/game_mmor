using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Pathfinding;
using Core.Sim;
using Core.WorldGen;

namespace Core.Jobs;

/// <summary>
/// Target search of the job board (ADR 0009): no map cache, every look is a ring search around the resident, nearest
/// ring first, limited to the territory plus a margin and to a tile budget per tick. Unreachable targets are cut by the
/// connectivity map before any path search (ADR 0003).
/// </summary>
internal static class JobSearch
{
    /// <summary>
    /// Tiles the job searches of one tick may check (NFR-02): an engine budget like the path node limit, not a balance
    /// number. A search started under the budget runs to its end; residents whose turn comes after the budget is spent
    /// look in the next tick.
    /// </summary>
    public const int TilesPerTick = 32_768;

    /// <summary>
    /// The nearest tile with a node of <paramref name="kind"/> that <paramref name="holder"/> can work: inside the
    /// territory disc grown by <paramref name="margin"/>, with stock left, not reserved, not banned for the holder, and
    /// with a neighbour in the holder's connectivity component. Rings by Chebyshev distance from
    /// (<paramref name="x"/>, <paramref name="y"/>); inside a ring the lowest tile index wins. -1 when there is none.
    /// </summary>
    /// <param name="checkedTiles">Incremented by every tile looked at.</param>
    public static int FindNode(Simulation sim, GameWorld world, EntityId holder, ResourceKind kind, int x, int y, Territory territory, int margin,
        int component, ref int checkedTiles)
    {
        int radius = territory.Radius + margin;
        var area = new Territory(territory.CenterX, territory.CenterY, radius);
        int size = world.Size;
        int minX = Math.Max(0, territory.CenterX - radius);
        int maxX = Math.Min(size - 1, territory.CenterX + radius);
        int minY = Math.Max(0, territory.CenterY - radius);
        int maxY = Math.Min(size - 1, territory.CenterY + radius);
        int maxRing = Math.Max(Math.Max(x - minX, maxX - x), Math.Max(y - minY, maxY - y));
        ReadOnlySpan<ResourceKind> kinds = world.Map.ResourceKinds;
        for (int ring = 0; ring <= maxRing; ring++)
        {
            // Row by row, left to right: the first hit of a ring has the lowest index.
            for (int ty = y - ring; ty <= y + ring; ty++)
            {
                if (ty < minY || ty > maxY)
                {
                    continue;
                }

                bool edgeRow = ty == y - ring || ty == y + ring;
                int step = edgeRow || ring == 0 ? 1 : 2 * ring;
                for (int tx = x - ring; tx <= x + ring; tx += step)
                {
                    if (tx < minX || tx > maxX)
                    {
                        continue;
                    }

                    checkedTiles++;
                    int index = (ty * size) + tx;
                    if (kinds[index] == kind && area.Contains(tx, ty) && world.Tiles.GetEffectiveAmount(tx, ty) > 0
                        && !world.Reservations.IsReserved(ReservationTarget.OfTile(index))
                        && !world.Reservations.IsBanned(holder, ReservationTarget.OfTile(index), sim.Tick)
                        && CanApproach(world.Pathfinder, component, tx, ty))
                    {
                        return index;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// The footprint tile of <paramref name="building"/> a resident at (<paramref name="x"/>, <paramref name="y"/>) in
    /// <paramref name="component"/> works next to: among the tiles with a neighbour in the component, the nearest
    /// (squared distance, ties to the lower index); -1 when none can be approached.
    /// </summary>
    public static int ApproachTile(ContentCatalog content, GameWorld world, Building building, int x, int y, int component)
    {
        (int width, int height) = Construction.FootprintOf(Construction.DefOf(content, building), building.Rotation);
        int best = -1;
        long bestDistance = long.MaxValue;
        for (int ty = building.Y; ty < building.Y + height; ty++)
        {
            for (int tx = building.X; tx < building.X + width; tx++)
            {
                long dx = tx - x;
                long dy = ty - y;
                long distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance && CanApproach(world.Pathfinder, component, tx, ty))
                {
                    best = (ty * world.Size) + tx;
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    /// <summary>True when a neighbour of (<paramref name="x"/>, <paramref name="y"/>) belongs to <paramref name="component"/>.</summary>
    public static bool CanApproach(Pathfinder pathfinder, int component, int x, int y)
    {
        PassabilityGrid grid = pathfinder.Grid;
        for (int d = 0; d < PassabilityGrid.DirectionCount; d++)
        {
            int nx = x + PassabilityGrid.DirectionX(d);
            int ny = y + PassabilityGrid.DirectionY(d);
            if (grid.Contains(nx, ny) && pathfinder.Connectivity.GetComponent(grid.Index(nx, ny)) == component)
            {
                return true;
            }
        }

        return false;
    }
}
