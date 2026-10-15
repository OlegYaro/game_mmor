using Core.Content;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Logging;
using Core.Pathfinding;
using Core.WorldGen;

namespace Core.Sim;

/// <summary>
/// The hero's rules (FR-HERO-01, FR-HERO-02, FR-HERO-04): creating and restoring the hero, validating and planning orders, walking
/// and gathering. Stateless: everything lives in the components, the tile layer and the content.
/// </summary>
/// <remarks>
/// <para>Paths are planned synchronously with a node limit (<see cref="NodeLimit"/>), so no search queue has to be
/// saved: the planned path is part of the <see cref="Mover"/>. A search that runs out of nodes gives a partial path;
/// at its end the walker plans again from where it stands.</para>
/// <para>Orders are checked when they execute, in a fixed order, and a rejected order only sets
/// <see cref="Hero.LastOrder"/> (and logs): outside the map, unknown in the fog, impassable, unreachable, no node.</para>
/// </remarks>
internal static class HeroRules
{
    /// <summary>Log category of hero events.</summary>
    public const string LogCategory = "hero";

    /// <summary>
    /// Node limit of one hero path search: the per-request default of the path queue (ADR 0003), an engine budget
    /// that keeps a tick within NFR-02 rather than a balance number.
    /// </summary>
    public static int NodeLimit { get; } = new PathfinderConfig().MaxNodesPerRequest;

    /// <summary>The gathering source name (<see cref="GatherDef.Source"/>) of a resource kind, or null when the hero cannot gather it.</summary>
    /// <remarks>
    /// Veins, ore and coal outcrops have no source: they are mined by buildings (M3), and the hero never lowers their
    /// stock, which keeps the generated <see cref="Vein.Stock"/> sums intact.
    /// </remarks>
    public static string? GatherSourceOf(ResourceKind kind) => kind switch
    {
        ResourceKind.Tree => "tree",
        ResourceKind.Stone => "rock",
        ResourceKind.BerryBush => "berry_bush",
        _ => null,
    };

    /// <summary>The gathering rule for <paramref name="kind"/>, or null when the hero cannot gather it.</summary>
    public static GatherDef? FindGatherDef(HeroBalanceDef balance, ResourceKind kind)
    {
        string? source = GatherSourceOf(kind);
        foreach (GatherDef def in balance.Gathering)
        {
            if (string.Equals(def.Source, source, StringComparison.Ordinal))
            {
                return def;
            }
        }

        return null;
    }

