using System.Globalization;
using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Logging;
using Core.Save;
using Core.Sim;
using Core.WorldGen;

namespace Core.DevConsole;

/// <summary>
/// The built-in console commands: help, tick, speed, pause, resume, seed, hash, spawn, save, load, log (M0),
/// world, worldhash, fog, reveal, goto (M1), hero, herogo, give, build, check, upgrade, movebuilding, demolish,
/// cancelbuilding and buildings (M2).
/// </summary>
internal static class BuiltInCommands
{
    private const string SaveExtension = ".sav";
    private const int MaxLogLines = 500;

    internal static void RegisterAll(DevConsole console)
    {
        console.Register("help", "help [command]", "List commands, or show help for one.", (_, args) => Help(console, args));
        console.Register("tick", "tick", "Print the current tick and calendar date.", Tick);
        console.Register("speed", "speed <1|2|4|20>", "Set or show the game speed (20 needs the dev flag).", Speed);
        console.Register("pause", "pause", "Pause the simulation.", (c, args) => SetPaused(c, args, true));
        console.Register("resume", "resume", "Resume the simulation.", (c, args) => SetPaused(c, args, false));
        console.Register("seed", "seed", "Print the world seed.", Seed);
        console.Register("hash", "hash", "Print the combined state hash and the hash of each subsystem.", Hash);
        console.Register("spawn", "spawn marker <x> <y>", "Spawn a marker entity at a tile (runs next tick).", Spawn);
        console.Register("save", "save <name>", "Save the game to the save directory.", Save);
        console.Register("load", "load <name>", "Load a save from the save directory.", Load);
        console.Register("log", "log <trace|debug|info|warn|error> [n]", "Set log verbosity, or print the last n events at that level or above.", Log);
        console.Register("world", "world", "Print the world: seed, size, start, points of interest, sites, resources, warnings.", World);
        console.Register("worldhash", "worldhash", "Print the map hash (same seed and size give the same hash).", WorldHash);
        console.Register("fog", "fog", "Print how many tiles are unknown, explored and visible.", FogCounts);
        console.Register("reveal", "reveal", "Mark the whole map explored (runs next tick, recorded in replays).", Reveal);
        console.Register("goto", "goto <x> <y>", "Move the camera to a tile.", GoTo);
        console.Register("hero", "hero", "Print the hero: position, health, stamina, activity, path and inventory.", HeroInfo);
        console.Register("herogo", "herogo <x> <y> [sprint]", "Order the hero to walk to a tile (runs next tick, recorded in replays).", HeroGo);
        console.Register("give", "give <item> <count>", "Put items into the hero's backpack, as many as fit (runs next tick, recorded in replays).", Give);
        console.Register("build", "build <id> <x> <y> [rot]", "Place a building with its top-left tile at x,y (runs next tick; see 'buildings').", Build);
        console.Register("check", "check <id> <x> <y> [rot]", "Show whether a building can be placed at x,y now, and why not.", Check);
        console.Register("upgrade", "upgrade <entity>", "Upgrade a building to its next level (runs next tick; see 'buildings').", (c, a) => EntityOrder(c, a, "upgrade", e => new UpgradeBuildingCommand(e)));
        console.Register("movebuilding", "movebuilding <entity> <x> <y> [rot]", "Move a building to a new top-left tile (runs next tick; see 'buildings').", MoveBuilding);
        console.Register("demolish", "demolish <entity>", "Demolish a building for a partial refund (runs next tick; see 'buildings').", (c, a) => EntityOrder(c, a, "demolish", e => new DemolishBuildingCommand(e)));
        console.Register("cancelbuilding", "cancelbuilding <entity>", "Cancel a building's construction, upgrade or move (runs next tick; see 'buildings').", (c, a) => EntityOrder(c, a, "cancelbuilding", e => new CancelBuildingCommand(e)));
        console.Register("buildings", "buildings", "List the buildings, the territory and the last building order's result.", BuildingList);
    }

    private static ConsoleResult Usage(DevConsole console, string name) =>
        ConsoleResult.Fail($"Usage: {console.Commands.First(c => c.Name == name).Usage}");

    private static ConsoleResult Help(DevConsole console, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return ConsoleResult.Ok(console.Commands.Select(c => $"{c.Usage} - {c.Help}"));
        }

        if (args.Count > 1)
        {
            return Usage(console, "help");
        }

