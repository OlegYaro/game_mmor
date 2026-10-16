using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Economy;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
using Core.Jobs;
using Core.Residents;
using Core.Sim;
using Core.Tiles;

namespace Core.Save;

/// <summary>
/// Maps <see cref="SimulationState"/> to the current save DTOs and back. Every member of the state must be handled
/// here or listed as excluded in the save guard test (ADR 0007).
/// </summary>
internal static class SaveMapper
{
    /// <summary>
    /// Stable save names of the RNG streams, indexed by <see cref="RngStreamId"/> value. Names, not enum
    /// <c>ToString()</c>, so renaming an enum member never changes the format. Append only.
    /// </summary>
    public static readonly IReadOnlyList<string> RngStreamNames =
        ["world_gen", "combat", "gathering", "ai", "storyteller", "factions"];

    /// <summary>
    /// Stable save names of <see cref="RevealerKind"/>, indexed by value (index 0, <see cref="RevealerKind.None"/>,
    /// is never saved). Append only.
    /// </summary>
    public static readonly IReadOnlyList<string> RevealerKindNames = ["none", "hero", "villager", "tower"];

    /// <summary>Stable save names of <see cref="HeroActivity"/>, indexed by value. Append only.</summary>
    public static readonly IReadOnlyList<string> HeroActivityNames = ["idle", "walking", "gathering"];

    /// <summary>Stable save names of <see cref="HeroStopReason"/>, indexed by value. Append only.</summary>
    public static readonly IReadOnlyList<string> HeroStopReasonNames =
        ["none", "arrived", "stopped", "path_blocked", "backpack_full", "depleted", "no_resource"];

    /// <summary>Stable save names of <see cref="HeroOrderResult"/>, indexed by value. Append only.</summary>
    public static readonly IReadOnlyList<string> HeroOrderResultNames =
        ["none", "accepted", "outside_map", "unexplored", "impassable", "unreachable", "no_resource"];

    /// <summary>Stable save names of <see cref="ResidentState"/>, indexed by value. Append only.</summary>
    public static readonly IReadOnlyList<string> ResidentStateNames = ["arriving", "idle", "walking", "working", "leaving"];

    /// <summary>Header of a state: the world's generator version for a game, the running generator's otherwise.</summary>
    public static SaveHeader ToHeader(SimulationState state, string codec) =>
        new(SaveFormat.CurrentVersion, GameVersion, state.World?.GeneratorVersion ?? SaveFormat.WorldGenVersion, state.Seed, codec);

    /// <summary>Largest inventory size a save may declare (a corrupt-file guard, far above any balance value).</summary>
    private const int MaxInventorySlots = 4096;

    /// <summary>Version of this build as written into headers.</summary>
    public static string GameVersion => CoreInfo.Version.ToString(3);

