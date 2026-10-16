using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Heroes;
using Core.Sim;
using Game.Gameplay;
using Game.World;
using Godot;

namespace Game.Smoke;

/// <summary>
/// Building part of the smoke test: build mode (preview colours, rotation anchor, flags, command), the building sprites under the
/// y-sorted objects root (frame follows the construction stage) and the selection frame. Pixels are not checked (headless).
/// Returns null when everything passed, otherwise the reason.
/// </summary>
public static class BuildingRenderSmoke
{
    private const int SearchRadius = 16;
    private const int MaxSteps = 300;

    /// <summary>Runs the checks on a revealed new game shown by <paramref name="view"/>; the nodes it adds are freed again.</summary>
    public static string? Check(WorldView view, Simulation sim)
    {
        var layer = new BuildingLayer();
        var overlay = new FootprintOverlay();
        var mode = new BuildMode();
        view.AddChild(layer);
        view.AddChild(overlay);
        view.AddChild(mode);
        try
        {
            var selection = new BuildingSelection();
            var input = new GameInputState();
            layer.Attach(view);
            overlay.Attach(view, selection);
            mode.Attach(view, input, selection, overlay, command => sim.Enqueue(command));
            return Run(view, sim, layer, overlay, mode, selection, input);
        }
        finally
        {
            mode.Cancel();
            view.RemoveChild(layer);
            view.RemoveChild(overlay);
            view.RemoveChild(mode);
            layer.Free();
            overlay.Free();
            mode.Free();
        }
    }

    private static string? Run(WorldView view, Simulation sim, BuildingLayer layer, FootprintOverlay overlay, BuildMode mode, BuildingSelection selection, GameInputState input)
    {
        GameWorld world = sim.World!;
        GiveWood(sim, 50);
        if (!FindSpot(sim, "campfire", EntityId.None, world.Map.Start.X, world.Map.Start.Y, out int cx, out int cy))
        {
            return "buildings: no free spot for a campfire near the start";
        }

        // Build mode flags, preview colours and the rotation anchor.
        if (mode.Begin("no_such_building") || mode.IsActive || input.BuildModeActive)
        {
            return "buildings: build mode started for an unknown building";
        }

        if (!mode.Begin("campfire") || !input.BuildModeActive || !mode.IsActive)
        {
            return "buildings: build mode did not set BuildModeActive";
        }

        if (!mode.Preview(cx, cy) || !overlay.PreviewIsOk)
        {
            return $"buildings: campfire preview at {cx},{cy} is not green ({mode.Placement.Reason})";
        }

        if (mode.Preview(-1, -1) || mode.Placement.Reason != BuildResult.OutOfMap || overlay.PreviewIsOk || !overlay.HasPreview)
        {
            return $"buildings: preview outside the map is not red OutOfMap ({mode.Placement.Reason})";
        }

        mode.Cancel();
        if (input.BuildModeActive || mode.IsActive || overlay.HasPreview)
        {
            return "buildings: leaving build mode kept the flag or the preview";
        }

        if (!mode.Begin("storage"))
        {
            return "buildings: storage build mode did not start";
        }

        mode.Preview(cx, cy);

        if (overlay.PreviewRect != (cx, cy, 3, 2))
        {
            return $"buildings: storage preview {overlay.PreviewRect}, want ({cx}, {cy}, 3, 2)";
        }

        mode.RotateStep();
        if (mode.Rotation != 1 || overlay.PreviewRect != (cx, cy, 2, 3))
        {
            return $"buildings: rotated storage preview {overlay.PreviewRect} rotation {mode.Rotation}, want ({cx}, {cy}, 2, 3) rotation 1";
        }

        mode.Cancel();

        // Placing through the mode sends the command and leaves the mode.
        if (!mode.Begin("campfire") || !mode.Preview(cx, cy) || !mode.Confirm() || input.BuildModeActive)
        {
            return "buildings: confirming the campfire did not send it and leave the mode";
        }

        sim.Step();
        layer.Refresh();
        if (world.Buildings.Count != 1 || layer.SpriteCount != 1)
        {
            return $"buildings: {world.Buildings.Count} buildings and {layer.SpriteCount} sprites after the placement";
        }

        EntityId campfire = world.Buildings.Entities[0];
        string? sprites = CheckStages(view, sim, layer, campfire);
        if (sprites is not null)
        {
            return sprites;
        }

        // Selection frame and a red preview over the standing building.
        selection.Select(campfire);
        overlay._Process(0);
        if (overlay.SelectionRect != (cx, cy, 1, 1))
        {
            return $"buildings: selection frame {overlay.SelectionRect}, want ({cx}, {cy}, 1, 1)";
        }

        if (!mode.Begin("house") || mode.Preview(cx, cy) || mode.Placement.Reason == BuildResult.Ok || overlay.PreviewIsOk)
        {
            return $"buildings: a house over the campfire is not red ({mode.Placement.Reason})";
        }

        mode.Cancel();
        selection.Clear();
        overlay._Process(0);
        if (overlay.SelectionRect is not null)
        {
            return "buildings: the selection frame stayed after clearing";
        }

        return CheckMove(sim, layer, mode, input, campfire, cx, cy);
    }

