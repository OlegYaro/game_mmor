using System.Globalization;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Jobs;
using Core.Residents;
using Core.Save;
using Core.Sim;

namespace Core.DevConsole;

/// <summary>
/// Console commands of the job board, the food and the tiles (NFR-07, FR-NPC-06, FR-NPC-07, FR-ECO-10, FR-WORLD-04,
/// FR-WORLD-15): jobs, setprofession, setpriority, food, tile.
/// </summary>
internal static partial class BuiltInCommands
{
    private static void RegisterWork(DevConsole console)
    {
        console.Register("jobs", "jobs", "List every resident's job and load, the reservations and the banned targets.", JobList);
        console.Register("setprofession", "setprofession <resident> <profession>",
            "Give a resident another ordinary profession; its priorities reset (runs next tick; see 'jobs').", SetProfession);
        console.Register("setpriority", "setpriority <resident> <job> <0-4>",
            "Set a resident's priority for a job type: 1 highest, 4 lowest, 0 never (runs next tick; see 'jobs').", SetPriority);
        console.Register("food", "food", "Print the stored food, the daily need, the days it lasts, the shortage and the tax.", FoodInfo);
        console.Register("tile", "tile [x y]", "Print the stumps, saplings, trails and roads of the map, or one tile: node, flora, walk counter and class.", TileInfo);
    }

    private static ConsoleResult JobList(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: jobs");
        }

        if (!TryGetWorld(context, out GameWorld? world))
        {
            return NoWorld();
        }

        Simulation sim = context.Simulation.Current;
        CultureInfo inv = CultureInfo.InvariantCulture;
        ReservationManager reservations = world.Reservations;
        int busy = world.Jobs.Values.ToArray().Count(j => j.Kind != JobKind.None);
        List<string> lines =
        [
            string.Create(inv, $"residents {world.Residents.Count} | with a job {busy} | reservations {reservations.Reservations.Count} | bans {reservations.Bans.Count}"),
        ];