    /// <summary>Builds the body of a state; <paramref name="content"/> names the items (saves hold string ids).</summary>
    public static SaveBodyDto ToBody(SimulationState state, ContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(content);
        if (state.RngStates.Count != RngStreamNames.Count)
        {
            throw new InvalidOperationException($"State has {state.RngStates.Count} RNG streams, the save format names {RngStreamNames.Count}.");
        }

        var rng = new RngStreamDto[state.RngStates.Count];
        for (int i = 0; i < rng.Length; i++)
        {
            Xoshiro256State s = state.RngStates[i];
            rng[i] = new RngStreamDto(RngStreamNames[i], s.S0, s.S1, s.S2, s.S3);
        }

        PositionEntryDto[] positions = state.Positions
            .Select(e => new PositionEntryDto(e.Entity.Index, e.Entity.Generation, e.Value.X, e.Value.Y))
            .ToArray();

        PendingCommandDto[] pending = state.PendingCommands
            .Select(p => new PendingCommandDto(p.ExecuteTick, p.Sequence, CommandDto.FromCommand(p.Command)))
            .ToArray();

        WorldState? world = state.World;
        RevealerEntryDto[] revealers = world is null
            ? []
            : world.Revealers.Select(e => new RevealerEntryDto(e.Entity.Index, e.Entity.Generation, RevealerKindName(e.Value.Kind))).ToArray();
        int tileCount = world is null ? 0 : world.MapSize * world.MapSize;
        int[] exploredRuns = world is null ? [] : FogRunLength.Encode(world.ExploredBits.ToArray(), tileCount);
        HeroEntryDto[] heroes = world is null ? [] : world.Heroes.Select(ToDto).ToArray();
        InventoryEntryDto[] inventories = world is null ? [] : world.Inventories.Select(e => ToDto(e, content)).ToArray();
        MoverEntryDto[] movers = world is null ? [] : world.Movers
            .Select(e => new MoverEntryDto(e.Entity.Index, e.Entity.Generation, e.Value.Goal, e.Value.GoalRange, e.Value.Path.ToArray(),
                e.Value.StepIndex, e.Value.StepProgress, e.Value.Sprint))
            .ToArray();

        BuildingEntryDto[] buildings = world is null ? [] : world.Buildings.Select(e => ToDto(e, content)).ToArray();
        ResidentEntryDto[] residents = world is null ? [] : world.Residents.Select(e => ToDto(e, content)).ToArray();
        JobEntryDto[] jobs = world is null ? [] : world.Jobs.Select(e => ToDto(e, content)).ToArray();
        WorkOutputEntryDto[] outputs = world is null ? [] : world.WorkOutputs
            .Select(e => new WorkOutputEntryDto(e.Entity.Index, e.Entity.Generation, e.Value.Count)).ToArray();
        StorageFilterEntryDto[] filters = world is null ? [] : world.StorageFilters
            .Select(e => new StorageFilterEntryDto(e.Entity.Index, e.Entity.Generation, CategoryNames(e.Value.Categories))).ToArray();
        ReservationsState reservations = world?.Reservations ?? ReservationsState.Empty;

        return new SaveBodyDto(
            state.Tick,
            rng,
            new EntitiesDto(state.Entities.Generations.ToArray(), state.Entities.Alive.ToArray()),
            new ComponentsDto(positions, revealers, heroes, inventories, movers, buildings, residents, jobs, outputs, filters),
            new CommandQueueDto(state.NextCommandSequence, pending),
            new GameDto(DifficultyNames.ToName(state.Difficulty), world?.MapSize ?? 0, world?.MapHash ?? 0),
            new FogDto(exploredRuns),
            ToDto(world?.Tiles ?? TileChangesState.Empty),
            new SettlementDto(world is null ? string.Empty : content.Get(world.ReachedEra).Id, world?.DaysWithoutFood ?? 0,
                world?.LastLeaveDay ?? Sim.GameWorld.NoLeaveDay),
            new JobsDto(
                reservations.Reservations.Select(r => new ReservationDto(NameOf(ReservationManager.LayerNames, (int)r.Target.Layer, "reservation layer"),
                    r.Target.Index, r.Target.Generation, r.Holder.Index, r.Holder.Generation, ItemName(content, r.Item), r.Amount)).ToArray(),
                reservations.Bans.Select(b => new JobBanDto(b.Holder.Index, b.Holder.Generation,
                    NameOf(ReservationManager.LayerNames, (int)b.Target.Layer, "reservation layer"), b.Target.Index, b.Target.Generation, b.UntilTick)).ToArray()));
    }

    private static ResidentEntryDto ToDto(ComponentEntry<Resident> entry, ContentCatalog content)
    {
        Resident r = entry.Value;
        var priorities = new List<WorkPriorityDto>();
        for (int k = 1; k <= JobKinds.Count; k++)
        {
            int priority = r.Priorities.Get((JobKind)k);
            if (priority > 0)
            {
                priorities.Add(new WorkPriorityDto(JobKinds.NameOf((JobKind)k), priority));
            }
        }

        return new ResidentEntryDto(entry.Entity.Index, entry.Entity.Generation, ResidentRules.NameOf(content, r), content.Get(r.Profession).Id,
            r.Skills.ToArray(), r.Home.Index, r.Home.Generation, NameOf(ResidentStateNames, (int)r.State, "resident state"), priorities);
    }

