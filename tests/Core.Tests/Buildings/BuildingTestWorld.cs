using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Sim;
using Core.Tests.Content;
using Core.Tests.Heroes;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.Buildings;

/// <summary>Helpers for building tests on a small real game (64², the hero test seed).</summary>
internal static class BuildingTestWorld
{
    /// <summary>
    /// xUnit collection of the building test classes. They all drive the same hot code (ticks, placement scans), and run
    /// in parallel under coverage instrumentation the shared hit counters made them about 20 times slower; run one
    /// after another they cost seconds.
    /// </summary>
    public const string Collection = "buildings";

    /// <summary>
    /// Extra building types for rules the shipped data has no example of: a level-1 era requirement, a specialist
    /// requirement, and a storage whose second level needs nothing (stock growth).
    /// </summary>
    private const string ExtraBuildings = """
        {
          "items": [
            { "id": "test_shrine", "category": "production", "footprint": { "width": 1, "height": 1 }, "outsideTerritory": false,
              "placement": "flat", "rotatable": false, "provisional": true,
              "levels": [ { "cost": [ { "item": "wood", "count": 1 } ], "buildTicks": 10, "moveTicks": 5,
                            "requirements": [ { "kind": "era", "value": "village" } ] } ] },
            { "id": "test_forge", "category": "production", "footprint": { "width": 1, "height": 1 }, "outsideTerritory": false,
              "placement": "flat", "rotatable": false, "provisional": true,
              "levels": [ { "cost": [ { "item": "wood", "count": 1 } ], "buildTicks": 10, "moveTicks": 5,
                            "requirements": [ { "kind": "specialist", "value": "blacksmith" } ] } ] },
            { "id": "test_depot", "category": "storage", "footprint": { "width": 1, "height": 1 }, "outsideTerritory": false,
              "placement": "flat", "rotatable": false, "provisional": true,
              "levels": [ { "cost": [ { "item": "wood", "count": 3 } ], "buildTicks": 10, "moveTicks": 5, "requirements": [], "storage": 2 },
                          { "cost": [ { "item": "stone", "count": 3 } ], "buildTicks": 10, "moveTicks": 5, "requirements": [], "storage": 4 } ] }
          ]
        }
        """;

    private static readonly Lazy<ContentCatalog> _extended = new(() =>
    {
        InMemoryContentSource source = TestPaths.LoadDataSource();
        source.Add("buildings/test_extra.json", ExtraBuildings);
        ContentLoadResult result = ContentCatalog.Load(source);
        return result.Catalog ?? throw new InvalidOperationException(string.Join("\n", result.Errors));
    });

    /// <summary>The real data plus <see cref="ExtraBuildings"/>.</summary>
    public static ContentCatalog Extended => _extended.Value;

    // The 64² map of the hero test seed, generated once: building tests create a new game each, and the map never changes.
    private static readonly Lazy<WorldMap> _map = new(() => WorldGenerator.Generate(new WorldGenRequest(HeroTestWorld.Seed, 64, SimTestData.Real.WorldGen)));

    /// <summary>The shared 64² map of <see cref="HeroTestWorld.Seed"/>.</summary>
    public static WorldMap SharedMap => _map.Value;

    /// <summary>A new 64² game of the hero test seed on the shared map (the extended content has the same world parameters).</summary>
    public static Simulation NewGame(ContentCatalog? content = null) =>
        Simulation.CreateNewGame(content ?? SimTestData.Real, new NewGameOptions(HeroTestWorld.Seed, 64, Difficulty.Normal), null, _map.Value);

    /// <summary>Puts items straight into the hero's backpack (no command, no tick).</summary>
    public static void Give(Simulation sim, string item, int count)
    {
        Assert.True(sim.Content.TryGetId(item, out DefId id));
        ref Inventory backpack = ref sim.World!.Inventories.Get(sim.World.HeroEntity);
        backpack = backpack.Add(id, count, sim.Content.Get<ItemDef>(id).StackSize, out int added);
        Assert.Equal(count, added);
    }

    public static int Count(Simulation sim, string item)
    {
        Assert.True(sim.Content.TryGetId(item, out DefId id));
        return Construction.CountAvailable(sim, id);
    }