        ReadOnlySpan<EntityId> owners = world.Residents.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            Resident resident = world.Residents.Values[i];
            ResidentJob job = world.Jobs.Get(owners[i]);
            string what = job.Kind == JobKind.None
                ? string.Create(inv, $"no job (looks again at tick {job.NextThinkTick})")
                : $"{JobKinds.NameOf(job.Kind)} {JobKinds.PhaseNames[(int)job.Phase]} {DescribeJobTarget(world, job)}";
            string carry = job.IsCarrying ? string.Create(inv, $"{job.CarryCount} {sim.Content.Get(job.CarryItem).Id}") : "nothing";
            string priorities = string.Join(" ", JobKinds.TierOrder.Select(k => string.Create(inv, $"{JobKinds.NameOf(k)}={resident.Priorities.Get(k)}")));
            lines.Add(string.Create(inv,
                $"{owners[i]} {ResidentRules.NameOf(sim.Content, resident)} {sim.Content.Get(resident.Profession).Id} {SaveMapper.ResidentStateNames[(int)resident.State]}: {what} | carries {carry} | {priorities}"));
        }

        foreach (Reservation r in reservations.Reservations)
        {
            string item = r.Item == default ? string.Empty : " " + sim.Content.Get(r.Item).Id;
            lines.Add(string.Create(inv, $"reserved {DescribeTarget(world, r.Target)} by {r.Holder}: {r.Amount}{item}"));
        }

        foreach (JobBan b in reservations.Bans)
        {
            lines.Add(string.Create(inv, $"banned {DescribeTarget(world, b.Target)} for {b.Holder} until tick {b.UntilTick}"));
        }

        return ConsoleResult.Ok(lines);
    }

    private static ConsoleResult SetProfession(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            return ConsoleResult.Fail("Usage: setprofession <resident> <profession>");
        }

        if (!TryGetResidentArg(context, args[0], out EntityId resident, out ConsoleResult failure))
        {
            return failure;
        }

        Simulation sim = context.Simulation.Current;
        string profession = args[1].ToLowerInvariant();
        if (!sim.Content.TryGet(profession, out ProfessionDef def) || !string.Equals(def.Kind, "ordinary", StringComparison.Ordinal))
        {
            return ConsoleResult.Fail($"'{args[1]}' is not an ordinary profession. Ordinary professions: "
                + string.Join(", ", sim.Content.Professions.Where(p => p.Kind == "ordinary").Select(p => p.Id)) + ".");
        }

        PendingCommand pending = sim.Enqueue(new SetProfessionCommand(resident, profession));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"profession {profession} for {resident} queued for tick {pending.ExecuteTick} (see 'jobs' for the result)"));
    }

    private static ConsoleResult SetPriority(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: setpriority <resident> <job> <0-4>";
        if (args.Count != 3)
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryGetResidentArg(context, args[0], out EntityId resident, out ConsoleResult failure))
        {
            return failure;
        }

        string job = args[1].ToLowerInvariant();
        if (!JobKinds.TryParse(job, out _))
        {
            return ConsoleResult.Fail($"Unknown job type '{args[1]}'. Job types: {string.Join(", ", JobKinds.Names.Skip(1))}.");
        }

        if (!TryParseInt(args[2], out int priority) || priority < 0 || priority > WorkPriorities.Max)
        {
            return ConsoleResult.Fail($"The priority must be a whole number from 0 to {WorkPriorities.Max}. {UsageText}");
        }

        PendingCommand pending = context.Simulation.Current.Enqueue(new SetWorkPriorityCommand(resident, job, priority));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"priority {job}={priority} for {resident} queued for tick {pending.ExecuteTick} (see 'jobs' for the result)"));
    }

    private static ConsoleResult FoodInfo(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: food");
        }

        if (!TryGetWorld(context, out GameWorld? world))
        {
            return NoWorld();
        }

        Simulation sim = context.Simulation.Current;
        CultureInfo inv = CultureInfo.InvariantCulture;
        long need = FoodSupply.DailyNeed(sim);
        long days = FoodSupply.DaysLeft(sim);
        string lasts = days < 0 ? "nobody eats" : string.Create(inv, $"lasts {days} days");
        string shortage = world.DaysWithoutFood == 0
            ? "no shortage"
            : string.Create(inv, $"SHORTAGE {world.DaysWithoutFood} days (work at {sim.Content.Economy.ShortageSlowdownPermille}‰, last left on day {(world.LastLeaveDay < 0 ? "-" : world.LastLeaveDay.ToString(inv))})");
        List<string> lines =
        [
            string.Create(inv, $"food {FoodSupply.Stored(sim)} units in storages | {FoodSupply.Eaters(world)} residents eat {need} a day | {lasts} | {shortage}"),
            string.Create(inv, $"tax {FoodSupply.TaxPerDay(sim)} {FoodSupply.TaxItem} a day ({sim.Content.Economy.TaxPerResidentPerDay} x residents x center level {FoodSupply.CenterLevel(sim)})"),
        ];
        if (world.EconomyEvents.Count > 0)
        {
            EconomyEvent last = world.EconomyEvents[^1];
            lines.Add(string.Create(inv, $"last event (tick {last.Tick}): {last.Kind} amount {last.Amount} lost {last.Lost} days {last.DaysLeft}"));
        }

        return ConsoleResult.Ok(lines);
    }

    private static ConsoleResult TileInfo(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: tile [x y]";
        if (args.Count is not (0 or 2))
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryGetWorld(context, out GameWorld? world))
        {
            return NoWorld();
        }

        Tiles.TileChanges tiles = world.Tiles;
        CultureInfo inv = CultureInfo.InvariantCulture;
        if (args.Count == 0)
        {
            return ConsoleResult.Ok(string.Create(inv,
                $"flora {tiles.FloraTiles} tiles (stumps, saplings, grown trees) | walked {tiles.WalkedTiles} tiles | trails {tiles.TrailTiles} | roads {tiles.RoadTiles}"));
        }

        if (!TryParseInt(args[0], out int x) || !TryParseInt(args[1], out int y))
        {
            return ConsoleResult.Fail($"Coordinates must be whole numbers. {UsageText}");
        }

        if (!world.Map.Terrain.Contains(x, y))
        {
            return ConsoleResult.Fail(string.Create(inv, $"Tile {x},{y} is outside the map (0..{world.Size - 1})."));
        }

        Tiles.FloraState flora = tiles.GetFlora(x, y);
        EntityId occupant = tiles.GetOccupant(x, y);
        string since = flora == Tiles.FloraState.None ? string.Empty : string.Create(inv, $" since tick {tiles.GetFloraTick(x, y)}");
        return ConsoleResult.Ok(string.Create(inv,
            $"tile {x},{y}: node {tiles.GetEffectiveResource(x, y)} ({tiles.GetEffectiveAmount(x, y)}) | flora {Tiles.TileStateNames.Flora[(int)flora]}{since} | "
            + $"walk {tiles.GetWalkCounter(x, y)} ({Tiles.TileStateNames.RoadClasses[(int)tiles.GetRoadClass(x, y)]}) | occupant {(occupant == EntityId.None ? "none" : occupant.ToString())}"));
    }

    // A resident argument: "#5v1", "5v1" or just the index "5".
    private static bool TryGetResidentArg(DevConsoleContext context, string text, out EntityId entity, out ConsoleResult failure)
    {
        entity = EntityId.None;
        if (!TryGetWorld(context, out GameWorld? world))
        {
            failure = NoWorld();
            return false;
        }

        string trimmed = text.TrimStart('#');
        int v = trimmed.IndexOf('v', StringComparison.OrdinalIgnoreCase);
        if (v >= 0)
        {
            if (TryParseInt(trimmed[..v], out int index) && TryParseInt(trimmed[(v + 1)..], out int generation))
            {
                entity = new EntityId(index, generation);
            }
        }
        else if (TryParseInt(trimmed, out int index))
        {
            foreach (EntityId owner in world.Residents.Entities)
            {
                entity = owner.Index == index ? owner : entity;
            }
        }

        if (entity == EntityId.None || !world.Residents.Has(entity))
        {
            failure = ConsoleResult.Fail($"'{text}' is not a resident. Type 'residents' for a list.");
            return false;
        }

        failure = ConsoleResult.Ok();
        return true;
    }

    private static string DescribeJobTarget(GameWorld world, ResidentJob job)
    {
        if (job.Phase == JobPhase.ToStorage)
        {
            return $"-> storage {job.Storage}";
        }

        return job.Building != EntityId.None
            ? $"at {job.Building}"
            : string.Create(CultureInfo.InvariantCulture, $"at node {job.TargetTile % world.Size},{job.TargetTile / world.Size}");
    }

    private static string DescribeTarget(GameWorld world, ReservationTarget target) => target.Layer == ReservationLayer.Tile
        ? string.Create(CultureInfo.InvariantCulture, $"tile {target.Index % world.Size},{target.Index / world.Size}")
        : $"{ReservationManager.LayerNames[(int)target.Layer]} of {target.Building}";
}