    private static JobEntryDto ToDto(ComponentEntry<ResidentJob> entry, ContentCatalog content)
    {
        ResidentJob j = entry.Value;
        return new JobEntryDto(entry.Entity.Index, entry.Entity.Generation, NameOf(JobKinds.Names, (int)j.Kind, "job kind"),
            NameOf(JobKinds.PhaseNames, (int)j.Phase, "job phase"), j.Building.Index, j.Building.Generation, j.TargetTile, j.Storage.Index,
            j.Storage.Generation, j.GoalTile, j.WorkLeft, ItemName(content, j.CarryItem), j.CarryCount, j.NextThinkTick);
    }

    // An item id, or the empty string for none.
    private static string ItemName(ContentCatalog content, DefId item) => item == default ? string.Empty : content.Get(item).Id;

    private static string[] CategoryNames(int categories) =>
        ItemCategories.All.Where((_, i) => (categories & (1 << i)) != 0).ToArray();

    private static HeroEntryDto ToDto(ComponentEntry<Hero> entry)
    {
        Hero h = entry.Value;
        return new HeroEntryDto(entry.Entity.Index, entry.Entity.Generation, h.Attack, h.Defense, h.SpellPower, h.Knowledge, h.Health, h.Stamina,
            NameOf(HeroActivityNames, (int)h.Activity, "hero activity"), h.TargetX, h.TargetY, h.ActionTicks,
            NameOf(HeroStopReasonNames, (int)h.LastStop, "hero stop reason"), NameOf(HeroOrderResultNames, (int)h.LastOrder, "hero order result"));
    }

    private static InventoryEntryDto ToDto(ComponentEntry<Inventory> entry, ContentCatalog content)
    {
        static ItemSlotDto[] Slots(IReadOnlyList<ItemSlot> slots, ContentCatalog content) => slots
            .Select((slot, i) => (slot, i))
            .Where(p => !p.slot.IsEmpty)
            .Select(p => new ItemSlotDto(p.i, content.Get(p.slot.Item).Id, p.slot.Count))
            .ToArray();

        Inventory inventory = entry.Value;
        return new InventoryEntryDto(entry.Entity.Index, entry.Entity.Generation, inventory.Backpack.Count, Slots(inventory.Backpack, content),
            inventory.Equipment.Count, Slots(inventory.Equipment, content));
    }

    private static BuildingEntryDto ToDto(ComponentEntry<Building> entry, ContentCatalog content)
    {
        Building b = entry.Value;
        return new BuildingEntryDto(entry.Entity.Index, entry.Entity.Generation, content.Get(b.Def).Id, b.Level,
            NameOf(Construction.StateNames, (int)b.State, "building state"), b.TicksLeft, b.X, b.Y, b.Rotation, b.TargetX, b.TargetY,
            b.TargetRotation, b.Settlement);
    }

    private static TilesDto ToDto(TileChangesState tiles) => new(
        tiles.Resources.Select(c => new ResourceChunkDto(c.ChunkX, c.ChunkY, c.Mask.ToArray(), c.Amounts.ToArray())).ToArray(),
        tiles.Occupancy.Select(c => new OccupancyChunkDto(c.ChunkX, c.ChunkY, c.Mask.ToArray(),
            c.Owners.Select(o => o.Index).ToArray(), c.Owners.Select(o => o.Generation).ToArray())).ToArray(),
        tiles.Flora.Select(c => new FloraChunkDto(c.ChunkX, c.ChunkY, c.Mask.ToArray(),
            c.States.Select(f => NameOf(TileStateNames.Flora, (int)f, "flora state")).ToArray(), c.Ticks.ToArray(), c.Stocks.ToArray())).ToArray(),
        tiles.Walks.Select(c => new WalkChunkDto(c.ChunkX, c.ChunkY, c.Mask.ToArray(), c.Counters.ToArray(), c.Ticks.ToArray(),
            c.Classes.Select(r => NameOf(TileStateNames.RoadClasses, (int)r, "road class")).ToArray())).ToArray());

