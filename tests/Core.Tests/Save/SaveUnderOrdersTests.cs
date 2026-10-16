using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Save;
using Core.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Save;

/// <summary>
/// Saves under a stream of orders (review R1 of M3a, FR-SAVE-04, NFR-04): a settlement at work gets seeded pseudo-random
/// orders (storage filters, work priorities, professions, placing, upgrading, moving, cancelling and demolishing
/// buildings, newcomers, items, hero gathering) while a copy of it is saved and loaded every few ticks. Every save must
/// load, and the copy must stay in step with the uninterrupted game. It finds states that live play reaches but the
/// load rejects (before the fix: a haul whose storage was demolished on the way). Category <c>Simulation</c>: the two
/// runs take about 17 s, too long for the coverage run.
/// </summary>
[Collection(Collection)]
public sealed class SaveUnderOrdersTests(ITestOutputHelper output)
{
    private static readonly string[] _jobTypes = ["haul", "chop", "plant", "quarry", "forage", "farm"];
    private static readonly string[] _professions = ["carrier", "builder", "lumberjack", "stonemason", "miner", "farmer", "hunter", "guard"];

    [Theory]
    [Trait("Category", "Simulation")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-07")]
    [Trait("Req", "NFR-04")]
    [InlineData(11UL, 61)]
    [InlineData(13UL, 61)]
    public void EverySaveUnderOrdersLoadsAndContinuesLikeTheUninterruptedGame(ulong seed, int every)
    {
        const int Ticks = 10_000; // both seeds reached review R1 (haul to a demolished storage) before the fix
        const int OrderEvery = 23;
        var orders = new Orders(seed);
        ContentCatalog content = Sim.SimTestData.Real;
        (Simulation sim, EntityId storage) = NewSettlement(content);
        Build(sim, "storage");
        Build(sim, "house");
        Build(sim, "house");
        Build(sim, "field");
        Fill(sim, storage, "berries", 60);
        foreach (string profession in new[] { "carrier", "lumberjack", "stonemason", "farmer", "lumberjack", "farmer", "carrier", "builder" })
        {
            Spawn(sim, profession);
        }

        Simulation copy = Load(SaveGame.ToBytes(sim), content, sim.Tick);
        long end = sim.Tick + Ticks;
        int given = 0;
        int loads = 0;
        while (sim.Tick < end)
        {
            if (sim.Tick % OrderEvery == 0 && orders.Pick(sim) is { } command)
            {
                sim.Enqueue(command);
                copy.Enqueue(command);
                given++;
            }

            sim.Step();
            copy.Step();
            if (sim.Tick % every == 0)
            {
                Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
                copy = Load(SaveGame.ToBytes(copy), content, sim.Tick);
                loads++;
            }
        }

        Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        output.WriteLine($"{given} orders, {loads} loads; residents {sim.World!.Residents.Count}, buildings {sim.World.Buildings.Count}");
    }

    private static Simulation Load(byte[] bytes, ContentCatalog content, long tick)
    {
        SaveLoadResult loaded = SaveGame.Read(bytes, content);
        Assert.True(loaded.Succeeded, $"tick {tick}: {loaded.Error?.Message}");
        return loaded.Simulation!;
    }

    // The order stream: a 64-bit LCG picks the kind of order and its target from the current state, so both games get the
    // same orders.
    private sealed class Orders(ulong seed)
    {
        private ulong _state = seed;

        public ISimCommand? Pick(Simulation sim)
        {
            GameWorld world = sim.World!;
            EntityId[] residents = world.Residents.Entities.ToArray();
            EntityId[] buildings = world.Buildings.Entities.ToArray();
            EntityId[] storages = [.. buildings.Where(b => Construction.IsStorage(Construction.DefOf(sim.Content, world.Buildings.Get(b))))];
            EntityId[] houses = [.. buildings.Where(b => Construction.DefOf(sim.Content, world.Buildings.Get(b)).Id == "house")];
            return Next(16) switch
            {
                0 => storages.Length == 0 ? null : new SetStorageFilterCommand(Any(storages), Next(ItemCategories.AllMask + 1)),
                1 or 2 => residents.Length == 0 ? null : new SetWorkPriorityCommand(Any(residents), Any(_jobTypes), Next(5)),
                3 => residents.Length == 0 ? null : new SetProfessionCommand(Any(residents), Any(_professions)),
                4 => storages.Length < 2 ? null : new DemolishBuildingCommand(Any(storages)),
                5 or 6 => new GiveItemCommand(Next(2) == 0 ? "wood" : "stone", 40),
                7 => Place(sim, storages.Length < 3 ? "storage" : "house"),
                8 => buildings.Length == 0 ? null : new UpgradeBuildingCommand(Any(buildings)),
                9 => buildings.Length == 0 ? null : new CancelBuildingCommand(Any(buildings)),
                10 => houses.Length < 2 ? null : new DemolishBuildingCommand(Any(houses)),
                11 => residents.Length > 14 ? null : new SpawnResidentCommand(),
                12 => Place(sim, "field"),
                13 => storages.Length == 0 ? null : new DepositHeroItemsCommand(Any(storages)),
                14 => buildings.Length == 0 ? null : Move(sim, Any(buildings)),
                _ => Gather(sim),
            };
        }

        private int Next(int n)
        {
            _state = (_state * 6364136223846793005UL) + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)n);
        }

        private T Any<T>(T[] items) => items[Next(items.Length)];

        private PlaceBuildingCommand? Place(Simulation sim, string id)
        {
            Building center = sim.World!.Buildings.Get(Construction.FindCenter(sim));
            int minDistance = 2 + Next(4);
            return TryFind(() => FindSpot(sim, id, 0, minDistance, near: (center.X, center.Y))) is { } spot
                ? new PlaceBuildingCommand(id, spot.X, spot.Y)
                : null;
        }

        private static MoveBuildingCommand? Move(Simulation sim, EntityId entity)
        {
            Building building = sim.World!.Buildings.Get(entity);
            BuildingDef def = Construction.DefOf(sim.Content, building);
            return !Construction.IsCenter(def) && TryFind(() => FindSpot(sim, def.Id, 0, 2, near: (building.X + 3, building.Y + 3), moving: entity)) is { } spot
                ? new MoveBuildingCommand(entity, spot.X, spot.Y)
                : null;
        }

        // The tree nearest to the hero (ring by ring, row by row) within 11 tiles.
        private static GatherCommand? Gather(Simulation sim)
        {
            GameWorld world = sim.World!;
            (int hx, int hy) = Heroes.HeroTestWorld.HeroTile(sim);
            for (int r = 1; r < 12; r++)
            {
                for (int y = hy - r; y <= hy + r; y++)
                {
                    for (int x = hx - r; x <= hx + r; x++)
                    {
                        if (world.Pathfinder.Grid.Contains(x, y) && world.Tiles.GetEffectiveResource(x, y) == ResourceKind.Tree)
                        {
                            return new GatherCommand(x, y);
                        }
                    }
                }
            }

            return null;
        }

        private static (int X, int Y)? TryFind(Func<(int X, int Y)> find)
        {
            try
            {
                return find();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
