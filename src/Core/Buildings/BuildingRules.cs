using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Heroes;
using Core.Logging;
using Core.Sim;

namespace Core.Buildings;

/// <summary>
/// The building orders and the construction system (FR-BUILD-02, 06, 09, 12, 13, ADR 0012): placing, cancelling,
/// upgrading, moving and demolishing, the countdown of construction, upgrade and move, and loading saved buildings.
/// Stateless: everything lives in the components, the tile layer and the content. The read-only rules (placement
/// check, territory, materials, refunds) are in <see cref="Construction"/>.
/// </summary>
/// <remarks>
/// <para>Orders are checked when they execute; a refusal changes nothing and is reported in
/// <see cref="GameWorld.BuildingEvents"/> and the log. Accepted orders take their materials at once (A2) and the work
/// runs for the level's build or move ticks without workers or the hero (A2, A3).</para>
/// <para>A placement creates its entity and reserves its footprint at once, and a demolition or a cancelled
/// construction destroys it at once, instead of going through the entity command buffer: a second order of the same
/// tick must see the reserved or freed tiles and must not refund the same building twice. Nothing iterates the stores
/// while commands run, so this is safe.</para>
/// <para>Moving keeps both footprints occupied until the move ends: the new place is reserved when the order is
/// accepted (so no other order can take it) and the old place stays blocked while the building is in transit (nothing
/// can be built into the gap and nobody walks through a site that is still being taken down). The old tiles are freed
/// when the move ends; tiles shared by both footprints stay occupied throughout.</para>
/// </remarks>
internal static class BuildingRules
{
    /// <summary>Log category of building events.</summary>
    public const string LogCategory = "build";

    /// <summary>Executes <see cref="PlaceBuildingCommand"/>.</summary>
    public static void OrderPlace(Simulation sim, string buildingId, int x, int y, int rotation)
    {
        if (!TryGetWorld(sim, PlaceBuildingCommand.KindName, out GameWorld? world))
        {
            return;
        }

        PlacementResult check = Construction.CheckPlacement(sim, buildingId, x, y, rotation);
        if (!check.IsOk)
        {
            Report(sim, PlaceBuildingCommand.KindName, check.Reason, EntityId.None, buildingId, x, y);
            return;
        }

        sim.Content.TryGetId(buildingId, out DefId defId);
        BuildingDef def = sim.Content.Get<BuildingDef>(defId);
        BuildingLevelDef first = def.Levels[0];
        Spend(sim, first.Cost);
        EntityId entity = sim.Entities.Create();
        (int width, int height) = Construction.FootprintOf(def, rotation);
        Occupy(world, entity, x, y, width, height);
        world.Buildings.Add(entity, new Building(defId, 1, BuildingState.Constructing, first.BuildTicks, x, y, rotation,
            Building.NoTarget, Building.NoTarget, Building.NoTarget, Building.PlayerSettlement));
        Report(sim, PlaceBuildingCommand.KindName, BuildResult.Ok, entity, buildingId, x, y);
    }

    /// <summary>Executes <see cref="UpgradeBuildingCommand"/>.</summary>
    public static void OrderUpgrade(Simulation sim, EntityId entity)
    {
        if (!TryGetBuilding(sim, UpgradeBuildingCommand.KindName, entity, out GameWorld? world, out Building building, out BuildingDef? def))
        {
            return;
        }

        BuildResult result = Construction.CheckUpgrade(sim, entity);
        BuildingLevelDef? next = building.Level < def.Levels.Count ? def.Levels[building.Level] : null;

        if (result == BuildResult.Ok)
        {
            Spend(sim, next!.Cost);
            world.Buildings.Get(entity) = building with { Level = building.Level + 1, State = BuildingState.Upgrading, TicksLeft = next.BuildTicks };
        }

        Report(sim, UpgradeBuildingCommand.KindName, result, entity, def.Id, building.X, building.Y);
    }

    /// <summary>Executes <see cref="MoveBuildingCommand"/>.</summary>
    public static void OrderMove(Simulation sim, EntityId entity, int x, int y, int rotation)
    {
        if (!TryGetBuilding(sim, MoveBuildingCommand.KindName, entity, out GameWorld? world, out Building building, out BuildingDef? def))
        {
            return;
        }

        BuildResult result = Construction.CheckPlacement(sim, def.Id, x, y, rotation, entity).Reason;
        if (result == BuildResult.Ok)
        {
            (int width, int height) = Construction.FootprintOf(def, rotation);
            Occupy(world, entity, x, y, width, height);
            world.Buildings.Get(entity) = building with
            {
                State = BuildingState.Moving,
                TicksLeft = def.Levels[building.Level - 1].MoveTicks,
                TargetX = x,
                TargetY = y,
                TargetRotation = rotation,
            };
        }

        Report(sim, MoveBuildingCommand.KindName, result, entity, def.Id, x, y);
    }

