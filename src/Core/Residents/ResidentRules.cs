using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Jobs;
using Core.Logging;
using Core.Pathfinding;
using Core.Sim;

namespace Core.Residents;

/// <summary>
/// The residents' rules (FR-NPC-01, FR-NPC-02, ADR 0009): newcomers, the console spawn, housing (moving in, eviction),
/// the reached era, walking, leaving the settlement and loading saved residents. Stateless: everything lives in the components, the world and
/// the content. Read-only rules for the game are in <see cref="Housing"/> and <see cref="Arrivals"/>.
/// </summary>
/// <remarks>
/// A resident is created at once (not through the entity command buffer), so the population, the reached era and the
/// free house places count it in the same tick; nothing iterates the stores while it happens.
/// </remarks>
internal static class ResidentRules
{
    /// <summary>Log category of resident events.</summary>
    public const string LogCategory = "residents";

    /// <summary>The name list residents take their first names from (the race id plus <c>_names</c>).</summary>
    public const string NameListId = "human_names";

    /// <summary>The era of an empty settlement: the era of population 0, which is the first era.</summary>
    public static DefId FirstEra(ContentCatalog content) => IdOf(content, Construction.EraFor(content, 0).Id);

    /// <summary>
    /// Resident system body: homeless residents move into free places, and at the arrival hour (once a day) a newcomer
    /// may arrive (<see cref="Arrivals"/>).
    /// </summary>
    public static void Update(Simulation sim)
    {
        if (sim.World is not { } world)
        {
            return;
        }

        HouseHomeless(sim, world);
        if (Arrivals.IsArrivalTick(sim))
        {
            TryArrive(sim, world);
        }
    }

    /// <summary>One tick of a resident's walking (the movement system calls it for each resident in entity order).</summary>
    public static void Walk(Simulation sim, GameWorld world, EntityId entity, ref Mover mover)
    {
        if (!mover.IsMoving || !sim.Positions.Has(entity))
        {
            return;
        }

        WalkOutcome outcome = AgentMovement.Advance(sim, world, entity, ref mover, sim.Content.Economy.ResidentStepTicks, Mover.ProgressPerTick, LogCategory);
        ref Resident resident = ref world.Residents.Get(entity);
        if (outcome != WalkOutcome.Walking && resident.State == ResidentState.Leaving)
        {
            Gone(sim, world, entity, resident);
        }
        else if (outcome != WalkOutcome.Walking && resident.State == ResidentState.Arriving)
        {
            // Arrived at the center, or the way got blocked: the newcomer stays where it is and waits for work.
            resident = resident with { State = ResidentState.Idle };
            sim.Log.Write(sim.Tick, outcome == WalkOutcome.Arrived ? LogLevel.Info : LogLevel.Warning, LogCategory,
                "{name} ({entity}) {outcome}", LogField.Of("name", NameOf(sim.Content, resident)), LogField.Of("entity", entity.ToString()),
                LogField.Of("outcome", outcome == WalkOutcome.Arrived ? "reached the settlement" : "stopped on the way in: the way is blocked"));
        }
    }

    /// <summary>
    /// Executes <see cref="SpawnResidentCommand"/>: a resident of <paramref name="profession"/> (empty: the next one of
    /// the automatic pattern) appears next to the center, waiting for work, in the nearest house with a free place or
    /// homeless. Refused (logged) without a center, at the resident limit or for an unknown profession.
    /// </summary>
    public static void OrderSpawn(Simulation sim, string profession)
    {
        if (sim.World is not { } world)
        {
            Refuse(sim, "the simulation has no world");
            return;
        }

        EconomyBalanceDef economy = sim.Content.Economy;
        if (Construction.SettlementPopulation(sim) >= economy.MaxResidents)
        {
            Refuse(sim, $"the settlement already has {economy.MaxResidents} residents");
            return;
        }

        DefId professionId;
        if (string.IsNullOrEmpty(profession))
        {
            professionId = AutoProfession(sim);
        }
        else if (!sim.Content.TryGetId(profession, out professionId) || sim.Content.Get(professionId) is not ProfessionDef)
        {
            Refuse(sim, $"'{profession}' is not a profession");
            return;
        }

        IReadOnlyList<int> meeting = Arrivals.MeetingTiles(sim);
        if (meeting.Count == 0)
        {
            Refuse(sim, Construction.FindCenter(sim) == EntityId.None ? "there is no settlement center" : "no free tile next to the center");
            return;
        }

        int tile = meeting[0];
        PassabilityGrid grid = world.Pathfinder.Grid;
        EntityId home = Housing.FindHome(sim, grid.XOf(tile), grid.YOf(tile));
        Spawn(sim, world, tile, Mover.Idle, ResidentState.Idle, professionId, home, ResidentEventKind.Spawned);
    }

