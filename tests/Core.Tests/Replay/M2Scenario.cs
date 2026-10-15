using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Replay;
using Core.Sim;
using Core.Tests.Sim;
using Core.WorldGen;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Replay;

/// <summary>
/// The M2 acceptance scenario (golden <c>scenario_m2</c>): on a new 64² game the hero gathers wood for real, places
/// a campfire, a house and a storage, gathers more wood and stone, upgrades the house to level 2 and moves it. Along
/// the way it gives two orders that are refused (a house before the center, a storage upgrade the era does not allow).
/// The script decides from the running simulation each tick, at most one order per tick, and stops a few ticks after
/// the move ends; the whole run must fit in <see cref="TickBudget"/> ticks.
/// </summary>
internal static class M2Scenario
{
    /// <summary>The acceptance limit for the whole scenario.</summary>
    public const long TickBudget = 3000;

    /// <summary>Ticks recorded after the move has ended.</summary>
    public const long TailTicks = 20;

    /// <summary>Wood for the campfire, the house and the storage (5 + 20 + 30).</summary>
    public const int SettlementWood = 55;

    /// <summary>New-game parameters.</summary>
    public static NewGameOptions Options { get; } = new(0x3022_B122, 64, Difficulty.Normal);

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
                throw new InvalidOperationException($"The M2 scenario did not finish within {TickBudget} ticks (stuck at {script.Stage}).");
            }
        }

        return recorder.ToReplay();
    }

    /// <summary>The first building of type <paramref name="id"/>, or <see cref="EntityId.None"/>.</summary>
    public static EntityId Find(Simulation sim, string id)
    {
        GameWorld world = sim.World!;
        for (int i = 0; i < world.Buildings.Count; i++)
        {
            if (string.Equals(Construction.DefOf(sim.Content, world.Buildings.Values[i]).Id, id, StringComparison.Ordinal))
            {
                return world.Buildings.Entities[i];
            }
        }

        return EntityId.None;
    }

    private sealed class Script(Simulation sim)
    {
        private bool _triedEarlyHouse;
        private bool _triedStorageUpgrade;
        private (int X, int Y) _houseHome = (-1, -1);

        public string Stage { get; private set; } = "start";

        public bool Done { get; private set; }

        private GameWorld World => sim.World!;

        public ISimCommand? Next()
        {
            if (!_triedEarlyHouse)
            {
                // Refused (no center yet): the replay also carries a rejected building order.
                _triedEarlyHouse = true;
                (int hx, int hy) = Heroes.HeroTestWorld.HeroTile(sim);
                return new PlaceBuildingCommand("house", hx + 2, hy);
            }

            EntityId campfire = Find(sim, "campfire");
            EntityId house = Find(sim, "house");
            EntityId storage = Find(sim, "storage");
            if (campfire == EntityId.None)
            {
                Stage = "wood for the settlement";
                return Count("wood") < SettlementWood ? KeepGathering(ResourceKind.Tree) : PlaceNear("campfire", Heroes.HeroTestWorld.HeroTile(sim));
            }

            Building center = World.Buildings.Get(campfire);
            if (house == EntityId.None)
            {
                Stage = "place the house";
                return PlaceNear("house", (center.X, center.Y));
            }

            if (storage == EntityId.None)
            {
                Stage = "place the storage";
                return PlaceNear("storage", (center.X, center.Y));
            }

            Building home = World.Buildings.Get(house);
            if (_houseHome.X < 0)
            {
                _houseHome = (home.X, home.Y);
            }

            if (!_triedStorageUpgrade && World.Buildings.Get(storage).State == BuildingState.Active)
            {
                // Refused: level 2 of the storage needs the village era.
                _triedStorageUpgrade = true;
                return new UpgradeBuildingCommand(storage);
            }

            if (home.Level == 1)
            {
                Stage = "upgrade the house";
                if (Count("wood") < 30)
                {
                    return KeepGathering(ResourceKind.Tree);
                }

                if (Count("stone") < 10)
                {
                    return KeepGathering(ResourceKind.Stone);
                }

                return home.State == BuildingState.Active ? new UpgradeBuildingCommand(house) : null;
            }

            if (home.State == BuildingState.Active && (home.X, home.Y) == _houseHome)
            {
                Stage = "move the house";
                (int x, int y) = FindSpot(sim, "house", rotation: 1, minDistance: 4, near: (home.X, home.Y), moving: house);
                return new MoveBuildingCommand(house, x, y, 1);
            }

            Stage = "wait for the move";
            Done = home.State == BuildingState.Active;
            return null;
        }

        private int Count(string item)
        {
            sim.Content.TryGetId(item, out DefId id);
            return Construction.CountAvailable(sim, id);
        }

        private PlaceBuildingCommand PlaceNear(string id, (int X, int Y) near)
        {
            (int x, int y) = FindSpot(sim, id, minDistance: 2, near: near);
            return new PlaceBuildingCommand(id, x, y);
        }

        // Gathers the nearest node of `kind` unless the hero is already at one.
        private GatherCommand? KeepGathering(ResourceKind kind)
        {
            Hero hero = World.Heroes.Get(World.HeroEntity);
            if (hero.Activity == HeroActivity.Gathering && World.Tiles.GetEffectiveResource(hero.TargetX, hero.TargetY) == kind)
            {
                return null;
            }

            (int x, int y) = Heroes.HeroTestWorld.NearestNode(sim, kind, minDistance: 1);
            return new GatherCommand(x, y);
        }
    }
}
