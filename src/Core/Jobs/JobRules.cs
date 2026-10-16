using Core.Buildings;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Heroes;
using Core.Logging;
using Core.Pathfinding;
using Core.Residents;
using Core.Sim;

namespace Core.Jobs;

/// <summary>
/// The job board's rules that change the state (FR-NPC-06, FR-NPC-07, ADR 0009): residents look for jobs, reserve the
/// targets, walk, work, carry loads to storages; the work orders; loading saved jobs. Stateless: everything lives in the
/// <see cref="ResidentJob"/> components, the <see cref="ReservationManager"/>, the movers, the tiles and the storages.
/// Read-only rules are in <see cref="JobBoard"/>.
/// </summary>
/// <remarks>
/// <para>The job system runs once per tick after the movement. Residents are handled in entity order: a job is checked
/// (target gone: the job ends) and moved on (arrived, a work action or cycle done, the load handed in); an idle resident
/// whose <see cref="ResidentJob.NextThinkTick"/> has come looks for a job while the tick's search budget lasts
/// (<see cref="JobSearch.TilesPerTick"/>), and one that finds none walks to a free tile next to the center.</para>
/// <para>Reservations end only in <see cref="End"/> (done or cancelled). A resident that cannot reach a target bans it
/// for <see cref="EconomyBalanceDef.JobRetryCooldownTicks"/>.</para>
/// <para>Idle residents spread out around the center (decision of wave 2b): an idle resident stays on its tile when the
/// tile lies within the parking rings around the center's footprint (as many rings as the population needs, one tile per
/// resident) and no resident with a lower id stands still on it; otherwise it walks to the first free tile of those
/// rings, ring by ring, row by row. So residents never stack on the meeting tiles, and a resident that ends work far
/// away walks back. A placement over a standing resident is still refused with <c>unit_in_the_way</c>; the player sees
/// who stands where, and a resident leaves the spot as soon as it gets work.</para>
/// </remarks>
internal static class JobRules
{
    /// <summary>Log category of job events.</summary>
    public const string LogCategory = "jobs";

    /// <summary>A resident works from a tile next to its target (Chebyshev distance 1): geometry, not balance.</summary>
    public const int WorkRange = 1;

