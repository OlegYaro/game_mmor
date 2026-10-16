using Core.Content;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Logging;
using Core.WorldGen;

namespace Core.Sim;

/// <summary>
/// The hero's rules (FR-HERO-01, FR-HERO-02, FR-HERO-04): creating and restoring the hero, validating and planning orders, walking
/// and gathering. Stateless: everything lives in the components, the tile layer and the content.
/// </summary>
/// <remarks>
/// <para>Walking itself (planning, steps, replanning, the search after a stall) is <see cref="AgentMovement"/>, shared
/// with the residents; the hero adds sprinting, stamina and what an arrival or a blocked way means for its activity.</para>
/// <para>Orders are checked when they execute, in a fixed order, and a rejected order only sets
/// <see cref="Hero.LastOrder"/> (and logs): outside the map, unknown in the fog, impassable, unreachable, no node.</para>
/// </remarks>
internal static class HeroRules
{
    /// <summary>Log category of hero events.</summary>
    public const string LogCategory = "hero";

    /// <summary>Node limit of one hero path search (<see cref="AgentMovement.NodeLimit"/>).</summary>
    public static int NodeLimit => AgentMovement.NodeLimit;

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

    /// <summary>Chebyshev distance in tiles (<see cref="AgentMovement.Distance"/>).</summary>
    public static int Distance(int ax, int ay, int bx, int by) => AgentMovement.Distance(ax, ay, bx, by);

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
            if (!AgentMovement.IsValidMover(entry.Value, world.Size))
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

    /// <summary>
    /// One tick of the hero's walking (the movement system calls it for each hero in entity order): it advances along its
    /// path, sprinting while it has stamina, and spends or regains stamina.
    /// </summary>
    public static void Walk(Simulation sim, GameWorld world, HeroBalanceDef balance, EntityId entity, ref Mover mover)
    {
        Hero hero = world.Heroes.Get(entity);
        bool moving = mover.IsMoving && sim.Positions.Has(entity);
        bool sprintOrder = moving && mover.Sprint;
        bool sprinting = sprintOrder && hero.Stamina > 0;
        if (moving)
        {
            WalkOutcome outcome = AgentMovement.Advance(sim, world, entity, ref mover, balance.StepTicks,
                sprinting ? balance.SprintPermille : Mover.ProgressPerTick, LogCategory);
            if (outcome == WalkOutcome.Arrived && hero.Activity == HeroActivity.Walking)
            {
                hero = hero.Stop(HeroStopReason.Arrived);
            }
            else if (outcome == WalkOutcome.Blocked && hero.Activity != HeroActivity.Idle)
            {
                hero = hero.Stop(HeroStopReason.PathBlocked);
            }
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

    // Plans a fresh path for an order (AgentMovement.TryPlan).
    private static HeroOrderResult Plan(Simulation sim, GameWorld world, EntityId entity, int goal, int range, bool sprint, out Mover planned) =>
        AgentMovement.TryPlan(sim, world, entity, goal, range, sprint, out planned) ? HeroOrderResult.Accepted : HeroOrderResult.Unreachable;

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
}