    /// <summary>Builds the simulation state from a parsed header and body; <paramref name="content"/> resolves the item ids.</summary>
    /// <exception cref="InvalidDataException">The body is structurally invalid (null entries, unknown names, items or RNG streams).</exception>
    public static SimulationState ToState(SaveHeader header, SaveBodyDto body, ContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(content);

        var rng = new Xoshiro256State[RngStreamNames.Count];
        var seen = new bool[rng.Length];
        foreach (RngStreamDto? stream in body.Rng)
        {
            NotNull(stream, "rng entry");
            int index = IndexOf(RngStreamNames, stream.Stream);
            if (index < 0)
            {
                throw new InvalidDataException($"Unknown RNG stream '{stream.Stream}'.");
            }

            if (seen[index])
            {
                throw new InvalidDataException($"RNG stream '{stream.Stream}' appears twice.");
            }

            seen[index] = true;
            rng[index] = new Xoshiro256State(stream.S0, stream.S1, stream.S2, stream.S3);
        }

        int missing = Array.IndexOf(seen, false);
        if (missing >= 0)
        {
            throw new InvalidDataException($"RNG stream '{RngStreamNames[missing]}' is missing.");
        }

        var positions = new ComponentEntry<Position>[body.Components.Position.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            PositionEntryDto? p = body.Components.Position[i];
            NotNull(p, "position entry");
            positions[i] = new ComponentEntry<Position>(new EntityId(p.Index, p.Generation), new Position(p.X, p.Y));
        }

        var pending = new PendingCommand[body.Commands.Pending.Count];
        for (int i = 0; i < pending.Length; i++)
        {
            PendingCommandDto? p = body.Commands.Pending[i];
            NotNull(p, "pending command");
            pending[i] = new PendingCommand(p.ExecuteTick, p.Sequence, p.Command.ToCommand());
        }

        if (!DifficultyNames.TryParse(body.Game.Difficulty, out Difficulty difficulty))
        {
            throw new InvalidDataException($"Unknown difficulty '{body.Game.Difficulty}'.");
        }

        return new SimulationState(
            header.Seed,
            body.Tick,
            rng,
            new EntityRegistryState(body.Entities.Generations.ToArray(), body.Entities.Alive.ToArray()),
            positions,
            body.Commands.NextSequence,
            pending,
            difficulty,
            ToWorld(header, body, content));
    }

    private static WorldState? ToWorld(SaveHeader header, SaveBodyDto body, ContentCatalog content)
    {
        int size = body.Game.MapSize;
        ComponentsDto components = body.Components;
        if (size == 0)
        {
            if (body.Game.MapHash != 0 || body.Fog.ExploredRuns.Count != 0 || components.Revealer.Count != 0 || components.Hero.Count != 0
                || components.Inventory.Count != 0 || components.Mover.Count != 0 || components.Building.Count != 0 || body.Tiles.Resources.Count != 0
                || body.Tiles.Occupancy.Count != 0 || body.Tiles.Flora.Count != 0 || body.Tiles.Walks.Count != 0 || components.Resident.Count != 0 || body.Settlement.ReachedEra.Length != 0
                || components.Job.Count != 0 || components.WorkOutput.Count != 0 || components.StorageFilter.Count != 0
                || body.Jobs.Reservations.Count != 0 || body.Jobs.Bans.Count != 0 || body.Settlement.DaysWithoutFood != 0
                || body.Settlement.LastLeaveDay != Sim.GameWorld.NoLeaveDay)
            {
                throw new InvalidDataException(
                    "A save without a world (mapSize 0) must have mapHash 0, no fog runs, components of the world, tile changes, reached era, jobs or food state.");
            }

            return null;
        }

        if (size < 0 || size > WorldGen.WorldGenRequest.MaxSize)
        {
            throw new InvalidDataException($"Map size {size} is out of range.");
        }

        var revealers = new ComponentEntry<Revealer>[body.Components.Revealer.Count];
        for (int i = 0; i < revealers.Length; i++)
        {
            RevealerEntryDto? r = body.Components.Revealer[i];
            NotNull(r, "revealer entry");
            int kind = IndexOf(RevealerKindNames, r.Kind);
            if (kind <= 0)
            {
                throw new InvalidDataException($"Unknown revealer kind '{r.Kind}'.");
            }

            revealers[i] = new ComponentEntry<Revealer>(new EntityId(r.Index, r.Generation), new Revealer((RevealerKind)kind));
        }

        ulong[] explored = FogRunLength.Decode(body.Fog.ExploredRuns, size * size);
        return new WorldState(size, header.WorldGenVersion, body.Game.MapHash, explored, revealers,
            components.Hero.Select(ToHero).ToArray(),
            components.Inventory.Select(i => ToInventory(i, content)).ToArray(),
            components.Mover.Select(ToMover).ToArray(),
            components.Building.Select(b => ToBuilding(b, content)).ToArray(),
            ToTiles(body.Tiles),
            components.Resident.Select(r => ToResident(r, content)).ToArray(),
            ToEra(body.Settlement.ReachedEra, content),
            components.Job.Select(j => ToJob(j, content)).ToArray(),
            components.WorkOutput.Select(ToWorkOutput).ToArray(),
            components.StorageFilter.Select(ToStorageFilter).ToArray(),
            ToReservations(body.Jobs, content),
            body.Settlement.DaysWithoutFood,
            body.Settlement.LastLeaveDay);
    }