    /// <summary>Job system body: every resident's job is moved on, idle residents look for work (see the class remarks).</summary>
    public static void Update(Simulation sim)
    {
        if (sim.World is not { } world)
        {
            return;
        }

        world.Reservations.Prune(sim.Tick, sim.Entities.Registry);
        int checkedTiles = 0;
        ReadOnlySpan<EntityId> owners = world.Residents.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            EntityId entity = owners[i];
            ref Resident resident = ref world.Residents.Get(entity);
            if (resident.State is ResidentState.Arriving or ResidentState.Leaving)
            {
                continue;
            }

            ref ResidentJob job = ref world.Jobs.Get(entity);
            if (job.Kind != JobKind.None)
            {
                Advance(sim, world, entity, resident, ref job);
            }

            if (job.Kind == JobKind.None && sim.Tick >= job.NextThinkTick && checkedTiles < JobSearch.TilesPerTick)
            {
                Think(sim, world, entity, resident, ref job, ref checkedTiles);
            }

            ResidentState state = job.Phase == JobPhase.Working ? ResidentState.Working
                : world.Movers.Get(entity).IsMoving ? ResidentState.Walking
                : ResidentState.Idle;
            if (resident.State != state)
            {
                resident = resident with { State = state };
            }
        }
    }

    /// <summary>Executes <see cref="SetWorkPriorityCommand"/>: a priority of 0 also ends a running job of that type.</summary>
    public static void OrderSetPriority(Simulation sim, EntityId entity, string jobType, int priority)
    {
        if (!TryGetResident(sim, SetWorkPriorityCommand.KindName, entity, out GameWorld? world))
        {
            return;
        }

        if (!JobKinds.TryParse(jobType, out JobKind kind) || priority < 0 || priority > WorkPriorities.Max)
        {
            Log(sim, LogLevel.Warning, $"{SetWorkPriorityCommand.KindName} ignored: '{jobType}' {priority} is not a job type with a priority 0..{WorkPriorities.Max}");
            return;
        }

        ref Resident resident = ref world.Residents.Get(entity);
        resident = resident with { Priorities = resident.Priorities.With(kind, priority) };
        ref ResidentJob job = ref world.Jobs.Get(entity);
        if (priority == 0 && job.Kind == kind)
        {
            End(sim, world, entity, ref job, "its priority is now 0");
        }

        Log(sim, LogLevel.Info, $"{entity} priority of {JobKinds.NameOf(kind)}: {priority}");
    }

    /// <summary>
    /// Executes <see cref="SetProfessionCommand"/>: an ordinary profession only; the priorities become the new
    /// profession's defaults and a running job ends (the load stays with the resident).
    /// </summary>
    public static void OrderSetProfession(Simulation sim, EntityId entity, string profession)
    {
        if (!TryGetResident(sim, SetProfessionCommand.KindName, entity, out GameWorld? world))
        {
            return;
        }

        if (!sim.Content.TryGet(profession, out ProfessionDef def) || !string.Equals(def.Kind, "ordinary", StringComparison.Ordinal))
        {
            Log(sim, LogLevel.Warning, $"{SetProfessionCommand.KindName} ignored: '{profession}' is not an ordinary profession");
            return;
        }

        sim.Content.TryGetId(profession, out DefId id);
        ref Resident resident = ref world.Residents.Get(entity);
        resident = resident with { Profession = id, Priorities = WorkPriorities.ForProfession(def) };
        ref ResidentJob job = ref world.Jobs.Get(entity);
        if (job.Kind != JobKind.None)
        {
            End(sim, world, entity, ref job, "its profession changed");
        }

        Log(sim, LogLevel.Info, $"{entity} is now a {profession}");
    }

    /// <summary>
    /// Ends the job of a resident that leaves the settlement and takes its load from it: the load goes into the storages
    /// that accept it; what does not fit is lost (returned for the log).
    /// </summary>
    public static int CancelForLeaving(Simulation sim, GameWorld world, EntityId entity)
    {
        ref ResidentJob job = ref world.Jobs.Get(entity);
        if (job.Kind != JobKind.None)
        {
            End(sim, world, entity, ref job, "it leaves the settlement");
        }

        int lost = 0;
        if (job.IsCarrying)
        {
            lost = job.CarryCount - Storages.DeliverToStorages(sim, job.CarryItem, job.CarryCount);
        }

        job = ResidentJob.Idle(sim.Tick);
        return lost;
    }

    /// <summary>Loads the saved jobs and reservations after the residents and the work outputs, validating them.</summary>
    /// <exception cref="ArgumentException">A job, a reservation or a ban is inconsistent.</exception>
    public static void Restore(Simulation sim, WorldState state)
    {
        GameWorld world = sim.World!;
        foreach (ComponentEntry<ResidentJob> entry in state.Jobs)
        {
            if (!sim.Entities.IsAlive(entry.Entity) || !world.Residents.TryGet(entry.Entity, out Resident resident))
            {
                throw new ArgumentException($"Job belongs to {entry.Entity}, which is not a living resident.", nameof(state));
            }

            if (!IsValid(sim, world, entry.Value, resident))
            {
                throw new ArgumentException($"Job of {entry.Entity} is invalid: {entry.Value}.", nameof(state));
            }

            world.Jobs.Add(entry.Entity, entry.Value);
        }

        foreach (EntityId resident in world.Residents.Entities)
        {
            if (!world.Jobs.Has(resident))
            {
                throw new ArgumentException($"Resident {resident} has no job component.", nameof(state));
            }
        }

        world.Reservations.Load(state.Reservations);
        foreach (Reservation reservation in world.Reservations.Reservations)
        {
            if (!world.Jobs.TryGet(reservation.Holder, out ResidentJob job) || !Requires(sim, job, reservation))
            {
                throw new ArgumentException($"Reservation {reservation} does not belong to its holder's job.", nameof(state));
            }
        }

        foreach (ComponentEntry<ResidentJob> entry in state.Jobs)
        {
            foreach (ReservationTarget target in RequiredTargets(entry.Value))
            {
                if (!world.Reservations.HeldBy(entry.Entity).Any(r => r.Target == target))
                {
                    throw new ArgumentException($"Job of {entry.Entity} lacks its reservation of {target}.", nameof(state));
                }
            }
        }

        foreach (JobBan ban in world.Reservations.Bans)
        {
            if (!world.Residents.Has(ban.Holder) || ban.UntilTick <= sim.Tick)
            {
                throw new ArgumentException($"Ban {ban} belongs to no resident or has ended.", nameof(state));
            }
        }
    }

    // A running job: target check, then the step of its phase.
    private static void Advance(Simulation sim, GameWorld world, EntityId entity, Resident resident, ref ResidentJob job)
    {
        if (!IsTargetValid(sim, world, job))
        {
            End(sim, world, entity, ref job, "its target is gone");
            return;
        }

        bool walking = world.Movers.Get(entity).IsMoving;
        switch (job.Phase)
        {
            case JobPhase.ToTarget when !walking:
                if (IsNear(sim, world, entity, job.GoalTile))
                {
                    Arrive(sim, world, entity, ref job);
                }
                else
                {
                    Fail(sim, world, entity, ref job, TargetOf(job));
                }

                break;
            case JobPhase.Working:
                Work(sim, world, entity, resident, ref job);
                break;
            case JobPhase.ToStorage when !walking:
                if (IsNear(sim, world, entity, job.GoalTile))
                {
                    HandIn(sim, world, entity, ref job);
                }
                else
                {
                    Fail(sim, world, entity, ref job, ReservationTarget.OfBuilding(ReservationLayer.Storage, job.Storage));
                }

                break;
        }
    }

    // Next to the target: gathering and farming start to work, a carrier takes the output and heads for the storage.
    private static void Arrive(Simulation sim, GameWorld world, EntityId entity, ref ResidentJob job)
    {
        switch (job.Kind)
        {
            case JobKind.Farm:
                job = job with { Phase = JobPhase.Working, WorkLeft = WorkOf(sim, world, job.Building).CycleTicks * JobBoard.BaseSpeedPermille };
                return;
            case JobKind.Haul:
                WorkDef work = WorkOf(sim, world, job.Building);
                int reserved = HeldAmount(world, entity, ReservationTarget.OfBuilding(ReservationLayer.Output, job.Building));
                ref WorkOutput output = ref world.WorkOutputs.Get(job.Building);
                int take = Math.Min(reserved, output.Count);
                output = new WorkOutput(output.Count - take);
                job = job with { CarryItem = ItemId(sim.Content, work.Item), CarryCount = take };
                if (take == 0)
                {
                    End(sim, world, entity, ref job, "the output was empty");
                }
                else if (!Storages.Accepts(sim, job.Storage, job.CarryItem))
                {
                    End(sim, world, entity, ref job, "the storage is gone; the load is hauled elsewhere");
                }
                else if (!TryPlanTo(sim, world, entity, job.Storage, out int goal))
                {
                    Fail(sim, world, entity, ref job, ReservationTarget.OfBuilding(ReservationLayer.Storage, job.Storage));
                }
                else
                {
                    job = job with { Phase = JobPhase.ToStorage, GoalTile = goal };
                }

                return;
            default:
                job = job with { Phase = JobPhase.Working, WorkLeft = GatherDefOf(sim, job.Kind).TicksPerAction * JobBoard.BaseSpeedPermille };
                return;
        }
    }

    // One tick of work at the resident's speed; at the end of an action or cycle its yield.
    private static void Work(Simulation sim, GameWorld world, EntityId entity, Resident resident, ref ResidentJob job)
    {
        int left = job.WorkLeft - JobBoard.WorkSpeedPermille(sim, resident, job.Kind);
        if (left > 0)
        {
            job = job with { WorkLeft = left };
            return;
        }

        if (job.Kind == JobKind.Farm)
        {
            WorkDef work = WorkOf(sim, world, job.Building);
            int stackSize = sim.Content.Get<ItemDef>(ItemId(sim.Content, work.Item)).StackSize;
            ref WorkOutput output = ref world.WorkOutputs.Get(job.Building);
            output = new WorkOutput(output.Count + Math.Min(work.Yield, stackSize - output.Count));
            End(sim, world, entity, ref job, "the cycle is done");
            return;
        }

        GatherDef def = GatherDefOf(sim, job.Kind);
        int capacity = sim.Content.Economy.HaulCapacity;
        PassabilityGrid grid = world.Pathfinder.Grid;
        int x = grid.XOf(job.TargetTile);
        int y = grid.YOf(job.TargetTile);
        int taken = world.Tiles.TakeResource(x, y, Math.Min(def.YieldPerAction, capacity - job.CarryCount));
        job = job with { CarryItem = ItemId(sim.Content, def.Item), CarryCount = job.CarryCount + taken, WorkLeft = 0 };
        if (taken > 0 && job.CarryCount < capacity && world.Tiles.GetEffectiveAmount(x, y) > 0)
        {
            job = job with { WorkLeft = def.TicksPerAction * JobBoard.BaseSpeedPermille };
        }
        else if (!TryHeadToStorage(sim, world, entity, ref job))
        {
            End(sim, world, entity, ref job, "no storage has room for the load");
        }
    }

    // At the storage: what fits goes in; a rest stays with the resident and is hauled again.
    private static void HandIn(Simulation sim, GameWorld world, EntityId entity, ref ResidentJob job)
    {
        int added = Storages.Deposit(sim, job.Storage, job.CarryItem, job.CarryCount);
        int left = job.CarryCount - added;
        job = left == 0 ? job with { CarryItem = default, CarryCount = 0 } : job with { CarryCount = left };
        End(sim, world, entity, ref job, left == 0 ? "the load is stored" : "the storage was full");
    }

    // An idle resident looks for work: its own load first, then the job types tier by tier.
    private static void Think(Simulation sim, GameWorld world, EntityId entity, Resident resident, ref ResidentJob job, ref int checkedTiles)
    {
        bool started;
        if (job.IsCarrying)
        {
            started = TryHeadToStorage(sim, world, entity, ref job);
            if (started)
            {
                job = job with { Kind = JobKind.Haul };
            }
        }
        else
        {
            started = TryTakeJob(sim, world, entity, resident, ref job, ref checkedTiles);
        }

        if (started)
        {
            if (sim.Log.IsEnabled(LogLevel.Debug))
            {
                Log(sim, LogLevel.Debug, $"{entity} takes {JobKinds.NameOf(job.Kind)}: {DescribeTarget(job)}");
            }

            return;
        }

        job = job with { NextThinkTick = sim.Tick + sim.Content.Economy.ThinkIntervalTicks };
        Park(sim, world, entity);
    }

    private static bool TryTakeJob(Simulation sim, GameWorld world, EntityId entity, Resident resident, ref ResidentJob job, ref int checkedTiles)
    {
        Position position = sim.Positions.Get(entity);
        PassabilityGrid grid = world.Pathfinder.Grid;
        if (!grid.Contains(position.TileX, position.TileY))
        {
            return false;
        }

        int component = world.Pathfinder.Connectivity.GetComponent(grid.Index(position.TileX, position.TileY));
        if (component == ConnectivityMap.NoComponent)
        {
            return false;
        }

        var at = new Spot(position.TileX, position.TileY, component);
        for (int tier = 1; tier <= WorkPriorities.Max; tier++)
        {
            foreach (JobKind kind in JobKinds.TierOrder)
            {
                if (resident.Priorities.Get(kind) != tier)
                {
                    continue;
                }

                bool started = kind switch
                {
                    JobKind.Farm => TryStartFarm(sim, world, entity, resident, at, ref job),
                    JobKind.Haul => TryStartHaul(sim, world, entity, resident, at, ref job),
                    JobKind.Chop or JobKind.Quarry or JobKind.Forage => TryStartGather(sim, world, entity, kind, at, ref job, ref checkedTiles),

                    // Planting saplings arrives with tree regrowth (the next wave): the board offers no such job yet.
                    _ => false,
                };
                if (started)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryStartGather(Simulation sim, GameWorld world, EntityId entity, JobKind kind, Spot at, ref ResidentJob job, ref int checkedTiles)
    {
        if (Construction.GetTerritory(sim) is not { } territory)
        {
            return false;
        }

        int tile = JobSearch.FindNode(sim, world, entity, JobKinds.ResourceOf(kind), at.X, at.Y, territory, sim.Content.Economy.JobSearchMarginTiles,
            at.Component, ref checkedTiles);
        if (tile < 0)
        {
            return false;
        }

        ReservationTarget target = ReservationTarget.OfTile(tile);
        if (!AgentMovement.TryPlan(sim, world, entity, tile, WorkRange, sprint: false, out Mover mover))
        {
            world.Reservations.Ban(entity, target, sim.Tick + sim.Content.Economy.JobRetryCooldownTicks);
            return false;
        }

        if (!world.Reservations.TryReserveAll(entity, [new ReservationRequest(target, default, 1, 1)]))
        {
            return false;
        }

        world.Movers.Get(entity) = mover;
        job = job with { Kind = kind, Phase = JobPhase.ToTarget, Building = EntityId.None, TargetTile = tile, Storage = EntityId.None, GoalTile = tile, WorkLeft = 0 };
        return true;
    }

    private static bool TryStartFarm(Simulation sim, GameWorld world, EntityId entity, Resident resident, Spot at, ref ResidentJob job)
    {
        EntityId best = EntityId.None;
        int bestGoal = -1;
        long bestWeight = long.MaxValue;
        int slots = 0;
        ReadOnlySpan<EntityId> owners = world.WorkOutputs.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            if (!TryGetWorkplace(sim, world, owners[i], JobKind.Farm, out Building building, out WorkDef? work))
            {
                continue;
            }

            ReservationTarget target = ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, owners[i]);
            int holders = world.Reservations.HolderCount(target);
            int stackSize = sim.Content.Get<ItemDef>(ItemId(sim.Content, work.Item)).StackSize;
            if (holders >= work.Slots || world.WorkOutputs.Values[i].Count + ((long)work.Yield * (holders + 1)) > stackSize
                || world.Reservations.IsBanned(entity, target, sim.Tick))
            {
                continue;
            }

            int goal = JobSearch.ApproachTile(sim.Content, world, building, at.X, at.Y, at.Component);
            long weight = goal < 0 ? long.MaxValue : JobBoard.Weight(sim, resident, JobKind.Farm, at.DistanceTo(world, goal), work.CycleTicks);
            if (weight < bestWeight)
            {
                (best, bestGoal, bestWeight, slots) = (owners[i], goal, weight, work.Slots);
            }
        }

        if (best == EntityId.None)
        {
            return false;
        }

        ReservationTarget slot = ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, best);
        if (!TryPlanAndReserve(sim, world, entity, bestGoal, slot, [new ReservationRequest(slot, default, 1, slots)]))
        {
            return false;
        }

        job = job with { Kind = JobKind.Farm, Phase = JobPhase.ToTarget, Building = best, TargetTile = -1, Storage = EntityId.None, GoalTile = bestGoal, WorkLeft = 0 };
        return true;
    }

    private static bool TryStartHaul(Simulation sim, GameWorld world, EntityId entity, Resident resident, Spot at, ref ResidentJob job)
    {
        EntityId best = EntityId.None;
        int bestGoal = -1;
        long bestWeight = long.MaxValue;
        int available = 0;
        int stock = 0;
        DefId item = default;
        ReadOnlySpan<EntityId> owners = world.WorkOutputs.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            Building building = world.Buildings.Get(owners[i]);
            WorkDef? work = Construction.LevelOf(sim.Content, building).Work;
            ReservationTarget target = ReservationTarget.OfBuilding(ReservationLayer.Output, owners[i]);
            int count = world.WorkOutputs.Values[i].Count;
            int free = count - world.Reservations.AmountReserved(target);
            if (work is null || building.State != BuildingState.Active || free <= 0 || world.Reservations.IsBanned(entity, target, sim.Tick))
            {
                continue;
            }

            int goal = JobSearch.ApproachTile(sim.Content, world, building, at.X, at.Y, at.Component);
            long weight = goal < 0 ? long.MaxValue : JobBoard.Weight(sim, resident, JobKind.Haul, at.DistanceTo(world, goal), 0);
            if (weight < bestWeight)
            {
                (best, bestGoal, bestWeight, available, stock, item) = (owners[i], goal, weight, free, count, ItemId(sim.Content, work.Item));
            }
        }

        if (best == EntityId.None)
        {
            return false;
        }

        PassabilityGrid grid = world.Pathfinder.Grid;
        EntityId storage = Storages.FindNearest(sim, grid.XOf(bestGoal), grid.YOf(bestGoal), item, entity, out int room);
        if (storage == EntityId.None)
        {
            return false;
        }

        int amount = Math.Min(Math.Min(available, sim.Content.Economy.HaulCapacity), room);
        ReservationTarget source = ReservationTarget.OfBuilding(ReservationLayer.Output, best);
        ReservationTarget into = ReservationTarget.OfBuilding(ReservationLayer.Storage, storage);
        ReservationRequest[] requests =
        [
            new ReservationRequest(source, item, amount, stock),
            new ReservationRequest(into, item, amount, world.Reservations.AmountReserved(into) + room),
        ];
        if (!TryPlanAndReserve(sim, world, entity, bestGoal, source, requests))
        {
            return false;
        }

        job = job with { Kind = JobKind.Haul, Phase = JobPhase.ToTarget, Building = best, TargetTile = -1, Storage = storage, GoalTile = bestGoal, WorkLeft = 0 };
        return true;
    }

    // Plans the walk to `goal`, then reserves every request; a path that cannot be planned bans `target`.
    private static bool TryPlanAndReserve(Simulation sim, GameWorld world, EntityId entity, int goal, ReservationTarget target, ReadOnlySpan<ReservationRequest> requests)
    {
        if (!AgentMovement.TryPlan(sim, world, entity, goal, WorkRange, sprint: false, out Mover mover))
        {
            world.Reservations.Ban(entity, target, sim.Tick + sim.Content.Economy.JobRetryCooldownTicks);
            return false;
        }

        if (!world.Reservations.TryReserveAll(entity, requests))
        {
            return false;
        }

        world.Movers.Get(entity) = mover;
        return true;
    }

    // The load goes to the nearest storage with room: reserve the room, walk there. False (nothing changed but a ban of
    // an unreachable storage) when no storage takes it.
    private static bool TryHeadToStorage(Simulation sim, GameWorld world, EntityId entity, ref ResidentJob job)
    {
        Position position = sim.Positions.Get(entity);
        EntityId storage = Storages.FindNearest(sim, position.TileX, position.TileY, job.CarryItem, entity, out int room);
        if (storage == EntityId.None)
        {
            return false;
        }

        ReservationTarget into = ReservationTarget.OfBuilding(ReservationLayer.Storage, storage);
        if (!TryPlanTo(sim, world, entity, storage, out int goal))
        {
            world.Reservations.Ban(entity, into, sim.Tick + sim.Content.Economy.JobRetryCooldownTicks);
            return false;
        }

        int amount = Math.Min(room, job.CarryCount);
        if (!world.Reservations.TryReserveAll(entity, [new ReservationRequest(into, job.CarryItem, amount, world.Reservations.AmountReserved(into) + room)]))
        {
            return false;
        }

        job = job with { Phase = JobPhase.ToStorage, Storage = storage, GoalTile = goal, WorkLeft = 0 };
        return true;
    }

    // Plans the resident's walk to the building's nearest approachable footprint tile and sets its mover.
    private static bool TryPlanTo(Simulation sim, GameWorld world, EntityId entity, EntityId building, out int goal)
    {
        Position position = sim.Positions.Get(entity);
        PassabilityGrid grid = world.Pathfinder.Grid;
        goal = -1;
        if (!grid.Contains(position.TileX, position.TileY) || !world.Buildings.TryGet(building, out Building target))
        {
            return false;
        }

        int component = world.Pathfinder.Connectivity.GetComponent(grid.Index(position.TileX, position.TileY));
        goal = component == ConnectivityMap.NoComponent
            ? -1
            : JobSearch.ApproachTile(sim.Content, world, target, position.TileX, position.TileY, component);
        if (goal < 0 || !AgentMovement.TryPlan(sim, world, entity, goal, WorkRange, sprint: false, out Mover mover))
        {
            return false;
        }

        world.Movers.Get(entity) = mover;
        return true;
    }

    // An idle resident without work stands on a free tile next to the center (see the class remarks).
    private static void Park(Simulation sim, GameWorld world, EntityId entity)
    {
        EntityId center = Construction.FindCenter(sim);
        if (center == EntityId.None || world.Movers.Get(entity).IsMoving)
        {
            return;
        }

        Building building = world.Buildings.Get(center);
        (int width, int height) = Construction.FootprintOf(Construction.DefOf(sim.Content, building), building.Rotation);
        PassabilityGrid grid = world.Pathfinder.Grid;
        Position position = sim.Positions.Get(entity);
        if (!grid.Contains(position.TileX, position.TileY))
        {
            return;
        }

        int here = grid.Index(position.TileX, position.TileY);
        int rings = ParkingRings(width, height, world.Residents.Count);
        var taken = new List<int>();
        bool shared = false;
        ReadOnlySpan<EntityId> owners = world.Residents.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            if (owners[i] == entity)
            {
                continue;
            }

            Mover other = world.Movers.Get(owners[i]);
            Position at = sim.Positions.Get(owners[i]);
            int tile = other.IsMoving ? other.Goal : grid.Contains(at.TileX, at.TileY) ? grid.Index(at.TileX, at.TileY) : -1;
            taken.Add(tile);
            shared |= !other.IsMoving && tile == here && owners[i].Index < entity.Index;
        }

        int ring = RingOf(building, width, height, position.TileX, position.TileY);
        if (ring >= 1 && ring <= rings && !shared)
        {
            return;
        }

        taken.Sort();
        int component = world.Pathfinder.Connectivity.GetComponent(here);
        int maxRing = Math.Max(rings, Construction.CurrentEra(sim).TerritoryRadius);
        for (int r = 1; r <= maxRing; r++)
        {
            for (int y = building.Y - r; y < building.Y + height + r; y++)
            {
                for (int x = building.X - r; x < building.X + width + r; x++)
                {
                    if (RingOf(building, width, height, x, y) != r || !grid.Contains(x, y))
                    {
                        continue;
                    }

                    int tile = grid.Index(x, y);
                    if (component != ConnectivityMap.NoComponent && world.Pathfinder.Connectivity.GetComponent(tile) == component
                        && taken.BinarySearch(tile) < 0 && AgentMovement.TryPlan(sim, world, entity, tile, 0, sprint: false, out Mover mover))
                    {
                        world.Movers.Get(entity) = mover;
                        return;
                    }
                }
            }
        }
    }

    // Rings around a w × h footprint needed for one tile per resident: ring r has 2(w + 2r) + 2(h + 2r) − 4 tiles.
    private static int ParkingRings(int width, int height, int population)
    {
        int rings = 1;
        long tiles = (2L * (width + 2)) + (2L * (height + 2)) - 4;
        while (tiles < population)
        {
            rings++;
            tiles += (2L * (width + (2 * rings))) + (2L * (height + (2 * rings))) - 4;
        }

        return rings;
    }

    // Chebyshev distance from a tile to the footprint rectangle (0 inside it).
    private static int RingOf(Building building, int width, int height, int x, int y)
    {
        int dx = Math.Max(Math.Max(building.X - x, 0), x - (building.X + width - 1));
        int dy = Math.Max(Math.Max(building.Y - y, 0), y - (building.Y + height - 1));
        return Math.Max(dx, dy);
    }

    // The single release point: the job's reservations end, a walk towards it stops, the load stays.
    private static void End(Simulation sim, GameWorld world, EntityId entity, ref ResidentJob job, string why)
    {
        world.Reservations.ReleaseAll(entity);
        ref Mover mover = ref world.Movers.Get(entity);
        if (mover.IsMoving)
        {
            mover = mover.Halt();
        }

        if (sim.Log.IsEnabled(LogLevel.Debug))
        {
            Log(sim, LogLevel.Debug, $"{entity} ends {JobKinds.NameOf(job.Kind)}: {why}");
        }

        job = job.Cleared(sim.Tick);
    }

    // The target could not be reached: banned for the cooldown, the job ends.
    private static void Fail(Simulation sim, GameWorld world, EntityId entity, ref ResidentJob job, ReservationTarget target)
    {
        world.Reservations.Ban(entity, target, sim.Tick + sim.Content.Economy.JobRetryCooldownTicks);
        Log(sim, LogLevel.Info, $"{entity} cannot reach {target}; banned for {sim.Content.Economy.JobRetryCooldownTicks} ticks");
        End(sim, world, entity, ref job, "the target cannot be reached");
    }

    private static bool IsTargetValid(Simulation sim, GameWorld world, ResidentJob job)
    {
        if (job.Phase == JobPhase.ToStorage)
        {
            return Storages.Accepts(sim, job.Storage, job.CarryItem);
        }

        if (JobKinds.IsGathering(job.Kind))
        {
            PassabilityGrid grid = world.Pathfinder.Grid;
            return world.Tiles.GetEffectiveResource(grid.XOf(job.TargetTile), grid.YOf(job.TargetTile)) == JobKinds.ResourceOf(job.Kind);
        }

        return job.Kind == JobKind.Farm
            ? TryGetWorkplace(sim, world, job.Building, JobKind.Farm, out _, out _)
            : world.Buildings.TryGet(job.Building, out Building source) && source.State == BuildingState.Active && world.WorkOutputs.Has(job.Building);
    }

    // An active building of the player's settlement where residents do `kind`, with its output store.
    private static bool TryGetWorkplace(Simulation sim, GameWorld world, EntityId entity, JobKind kind, out Building building,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDef? work)
    {
        work = null;
        if (!world.Buildings.TryGet(entity, out building) || building.State != BuildingState.Active || building.Settlement != Building.PlayerSettlement
            || !world.WorkOutputs.Has(entity))
        {
            return false;
        }

        work = Construction.LevelOf(sim.Content, building).Work;
        return work is not null && string.Equals(work.Job, JobKinds.NameOf(kind), StringComparison.Ordinal);
    }

    private static bool IsNear(Simulation sim, GameWorld world, EntityId entity, int tile)
    {
        Position position = sim.Positions.Get(entity);
        PassabilityGrid grid = world.Pathfinder.Grid;
        return AgentMovement.Distance(position.TileX, position.TileY, grid.XOf(tile), grid.YOf(tile)) <= WorkRange;
    }

    private static ReservationTarget TargetOf(ResidentJob job) => job.Kind switch
    {
        JobKind.Farm => ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, job.Building),
        JobKind.Haul => ReservationTarget.OfBuilding(ReservationLayer.Output, job.Building),
        _ => ReservationTarget.OfTile(job.TargetTile),
    };

    // The targets a job must hold in its phase.
    private static IEnumerable<ReservationTarget> RequiredTargets(ResidentJob job)
    {
        if (JobKinds.IsGathering(job.Kind))
        {
            yield return ReservationTarget.OfTile(job.TargetTile);
        }
        else if (job.Kind == JobKind.Farm)
        {
            yield return ReservationTarget.OfBuilding(ReservationLayer.WorkSlot, job.Building);
        }
        else if (job.Kind == JobKind.Haul && job.Building != EntityId.None)
        {
            yield return ReservationTarget.OfBuilding(ReservationLayer.Output, job.Building);
        }

        if (job.Storage != EntityId.None)
        {
            yield return ReservationTarget.OfBuilding(ReservationLayer.Storage, job.Storage);
        }
    }

    // True when `reservation` is one of the targets `job` holds, with a fitting item and amount.
    private static bool Requires(Simulation sim, ResidentJob job, Reservation reservation)
    {
        bool counted = reservation.Target.Layer is ReservationLayer.Output or ReservationLayer.Storage;
        bool itemOk = counted
            ? reservation.Item.Value >= 1 && reservation.Item.Value <= sim.Content.Count && sim.Content.Get(reservation.Item) is ItemDef
            : reservation.Item == default && reservation.Amount == 1;
        return itemOk && RequiredTargets(job).Contains(reservation.Target);
    }

    private static bool IsValid(Simulation sim, GameWorld world, ResidentJob job, Resident resident)
    {
        int tiles = world.Size * world.Size;
        bool carryOk = job.CarryCount == 0
            ? job.CarryItem == default
            : job.CarryCount > 0 && job.CarryCount <= sim.Content.Economy.HaulCapacity && job.CarryItem.Value >= 1
                && job.CarryItem.Value <= sim.Content.Count && sim.Content.Get(job.CarryItem) is ItemDef;
        bool storageOk = job.Phase == JobPhase.ToStorage
            ? job.CarryCount > 0 && world.Buildings.TryGet(job.Storage, out Building storage) && Construction.IsStorage(Construction.DefOf(sim.Content, storage))
            : job.Kind == JobKind.Haul && job.Phase == JobPhase.ToTarget ? world.Buildings.Has(job.Storage) : job.Storage == EntityId.None;
        bool workOk = job.Phase == JobPhase.Working ? job.WorkLeft > 0 : job.WorkLeft == 0;
        bool goalOk = job.Kind == JobKind.None ? job.GoalTile == -1 : job.GoalTile >= 0 && job.GoalTile < tiles;
        bool shapeOk = job.Kind switch
        {
            JobKind.None => job.Phase == JobPhase.None && job.Building == EntityId.None && job.TargetTile == -1,
            JobKind.Chop or JobKind.Quarry or JobKind.Forage => job.Phase != JobPhase.None && job.Building == EntityId.None
                && job.TargetTile >= 0 && job.TargetTile < tiles,
            JobKind.Farm => job.Phase is JobPhase.ToTarget or JobPhase.Working && job.TargetTile == -1 && job.CarryCount == 0
                && TryGetWorkplace(sim, world, job.Building, JobKind.Farm, out _, out _),
            JobKind.Haul => job.TargetTile == -1 && (job.Phase == JobPhase.ToStorage
                || (job.Phase == JobPhase.ToTarget && job.CarryCount == 0 && world.WorkOutputs.Has(job.Building))),
            _ => false,
        };
        bool leavingOk = resident.State != ResidentState.Leaving || (job.Kind == JobKind.None && job.CarryCount == 0);
        return Enum.IsDefined(job.Kind) && Enum.IsDefined(job.Phase) && carryOk && storageOk && workOk && goalOk && shapeOk && leavingOk
            && job.NextThinkTick >= 0;
    }

    private static int HeldAmount(GameWorld world, EntityId holder, ReservationTarget target)
    {
        foreach (Reservation reservation in world.Reservations.HeldBy(holder))
        {
            if (reservation.Target == target)
            {
                return reservation.Amount;
            }
        }

        return 0;
    }

    private static string DescribeTarget(ResidentJob job) => job.Phase == JobPhase.ToStorage
        ? $"load to storage {job.Storage}"
        : job.Building != EntityId.None ? $"building {job.Building}" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tile {job.TargetTile}");

    private static WorkDef WorkOf(Simulation sim, GameWorld world, EntityId building) =>
        Construction.LevelOf(sim.Content, world.Buildings.Get(building)).Work
            ?? throw new InvalidOperationException($"Building {building} has no work at its level.");

    private static GatherDef GatherDefOf(Simulation sim, JobKind kind) =>
        HeroRules.FindGatherDef(sim.Content.Hero, JobKinds.ResourceOf(kind))
            ?? throw new InvalidOperationException($"No gathering rule for the job {JobKinds.NameOf(kind)}.");

    private static DefId ItemId(ContentCatalog content, string item) =>
        content.TryGetId(item, out DefId id) ? id : throw new InvalidOperationException($"Item '{item}' is not in the content.");

    private static bool TryGetResident(Simulation sim, string kind, EntityId entity, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world)
    {
        world = sim.World;
        if (world is not null && world.Residents.TryGet(entity, out Resident resident) && resident.State != ResidentState.Leaving)
        {
            return true;
        }

        Log(sim, LogLevel.Warning, $"{kind} ignored: {entity} is not a resident of the settlement");
        world = null;
        return false;
    }

    private static void Log(Simulation sim, LogLevel level, string message) =>
        sim.Log.Write(sim.Tick, level, LogCategory, "{message}", LogField.Of("message", message));

    // Where a resident looks from: its tile and connectivity component.
    private readonly record struct Spot(int X, int Y, int Component)
    {
        public int DistanceTo(GameWorld world, int tile) =>
            AgentMovement.Distance(X, Y, world.Pathfinder.Grid.XOf(tile), world.Pathfinder.Grid.YOf(tile));
    }
}
