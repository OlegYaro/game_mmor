using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Residents;
using Core.Sim;
using Core.Tests.Content;
using Core.Tests.Heroes;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Residents;

/// <summary>Helpers for resident tests on the small real game of the building tests (64², the hero test seed).</summary>
internal static class ResidentTestWorld
{
    // Every morning with a free place brings a newcomer: the raid-free points alone make 100 % and the cap is 100 %.
    private static readonly Lazy<ContentCatalog> _certain = new(() => WithEconomy(json => json
        .Replace("\"arrivalNoRaidPoints\": 20", "\"arrivalNoRaidPoints\": 100", StringComparison.Ordinal)
        .Replace("\"arrivalChanceCapPermille\": 800", "\"arrivalChanceCapPermille\": 1000", StringComparison.Ordinal)));

    // A 256² map (seed 1) with one site of good people, generated once.
    private static readonly Lazy<Core.WorldGen.WorldMap> _largeMap = new(() =>
        Core.WorldGen.WorldGenerator.Generate(new Core.WorldGen.WorldGenRequest(LargeSeed, 256, SimTestData.Real.WorldGen)));

    /// <summary>Seed of <see cref="NewLargeGame"/>.</summary>
    public const ulong LargeSeed = 1;

    /// <summary>A new 256² game (seed <see cref="LargeSeed"/>, one human settlement site) on a shared map.</summary>
    public static Simulation NewLargeGame(ContentCatalog? content = null) =>
        Simulation.CreateNewGame(content ?? SimTestData.Real, new NewGameOptions(LargeSeed, 256, Difficulty.Normal), null, _largeMap.Value);

    /// <summary>The real data, except that a newcomer arrives on every morning with a free place (chance 1000 ‰).</summary>
    public static ContentCatalog Certain => _certain.Value;

    /// <summary>The real data with <c>data/balance/economy.json</c> edited by <paramref name="edit"/> (the edit must change it).</summary>
    public static ContentCatalog WithEconomy(Func<string, string> edit)
    {
        InMemoryContentSource source = new();
        foreach (ContentFile file in TestPaths.LoadDataSource().GetFiles())
        {
            string text = file.Text;
            if (file.Path == "balance/economy.json")
            {
                text = edit(text);
                Assert.NotEqual(file.Text, text);
            }

            source.Add(file.Path, text);
        }

        ContentLoadResult result = ContentCatalog.Load(source);
        return result.Catalog ?? throw new InvalidOperationException(string.Join("\n", result.Errors));
    }

    /// <summary>A campfire and <paramref name="houses"/> level-1 houses near the hero, all built; returns the houses in order.</summary>
    public static EntityId[] Settle(Simulation sim, int houses)
    {
        Build(sim, "campfire");
        var built = new EntityId[houses];
        for (int i = 0; i < houses; i++)
        {
            built[i] = Build(sim, "house");
        }

        return built;
    }

    /// <summary>Runs a <see cref="SpawnResidentCommand"/> and returns the new resident.</summary>
    public static EntityId Spawn(Simulation sim, string profession = "")
    {
        int before = sim.World!.Residents.Count;
        sim.Enqueue(new SpawnResidentCommand(profession));
        sim.Step();
        Assert.Equal(before + 1, sim.World.Residents.Count);
        return Newest(sim);
    }

    /// <summary>The resident created last (the highest entity id among those created in the latest spawn event).</summary>
    public static EntityId Newest(Simulation sim) => sim.World!.ResidentEvents.Last(e => e.Kind is ResidentEventKind.Arrived or ResidentEventKind.Spawned).Resident;

    public static Resident ResidentOf(Simulation sim, EntityId entity) => sim.World!.Residents.Get(entity);

    /// <summary>First tick of the arrival hour on <paramref name="day"/>.</summary>
    public static long ArrivalTick(Simulation sim, long day) => sim.Calendar.FirstTickOf(day, sim.Content.Economy.ArrivalHour);

    /// <summary>Steps until <see cref="Simulation.Tick"/> is <paramref name="tick"/>.</summary>
    public static void StepTo(Simulation sim, long tick)
    {
        Assert.True(sim.Tick <= tick, $"already at tick {sim.Tick}, past {tick}");
        while (sim.Tick < tick)
        {
            sim.Step();
        }
    }

    /// <summary>Puts <paramref name="count"/> berries (food value 1 each) straight into <paramref name="storage"/>.</summary>
    public static void StoreFood(Simulation sim, EntityId storage, int count) => Jobs.JobTestWorld.Fill(sim, storage, "berries", count);

    /// <summary>The settlement center's tile.</summary>
    public static (int X, int Y) CenterTile(Simulation sim)
    {
        Building center = sim.World!.Buildings.Get(Construction.FindCenter(sim));
        return (center.X, center.Y);
    }

    /// <summary>The resident's tile.</summary>
    public static (int X, int Y) TileOf(Simulation sim, EntityId entity)
    {
        Position p = sim.Positions.Get(entity);
        return (p.TileX, p.TileY);
    }

    /// <summary>A new 64² game of the hero test seed (<see cref="HeroTestWorld.Seed"/>) on the shared map.</summary>
    public static Simulation NewResidentGame(ContentCatalog? content = null) => NewGame(content ?? SimTestData.Real);
}