    // The sprite sits under the y-sorted root and its frame follows the construction stage s0..s3.
    private static string? CheckStages(WorldView view, Simulation sim, BuildingLayer layer, EntityId campfire)
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < MaxSteps; i++)
        {
            layer.Refresh();
            Building building = sim.World!.Buildings.Get(campfire);
            (int level, int stage) = BuildingLayer.FrameOf(sim.Content, building);
            string want = $"campfire_l{level}_s{stage}";
            string? have = layer.FrameIdOf(campfire);
            if (have != want)
            {
                return $"buildings: frame {have} at progress {Construction.ProgressPermille(sim.Content, building)}, want {want}";
            }

            Sprite2D? sprite = layer.SpriteOf(campfire);
            if (sprite is null || sprite.GetParent() != view.ObjectsRoot || !sprite.RegionEnabled || sprite.RegionRect.Size.X <= 0)
            {
                return "buildings: the sprite is not a region sprite under the objects root";
            }

            seen.Add(have);
            if (building.State == BuildingState.Active)
            {
                break;
            }

            sim.Step();
        }

        string[] all = ["campfire_l1_s0", "campfire_l1_s1", "campfire_l1_s2", "campfire_l1_s3"];
        if (!all.All(seen.Contains) || sim.World!.Buildings.Get(campfire).State != BuildingState.Active)
        {
            return $"buildings: construction showed frames [{string.Join(", ", seen)}], want all four stages and an active building";
        }

        return null;
    }

    // A move keeps the old sprite finished and marks the target with a ghost until it ends.
    private static string? CheckMove(Simulation sim, BuildingLayer layer, BuildMode mode, GameInputState input, EntityId campfire, int cx, int cy)
    {
        if (!mode.BeginMove(campfire) || !input.BuildModeActive || mode.MovingBuilding != campfire)
        {
            return "buildings: move mode did not start";
        }

        if (!FindSpot(sim, "campfire", campfire, cx + 4, cy + 4, out int tx, out int ty) || (tx == cx && ty == cy))
        {
            return "buildings: no free spot to move the campfire to";
        }

        if (!mode.Preview(tx, ty) || !mode.Confirm() || input.BuildModeActive)
        {
            return $"buildings: moving to {tx},{ty} was not accepted ({mode.Placement.Reason})";
        }

        sim.Step();
        layer.Refresh();
        if (layer.GhostOf(campfire) is null || layer.FrameIdOf(campfire) != "campfire_l1_s3")
        {
            return $"buildings: during a move want a ghost and the finished frame, got {layer.FrameIdOf(campfire)}";
        }

        for (int i = 0; i < MaxSteps && sim.World!.Buildings.Get(campfire).State != BuildingState.Active; i++)
        {
            sim.Step();
        }

        layer.Refresh();
        Building moved = sim.World!.Buildings.Get(campfire);
        if (layer.GhostOf(campfire) is not null || moved.X != tx || moved.Y != ty)
        {
            return $"buildings: after the move the ghost stayed or the place is {moved.X},{moved.Y}, want {tx},{ty}";
        }

        GD.Print($"Smoke buildings: campfire {cx},{cy} built through s0-s3, moved to {tx},{ty}; build mode, preview and selection ok");
        return null;
    }

    private static void GiveWood(Simulation sim, int count)
    {
        sim.Content.TryGetId("wood", out DefId id);
        ref Inventory backpack = ref sim.World!.Inventories.Get(sim.World.HeroEntity);
        backpack = backpack.Add(id, count, sim.Content.Get<ItemDef>(id).StackSize, out _);
    }

    // The first tile, scanning rings around (x, y), where Core accepts the building.
    private static bool FindSpot(Simulation sim, string building, EntityId moving, int x, int y, out int foundX, out int foundY)
    {
        for (int r = 0; r <= SearchRadius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Construction.CheckPlacement(sim, building, x + dx, y + dy, 0, moving).IsOk)
                    {
                        foundX = x + dx;
                        foundY = y + dy;
                        return true;
                    }
                }
            }
        }

        foundX = 0;
        foundY = 0;
        return false;
    }
}