    /// <summary>Chebyshev distance in tiles (diagonal steps are allowed, so it is the step count on open ground).</summary>
    public static int Distance(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    /// <summary>Gives <paramref name="entity"/> the hero components: balance defaults, an inventory (with the start items or empty) and an idle mover.</summary>
    public static void AddHero(Simulation sim, EntityId entity, bool withStartItems)
    {
        GameWorld world = sim.World!;
        HeroBalanceDef balance = sim.Content.Hero;
        world.Heroes.Add(entity, Hero.FromBalance(balance));
        Inventory inventory = Inventory.Create(balance);
        if (withStartItems)
        {
            foreach (ItemAmountDef start in balance.StartItems)
            {
                (DefId item, int stackSize) = ItemOf(sim.Content, start.Item);
                inventory = inventory.Add(item, start.Count, stackSize, out int added);
                if (added < start.Count)
                {
                    sim.Log.Write(sim.Tick, LogLevel.Warning, LogCategory, "Start item {item}: only {added} of {count} fit in the backpack",
                        LogField.Of("item", start.Item), LogField.Of("added", added), LogField.Of("count", start.Count));
                }
            }
        }

        world.Inventories.Add(entity, inventory);
        world.Movers.Add(entity, Mover.Idle);
    }

    /// <summary>
    /// Loads the hero components and the tile changes of a saved world, validating them. A world without any hero
    /// component (saves from before M2) promotes its hero revealer entity to the hero with default data.
    /// </summary>
    /// <exception cref="ArgumentException">The saved components or tiles are inconsistent.</exception>
    public static void Restore(Simulation sim, WorldState state)
    {
        GameWorld world = sim.World!;
        int maxTile = world.Size * world.Size;
        foreach (ComponentEntry<Hero> entry in state.Heroes)
        {
            CheckAlive(sim, entry.Entity, "Hero");
            Hero hero = entry.Value;
            bool targetOk = hero.Activity == HeroActivity.Gathering
                ? world.Map.Terrain.Contains(hero.TargetX, hero.TargetY)
                : hero.TargetX == -1 && hero.TargetY == -1;
            if (!Enum.IsDefined(hero.Activity) || !Enum.IsDefined(hero.LastStop) || !Enum.IsDefined(hero.LastOrder)
                || hero.Health < 0 || hero.Stamina < 0 || hero.ActionTicks < 0 || !targetOk)
            {
                throw new ArgumentException($"Hero of {entry.Entity} is invalid: {hero}.", nameof(state));
            }

            world.Heroes.Add(entry.Entity, hero);
        }

        foreach (ComponentEntry<Inventory> entry in state.Inventories)
        {
            CheckAlive(sim, entry.Entity, "Inventory");
            foreach (ItemSlot slot in entry.Value.Backpack.Concat(entry.Value.Equipment))
            {
                bool valid = slot.IsEmpty
                    ? slot.Item == default
                    : slot.Count > 0 && slot.Item.Value <= sim.Content.Count && sim.Content.Get(slot.Item) is ItemDef def && slot.Count <= def.StackSize;
                if (!valid)
                {
                    throw new ArgumentException($"Inventory of {entry.Entity} has an invalid slot {slot}.", nameof(state));
                }
            }

            world.Inventories.Add(entry.Entity, entry.Value);
        }

        foreach (ComponentEntry<Mover> entry in state.Movers)
        {
            CheckAlive(sim, entry.Entity, "Mover");
            if (!IsValidMover(entry.Value, world, maxTile))
            {
                throw new ArgumentException($"Mover of {entry.Entity} is invalid.", nameof(state));
            }

            world.Movers.Add(entry.Entity, entry.Value);
        }

        if (state.Heroes.Count == 0 && state.Inventories.Count == 0 && state.Movers.Count == 0)
        {
            ReadOnlySpan<EntityId> owners = world.Revealers.Entities;
            for (int i = 0; i < owners.Length; i++)
            {
                if (world.Revealers.Values[i].Kind == RevealerKind.Hero && sim.Positions.Has(owners[i]))
                {
                    AddHero(sim, owners[i], withStartItems: false);
                    break;
                }
            }
        }

        world.Tiles.Load(state.Tiles, sim.Entities.Registry);
    }

    /// <summary>Executes <see cref="MoveHeroCommand"/>.</summary>
    public static void OrderMove(Simulation sim, int x, int y, bool sprint)
    {
        if (!TryGetHero(sim, MoveHeroCommand.KindName, out GameWorld? world, out EntityId entity))
        {
            return;
        }

        HeroOrderResult result = CheckTarget(world, x, y);
        if (result == HeroOrderResult.Accepted && !world.Pathfinder.Grid.IsPassable(x, y))
        {
            result = HeroOrderResult.Impassable;
        }

        Mover mover = Mover.Idle;
        if (result == HeroOrderResult.Accepted)
        {
            result = Plan(sim, world, entity, world.Pathfinder.Grid.Index(x, y), 0, sprint, out mover);
        }

        ref Hero hero = ref world.Heroes.Get(entity);
        if (Reject(sim, ref hero, result, MoveHeroCommand.KindName, x, y))
        {
            return;
        }

        world.Movers.Get(entity) = mover;
        hero = mover.IsMoving
            ? hero with { Activity = HeroActivity.Walking, TargetX = -1, TargetY = -1, ActionTicks = 0, LastOrder = HeroOrderResult.Accepted }
            : hero.Stop(HeroStopReason.Arrived) with { LastOrder = HeroOrderResult.Accepted };
        LogOrder(sim, MoveHeroCommand.KindName, x, y, mover);
    }

    /// <summary>Executes <see cref="GatherCommand"/>.</summary>
    public static void OrderGather(Simulation sim, int x, int y)
    {
        if (!TryGetHero(sim, GatherCommand.KindName, out GameWorld? world, out EntityId entity))
        {
            return;
        }

        HeroBalanceDef balance = sim.Content.Hero;
        HeroOrderResult result = CheckTarget(world, x, y);
        if (result == HeroOrderResult.Accepted && FindGatherDef(balance, world.Tiles.GetEffectiveResource(x, y)) is null)
        {
            result = HeroOrderResult.NoResource;
        }

        Mover mover = Mover.Idle;
        if (result == HeroOrderResult.Accepted)
        {
            result = Plan(sim, world, entity, world.Pathfinder.Grid.Index(x, y), balance.InteractionRange, sprint: false, out mover);
        }

        ref Hero hero = ref world.Heroes.Get(entity);
        if (Reject(sim, ref hero, result, GatherCommand.KindName, x, y))
        {
            return;
        }

        world.Movers.Get(entity) = mover;
        hero = hero with { Activity = HeroActivity.Gathering, TargetX = x, TargetY = y, ActionTicks = 0, LastOrder = HeroOrderResult.Accepted };
        LogOrder(sim, GatherCommand.KindName, x, y, mover);
    }

    /// <summary>Executes <see cref="StopHeroCommand"/>: idle at once, finishing the current step.</summary>
    public static void OrderStop(Simulation sim)
    {
        if (!TryGetHero(sim, StopHeroCommand.KindName, out GameWorld? world, out EntityId entity))
        {
            return;
        }

        ref Mover mover = ref world.Movers.Get(entity);
        mover = mover.Halt();
        ref Hero hero = ref world.Heroes.Get(entity);
        hero = hero.Stop(HeroStopReason.Stopped) with { LastOrder = HeroOrderResult.Accepted };
        sim.Log.Write(sim.Tick, LogLevel.Debug, LogCategory, "Hero stopped");
    }

    /// <summary>Executes <see cref="GiveItemCommand"/>: adds what fits, logs the rest.</summary>
    public static void GiveItem(Simulation sim, string item, int count)
    {
        if (!TryGetHero(sim, GiveItemCommand.KindName, out GameWorld? world, out EntityId entity))
        {
            return;
        }

        if (count <= 0 || !sim.Content.TryGet(item, out ItemDef _))
        {
            sim.Log.Write(sim.Tick, LogLevel.Warning, LogCategory, "give ignored: {count} of unknown or invalid item {item}",
                LogField.Of("count", count), LogField.Of("item", item));
            return;
        }

        (DefId id, int stackSize) = ItemOf(sim.Content, item);
        ref Inventory inventory = ref world.Inventories.Get(entity);
        inventory = inventory.Add(id, count, stackSize, out int added);
        sim.Log.Write(sim.Tick, added == count ? LogLevel.Info : LogLevel.Warning, LogCategory, "Gave {added} of {count} {item}",
            LogField.Of("added", added), LogField.Of("count", count), LogField.Of("item", item));
    }

    /// <summary>Movement system body: every walker advances along its path; heroes spend or regain stamina.</summary>
    public static void UpdateMovement(Simulation sim)
    {
        GameWorld? world = sim.World;
        if (world is null)
        {
            return;
        }

        HeroBalanceDef balance = sim.Content.Hero;
        ReadOnlySpan<EntityId> owners = world.Movers.Entities;
        Span<Mover> movers = world.Movers.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            EntityId entity = owners[i];
            bool hasHero = world.Heroes.TryGet(entity, out Hero hero);
            bool moving = movers[i].IsMoving && sim.Positions.Has(entity);
            bool sprintOrder = moving && movers[i].Sprint;
            bool sprinting = sprintOrder && hasHero && hero.Stamina > 0;
            if (moving)
            {
                Advance(sim, world, entity, ref movers[i], ref hero, hasHero, sprinting ? balance.SprintPermille : Mover.ProgressPerTick);
            }

            if (!hasHero)
            {
                continue;
            }

            // Sprinting spends stamina; walking with the sprint order on (exhausted) neither spends nor regains it.
            if (sprinting)
            {
                hero = hero with { Stamina = Math.Max(0, hero.Stamina - balance.SprintStaminaDrain) };
            }
            else if (!sprintOrder && hero.Stamina < balance.Stamina)
            {
                hero = hero with { Stamina = Math.Min(balance.Stamina, hero.Stamina + balance.StaminaRegen) };
            }

            world.Heroes.Get(entity) = hero;
        }
    }