    /// <summary>
    /// Executes <see cref="DemolishBuildingCommand"/>: refund (E5), the storage's stock and a field's waiting output go to
    /// the other storages, then the backpack; a house's residents lose their home and move into other houses with free
    /// places, if any.
    /// </summary>
    public static void OrderDemolish(Simulation sim, EntityId entity)
    {
        if (!TryGetBuilding(sim, DemolishBuildingCommand.KindName, entity, out GameWorld? world, out Building building, out BuildingDef? def))
        {
            return;
        }

        BuildResult result = Construction.CheckDemolish(sim, entity);

        if (result != BuildResult.Ok)
        {
            Report(sim, DemolishBuildingCommand.KindName, result, entity, def.Id, building.X, building.Y);
            return;
        }

        var returned = new List<ItemAmountDef>();
        if (world.Inventories.TryGet(entity, out Inventory stock))
        {
            foreach (ItemSlot slot in stock.Backpack)
            {
                if (!slot.IsEmpty)
                {
                    returned.Add(new ItemAmountDef(sim.Content.Get(slot.Item).Id, slot.Count));
                }
            }
        }

        if (world.WorkOutputs.TryGet(entity, out WorkOutput output) && output.Count > 0)
        {
            returned.Add(new ItemAmountDef(Construction.LevelOf(sim.Content, building).Work!.Item, output.Count));
        }

        returned.AddRange(Construction.Refund(def, 1, building.Level, sim.Content.Construction.DemolishRefundPermille));
        Remove(sim, world, entity, building, def);
        Residents.ResidentRules.Evict(sim, entity);
        IReadOnlyList<ItemAmountDef> lost = Deliver(sim, returned);
        Report(sim, DemolishBuildingCommand.KindName, BuildResult.Ok, entity, def.Id, building.X, building.Y, lost);
    }

    /// <summary>
    /// Executes <see cref="CancelBuildingCommand"/>: a construction is removed and its level-1 cost refunded at
    /// <see cref="ConstructionBalanceDef.CancelRefundPermille"/>; an upgrade goes back to the previous level with the
    /// same refund of the upgrade's cost; a move ends at the old place and frees the reserved new one.
    /// </summary>
    public static void OrderCancel(Simulation sim, EntityId entity)
    {
        if (!TryGetBuilding(sim, CancelBuildingCommand.KindName, entity, out GameWorld? world, out Building building, out BuildingDef? def))
        {
            return;
        }

        int permille = sim.Content.Construction.CancelRefundPermille;
        IReadOnlyList<ItemAmountDef> lost = [];
        BuildResult result = BuildResult.Ok;
        switch (building.State)
        {
            case BuildingState.Constructing when Construction.IsCenter(def) && CountSettlementBuildings(world) > 1:
                result = BuildResult.CenterInUse;
                break;
            case BuildingState.Constructing:
                Remove(sim, world, entity, building, def);
                lost = Deliver(sim, Construction.Refund(def, building.Level, building.Level, permille));
                break;
            case BuildingState.Upgrading:
                world.Buildings.Get(entity) = building with { Level = building.Level - 1, State = BuildingState.Active, TicksLeft = 0 };
                lost = Deliver(sim, Construction.Refund(def, building.Level, building.Level, permille));
                break;
            case BuildingState.Moving:
                (int width, int height) = Construction.FootprintOf(def, building.Rotation);
                (int targetWidth, int targetHeight) = Construction.FootprintOf(def, building.TargetRotation);
                Release(world, entity, building.TargetX, building.TargetY, targetWidth, targetHeight, building.X, building.Y, width, height);
                world.Buildings.Get(entity) = building with
                {
                    State = BuildingState.Active,
                    TicksLeft = 0,
                    TargetX = Building.NoTarget,
                    TargetY = Building.NoTarget,
                    TargetRotation = Building.NoTarget,
                };
                break;
            default:
                result = BuildResult.NothingToCancel;
                break;
        }

        Report(sim, CancelBuildingCommand.KindName, result, entity, def.Id, building.X, building.Y, lost);
    }