    private static ComponentEntry<ResidentJob> ToJob(JobEntryDto? dto, ContentCatalog content)
    {
        NotNull(dto, "job entry");
        return new ComponentEntry<ResidentJob>(new EntityId(dto.Index, dto.Generation), new ResidentJob(
            (JobKind)ValueOf(JobKinds.Names, dto.Kind, "job kind"), (JobPhase)ValueOf(JobKinds.PhaseNames, dto.Phase, "job phase"),
            new EntityId(dto.BuildingIndex, dto.BuildingGeneration), dto.TargetTile, new EntityId(dto.StorageIndex, dto.StorageGeneration),
            dto.GoalTile, dto.WorkLeft, ToItem(dto.CarryItem, content), dto.CarryCount, dto.NextThinkTick));
    }

    private static ComponentEntry<WorkOutput> ToWorkOutput(WorkOutputEntryDto? dto)
    {
        NotNull(dto, "work output entry");
        return new ComponentEntry<WorkOutput>(new EntityId(dto.Index, dto.Generation), new WorkOutput(dto.Count));
    }

    private static ComponentEntry<StorageFilter> ToStorageFilter(StorageFilterEntryDto? dto)
    {
        NotNull(dto, "storage filter entry");
        int mask = 0;
        int previous = -1;
        foreach (string? name in dto.Categories)
        {
            int category = IndexOf(ItemCategories.All, name ?? string.Empty);
            if (category <= previous)
            {
                throw new InvalidDataException($"Storage filter category '{name}' is unknown, repeated or out of order.");
            }

            previous = category;
            mask |= 1 << category;
        }

        return new ComponentEntry<StorageFilter>(new EntityId(dto.Index, dto.Generation), new StorageFilter(mask));
    }

    private static ReservationsState ToReservations(JobsDto jobs, ContentCatalog content)
    {
        var reservations = new Reservation[jobs.Reservations.Count];
        for (int i = 0; i < reservations.Length; i++)
        {
            ReservationDto? r = jobs.Reservations[i];
            NotNull(r, "reservation");
            var target = new ReservationTarget((ReservationLayer)ValueOf(ReservationManager.LayerNames, r.Layer, "reservation layer"), r.Index, r.Generation);
            reservations[i] = new Reservation(target, new EntityId(r.HolderIndex, r.HolderGeneration), ToItem(r.Item, content), r.Amount);
        }

        var bans = new JobBan[jobs.Bans.Count];
        for (int i = 0; i < bans.Length; i++)
        {
            JobBanDto? b = jobs.Bans[i];
            NotNull(b, "job ban");
            var target = new ReservationTarget((ReservationLayer)ValueOf(ReservationManager.LayerNames, b.Layer, "reservation layer"), b.Index, b.Generation);
            bans[i] = new JobBan(new EntityId(b.HolderIndex, b.HolderGeneration), target, b.UntilTick);
        }

        return new ReservationsState(reservations, bans);
    }

    // An item id, or default for the empty string.
    private static DefId ToItem(string item, ContentCatalog content)
    {
        if (item.Length == 0)
        {
            return default;
        }

        return content.TryGetId(item, out DefId id) && content.Get(id) is ItemDef ? id : throw new InvalidDataException($"Unknown item '{item}'.");
    }