    /// <summary>Gathering system body: heroes next to their node work on it and fill the backpack.</summary>
    public static void UpdateGathering(Simulation sim)
    {
        GameWorld? world = sim.World;
        if (world is null)
        {
            return;
        }

        HeroBalanceDef balance = sim.Content.Hero;
        ReadOnlySpan<EntityId> owners = world.Heroes.Entities;
        Span<Hero> heroes = world.Heroes.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (heroes[i].Activity == HeroActivity.Gathering)
            {
                Gather(sim, world, balance, owners[i], ref heroes[i]);
            }
        }
    }

    private static void Gather(Simulation sim, GameWorld world, HeroBalanceDef balance, EntityId entity, ref Hero hero)
    {
        int x = hero.TargetX;
        int y = hero.TargetY;
        if (!world.Movers.TryGet(entity, out Mover mover) || !world.Inventories.Has(entity) || !sim.Positions.TryGet(entity, out Position position))
        {
            hero = hero.Stop(HeroStopReason.NoResource);
            return;
        }

        GatherDef? def = FindGatherDef(balance, world.Tiles.GetEffectiveResource(x, y));
        if (def is null)
        {
            hero = hero.Stop(world.Tiles.IsResourceChanged(x, y) ? HeroStopReason.Depleted : HeroStopReason.NoResource);
            world.Movers.Get(entity) = mover.Halt();
            return;
        }

        if (mover.IsMoving)
        {
            return; // still walking up to the node
        }

        if (Distance(position.TileX, position.TileY, x, y) > balance.InteractionRange)
        {
            hero = hero.Stop(HeroStopReason.PathBlocked);
            return;
        }

        (DefId item, int stackSize) = ItemOf(sim.Content, def.Item);
        ref Inventory inventory = ref world.Inventories.Get(entity);
        int space = inventory.SpaceFor(item, stackSize);
        if (space == 0)
        {
            hero = hero.Stop(HeroStopReason.BackpackFull);
            return;
        }

        if (hero.ActionTicks + 1 < def.TicksPerAction)
        {
            hero = hero with { ActionTicks = hero.ActionTicks + 1 };
            return;
        }

        int taken = world.Tiles.TakeResource(x, y, Math.Min(def.YieldPerAction, space));
        inventory = inventory.Add(item, taken, stackSize, out _);
        hero = hero with { ActionTicks = 0 };
        if (sim.Log.IsEnabled(LogLevel.Debug))
        {
            sim.Log.Write(sim.Tick, LogLevel.Debug, LogCategory, "Gathered {count} {item} at {x},{y}",
                LogField.Of("count", taken), LogField.Of("item", def.Item), LogField.Of("x", x), LogField.Of("y", y));
        }

        if (world.Tiles.GetEffectiveAmount(x, y) == 0)
        {
            hero = hero.Stop(HeroStopReason.Depleted);
        }
        else if (inventory.SpaceFor(item, stackSize) == 0)
        {
            hero = hero.Stop(HeroStopReason.BackpackFull);
        }
    }

    // Moves one walker by `rate` progress units along its path (see Mover for the units).
    private static void Advance(Simulation sim, GameWorld world, EntityId entity, ref Mover mover, ref Hero hero, bool hasHero, int rate)
    {
        PassabilityGrid grid = world.Pathfinder.Grid;
        int stepTicks = sim.Content.Hero.StepTicks;
        IReadOnlyList<int> path = mover.Path;
        int index = mover.StepIndex;
        int progress = mover.StepProgress + rate;
        bool stepStarts = mover.StepProgress == 0;
        while (index < path.Count - 1 && !InRange(grid, path[index], mover))
        {
            int from = path[index];
            int to = path[index + 1];
            if (stepStarts && !CanStep(grid, from, to))
            {
                if (!TryReplan(world, from, mover, out path))
                {
                    Halt(sim, entity, ref mover, ref hero, hasHero, from, HeroStopReason.PathBlocked);
                    return;
                }

                index = 0;
                continue;
            }

            int cost = Mover.StepCost(stepTicks, grid.XOf(from) != grid.XOf(to) && grid.YOf(from) != grid.YOf(to));
            if (progress < cost)
            {
                mover = mover with { Path = path, StepIndex = index, StepProgress = progress };
                sim.Positions.Get(entity) = Lerp(grid, from, to, progress, cost);
                return;
            }

            progress -= cost;
            index++;
            stepStarts = true;
        }

        // End of the path (or within range of the goal): leftover progress is dropped.
        int tile = path[index];
        sim.Positions.Get(entity) = Center(grid, tile);
        if (InRange(grid, tile, mover) || (mover.GoalRange == 0 && tile == mover.Goal))
        {
            mover = Mover.Idle;
            if (hasHero && hero.Activity == HeroActivity.Walking)
            {
                hero = hero.Stop(HeroStopReason.Arrived);
            }

            return;
        }

        // A partial path ran out before the goal: plan again from here.
        if (TryReplan(world, tile, mover, out IReadOnlyList<int> next))
        {
            mover = mover with { Path = next, StepIndex = 0, StepProgress = 0 };
            return;
        }

        Halt(sim, entity, ref mover, ref hero, hasHero, tile, HeroStopReason.PathBlocked);
    }

    private static void Halt(Simulation sim, EntityId entity, ref Mover mover, ref Hero hero, bool hasHero, int tile, HeroStopReason reason)
    {
        mover = Mover.Idle;
        sim.Positions.Get(entity) = Center(sim.World!.Pathfinder.Grid, tile);
        if (hasHero && hero.Activity != HeroActivity.Idle)
        {
            hero = hero.Stop(reason);
        }

        sim.Log.Write(sim.Tick, LogLevel.Info, LogCategory, "{entity} stopped: the way is blocked", LogField.Of("entity", entity.ToString()));
    }

    // True when a ranged mover (gathering) standing on `tile` is close enough to its goal.
    private static bool InRange(PassabilityGrid grid, int tile, Mover mover) =>
        mover.GoalRange > 0 && Distance(grid.XOf(tile), grid.YOf(tile), grid.XOf(mover.Goal), grid.YOf(mover.Goal)) <= mover.GoalRange;

    private static bool CanStep(PassabilityGrid grid, int from, int to)
    {
        int x = grid.XOf(from);
        int y = grid.YOf(from);
        int dx = grid.XOf(to) - x;
        int dy = grid.YOf(to) - y;
        for (int d = 0; d < PassabilityGrid.DirectionCount; d++)
        {
            if (PassabilityGrid.DirectionX(d) == dx && PassabilityGrid.DirectionY(d) == dy)
            {
                return grid.CanStep(x, y, d);
            }
        }

        return false;
    }

    // A new path from `from` towards the mover's goal; false when there is none or it does not leave `from`.
    private static bool TryReplan(GameWorld world, int from, Mover mover, out IReadOnlyList<int> path)
    {
        var tiles = new List<int>();
        PathResult result = mover.GoalRange == 0
            ? world.Pathfinder.FindPath(from, mover.Goal, tiles, NodeLimit)
            : world.Pathfinder.FindPathToAdjacent(from, mover.Goal, tiles, NodeLimit);
        path = [.. tiles];
        return result.Length > 1;
    }

    // Plans a fresh path for an order. Mid-step, the walker first finishes its step: the new path starts at the step's
    // end and the current step is kept in front of it.
    private static HeroOrderResult Plan(Simulation sim, GameWorld world, EntityId entity, int goal, int range, bool sprint, out Mover planned)
    {
        Pathfinder pathfinder = world.Pathfinder;
        PassabilityGrid grid = pathfinder.Grid;
        Mover current = world.Movers.Get(entity);
        bool midStep = current.StepProgress > 0 && current.NextTile >= 0;
        int start;
        if (midStep)
        {
            start = current.NextTile;
        }
        else
        {
            Position position = sim.Positions.Get(entity);
            if (!grid.Contains(position.TileX, position.TileY))
            {
                planned = Mover.Idle;
                return HeroOrderResult.Unreachable;
            }

            start = grid.Index(position.TileX, position.TileY);
            if (range > 0 && Distance(position.TileX, position.TileY, grid.XOf(goal), grid.YOf(goal)) <= range)
            {
                planned = Mover.Idle;
                return HeroOrderResult.Accepted; // already close enough
            }
        }

        var path = new List<int>();
        PathResult result = range == 0
            ? pathfinder.FindPath(start, goal, path, NodeLimit)
            : pathfinder.FindPathToAdjacent(start, goal, path, NodeLimit);
        if (!result.HasPath || (result.Status == PathStatus.Partial && result.Length <= 1))
        {
            planned = Mover.Idle;
            return HeroOrderResult.Unreachable;
        }

        if (midStep)
        {
            planned = new Mover(goal, range, [current.CurrentTile, .. path], 0, current.StepProgress, sprint);
        }
        else
        {
            planned = path.Count > 1 ? new Mover(goal, range, [.. path], 0, 0, sprint) : Mover.Idle;
        }

        return HeroOrderResult.Accepted;
    }

    private static HeroOrderResult CheckTarget(GameWorld world, int x, int y)
    {
        if (!world.Map.Terrain.Contains(x, y))
        {
            return HeroOrderResult.OutsideMap;
        }

        return world.Fog.GetState(x, y) == FogState.Unknown ? HeroOrderResult.Unexplored : HeroOrderResult.Accepted;
    }

    private static bool Reject(Simulation sim, ref Hero hero, HeroOrderResult result, string kind, int x, int y)
    {
        if (result == HeroOrderResult.Accepted)
        {
            return false;
        }

        hero = hero with { LastOrder = result };
        sim.Log.Write(sim.Tick, LogLevel.Info, LogCategory, "{kind} to {x},{y} rejected: {reason}",
            LogField.Of("kind", kind), LogField.Of("x", x), LogField.Of("y", y), LogField.Of("reason", result.ToString()));
        return true;
    }

    private static void LogOrder(Simulation sim, string kind, int x, int y, Mover mover)
    {
        if (sim.Log.IsEnabled(LogLevel.Debug))
        {
            sim.Log.Write(sim.Tick, LogLevel.Debug, LogCategory, "{kind} to {x},{y}: path of {steps} steps",
                LogField.Of("kind", kind), LogField.Of("x", x), LogField.Of("y", y), LogField.Of("steps", Math.Max(0, mover.Path.Count - 1)));
        }
    }

    private static bool TryGetHero(Simulation sim, string kind, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world, out EntityId hero)
    {
        world = sim.World;
        hero = world?.HeroEntity ?? EntityId.None;
        if (world is not null && hero != EntityId.None && sim.Positions.Has(hero) && world.Inventories.Has(hero) && world.Movers.Has(hero))
        {
            return true;
        }

        sim.Log.Write(sim.Tick, LogLevel.Warning, LogCategory, "{kind} ignored: there is no hero", LogField.Of("kind", kind));
        return false;
    }

    private static (DefId Item, int StackSize) ItemOf(ContentCatalog content, string item) =>
        content.TryGetId(item, out DefId id) && content.Get(id) is ItemDef def
            ? (id, def.StackSize)
            : throw new InvalidOperationException($"Item '{item}' is not in the content.");

    private static void CheckAlive(Simulation sim, EntityId entity, string what)
    {
        if (!sim.Entities.IsAlive(entity))
        {
            throw new ArgumentException($"{what} belongs to {entity}, which is not alive.", nameof(entity));
        }
    }

    private static bool IsValidMover(Mover mover, GameWorld world, int maxTile)
    {
        IReadOnlyList<int> path = mover.Path;
        if (path.Count == 0)
        {
            return mover == Mover.Idle;
        }

        if (path.Count < 2 || mover.Goal < 0 || mover.Goal >= maxTile || mover.GoalRange < 0 || mover.StepIndex < 0
            || mover.StepIndex >= path.Count - 1 || mover.StepProgress < 0)
        {
            return false;
        }

        for (int i = 0; i < path.Count; i++)
        {
            if (path[i] < 0 || path[i] >= maxTile
                || (i > 0 && Distance(path[i] % world.Size, path[i] / world.Size, path[i - 1] % world.Size, path[i - 1] / world.Size) != 1))
            {
                return false;
            }
        }

        return true;
    }

    private static Position Center(PassabilityGrid grid, int tile) => Simulation.TileCenter(grid.XOf(tile), grid.YOf(tile));

    private static Position Lerp(PassabilityGrid grid, int from, int to, int progress, int cost)
    {
        Position a = Center(grid, from);
        Position b = Center(grid, to);
        return new Position(
            a.X + (int)((long)(b.X - a.X) * progress / cost),
            a.Y + (int)((long)(b.Y - a.Y) * progress / cost));
    }
}