    /// <summary>
    /// Construction system body: every construction, upgrade and move counts down one tick (the tick of the order
    /// counts as the first) and ends at 0: a construction or an upgrade becomes active (a storage gets or grows its
    /// stock, a building where residents work gets its output), a move takes the new place and frees the old tiles.
    /// </summary>
    public static void Update(Simulation sim)
    {
        GameWorld? world = sim.World;
        if (world is null)
        {
            return;
        }

        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            ref Building building = ref buildings[i];
            if (building.State == BuildingState.Active)
            {
                continue;
            }

            building = building with { TicksLeft = building.TicksLeft - 1 };
            if (building.TicksLeft == 0)
            {
                Complete(sim, world, owners[i], ref building);
            }
        }
    }

    /// <summary>
    /// Loads the saved buildings after the hero components and the tile changes, validating each one against the
    /// content, its footprint's occupancy and its storage stock.
    /// </summary>
    /// <exception cref="ArgumentException">A saved building is inconsistent.</exception>
    public static void Restore(Simulation sim, WorldState state)
    {
        GameWorld world = sim.World!;
        int centers = 0;
        foreach (ComponentEntry<Building> entry in state.Buildings)
        {
            if (!sim.Entities.IsAlive(entry.Entity))
            {
                throw new ArgumentException($"Building belongs to {entry.Entity}, which is not alive.", nameof(state));
            }

            Building building = entry.Value;
            if (!IsValid(sim, world, building, out BuildingDef? def))
            {
                throw new ArgumentException($"Building of {entry.Entity} is invalid: {building}.", nameof(state));
            }

            centers += Construction.IsCenter(def) ? 1 : 0;
            CheckFootprint(world, entry.Entity, building.X, building.Y, Construction.FootprintOf(def, building.Rotation));
            if (building.State == BuildingState.Moving)
            {
                CheckFootprint(world, entry.Entity, building.TargetX, building.TargetY, Construction.FootprintOf(def, building.TargetRotation));
            }

            int stacks = StorageStacks(def, building);
            bool hasStock = world.Inventories.TryGet(entry.Entity, out Inventory stock);
            if (stacks == 0 ? hasStock : !hasStock || stock.Backpack.Count != stacks || stock.Equipment.Count != 0)
            {
                throw new ArgumentException($"Building {entry.Entity} must have {(stacks == 0 ? "no stock" : $"a stock of {stacks} stacks")}.", nameof(state));
            }

            world.Buildings.Add(entry.Entity, building);
        }

        if (centers > 1)
        {
            throw new ArgumentException("A settlement has at most one center.", nameof(state));
        }
    }

    /// <summary>True when <paramref name="entity"/> is a building (<see cref="DestroyEntityCommand"/> must not remove one).</summary>
    public static bool IsBuilding(Simulation sim, EntityId entity) => sim.World?.Buildings.Has(entity) ?? false;

    // Stacks the building's stock has now: the storage of the level it works at (the previous one while upgrading),
    // none while under construction.
    private static int StorageStacks(BuildingDef def, Building building) => building.State switch
    {
        BuildingState.Constructing => 0,
        BuildingState.Upgrading => def.Levels[building.Level - 2].Storage,
        _ => def.Levels[building.Level - 1].Storage,
    };

    private static bool IsValid(Simulation sim, GameWorld world, Building b, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BuildingDef? def)
    {
        def = b.Def.Value >= 1 && b.Def.Value <= sim.Content.Count ? sim.Content.Get(b.Def) as BuildingDef : null;
        if (def is null || !Enum.IsDefined(b.State) || b.Settlement != Building.PlayerSettlement || b.Level < 1 || b.Level > def.Levels.Count)
        {
            return false;
        }

        bool levelOk = b.State switch
        {
            BuildingState.Constructing => b.Level == 1,
            BuildingState.Upgrading => b.Level >= 2,
            _ => true,
        };
        int total = Construction.TotalTicks(sim.Content, b);
        bool ticksOk = b.State == BuildingState.Active ? b.TicksLeft == 0 : b.TicksLeft >= 1 && b.TicksLeft <= total;
        bool targetOk = b.State == BuildingState.Moving
            ? IsPlaceValid(world, def, b.TargetX, b.TargetY, b.TargetRotation)
            : b.TargetX == Building.NoTarget && b.TargetY == Building.NoTarget && b.TargetRotation == Building.NoTarget;
        return levelOk && ticksOk && targetOk && IsPlaceValid(world, def, b.X, b.Y, b.Rotation);
    }

    private static bool IsPlaceValid(GameWorld world, BuildingDef def, int x, int y, int rotation)
    {
        (int width, int height) = Construction.FootprintOf(def, rotation);
        return rotation >= 0 && rotation <= Construction.MaxRotation && (def.Rotatable || rotation == 0)
            && x >= 0 && y >= 0 && (long)x + width <= world.Size && (long)y + height <= world.Size;
    }

    private static void CheckFootprint(GameWorld world, EntityId entity, int x, int y, (int Width, int Height) size)
    {
        for (int ty = y; ty < y + size.Height; ty++)
        {
            for (int tx = x; tx < x + size.Width; tx++)
            {
                if (world.Tiles.GetOccupant(tx, ty) != entity)
                {
                    throw new ArgumentException($"Tile {tx},{ty} of building {entity} is not occupied by it.", nameof(entity));
                }
            }
        }
    }

    private static void Complete(Simulation sim, GameWorld world, EntityId entity, ref Building building)
    {
        BuildingDef def = Construction.DefOf(sim.Content, building);
        BuildingLevelDef level = def.Levels[building.Level - 1];
        BuildingState was = building.State;
        if (was == BuildingState.Moving)
        {
            (int oldWidth, int oldHeight) = Construction.FootprintOf(def, building.Rotation);
            (int width, int height) = Construction.FootprintOf(def, building.TargetRotation);
            Release(world, entity, building.X, building.Y, oldWidth, oldHeight, building.TargetX, building.TargetY, width, height);
            building = building with
            {
                X = building.TargetX,
                Y = building.TargetY,
                Rotation = building.TargetRotation,
                TargetX = Building.NoTarget,
                TargetY = Building.NoTarget,
                TargetRotation = Building.NoTarget,
            };
        }
        else if (level.Storage > 0)
        {
            // Grows only: the content check keeps storage non-decreasing over the levels.
            Inventory stock = world.Inventories.TryGet(entity, out Inventory existing) ? existing : Inventory.Create(0, 0);
            int extra = Math.Max(0, level.Storage - stock.Backpack.Count);
            sim.Buffer.Add(entity, new Inventory(stock.Backpack.Concat(new ItemSlot[extra]), []));
        }

        if (level.Work is not null && !world.WorkOutputs.Has(entity))
        {
            sim.Buffer.Add(entity, new WorkOutput(0));
        }

        building = building with { State = BuildingState.Active, TicksLeft = 0 };
        sim.Log.Write(sim.Tick, LogLevel.Info, LogCategory, "{building} {id} level {level}: {state} done",
            LogField.Of("building", entity.ToString()), LogField.Of("id", def.Id), LogField.Of("level", building.Level),
            LogField.Of("state", Construction.NameOf(was)));
    }

    // Takes a cost that Construction.CanPay accepted: the backpack first, then the available storages in entity order.
    private static void Spend(Simulation sim, IReadOnlyList<ItemAmountDef> cost)
    {
        GameWorld world = sim.World!;
        EntityId hero = world.HeroEntity;
        foreach (ItemAmountDef entry in cost)
        {
            sim.Content.TryGetId(entry.Item, out DefId item);
            int left = entry.Count;
            if (hero != EntityId.None && world.Inventories.Has(hero))
            {
                ref Inventory backpack = ref world.Inventories.Get(hero);
                backpack = backpack.Remove(item, left, out int removed);
                left -= removed;
            }

            ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
            Span<Building> buildings = world.Buildings.Values;
            for (int i = 0; i < owners.Length && left > 0; i++)
            {
                if (Construction.IsAvailableStorage(world, owners[i], buildings[i]))
                {
                    ref Inventory stock = ref world.Inventories.Get(owners[i]);
                    stock = stock.Remove(item, left, out int removed);
                    left -= removed;
                }
            }

            if (left > 0)
            {
                throw new InvalidOperationException($"Spent a cost that cannot be paid: {left} {entry.Item} missing.");
            }
        }
    }

    // Puts items into the available storages that take them (entity order, filters respected), then the hero's backpack;
    // returns what did not fit (E6), one entry per item in first-appearance order.
    private static List<ItemAmountDef> Deliver(Simulation sim, IReadOnlyList<ItemAmountDef> items)
    {
        GameWorld world = sim.World!;
        EntityId hero = world.HeroEntity;
        var lost = new List<ItemAmountDef>();
        foreach (ItemAmountDef entry in items)
        {
            sim.Content.TryGetId(entry.Item, out DefId item);
            int stackSize = sim.Content.Get<ItemDef>(item).StackSize;
            int left = entry.Count - Storages.DeliverToStorages(sim, item, entry.Count);
            if (left > 0 && hero != EntityId.None && world.Inventories.Has(hero))
            {
                ref Inventory backpack = ref world.Inventories.Get(hero);
                backpack = backpack.Add(item, left, stackSize, out int added);
                left -= added;
            }

            if (left > 0)
            {
                int index = lost.FindIndex(l => string.Equals(l.Item, entry.Item, StringComparison.Ordinal));
                if (index < 0)
                {
                    lost.Add(new ItemAmountDef(entry.Item, left));
                }
                else
                {
                    lost[index] = lost[index] with { Count = lost[index].Count + left };
                }
            }
        }

        return lost;
    }

    // Frees the footprint and destroys the entity (with its stock) at once; see the class remarks.
    private static void Remove(Simulation sim, GameWorld world, EntityId entity, Building building, BuildingDef def)
    {
        (int width, int height) = Construction.FootprintOf(def, building.Rotation);
        Release(world, entity, building.X, building.Y, width, height, 0, 0, 0, 0);
        sim.Entities.Destroy(entity);
    }

    private static void Occupy(GameWorld world, EntityId entity, int x, int y, int width, int height)
    {
        for (int ty = y; ty < y + height; ty++)
        {
            for (int tx = x; tx < x + width; tx++)
            {
                if (!world.Tiles.TryOccupy(tx, ty, entity) && world.Tiles.GetOccupant(tx, ty) != entity)
                {
                    throw new InvalidOperationException($"Tile {tx},{ty} is occupied; the placement check should have refused it.");
                }
            }
        }
    }

    // Frees the entity's tiles of the first rectangle that are not in the second (keep) rectangle.
    private static void Release(GameWorld world, EntityId entity, int x, int y, int width, int height, int keepX, int keepY, int keepWidth, int keepHeight)
    {
        for (int ty = y; ty < y + height; ty++)
        {
            for (int tx = x; tx < x + width; tx++)
            {
                bool kept = tx >= keepX && tx < keepX + keepWidth && ty >= keepY && ty < keepY + keepHeight;
                if (!kept)
                {
                    world.Tiles.Release(tx, ty, entity);
                }
            }
        }
    }

    internal static bool HasOtherBuiltStorage(Simulation sim, GameWorld world, EntityId entity)
    {
        ReadOnlySpan<EntityId> owners = world.Buildings.Entities;
        Span<Building> buildings = world.Buildings.Values;
        for (int i = 0; i < owners.Length; i++)
        {
            if (owners[i] != entity && buildings[i].Settlement == Building.PlayerSettlement && buildings[i].State != BuildingState.Constructing
                && Construction.IsStorage(Construction.DefOf(sim.Content, buildings[i])))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountSettlementBuildings(GameWorld world)
    {
        int count = 0;
        foreach (Building building in world.Buildings.Values)
        {
            count += building.Settlement == Building.PlayerSettlement ? 1 : 0;
        }

        return count;
    }

    private static void Report(Simulation sim, string command, BuildResult result, EntityId entity, string? buildingId, int x, int y,
        IReadOnlyList<ItemAmountDef>? lost = null)
    {
        lost ??= [];
        sim.World!.AddBuildingEvent(sim.Tick, command, result, entity, buildingId ?? string.Empty, x, y, lost);
        int lostCount = lost.Sum(l => l.Count);
        sim.Log.Write(sim.Tick, lostCount > 0 ? LogLevel.Warning : LogLevel.Info, LogCategory,
            "{command} {id} at {x},{y} ({building}): {result}{lost}",
            LogField.Of("command", command), LogField.Of("id", buildingId ?? string.Empty), LogField.Of("x", x), LogField.Of("y", y),
            LogField.Of("building", entity.ToString()), LogField.Of("result", Construction.NameOf(result)),
            LogField.Of("lost", lostCount > 0 ? "; lost " + string.Join(", ", lost.Select(l => $"{l.Count} {l.Item}")) : string.Empty));
    }

    private static bool TryGetWorld(Simulation sim, string kind, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world)
    {
        world = sim.World;
        if (world is null)
        {
            sim.Log.Write(sim.Tick, LogLevel.Warning, LogCategory, "{kind} ignored: the simulation has no world", LogField.Of("kind", kind));
        }

        return world is not null;
    }

    private static bool TryGetBuilding(Simulation sim, string kind, EntityId entity, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameWorld? world,
        out Building building, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BuildingDef? def)
    {
        building = default;
        def = null;
        if (!TryGetWorld(sim, kind, out world))
        {
            return false;
        }

        if (!world.Buildings.TryGet(entity, out building))
        {
            Report(sim, kind, BuildResult.NotABuilding, entity, null, -1, -1);
            world = null;
            return false;
        }

        def = Construction.DefOf(sim.Content, building);
        return true;
    }
}