    /// <summary>
    /// <paramref name="entity"/> leaves the settlement for good (food shortage, A5): its job ends and its load goes to the
    /// storages, it gives up its home and walks to where newcomers come from (<see cref="Arrivals.FindSpawnTile"/>),
    /// where it disappears; with no way out it disappears at once. It still counts for the population (and the era)
    /// until it is gone, but it no longer eats, pays tax or takes a house place.
    /// </summary>
    public static void Leave(Simulation sim, EntityId entity)
    {
        GameWorld world = sim.World!;
        int lost = JobRules.CancelForLeaving(sim, world, entity);
        ref Resident resident = ref world.Residents.Get(entity);
        resident = resident with { State = ResidentState.Leaving, Home = EntityId.None };
        Report(sim, world, ResidentEventKind.LeftHungry, entity, resident);
        if (lost > 0)
        {
            Log(sim, LogLevel.Warning, $"{entity} took {lost} items with it: no storage had room");
        }

        int exit = Arrivals.FindSpawnTile(sim, Arrivals.MeetingTiles(sim));
        if (exit >= 0 && AgentMovement.TryPlan(sim, world, entity, exit, 0, sprint: false, out Mover mover) && mover.IsMoving)
        {
            world.Movers.Get(entity) = mover;
            return;
        }

        Gone(sim, world, entity, resident);
    }

    /// <summary>
    /// The residents of a demolished <paramref name="house"/> lose their home; each moves into the nearest house with a
    /// free place, if any (<see cref="Housing.FindHome"/>), and stays homeless otherwise. Called after the house is gone.
    /// </summary>
    public static void Evict(Simulation sim, EntityId house)
    {
        GameWorld world = sim.World!;
        ReadOnlySpan<EntityId> owners = world.Residents.Entities;
        Span<Resident> residents = world.Residents.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (residents[i].Home == house)
            {
                residents[i] = residents[i] with { Home = EntityId.None };
                Report(sim, world, ResidentEventKind.Evicted, owners[i], residents[i]);
            }
        }