        return console.TryGetCommand(args[0], out ConsoleCommand command)
            ? ConsoleResult.Ok($"{command.Usage} - {command.Help}")
            : ConsoleResult.Fail($"Unknown command '{args[0]}'. Type 'help' for a list.");
    }

    private static ConsoleResult Tick(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: tick");
        }

        Simulation sim = context.Simulation.Current;
        CalendarDate d = sim.Now;
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"tick {sim.Tick} | day {d.Day + 1} {d.Hour:D2}:{d.Minute:D2} | week {d.Week + 1} | season {d.Season + 1} day {d.DayOfSeason + 1} | year {d.Year + 1} | {(d.IsNight ? "night" : "day")}"));
    }

    private static ConsoleResult Speed(DevConsoleContext context, IReadOnlyList<string> args)
    {
        TickScheduler scheduler = context.Scheduler;
        if (args.Count == 0)
        {
            return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
                $"speed x{(int)scheduler.Speed}{(scheduler.IsPaused ? " (paused)" : string.Empty)}"));
        }

        if (args.Count != 1 || !TryParseInt(args[0], out int value) || !Enum.IsDefined((GameSpeed)value))
        {
            return ConsoleResult.Fail("Usage: speed <1|2|4|20>");
        }

        if (!scheduler.TrySetSpeed((GameSpeed)value))
        {
            return ConsoleResult.Fail("Speed x20 is only available with the dev speed flag.");
        }

        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture, $"speed x{value}"));
    }

    private static ConsoleResult SetPaused(DevConsoleContext context, IReadOnlyList<string> args, bool paused)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail($"Usage: {(paused ? "pause" : "resume")}");
        }

        context.Scheduler.IsPaused = paused;
        return ConsoleResult.Ok(paused ? "paused" : "resumed");
    }

    private static ConsoleResult Seed(DevConsoleContext context, IReadOnlyList<string> args) =>
        args.Count != 0
            ? ConsoleResult.Fail("Usage: seed")
            : ConsoleResult.Ok(context.Simulation.Current.Seed.ToString(CultureInfo.InvariantCulture));

    private static ConsoleResult Hash(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: hash");
        }

        SubsystemHashes hashes = context.Simulation.Current.ComputeHashes();
        List<string> lines = [$"combined {ContentHash.Format(hashes.Combined)}"];
        lines.AddRange(hashes.Entries.Select(e => $"{e.Name} {ContentHash.Format(e.Hash)}"));
        return ConsoleResult.Ok(lines);
    }

    private static ConsoleResult Spawn(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: spawn marker <x> <y> (tile coordinates)";
        if (args.Count != 3 || !string.Equals(args[0], "marker", StringComparison.OrdinalIgnoreCase))
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryParseInt(args[1], out int x) || !TryParseInt(args[2], out int y))
        {
            return ConsoleResult.Fail($"Coordinates must be whole numbers. {UsageText}");
        }

        if (Math.Abs((long)x) > int.MaxValue / Position.UnitsPerTile || Math.Abs((long)y) > int.MaxValue / Position.UnitsPerTile)
        {
            return ConsoleResult.Fail("Coordinates are out of range.");
        }

        Simulation sim = context.Simulation.Current;
        PendingCommand pending = sim.Enqueue(new SpawnMarkerCommand(Position.FromTile(x, y)));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture, $"marker queued at {x},{y} for tick {pending.ExecuteTick}"));
    }

    private static bool TryGetWorld(DevConsoleContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world)
    {
        world = context.Simulation.Current.World;
        return world is not null;
    }

    private static ConsoleResult NoWorld() => ConsoleResult.Fail("This simulation has no world (start a new game).");

    private static ConsoleResult World(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: world");
        }

        if (!TryGetWorld(context, out GameWorld? world))
        {
            return NoWorld();
        }

        Simulation sim = context.Simulation.Current;
        WorldMap map = world.Map;
        CultureInfo inv = CultureInfo.InvariantCulture;
        int[] resources = map.CountResources();
        List<string> lines =
        [
            string.Create(inv, $"seed {map.Seed} | size {map.Size}x{map.Size} | generator v{map.Version} | difficulty {DifficultyNames.ToName(sim.Difficulty)}"),
            string.Create(inv, $"start {map.Start.X},{map.Start.Y} (relaxed: {map.StartRelaxation})"),
            "points of interest " + map.PointsOfInterest.Count.ToString(inv) + ": " + string.Join(", ",
                WorldNames.PoiKinds.Select(k => string.Create(inv, $"{WorldNames.ToName(k)} {map.PointsOfInterest.Count(p => p.Kind == k)}"))),
            "settlement sites " + map.SettlementSites.Count.ToString(inv) + ": " + string.Join(", ",
                WorldNames.Factions.Select(f => string.Create(inv, $"{WorldNames.ToName(f)} {map.SettlementSites.Count(s => s.Faction == f)}"))),
            "resource tiles: " + string.Join(", ",
                WorldNames.Resources.Select(r => string.Create(inv, $"{WorldNames.ToName(r)} {resources[(int)r]}"))),
            "warnings " + map.Warnings.Count.ToString(inv) + (map.Warnings.Count == 0 ? string.Empty : ":"),
        ];
        lines.AddRange(map.Warnings.Select(w => "  " + w));
        return ConsoleResult.Ok(lines);
    }

    private static ConsoleResult WorldHash(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: worldhash");
        }

        return TryGetWorld(context, out GameWorld? world) ? ConsoleResult.Ok(ContentHash.Format(world.MapHash)) : NoWorld();
    }

    private static ConsoleResult FogCounts(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: fog");
        }

        if (!TryGetWorld(context, out GameWorld? world))
        {
            return NoWorld();
        }

        FogCounts counts = world.Fog.CountStates();
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"unknown {counts.Unknown} | explored {counts.Explored} | visible {counts.Visible} | total {world.Fog.TileCount}"));
    }

    private static ConsoleResult Reveal(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: reveal");
        }

        if (!TryGetWorld(context, out _))
        {
            return NoWorld();
        }

        PendingCommand pending = context.Simulation.Current.Enqueue(new RevealAllCommand());
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture, $"reveal queued for tick {pending.ExecuteTick}"));
    }

    private static ConsoleResult GoTo(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: goto <x> <y> (tile coordinates)";
        if (args.Count != 2)
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryParseInt(args[0], out int x) || !TryParseInt(args[1], out int y))
        {
            return ConsoleResult.Fail($"Coordinates must be whole numbers. {UsageText}");
        }

        if (!TryGetWorld(context, out GameWorld? world))
        {
            return NoWorld();
        }

        if (!world.Map.Terrain.Contains(x, y))
        {
            return ConsoleResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Tile {x},{y} is outside the map (0..{world.Size - 1})."));
        }

        return ConsoleResult.OkWithCamera(new CameraRequest(x, y), string.Create(CultureInfo.InvariantCulture, $"camera to {x},{y}"));
    }

    private static bool TryGetHero(DevConsoleContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world,
        out EntityId hero, out ConsoleResult failure)
    {
        world = context.Simulation.Current.World;
        hero = world?.HeroEntity ?? EntityId.None;
        failure = world is null ? NoWorld() : ConsoleResult.Fail("There is no hero in this game.");
        return world is not null && hero != EntityId.None;
    }

    private static ConsoleResult HeroInfo(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: hero");
        }

        if (!TryGetHero(context, out GameWorld? world, out EntityId entity, out ConsoleResult failure))
        {
            return failure;
        }

        Simulation sim = context.Simulation.Current;
        ContentCatalog content = sim.Content;
        CultureInfo inv = CultureInfo.InvariantCulture;
        Hero hero = world.Heroes.Get(entity);
        Position position = sim.Positions.TryGet(entity, out Position p) ? p : default;
        Mover mover = world.Movers.TryGet(entity, out Mover m) ? m : Mover.Idle;
        Inventory inventory = world.Inventories.TryGet(entity, out Inventory i) ? i : Inventory.Create(0, 0);
        int size = world.Size;

        string activity = SaveMapper.HeroActivityNames[(int)hero.Activity]
            + (hero.Activity == HeroActivity.Gathering ? string.Create(inv, $" {hero.TargetX},{hero.TargetY} (action {hero.ActionTicks} ticks)") : string.Empty);
        string path = mover.IsMoving
            ? string.Create(inv, $"path {mover.Path.Count - 1 - mover.StepIndex} steps left to {mover.Goal % size},{mover.Goal / size}{(mover.Sprint ? " (sprint)" : string.Empty)}")
            : "path none";
        string backpack = string.Join(", ", inventory.Backpack
            .Select((slot, index) => (slot, index))
            .Where(e => !e.slot.IsEmpty)
            .Select(e => string.Create(inv, $"[{e.index}] {content.Get(e.slot.Item).Id} {e.slot.Count}")));
        string equipment = string.Join(", ", inventory.Equipment.Select((slot, index) =>
            (index < content.Hero.EquipmentSlots.Count ? content.Hero.EquipmentSlots[index] : index.ToString(inv)) + " "
            + (slot.IsEmpty ? "-" : content.Get(slot.Item).Id)));

        return ConsoleResult.Ok(
        [
            string.Create(inv, $"hero {entity} at tile {position.TileX},{position.TileY} ({position.X},{position.Y}) | health {hero.Health}/{content.Hero.Health} | stamina {hero.Stamina}/{content.Hero.Stamina}"),
            string.Create(inv, $"attack {hero.Attack} | defense {hero.Defense} | spell power {hero.SpellPower} | knowledge {hero.Knowledge}"),
            $"activity {activity} | last stop {SaveMapper.HeroStopReasonNames[(int)hero.LastStop]} | last order {SaveMapper.HeroOrderResultNames[(int)hero.LastOrder]} | {path}",
            string.Create(inv, $"backpack {inventory.UsedStacks}/{inventory.Backpack.Count}{(backpack.Length == 0 ? string.Empty : ": " + backpack)}"),
            "equipment: " + equipment,
        ]);
    }

    private static ConsoleResult HeroGo(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: herogo <x> <y> [sprint] (tile coordinates)";
        bool sprint = args.Count == 3 && string.Equals(args[2], "sprint", StringComparison.OrdinalIgnoreCase);
        if (args.Count is < 2 or > 3 || (args.Count == 3 && !sprint))
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryParseInt(args[0], out int x) || !TryParseInt(args[1], out int y))
        {
            return ConsoleResult.Fail($"Coordinates must be whole numbers. {UsageText}");
        }

        if (!TryGetHero(context, out GameWorld? world, out _, out ConsoleResult failure))
        {
            return failure;
        }

        if (!world.Map.Terrain.Contains(x, y))
        {
            return ConsoleResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Tile {x},{y} is outside the map (0..{world.Size - 1})."));
        }

        PendingCommand pending = context.Simulation.Current.Enqueue(new MoveHeroCommand(x, y, sprint));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"hero ordered to {x},{y}{(sprint ? " sprinting" : string.Empty)} for tick {pending.ExecuteTick} (see 'hero' for the result)"));
    }

    private static ConsoleResult Give(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: give <item> <count>";
        if (args.Count != 2)
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryParseInt(args[1], out int count) || count < 1)
        {
            return ConsoleResult.Fail($"Count must be a whole number of at least 1. {UsageText}");
        }

        if (!TryGetHero(context, out _, out _, out ConsoleResult failure))
        {
            return failure;
        }

        Simulation sim = context.Simulation.Current;
        string item = args[0].ToLowerInvariant();
        if (!sim.Content.TryGet(item, out ItemDef _))
        {
            return ConsoleResult.Fail($"Unknown item '{args[0]}'. Items: {string.Join(", ", sim.Content.Items.Select(i => i.Id))}.");
        }

        PendingCommand pending = sim.Enqueue(new GiveItemCommand(item, count));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture, $"give {count} {item} queued for tick {pending.ExecuteTick}"));
    }

    private static ConsoleResult Build(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (!TryParsePlacement(context, args, "build", out _, out string id, out int x, out int y, out int rotation, out ConsoleResult failure))
        {
            return failure;
        }

        PendingCommand pending = context.Simulation.Current.Enqueue(new PlaceBuildingCommand(id, x, y, rotation));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"build {id} at {x},{y} rot {rotation} queued for tick {pending.ExecuteTick} (see 'buildings' for the result)"));
    }

    private static ConsoleResult Check(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (!TryParsePlacement(context, args, "check", out _, out string id, out int x, out int y, out int rotation, out ConsoleResult failure))
        {
            return failure;
        }

        PlacementResult result = Construction.CheckPlacement(context.Simulation.Current, id, x, y, rotation);
        string tile = result.TileX >= 0 ? string.Create(CultureInfo.InvariantCulture, $" at tile {result.TileX},{result.TileY}") : string.Empty;
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture, $"{id} at {x},{y} rot {rotation}: {Construction.NameOf(result.Reason)}{tile}"));
    }

    private static ConsoleResult MoveBuilding(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: movebuilding <entity> <x> <y> [rot]";
        if (args.Count is < 3 or > 4)
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (!TryGetBuildingArg(context, args[0], out GameWorld? world, out EntityId entity, out ConsoleResult failure))
        {
            return failure;
        }

        int rotation = world.Buildings.Get(entity).Rotation;
        if (!TryParseInt(args[1], out int x) || !TryParseInt(args[2], out int y) || (args.Count == 4 && !TryParseInt(args[3], out rotation)))
        {
            return ConsoleResult.Fail($"Coordinates and rotation must be whole numbers. {UsageText}");
        }

        PendingCommand pending = context.Simulation.Current.Enqueue(new MoveBuildingCommand(entity, x, y, rotation));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"move {entity} to {x},{y} rot {rotation} queued for tick {pending.ExecuteTick} (see 'buildings' for the result)"));
    }

    private static ConsoleResult EntityOrder(DevConsoleContext context, IReadOnlyList<string> args, string name, Func<EntityId, ISimCommand> order)
    {
        if (args.Count != 1)
        {
            return ConsoleResult.Fail($"Usage: {name} <entity>");
        }

        if (!TryGetBuildingArg(context, args[0], out _, out EntityId entity, out ConsoleResult failure))
        {
            return failure;
        }

        PendingCommand pending = context.Simulation.Current.Enqueue(order(entity));
        return ConsoleResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"{name} {entity} queued for tick {pending.ExecuteTick} (see 'buildings' for the result)"));
    }

    private static ConsoleResult BuildingList(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 0)
        {
            return ConsoleResult.Fail("Usage: buildings");
        }

        if (!TryGetWorld(context, out GameWorld? world))
        {
            return NoWorld();
        }

        Simulation sim = context.Simulation.Current;
        CultureInfo inv = CultureInfo.InvariantCulture;
        EraDef era = Construction.CurrentEra(sim);
        List<string> lines =
        [
            Construction.GetTerritory(sim) is { } territory
                ? string.Create(inv, $"territory: center {territory.CenterX},{territory.CenterY} radius {territory.Radius} | era {era.Id} | population {Construction.SettlementPopulation(sim)}")
                : string.Create(inv, $"territory: none (place a center) | era {era.Id} | population {Construction.SettlementPopulation(sim)}"),
        ];

        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            Building b = world.Buildings.Values[i];
            BuildingDef def = Construction.DefOf(sim.Content, b);
            (int width, int height) = Construction.FootprintOf(def, b.Rotation);
            string line = string.Create(inv,
                $"{owners[i]} {def.Id} L{b.Level} {Construction.NameOf(b.State)} at {b.X},{b.Y} rot {b.Rotation} ({width}x{height}) progress {Construction.ProgressPermille(sim.Content, b)}");
            if (b.State != BuildingState.Active)
            {
                line += string.Create(inv, $" ({b.TicksLeft} ticks left)");
            }

            if (b.State == BuildingState.Moving)
            {
                line += string.Create(inv, $" -> {b.TargetX},{b.TargetY} rot {b.TargetRotation}");
            }

            if (world.Inventories.TryGet(owners[i], out Inventory stock))
            {
                string items = string.Join(", ", stock.Backpack.Where(s => !s.IsEmpty).Select(s => string.Create(inv, $"{sim.Content.Get(s.Item).Id} {s.Count}")));
                line += string.Create(inv, $" | stock {stock.UsedStacks}/{stock.Backpack.Count}{(items.Length == 0 ? string.Empty : ": " + items)}");
            }

            lines.Add(line);
        }

        if (owners.Length == 0)
        {
            lines.Add("no buildings");
        }

        if (world.BuildingEvents.Count > 0)
        {
            BuildingEvent last = world.BuildingEvents[^1];
            string lost = last.Lost.Count == 0 ? string.Empty : "; lost " + string.Join(", ", last.Lost.Select(l => string.Create(inv, $"{l.Count} {l.Item}")));
            lines.Add(string.Create(inv, $"last order (tick {last.Tick}): {last.Command} {last.BuildingId} {last.Building} at {last.X},{last.Y}: {Construction.NameOf(last.Result)}{lost}"));
        }

        return ConsoleResult.Ok(lines);
    }

    // build/check arguments: <id> <x> <y> [rot], a known building type, a game.
    private static bool TryParsePlacement(DevConsoleContext context, IReadOnlyList<string> args, string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world, out string id, out int x, out int y, out int rotation, out ConsoleResult failure)
    {
        string usage = $"Usage: {name} <id> <x> <y> [rot]";
        world = null;
        id = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
        x = y = rotation = 0;
        if (args.Count is < 3 or > 4)
        {
            failure = ConsoleResult.Fail(usage);
            return false;
        }

        if (!TryParseInt(args[1], out x) || !TryParseInt(args[2], out y) || (args.Count == 4 && !TryParseInt(args[3], out rotation)))
        {
            failure = ConsoleResult.Fail($"Coordinates and rotation must be whole numbers. {usage}");
            return false;
        }

        if (!TryGetWorld(context, out world))
        {
            failure = NoWorld();
            return false;
        }

        ContentCatalog content = context.Simulation.Current.Content;
        if (!content.TryGet(id, out BuildingDef _))
        {
            failure = ConsoleResult.Fail($"Unknown building '{args[0]}'. Buildings: {string.Join(", ", content.Buildings.Select(b => b.Id))}.");
            world = null;
            return false;
        }

        failure = ConsoleResult.Ok();
        return true;
    }

    // An entity argument naming a live building: "#5v1", "5v1" or just the index "5".
    private static bool TryGetBuildingArg(DevConsoleContext context, string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world,
        out EntityId entity, out ConsoleResult failure)
    {
        entity = EntityId.None;
        if (!TryGetWorld(context, out world))
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
            foreach (EntityId owner in world.Buildings.Entities)
            {
                entity = owner.Index == index ? owner : entity;
            }
        }

        if (entity == EntityId.None || !world.Buildings.Has(entity))
        {
            failure = ConsoleResult.Fail($"'{text}' is not a building. Type 'buildings' for a list.");
            world = null;
            return false;
        }

        failure = ConsoleResult.Ok();
        return true;
    }

    private static bool TryValidName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    private static ConsoleResult Save(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return ConsoleResult.Fail("Usage: save <name>");
        }

        if (!TryValidName(args[0]))
        {
            return ConsoleResult.Fail("Save names use letters, digits, '_' and '-' only (max 64).");
        }

        string path = Path.Combine(context.SaveDirectory, args[0] + SaveExtension);
        try
        {
            SaveGame.WriteFile(context.Simulation.Current, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConsoleResult.Fail($"Cannot write save '{args[0]}': {ex.Message}");
        }

        return ConsoleResult.Ok($"saved '{args[0]}' at tick {context.Simulation.Current.Tick.ToString(CultureInfo.InvariantCulture)}");
    }

    private static ConsoleResult Load(DevConsoleContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return ConsoleResult.Fail("Usage: load <name>");
        }

        if (!TryValidName(args[0]))
        {
            return ConsoleResult.Fail("Save names use letters, digits, '_' and '-' only (max 64).");
        }

        string path = Path.Combine(context.SaveDirectory, args[0] + SaveExtension);
        SaveLoadResult result = SaveGame.ReadFile(path, context.Content, context.Log);
        if (result.Simulation is null)
        {
            return ConsoleResult.Fail($"Cannot load '{args[0]}': {result.Error?.Message ?? "unknown error"}");
        }

        context.Simulation.Current = result.Simulation;
        context.Scheduler.ResetAccumulator();
        return ConsoleResult.Ok($"loaded '{args[0]}' at tick {result.Simulation.Tick.ToString(CultureInfo.InvariantCulture)}");
    }

    private static ConsoleResult Log(DevConsoleContext context, IReadOnlyList<string> args)
    {
        const string UsageText = "Usage: log <trace|debug|info|warn|error> [n]";
        if (args.Count is < 1 or > 2 || !TryParseLevel(args[0], out LogLevel level))
        {
            return ConsoleResult.Fail(UsageText);
        }

        if (args.Count == 1)
        {
            context.Log.MinimumLevel = level;
            return ConsoleResult.Ok($"log level {level}");
        }

        if (!TryParseInt(args[1], out int n) || n < 1 || n > MaxLogLines)
        {
            return ConsoleResult.Fail($"n must be a whole number from 1 to {MaxLogLines.ToString(CultureInfo.InvariantCulture)}. {UsageText}");
        }

        List<LogEvent> events = [.. context.Log.GetEvents().Where(e => e.Level >= level)];
        return ConsoleResult.Ok(events.Skip(Math.Max(0, events.Count - n)).Select(e => e.ToString()));
    }

    private static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text.ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info": level = LogLevel.Info; return true;
            case "warn" or "warning": level = LogLevel.Warning; return true;
            case "error": level = LogLevel.Error; return true;
            default: level = default; return false;
        }
    }

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