    private static DefId ToEra(string id, ContentCatalog content) =>
        content.TryGetId(id, out DefId era) && content.Get(era) is EraDef ? era : throw new InvalidDataException($"Unknown era '{id}'.");

    private static ComponentEntry<Resident> ToResident(ResidentEntryDto? dto, ContentCatalog content)
    {
        NotNull(dto, "resident entry");
        int name = content.TryGet(ResidentRules.NameListId, out NameListDef names) ? IndexOf(names.Names, dto.Name) : -1;
        if (name < 0)
        {
            throw new InvalidDataException($"Unknown resident name '{dto.Name}'.");
        }

        if (!content.TryGetId(dto.Profession, out DefId profession) || content.Get(profession) is not ProfessionDef)
        {
            throw new InvalidDataException($"Unknown profession '{dto.Profession}'.");
        }

        SkillSet skills;
        try
        {
            skills = SkillSet.From(dto.Skills.ToArray());
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Resident skills are invalid: {ex.Message}", ex);
        }

        var priorities = default(WorkPriorities);
        JobKind previous = JobKind.None;
        foreach (WorkPriorityDto? entry in dto.Priorities)
        {
            NotNull(entry, "work priority");
            if (!JobKinds.TryParse(entry.Job, out JobKind kind) || kind <= previous || entry.Priority is < 1 or > WorkPriorities.Max)
            {
                throw new InvalidDataException($"Work priority '{entry.Job}' {entry.Priority} is unknown, repeated, out of order or out of range.");
            }

            previous = kind;
            priorities = priorities.With(kind, entry.Priority);
        }

        return new ComponentEntry<Resident>(new EntityId(dto.Index, dto.Generation), new Resident(name, profession, skills,
            new EntityId(dto.HomeIndex, dto.HomeGeneration), (ResidentState)ValueOf(ResidentStateNames, dto.State, "resident state"), priorities));
    }

    private static ComponentEntry<Building> ToBuilding(BuildingEntryDto? dto, ContentCatalog content)
    {
        NotNull(dto, "building entry");
        if (!content.TryGetId(dto.Building, out DefId def) || content.Get(def) is not BuildingDef)
        {
            throw new InvalidDataException($"Unknown building '{dto.Building}'.");
        }

        return new ComponentEntry<Building>(new EntityId(dto.Index, dto.Generation), new Building(def, dto.Level,
            (BuildingState)ValueOf(Construction.StateNames, dto.State, "building state"), dto.TicksLeft, dto.X, dto.Y, dto.Rotation,
            dto.TargetX, dto.TargetY, dto.TargetRotation, dto.Settlement));
    }

    private static ComponentEntry<Hero> ToHero(HeroEntryDto? h)
    {
        NotNull(h, "hero entry");
        return new ComponentEntry<Hero>(new EntityId(h.Index, h.Generation), new Hero(h.Attack, h.Defense, h.SpellPower, h.Knowledge, h.Health, h.Stamina,
            (HeroActivity)ValueOf(HeroActivityNames, h.Activity, "hero activity"), h.TargetX, h.TargetY, h.ActionTicks,
            (HeroStopReason)ValueOf(HeroStopReasonNames, h.LastStop, "hero stop reason"),
            (HeroOrderResult)ValueOf(HeroOrderResultNames, h.LastOrder, "hero order result")));
    }

    private static ComponentEntry<Inventory> ToInventory(InventoryEntryDto? dto, ContentCatalog content)
    {
        NotNull(dto, "inventory entry");
        return new ComponentEntry<Inventory>(new EntityId(dto.Index, dto.Generation),
            new Inventory(ToSlots(dto.BackpackSize, dto.Backpack, content), ToSlots(dto.EquipmentSize, dto.Equipment, content)));
    }