        HouseHomeless(sim, world);
    }

    /// <summary>Loads the saved residents and the reached era after the buildings, validating them.</summary>
    /// <exception cref="ArgumentException">A saved resident or the reached era is inconsistent.</exception>
    public static void Restore(Simulation sim, WorldState state)
    {
        GameWorld world = sim.World!;
        ContentCatalog content = sim.Content;
        if (state.ReachedEra.Value < 1 || state.ReachedEra.Value > content.Count || content.Get(state.ReachedEra) is not EraDef)
        {
            throw new ArgumentException($"Reached era {state.ReachedEra} is not an era.", nameof(state));
        }

        world.ReachedEra = state.ReachedEra;
        int names = NameList(content).Names.Count;
        foreach (ComponentEntry<Resident> entry in state.Residents)
        {
            Resident r = entry.Value;
            bool professionOk = r.Profession.Value >= 1 && r.Profession.Value <= content.Count && content.Get(r.Profession) is ProfessionDef;
            bool homeOk = r.Home == EntityId.None
                || (world.Buildings.TryGet(r.Home, out Building house) && Housing.CapacityOf(content, house) > 0);
            if (!sim.Entities.IsAlive(entry.Entity) || !sim.Positions.Has(entry.Entity) || !world.Movers.Has(entry.Entity))
            {
                throw new ArgumentException($"Resident {entry.Entity} must be alive and have a position and a mover.", nameof(state));
            }

            bool leavingOk = r.State != ResidentState.Leaving || r.Home == EntityId.None;
            if (r.Name < 0 || r.Name >= names || !professionOk || !r.Skills.IsValid() || !homeOk || !Enum.IsDefined(r.State) || !r.Priorities.IsValid()
                || !leavingOk)
            {
                throw new ArgumentException($"Resident of {entry.Entity} is invalid: {r}.", nameof(state));
            }

            world.Residents.Add(entry.Entity, r);
        }

        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            int occupants = Housing.CountOccupants(world, owners[i]);
            if (occupants > Housing.CapacityOf(content, buildings[i]))
            {
                throw new ArgumentException($"House {owners[i]} has {occupants} residents, more than its places.", nameof(state));
            }
        }
    }

    /// <summary>The first name of <paramref name="resident"/>.</summary>
    public static string NameOf(ContentCatalog content, Resident resident) => NameList(content).Names[resident.Name];

    // At the arrival hour: roll the odds, and on success a newcomer appears and walks to the center.
    private static void TryArrive(Simulation sim, GameWorld world)
    {
        ArrivalOdds odds = Arrivals.ComputeOdds(sim);
        if (odds.ChancePermille == 0)
        {
            Log(sim, LogLevel.Info, $"Nobody arrives today (center {odds.HasCenter}, free places {odds.FreePlaces}, population {odds.Population})");
            return;
        }

        if (!sim.Rng[RngStreamId.Events].ChancePermille(odds.ChancePermille))
        {
            Log(sim, LogLevel.Info, $"Nobody arrives today (chance {odds.ChancePermille}‰)");
            return;
        }

        IReadOnlyList<int> meeting = Arrivals.MeetingTiles(sim);
        int spawn = Arrivals.FindSpawnTile(sim, meeting);
        int goal = spawn < 0 ? -1 : Arrivals.NearestMeetingTile(world, spawn, meeting);
        if (goal < 0 || !AgentMovement.TryPlanFrom(world, spawn, goal, 0, sprint: false, out Mover mover))
        {
            Log(sim, LogLevel.Warning, "A newcomer would arrive, but no tile it could walk in from reaches the center");
            return;
        }

        PassabilityGrid grid = world.Pathfinder.Grid;
        EntityId home = Housing.FindHome(sim, grid.XOf(goal), grid.YOf(goal));
        ResidentState state = mover.IsMoving ? ResidentState.Arriving : ResidentState.Idle;
        Spawn(sim, world, spawn, mover, state, AutoProfession(sim), home, ResidentEventKind.Arrived);
    }

    // Creates the resident at once: name and skills from the events stream, position on `tile`.
    private static void Spawn(Simulation sim, GameWorld world, int tile, Mover mover, ResidentState state, DefId profession, EntityId home,
        ResidentEventKind kind)
    {
        Xoshiro256StarStar rng = sim.Rng[RngStreamId.Events];
        int name = rng.NextInt(NameList(sim.Content).Names.Count);
        int[] skills = new int[SkillSet.Count];
        for (int i = 0; i < skills.Length; i++)
        {
            skills[i] = rng.NextInt(sim.Content.Economy.InitialSkillMax + 1);
        }

        PassabilityGrid grid = world.Pathfinder.Grid;
        var resident = new Resident(name, profession, SkillSet.From(skills), home, state, WorkPriorities.ForProfession(sim.Content.Get<ProfessionDef>(profession)));
        EntityId entity = sim.Entities.Create();
        sim.Positions.Add(entity, Simulation.TileCenter(grid.XOf(tile), grid.YOf(tile)));
        world.Revealers.Add(entity, new Revealer(RevealerKind.Villager));
        world.Movers.Add(entity, mover);
        world.Residents.Add(entity, resident);
        world.Jobs.Add(entity, ResidentJob.Idle(sim.Tick));
        world.ReachedEra = IdOf(sim.Content, Construction.CurrentEra(sim).Id);
        Report(sim, world, kind, entity, resident);
    }

    // A leaving resident reached the edge (or has no way out): it is removed after the system (the stores are being
    // iterated), and reported now.
    private static void Gone(Simulation sim, GameWorld world, EntityId entity, Resident resident)
    {
        Report(sim, world, ResidentEventKind.Left, entity, resident);
        sim.Buffer.Destroy(entity);
    }

    // Homeless residents, in entity order, move into the nearest house with a free place; leaving ones stay homeless.
    private static void HouseHomeless(Simulation sim, GameWorld world)
    {
        ReadOnlySpan<EntityId> owners = world.Residents.Entities;
        Span<Resident> residents = world.Residents.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (residents[i].Home != EntityId.None || residents[i].State == ResidentState.Leaving)
            {
                continue;
            }

            Position position = sim.Positions.Get(owners[i]);
            EntityId home = Housing.FindHome(sim, position.TileX, position.TileY);
            if (home == EntityId.None)
            {
                return; // every house is full
            }

            residents[i] = residents[i] with { Home = home };
            Report(sim, world, ResidentEventKind.Housed, owners[i], residents[i]);
        }
    }

    // The automatic profession of the next resident: the pattern entry at the current population, wrapping around.
    private static DefId AutoProfession(Simulation sim)
    {
        IReadOnlyList<string> pattern = sim.Content.Economy.AutoAssignPattern;
        return IdOf(sim.Content, pattern[Construction.SettlementPopulation(sim) % pattern.Count]);
    }

    private static NameListDef NameList(ContentCatalog content) =>
        content.TryGet(NameListId, out NameListDef list)
            ? list
            : throw new InvalidOperationException($"The name list '{NameListId}' is not in the content.");

    private static DefId IdOf(ContentCatalog content, string id) =>
        content.TryGetId(id, out DefId def) ? def : throw new InvalidOperationException($"'{id}' is not in the content.");

    private static void Report(Simulation sim, GameWorld world, ResidentEventKind kind, EntityId entity, Resident resident)
    {
        Position position = sim.Positions.Get(entity);
        string name = NameOf(sim.Content, resident);
        string profession = sim.Content.Get(resident.Profession).Id;
        world.AddResidentEvent(sim.Tick, kind, entity, name, profession, resident.Home, position.TileX, position.TileY);
        sim.Log.Write(sim.Tick, kind is ResidentEventKind.Evicted or ResidentEventKind.LeftHungry ? LogLevel.Warning : LogLevel.Info, LogCategory,
            "{name} ({entity}, {profession}) {kind} at {x},{y}; home {home}",
            LogField.Of("name", name), LogField.Of("entity", entity.ToString()), LogField.Of("profession", profession),
            LogField.Of("kind", kind.ToString()), LogField.Of("x", position.TileX), LogField.Of("y", position.TileY),
            LogField.Of("home", resident.Home.ToString()));
    }

    private static void Refuse(Simulation sim, string reason) =>
        Log(sim, LogLevel.Warning, $"{SpawnResidentCommand.KindName} ignored: {reason}");

    private static void Log(Simulation sim, LogLevel level, string message) =>
        sim.Log.Write(sim.Tick, level, LogCategory, "{message}", LogField.Of("message", message));
}
