using Core.Buildings;
using Core.Entities;
using Core.Residents;
using Core.Sim;
using Game.Gameplay;
using Game.Sim;
using Game.Ui;
using Game.World;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Smoke;

/// <summary>
/// Residents on the map part of the smoke test (FR-NPC-01): a resident ordered through <see cref="SpawnResidentCommand"/> gets a
/// sprite of its profession in <see cref="ResidentLayer"/>, the sprite stands where Core says, a click picks it (through the
/// picker's headless entry point), a resident who starved and left loses its sprite (the sprite is pooled), and the events of
/// the hungry days become toasts. Returns null when everything passed, otherwise the reason.
/// </summary>
public static class ResidentSmoke
{
    private const int MapSize = 64;
    private const string Folder = "user://smoke_residents";
    private const int BuildTicks = 130;
    private const int TicksPerDay = 9000;
    private const int LeaveWalkTicks = 4000;

    /// <summary>Runs the checks with nodes parented under <paramref name="owner"/> (freed before returning).</summary>
    public static string? Check(Node owner, ulong seed)
    {
        string dir = ProjectSettings.GlobalizePath(Folder);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }

        var host = new SimulationHost();
        owner.AddChild(host);
        host.SetProcess(false);
        host.SetProcessUnhandledInput(false);
        GameRoot? root = null;
        try
        {
            if (!host.Initialize(dir))
            {
                return "residents: host did not initialize";
            }

            string? startError = host.StartNew(new NewGameOptions(seed, MapSize, Difficulty.Normal));
            if (startError is not null)
            {
                return "residents: new game did not start " + startError;
            }

            root = GD.Load<PackedScene>("res://Gameplay/GameRoot.tscn").Instantiate<GameRoot>();
            owner.AddChild(root);
            root.Bind(host);
            return Run(host, root);
        }
        finally
        {
            root?.QueueFree();
            host.QueueFree();
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string? Run(SimulationHost host, GameRoot root)
    {
        ResidentLayer? layer = root.ResidentLayer;
        ResidentPicker? picker = root.ResidentPicker;
        var messages = root.GetNode<ResidentMessages>("Ui/ResidentMessages");
        if (layer is null || picker is null || !layer.IsBound || !WorldArt.TryGetShared(out WorldArt art, out _))
        {
            return "residents: the layer or the picker is missing";
        }

        CoreSimulation sim = host.Simulation;
        var toasts = new List<string>();
        host.Toast += toasts.Add;

        // A settlement center is needed before residents can appear.
        host.Send(new GiveItemCommand("wood", 5));
        sim.Step();
        if (Place(host, "campfire") == EntityId.None)
        {
            return "residents: no place for the campfire";
        }

        // A storage takes the tax, so the daily tax has an amount to report.
        sim.Step(BuildTicks);
        host.Send(new GiveItemCommand("wood", 30));
        sim.Step();
        if (Place(host, "storage") == EntityId.None)
        {
            return "residents: no place for the storage";
        }

        sim.Step(BuildTicks);
        messages.Poll(ResidentMessages.GapSeconds);
        toasts.Clear();

        host.Send(new SpawnResidentCommand("carrier"));
        host.Send(new SpawnResidentCommand("blacksmith"));
        host.Send(new SpawnResidentCommand("builder"));
        sim.Step(3);
        layer.Sync();
        messages.Poll(ResidentMessages.GapSeconds);
        string? error = CheckSprites(sim, layer, art);
        if (error is not null)
        {
            return error;
        }

        if (!toasts.Contains(ResidentMessages.ArrivedKey))
        {
            return $"residents: no toast for the arrived residents ({string.Join('|', toasts)})";
        }

        error = CheckPicking(host, root, layer, picker);
        if (error is not null)
        {
            return error;
        }

        error = CheckHungerDays(host, messages, layer, toasts);
        if (error is not null)
        {
            return error;
        }

        // Rebinding to a new simulation empties the layer; the pooled sprites go with it.
        layer.Unbind();
        if (layer.Count != 0 || layer.PoolSize != 0)
        {
            return "residents: Unbind left sprites behind";
        }

        GD.Print("Smoke residents: sprites, picking, departure and event toasts OK");
        return null;
    }

    // The three residents have sprites of the right frames at the places Core says.
    private static string? CheckSprites(CoreSimulation sim, ResidentLayer layer, WorldArt art)
    {
        GameWorld world = sim.World!;
        if (world.Residents.Count != 3 || layer.Count != 3)
        {
            return $"residents: expected 3 residents and sprites, Core has {world.Residents.Count}, the layer {layer.Count}";
        }

        ReadOnlySpan<EntityId> entities = world.Residents.Entities;
        string[] expected = [ResidentLayer.ArtPrefix + "carrier", ResidentLayer.SpecialistArtId, ResidentLayer.UnemployedArtId];
        for (int i = 0; i < entities.Length; i++)
        {
            EntityId entity = entities[i];
            Sprite2D? sprite = layer.SpriteOf(entity);
            if (sprite?.Texture is not AtlasTexture texture || layer.ArtIdOf(entity) != expected[i])
            {
                return $"residents: resident {i} has art '{layer.ArtIdOf(entity)}', expected '{expected[i]}'";
            }

            art.Table.TryGet(expected[i], out AtlasCell cell);
            if (texture.Region != new Rect2(cell.Region.Position, cell.Region.Size))
            {
                return $"residents: resident {i} shows the wrong region of the atlas";
            }

            Core.Entities.Position position = sim.Positions.Get(entity);
            Vector2 ground = EntityMotion.GroundPoint(position);
            if (layer.NodeOf(entity) is not { } node || node.Position.DistanceTo(ground) > 0.5f)
            {
                return $"residents: resident {i} is not drawn at its Core position";
            }
        }

        return null;
    }

    // A click on a sprite selects the resident, on empty ground clears; build mode blocks the click.
    private static string? CheckPicking(SimulationHost host, GameRoot root, ResidentLayer layer, ResidentPicker picker)
    {
        EntityId target = host.Simulation.World!.Residents.Entities[0];
        var raised = new List<EntityId>();
        root.ResidentSelection.ResidentSelected += raised.Add;
        Vector2 onSprite = layer.NodeOf(target)!.Position + layer.SpriteOf(target)!.Position + new Vector2(0, -12);

        root.InputState.BuildModeActive = true;
        bool blocked = picker.ClickAt(new Vector2(10, 10));
        root.InputState.BuildModeActive = false;
        if (blocked || root.ResidentSelection.Selected != EntityId.None)
        {
            return "residents: a click during build mode must not select";
        }

        // Residents may share a tile; then the layer's answer (the one in front) is the one that must be selected.
        if (!layer.TryPick(onSprite, out EntityId picked) || !picker.SelectAtWorld(onSprite) || root.ResidentSelection.Selected != picked)
        {
            return "residents: a click on the sprite did not select a resident";
        }

        target = picked;
        if (layer.RingOf(target)?.Visible != true)
        {
            return "residents: the selection ring is not shown";
        }

        if (raised.Count != 1 || raised[0] != target)
        {
            return "residents: ResidentSelected was not raised once with the resident";
        }

        if (picker.SelectAtWorld(onSprite + new Vector2(5000, 5000)) || root.ResidentSelection.Selected != EntityId.None
            || layer.RingOf(target)?.Visible != false || raised.Count != 2 || raised[1] != EntityId.None)
        {
            return "residents: a click on empty ground must clear the selection and stay free for the hero";
        }

        return null;
    }

    // Three hungry days: shortage toasts, then the first resident leaves, its sprite goes back to the pool.
    private static string? CheckHungerDays(SimulationHost host, ResidentMessages messages, ResidentLayer layer, List<string> toasts)
    {
        CoreSimulation sim = host.Simulation;
        toasts.Clear();
        for (int tick = 0; tick < (3 * TicksPerDay) + LeaveWalkTicks && sim.World!.Residents.Count == 3; tick++)
        {
            sim.Step();
            messages.Poll(ResidentMessages.GapSeconds);
        }

        for (int i = 0; i < ResidentMessages.QueueLimit; i++)
        {
            messages.Poll(ResidentMessages.GapSeconds);
        }

        if (sim.World!.Residents.Count >= 3)
        {
            return "residents: nobody left after the hungry days";
        }

        layer.Sync();
        if (layer.Count != sim.World.Residents.Count || layer.PoolSize < 3)
        {
            return $"residents: {layer.Count} sprites for {sim.World.Residents.Count} residents (pool {layer.PoolSize})";
        }

        if (!toasts.Contains(ResidentMessages.FoodShortageKey))
        {
            return $"residents: no food shortage toast ({string.Join('|', toasts)})";
        }

        if (!toasts.Contains(ResidentMessages.LeftHungryKey))
        {
            return $"residents: no toast for the resident who left hungry ({string.Join('|', toasts)})";
        }

        return toasts.Any(t => t.StartsWith("ui.residents.toast.tax", StringComparison.Ordinal) || t.Contains('+', StringComparison.Ordinal))
            ? null
            : $"residents: no tax toast ({string.Join('|', toasts)})";
    }

    private static EntityId Place(SimulationHost host, string building)
    {
        CoreSimulation sim = host.Simulation;
        var start = sim.World!.Map.Start;
        for (int ring = 2; ring < MapSize; ring++)
        {
            for (int y = start.Y - ring; y <= start.Y + ring; y++)
            {
                for (int x = start.X - ring; x <= start.X + ring; x++)
                {
                    if (Math.Max(Math.Abs(x - start.X), Math.Abs(y - start.Y)) != ring || !Construction.CheckPlacement(sim, building, x, y, 0).IsOk)
                    {
                        continue;
                    }

                    long before = sim.World.LastBuildingEventSequence;
                    host.Send(new PlaceBuildingCommand(building, x, y));
                    sim.Step();
                    BuildingEvent placed = sim.World.BuildingEvents[^1];
                    return sim.World.LastBuildingEventSequence == before + 1 && placed.Result == BuildResult.Ok ? placed.Building : EntityId.None;
                }
            }
        }

        return EntityId.None;
    }
}
