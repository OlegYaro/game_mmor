using Core.Buildings;
using Core.Economy;
using Core.Entities;
using Core.Pathfinding;
using Core.Replay;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Replay;

/// <summary>
/// The M3a acceptance scenario (golden <c>scenario_m3a</c>, GDD §18 P-30: residents work and feed the settlement on
/// their own). On a new 64² game the hero receives wood, stone and berries (console <c>give</c>), places a campfire, two
/// houses, a storage and a field, upgrades one house (five places), walks to the storage and hands the backpack in. Then
/// nobody gives orders: newcomers arrive on their own until <see cref="Newcomers"/> live in the settlement, and the
/// scenario runs on for <see cref="FedDays"/> more game days while they chop, quarry, forage, farm and haul. The script
/// decides from the running simulation each tick, at most one order per tick; the run must fit in
/// <see cref="TickBudget"/>.
/// </summary>
internal static class M3aScenario
{
    /// <summary>Newcomers the scenario waits for.</summary>
    public const int Newcomers = 5;

    /// <summary>Game days the residents must feed themselves after the last newcomer.</summary>
    public const int FedDays = 3;

    /// <summary>The limit for the whole scenario: sixteen game days.</summary>
    public const long TickBudget = 16 * 9000;

    /// <summary>Ticks recorded after the last fed day began.</summary>
    public const long TailTicks = 20;

    /// <summary>Wood for the campfire, two houses, the storage, the field and the house upgrade (5 + 2 × 20 + 30 + 15 + 30), with some to spare.</summary>
    public const int Wood = 140;

    /// <summary>Stone for the house upgrade.</summary>
    public const int Stone = 10;

    /// <summary>Berries: a first food stock, handed in at the storage.</summary>
    public const int Berries = 30;

    /// <summary>New-game parameters.</summary>
    public static NewGameOptions Options { get; } = new(0x3A_0002, 64, Difficulty.Normal);

    /// <summary>Plays the script on a new game and records it.</summary>
    public static ReplayFile Record()
    {
        Simulation sim = Simulation.CreateNewGame(SimTestData.Real, Options);
        var recorder = new ReplayRecorder(sim);
        var script = new Script(sim);
        long doneTick = -1;
        while (doneTick < 0 || sim.Tick < doneTick + TailTicks)
        {
            if (script.Next() is { } command)
            {
                recorder.Enqueue(command);
            }

            sim.Step();
            script.Observe();
            if (doneTick < 0 && script.Done)
            {
                doneTick = sim.Tick;
            }

            if (sim.Tick > TickBudget)
            {
                throw new InvalidOperationException($"The M3a scenario did not finish within {TickBudget} ticks (stuck at {script.Stage}).");
            }
        }

        return recorder.ToReplay();
    }

    /// <summary>The day on which the last newcomer had arrived (the first fed day starts the next morning).</summary>
    public static long LastArrivalDay(Simulation sim, IEnumerable<ResidentEvent> arrivals) => sim.Calendar.At(arrivals.Max(a => a.Tick)).Day;

    /// <summary>The first building of type <paramref name="id"/> (any state), or <see cref="EntityId.None"/>.</summary>
    public static EntityId Find(Simulation sim, string id) => M2Scenario.Find(sim, id);

    private sealed class Script(Simulation sim)
    {
        private readonly string[] _buildings = ["house", "house", "storage", "field"];
        private int _step;
        private int _arrived;
        private long _fedFrom = -1;

        public string Stage { get; private set; } = "start";

        public bool Done { get; private set; }

        private GameWorld World => sim.World!;

        public ISimCommand? Next()
        {
            switch (_step)
            {
                case 0:
                    Stage = "give";
                    _step++;
                    return new GiveItemCommand("wood", Wood);
                case 1:
                    _step++;
                    return new GiveItemCommand("stone", Stone);
                case 2:
                    _step++;
                    return new GiveItemCommand("berries", Berries);
                case 3:
                    Stage = "build";
                    _step++;
                    return PlaceNear("campfire", Heroes.HeroTestWorld.HeroTile(sim));
                case 4 or 5 or 6 or 7:
                    Building center = World.Buildings.Get(Construction.FindCenter(sim));
                    return PlaceNear(_buildings[_step++ - 4], (center.X, center.Y));
                case 8:
                    if (World.Buildings.Values.ToArray().Any(b => b.State != BuildingState.Active))
                    {
                        return null;
                    }

                    Stage = "upgrade";
                    _step++;
                    return new UpgradeBuildingCommand(Find(sim, "house"));
                case 9:
                    Stage = "walk to the storage";
                    _step++;
                    return WalkNextTo(sim, Find(sim, "storage"));
                case 10:
                    if (World.Movers.Get(World.HeroEntity).IsMoving)
                    {
                        return null;
                    }

                    Stage = "hand in";
                    _step++;
                    return new DepositHeroItemsCommand(Find(sim, "storage"));
                default:
                    Stage = _arrived < Newcomers ? "wait for newcomers" : "residents feed themselves";
                    return null;
            }
        }

        // After each tick: count the newcomers; once all are there, wait for the fed days.
        public void Observe()
        {
            _arrived += World.ResidentEvents.Count(e => e.Kind == ResidentEventKind.Arrived && e.Tick == sim.Tick);
            if (_arrived >= Newcomers && _fedFrom < 0)
            {
                _fedFrom = sim.Now.Day + 1;
            }

            Done = _fedFrom >= 0 && sim.Tick == sim.Calendar.FirstTickOf(_fedFrom + FedDays, 0);
        }

        private PlaceBuildingCommand PlaceNear(string id, (int X, int Y) near)
        {
            (int x, int y) = FindSpot(sim, id, minDistance: 2, near: near);
            return new PlaceBuildingCommand(id, x, y);
        }
    }

    /// <summary>An order to walk the hero to the first tile (row order) on the ring around <paramref name="storage"/> it can reach.</summary>
    public static MoveHeroCommand WalkNextTo(Simulation sim, EntityId storage)
    {
        GameWorld world = sim.World!;
        Building building = world.Buildings.Get(storage);
        (int width, int height) = Construction.FootprintOf(Construction.DefOf(sim.Content, building), building.Rotation);
        PassabilityGrid grid = world.Pathfinder.Grid;
        (int hx, int hy) = Heroes.HeroTestWorld.HeroTile(sim);
        for (int y = building.Y - 1; y <= building.Y + height; y++)
        {
            for (int x = building.X - 1; x <= building.X + width; x++)
            {
                bool ring = x == building.X - 1 || x == building.X + width || y == building.Y - 1 || y == building.Y + height;
                if (ring && grid.IsPassable(x, y) && world.Pathfinder.IsReachable(hx, hy, x, y))
                {
                    return new MoveHeroCommand(x, y);
                }
            }
        }

        throw new InvalidOperationException("No tile next to the storage.");
    }
}
