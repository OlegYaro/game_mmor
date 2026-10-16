using Core.Buildings;
using Core.Content;
using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Heroes;
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

        return new SaveBodyDto(
            state.Tick,
            rng,
            new EntitiesDto(state.Entities.Generations.ToArray(), state.Entities.Alive.ToArray()),
            new ComponentsDto(positions, revealers, heroes, inventories, movers, buildings, residents),
            new CommandQueueDto(state.NextCommandSequence, pending),
            new GameDto(DifficultyNames.ToName(state.Difficulty), world?.MapSize ?? 0, world?.MapHash ?? 0),
            new FogDto(exploredRuns),
            ToDto(world?.Tiles ?? TileChangesState.Empty),
            new SettlementDto(world is null ? string.Empty : content.Get(world.ReachedEra).Id));
    }

    private static ResidentEntryDto ToDto(ComponentEntry<Resident> entry, ContentCatalog content)
    {
        Resident r = entry.Value;
        return new ResidentEntryDto(entry.Entity.Index, entry.Entity.Generation, ResidentRules.NameOf(content, r), content.Get(r.Profession).Id,
            r.Skills.ToArray(), r.Home.Index, r.Home.Generation, NameOf(ResidentStateNames, (int)r.State, "resident state"), r.Job);
    }

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
            c.Owners.Select(o => o.Index).ToArray(), c.Owners.Select(o => o.Generation).ToArray())).ToArray());

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
                || body.Tiles.Occupancy.Count != 0 || components.Resident.Count != 0 || body.Settlement.ReachedEra.Length != 0)
            {
                throw new InvalidDataException(
                    "A save without a world (mapSize 0) must have mapHash 0, no fog runs, revealers, hero, building or resident components, tile changes or reached era.");
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
            ToEra(body.Settlement.ReachedEra, content));
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

        return new ComponentEntry<Resident>(new EntityId(dto.Index, dto.Generation), new Resident(name, profession, skills,
            new EntityId(dto.HomeIndex, dto.HomeGeneration), (ResidentState)ValueOf(ResidentStateNames, dto.State, "resident state"), dto.Job));
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

        return new TileChangesState(resources, occupancy);
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