    private static ItemSlot[] ToSlots(int size, IReadOnlyList<ItemSlotDto> saved, ContentCatalog content)
    {
        if (size < 0 || size > MaxInventorySlots)
        {
            throw new InvalidDataException($"Inventory size {size} is out of range.");
        }

        var slots = new ItemSlot[size];
        int previous = -1;
        foreach (ItemSlotDto? slot in saved)
        {
            NotNull(slot, "inventory slot");
            if (slot.Slot <= previous || slot.Slot >= size || slot.Count <= 0)
            {
                throw new InvalidDataException($"Inventory slot {slot.Slot} ({slot.Count} {slot.Item}) is out of order, out of range or empty.");
            }

            if (!content.TryGetId(slot.Item, out DefId item) || content.Get(item) is not ItemDef)
            {
                throw new InvalidDataException($"Unknown item '{slot.Item}'.");
            }

            previous = slot.Slot;
            slots[slot.Slot] = new ItemSlot(item, slot.Count);
        }

        return slots;
    }

    private static ComponentEntry<Mover> ToMover(MoverEntryDto? m)
    {
        NotNull(m, "mover entry");
        return new ComponentEntry<Mover>(new EntityId(m.Index, m.Generation),
            new Mover(m.Goal, m.GoalRange, m.Path.ToArray(), m.StepIndex, m.StepProgress, m.Sprint));
    }

    private static TileChangesState ToTiles(TilesDto tiles)
    {
        var resources = new ResourceChunkState[tiles.Resources.Count];
        for (int i = 0; i < resources.Length; i++)
        {
            ResourceChunkDto? c = tiles.Resources[i];
            NotNull(c, "resource chunk");
            resources[i] = new ResourceChunkState(c.X, c.Y, c.Mask.ToArray(), c.Amounts.ToArray());
        }

        var occupancy = new OccupancyChunkState[tiles.Occupancy.Count];
        for (int i = 0; i < occupancy.Length; i++)
        {
            OccupancyChunkDto? c = tiles.Occupancy[i];
            NotNull(c, "occupancy chunk");
            if (c.OwnerIndices.Count != c.OwnerGenerations.Count)
            {
                throw new InvalidDataException("An occupancy chunk has different numbers of owner indices and generations.");
            }

            occupancy[i] = new OccupancyChunkState(c.X, c.Y, c.Mask.ToArray(),
                c.OwnerIndices.Zip(c.OwnerGenerations, (index, generation) => new EntityId(index, generation)).ToArray());
        }

        var flora = new FloraChunkState[tiles.Flora.Count];
        for (int i = 0; i < flora.Length; i++)
        {
            FloraChunkDto? c = tiles.Flora[i];
            NotNull(c, "flora chunk");
            flora[i] = new FloraChunkState(c.X, c.Y, c.Mask.ToArray(),
                c.States.Select(name => (FloraState)ValueOf(TileStateNames.Flora, name ?? string.Empty, "flora state")).ToArray(), c.Ticks.ToArray(), c.Stocks.ToArray());
        }

        var walks = new WalkChunkState[tiles.Walks.Count];
        for (int i = 0; i < walks.Length; i++)
        {
            WalkChunkDto? c = tiles.Walks[i];
            NotNull(c, "walk chunk");
            walks[i] = new WalkChunkState(c.X, c.Y, c.Mask.ToArray(), c.Counters.ToArray(), c.Ticks.ToArray(),
                c.Classes.Select(name => (RoadClass)ValueOf(TileStateNames.RoadClasses, name ?? string.Empty, "road class")).ToArray());
        }

        return new TileChangesState(resources, occupancy, flora, walks);
    }

    private static string NameOf(IReadOnlyList<string> names, int value, string what) =>
        (uint)value < (uint)names.Count ? names[value] : throw new InvalidOperationException($"The {what} {value} has no save name.");

    private static int ValueOf(IReadOnlyList<string> names, string name, string what)
    {
        int value = IndexOf(names, name);
        return value >= 0 ? value : throw new InvalidDataException($"Unknown {what} '{name}'.");
    }

    private static string RevealerKindName(RevealerKind kind) =>
        kind != RevealerKind.None && (int)kind < RevealerKindNames.Count
            ? RevealerKindNames[(int)kind]
            : throw new InvalidOperationException($"Revealer kind {(int)kind} has no save name.");

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // System.Text.Json does not apply nullable annotations to collection elements, so null array items get here.
    private static void NotNull<T>([System.Diagnostics.CodeAnalysis.NotNull] T? value, string what)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidDataException($"A {what} is null.");
        }
    }
}
