using Core.Buildings;
using Core.Entities;
using Core.Replay;
using Core.Residents;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Replay;

/// <summary>
/// The M3a residents scenario (golden <c>scenario_m3a_residents</c>): on a new 64² game the hero receives wood and
/// berries (console <c>give</c>), places a campfire, two houses and a storage, and then nobody gives orders: over the
/// next game mornings newcomers arrive one by one (real data, chance from the formula) and walk to the campfire. The
/// script decides from the running simulation each tick, at most one order per tick, and stops a few ticks after the
/// <see cref="Newcomers"/>-th newcomer reached the settlement; the whole run must fit in <see cref="TickBudget"/>.
/// </summary>
internal static class ResidentScenario
{
    /// <summary>Newcomers the scenario waits for.</summary>
    public const int Newcomers = 3;

    /// <summary>The limit for the whole scenario: six game days.</summary>
    public const long TickBudget = 6 * 9000;

    /// <summary>Ticks recorded after the last newcomer reached the settlement.</summary>
    public const long TailTicks = 20;

    /// <summary>Wood for the campfire, two houses and the storage (5 + 2 × 20 + 30), with some to spare.</summary>
    public const int Wood = 100;

    /// <summary>Berries: food for three days for everyone, so the food points count.</summary>
    public const int Berries = 40;

    /// <summary>New-game parameters.</summary>
    public static NewGameOptions Options { get; } = new(0x3A_0001, 64, Difficulty.Normal);

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
            if (doneTick < 0 && script.Done)
            {
                doneTick = sim.Tick;
            }

            if (sim.Tick > TickBudget)
            {
                throw new InvalidOperationException($"The residents scenario did not finish within {TickBudget} ticks (stuck at {script.Stage}).");
            }
        }

        return recorder.ToReplay();
    }

    private sealed class Script(Simulation sim)
    {
        private int _orders;

        public string Stage { get; private set; } = "start";

        public bool Done { get; private set; }

        private GameWorld World => sim.World!;

        public ISimCommand? Next()
        {
            _orders++;
            switch (_orders)
            {
                case 1:
                    Stage = "give";
                    return new GiveItemCommand("wood", Wood);
                case 2:
                    return new GiveItemCommand("berries", Berries);
                case 3:
                    Stage = "build";
                    return PlaceNear("campfire", Heroes.HeroTestWorld.HeroTile(sim));
                case 4 or 5 or 6:
                    Building center = World.Buildings.Get(Construction.FindCenter(sim));
                    return PlaceNear(_orders == 6 ? "storage" : "house", (center.X, center.Y));
                default:
                    Stage = "wait for newcomers";
                    int arrived = World.ResidentEvents.Count(e => e.Kind == ResidentEventKind.Arrived);
                    Done = arrived >= Newcomers && World.Residents.Values.ToArray().All(r => r.State == ResidentState.Idle);
                    return null;
            }
        }

        private PlaceBuildingCommand PlaceNear(string id, (int X, int Y) near)
        {
            (int x, int y) = FindSpot(sim, id, minDistance: 2, near: near);
            return new PlaceBuildingCommand(id, x, y);
        }
    }
}