    public static int BackpackCount(Simulation sim, string item)
    {
        Assert.True(sim.Content.TryGetId(item, out DefId id));
        return HeroTestWorld.InventoryOf(sim).Count(id);
    }

    public static Building BuildingOf(Simulation sim, EntityId entity) => sim.World!.Buildings.Get(entity);

    public static BuildingEvent LastEvent(Simulation sim) => sim.World!.BuildingEvents[^1];

    /// <summary>
    /// The valid placement of <paramref name="id"/> nearest to (<paramref name="cx"/>, <paramref name="cy"/>) (default:
    /// the hero) at a Chebyshev distance of at least <paramref name="minDistance"/>, scanning rings outwards (ties by
    /// row, then column). Materials must already be there.
    /// </summary>
    public static (int X, int Y) FindSpot(Simulation sim, string id, int rotation = 0, int minDistance = 2, (int X, int Y)? near = null,
        EntityId moving = default)
    {
        (int cx, int cy) = near ?? HeroTestWorld.HeroTile(sim);
        int size = sim.World!.Size;
        for (int ring = minDistance; ring < size; ring++)
        {
            for (int y = cy - ring; y <= cy + ring; y++)
            {
                for (int x = cx - ring; x <= cx + ring; x++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) == ring && Construction.CheckPlacement(sim, id, x, y, rotation, moving).IsOk)
                    {
                        return (x, y);
                    }
                }
            }
        }

        throw new InvalidOperationException($"No valid place for {id} near {cx},{cy}.");
    }

    /// <summary>Finds a tile matching <paramref name="match"/> (row by row), or fails.</summary>
    public static (int X, int Y) FindTile(Simulation sim, Func<int, int, bool> match)
    {
        int size = sim.World!.Size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (match(x, y))
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No matching tile.");
    }

    /// <summary>Enqueues <paramref name="command"/>, runs one tick and returns the building order's event.</summary>
    public static BuildingEvent Order(Simulation sim, ISimCommand command)
    {
        long before = sim.World!.LastBuildingEventSequence;
        sim.Enqueue(command);
        sim.Step();
        Assert.Equal(before + 1, sim.World.LastBuildingEventSequence);
        return LastEvent(sim);
    }

    /// <summary>Places <paramref name="id"/> (materials given first) and returns the new building; the order must be accepted.</summary>
    public static EntityId Place(Simulation sim, string id, int x, int y, int rotation = 0, bool give = true)
    {
        if (give)
        {
            GiveCost(sim, id, 1);
        }

        BuildingEvent placed = Order(sim, new PlaceBuildingCommand(id, x, y, rotation));
        Assert.True(placed.Result == BuildResult.Ok, $"place {id} at {x},{y}: {placed.Result}");
        return placed.Building;
    }

    /// <summary>Places <paramref name="id"/> near the hero and runs until it is built.</summary>
    public static EntityId Build(Simulation sim, string id, int rotation = 0, int minDistance = 2)
    {
        GiveCost(sim, id, 1);
        (int x, int y) = FindSpot(sim, id, rotation, minDistance);
        EntityId entity = Place(sim, id, x, y, rotation, give: false);
        StepUntilActive(sim, entity);
        return entity;
    }

    /// <summary>Gives the hero the cost of level <paramref name="level"/> of <paramref name="id"/>.</summary>
    public static void GiveCost(Simulation sim, string id, int level)
    {
        Assert.True(sim.Content.TryGet(id, out BuildingDef def));
        foreach (ItemAmountDef cost in def.Levels[level - 1].Cost)
        {
            Give(sim, cost.Item, cost.Count);
        }
    }

    public static int StepUntilActive(Simulation sim, EntityId entity) =>
        HeroTestWorld.StepUntil(sim, () => BuildingOf(sim, entity).State == BuildingState.Active);

    /// <summary>All tiles of a footprint.</summary>
    public static IEnumerable<(int X, int Y)> FootprintTiles(int x, int y, int width, int height)
    {
        for (int ty = y; ty < y + height; ty++)
        {
            for (int tx = x; tx < x + width; tx++)
            {
                yield return (tx, ty);
            }
        }
    }
}
