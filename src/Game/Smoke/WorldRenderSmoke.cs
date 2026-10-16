using Core.Content;
using Core.Fog;
using Core.Geometry;
using Core.Sim;
using Core.Tiles;
using Core.WorldGen;
using Game.World;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Smoke;

/// <summary>
/// World rendering part of the smoke test (ADR 0010, ADR 0011): the Core isometric formula against the engine's
/// <c>TileMapLayer.MapToLocal</c>/<c>LocalToMap</c>, chunk layers of a 128² new game around the start, fog mask and
/// minimap uploads. Returns null when everything passed, otherwise the reason.
/// </summary>
public static class WorldRenderSmoke
{
    private const int MapSize = 128;
    private const int PixelSamples = 4000;

    /// <summary>Runs the checks with nodes parented under <paramref name="host"/> (freed before returning).</summary>
    public static string? Check(Node host, ContentCatalog content, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!WorldArt.TryGetShared(out WorldArt art, out string error))
        {
            return "world art: " + error;
        }

        string? iso = CheckProjection(art);
        if (iso is not null)
        {
            return iso;
        }

        CoreSimulation sim = CoreSimulation.CreateNewGame(content, new NewGameOptions(seed, MapSize, Difficulty.Normal));
        WorldView view = GD.Load<PackedScene>(WorldView.ScenePath).Instantiate<WorldView>();
        MinimapView minimap = GD.Load<PackedScene>(MinimapView.ScenePath).Instantiate<MinimapView>();
        host.AddChild(view);
        host.AddChild(minimap);
        try
        {
            return CheckView(view, minimap, sim);
        }
        finally
        {
            view.Unbind();
            minimap.Unbind();
            host.RemoveChild(view);
            host.RemoveChild(minimap);
            view.Free();
            minimap.Free();
        }
    }

    // MapToLocal of a TileMapLayer with the game's TileSet equals IsoProjection.TileCenter, and LocalToMap of pixel
    // centres equals IsoProjection.TileAtPixel.
    private static string? CheckProjection(WorldArt art)
    {
        var layer = new TileMapLayer { TileSet = art.TileSet };
        try
        {
            for (int y = -40; y <= 600; y += 7)
            {
                for (int x = -40; x <= 600; x += 5)
                {
                    Vector2 local = layer.MapToLocal(new Vector2I(x, y));
                    (int px, int py) = IsoProjection.TileCenter(x, y);
                    if (local != new Vector2(px, py))
                    {
                        return $"iso: MapToLocal({x},{y}) = {local}, Core TileCenter = ({px},{py})";
                    }
                }
            }

            uint state = 0x9E3779B9u;
            for (int i = 0; i < PixelSamples; i++)
            {
                state = (state * 1664525u) + 1013904223u;
                int px = (int)(state % 40000u) - 20000;
                state = (state * 1664525u) + 1013904223u;
                int py = (int)(state % 20000u) - 2000;
                Vector2I cell = layer.LocalToMap(new Vector2(px + 0.5f, py + 0.5f));
                (int tx, int ty) = IsoProjection.TileAtPixel(px, py);
                if (cell != new Vector2I(tx, ty))
                {
                    return $"iso: LocalToMap({px}+0.5,{py}+0.5) = {cell}, Core TileAtPixel = ({tx},{ty})";
                }
            }
        }
        finally
        {
            layer.Free();
        }

        GD.Print($"Smoke world render: iso formula matches MapToLocal/LocalToMap ({PixelSamples} pixel samples)");
        return null;
    }

    // Camera API: goto requests, visible tile rect, clamping to the map diamond, zoom steps.
    private static string? CheckCamera(WorldCamera camera, StartPoint start)
    {
        if (!camera.Apply(new Core.DevConsole.CameraRequest(start.X, start.Y)) || !camera.VisibleTileRect.HasPoint(new Vector2I(start.X, start.Y)))
        {
            return $"camera: visible tiles {camera.VisibleTileRect} miss the start {start.X},{start.Y}";
        }

        camera.CenterOnWorld(new Vector2(-1e6f, -1e6f));
        (int x, int y) = IsoProjection.TileAtPixel((int)camera.Position.X, (int)camera.Position.Y);
        if (x < -1 || y < -1 || x > MapSize || y > MapSize)
        {
            return $"camera: centre {camera.Position} (tile {x},{y}) left the map";
        }

        int zoom = camera.ZoomIndex;
        camera.StepZoom(-1);
        if (camera.ZoomIndex != zoom - 1)
        {
            return $"camera: zoom step {zoom} -> {camera.ZoomIndex}";
        }

        camera.SetZoomIndex(zoom);
        camera.CenterOnTile(start.X, start.Y);
        return null;
    }

    private static string? CheckView(WorldView view, MinimapView minimap, CoreSimulation sim)
    {
        GameWorld world = sim.World!;
        view.Bind(sim);
        minimap.Bind(sim, view.Camera);
        view.BuildNow();
        StartPoint start = world.Map.Start;
        ChunkCoord startChunk = ChunkCoord.OfTile(start.X, start.Y);
        if (!view.Chunks.Active.TryGetValue(startChunk, out WorldChunk? chunk))
        {
            return $"world render: start chunk {startChunk} not built";
        }

        int chunkTiles = ChunkCoord.Size * ChunkCoord.Size;
        int ground = chunk.Ground.GetUsedCells().Count;
        int objects = chunk.Objects.GetUsedCells().Count;
        int expectedObjects = view.Chunks.ExpectedObjectCount(startChunk);
        if (ground != chunkTiles || objects != expectedObjects || objects == 0)
        {
            return $"world render: start chunk has {ground} ground cells (want {chunkTiles}) and {objects} objects (want {expectedObjects} > 0)";
        }

        var marker = new Vector2I(start.X, start.Y);
        if (chunk.Objects.GetCellSourceId(marker) != WorldArt.ObjectSource || chunk.Objects.GetCellAtlasCoords(marker) != view.Chunks.Art!.ObjectTile(WorldObject.StartMarker))
        {
            return "world render: no start marker on the start tile";
        }

        int totalGround = view.Chunks.Active.Values.Sum(c => c.Ground.GetUsedCells().Count);
        if (totalGround != chunkTiles * view.Chunks.Active.Count)
        {
            return $"world render: {view.Chunks.Active.Count} chunks hold {totalGround} ground cells";
        }

        if (view.Fog.UploadCount != 1 || minimap.UploadCount != 1)
        {
            return $"world render: fog mask uploads {view.Fog.UploadCount}, minimap uploads {minimap.UploadCount} (want 1 each)";
        }

        if (minimap.GetTileColor(start.X, start.Y) == Colors.Black || world.Fog.GetState(0, 0) != FogState.Unknown || minimap.GetTileColor(0, 0) != Colors.Black)
        {
            return "world render: minimap colours do not follow the fog";
        }

        string? camera = CheckCamera(view.Camera, start);
        if (camera is not null)
        {
            return camera;
        }

        // Revealing the map re-uploads the fog and the minimap once and adds objects on newly explored tiles.
        sim.Enqueue(new RevealAllCommand());
        sim.Step();
        view.BuildNow();
        minimap.Refresh();
        int revealedObjects = chunk.Objects.GetUsedCells().Count;
        if (view.Fog.UploadCount != 2 || minimap.UploadCount != 2 || revealedObjects != view.Chunks.ExpectedObjectCount(startChunk) || revealedObjects < objects)
        {
            return $"world render: after reveal fog uploads {view.Fog.UploadCount}, minimap uploads {minimap.UploadCount}, objects {objects} -> {revealedObjects}";
        }

        GD.Print($"Smoke world render: map {MapSize}, {view.Chunks.Active.Count} chunks, start chunk {ground} ground + {objects} -> {revealedObjects} object cells");
        return CheckHero(view, sim, start) ?? BuildingRenderSmoke.Check(view, sim);
    }

    // Picking, the hero sprite, a move order and a gather order through the Core commands (FR-HERO-01, FR-HERO-02).
    private static string? CheckHero(WorldView view, CoreSimulation sim, StartPoint start)
    {
        GameWorld world = sim.World!;
        WorldArt art = view.Chunks.Art!;
        string? picked = CheckPicking(view, world, art, start);
        if (picked is not null)
        {
            return picked;
        }

        var layer = new EntityLayer();
        view.ObjectsRoot.AddChild(layer);
        try
        {
            layer.Bind(sim, art);
            string? result = CheckHeroSprite(layer, sim, world, art, start) ?? CheckHeroOrders(view, layer, sim, world);
            if (result is null)
            {
                GD.Print("Smoke world render: picking, hero sprite, move and gather orders ok");
            }

            return result;
        }
        finally
        {
            layer.Unbind();
            view.ObjectsRoot.RemoveChild(layer);
            layer.Free();
        }
    }

    // On flat ground the picked tile is the one under the cursor: it matches the engine LocalToMap of the ground-plane centre and the
    // tile itself at the centre of its raised top face.
    private static string? CheckPicking(WorldView view, GameWorld world, WorldArt art, StartPoint start)
    {
        WorldCamera camera = view.Camera;
        camera.CenterOnTile(start.X, start.Y);
        Vector2 half = camera.GetViewportRect().Size / 2;
        TerrainMap terrain = world.Map.Terrain;
        int samples = 0;
        int engineSamples = 0;
        for (int y = start.Y - 8; y <= start.Y + 8; y++)
        {
            for (int x = start.X - 8; x <= start.X + 8; x++)
            {
                if (!IsFlat(terrain, x, y, 2))
                {
                    continue;
                }

                int h = art.VisualHeight(terrain.GetBiome(x, y), terrain.GetFeatures(x, y), terrain.GetHeight(x, y));
                (int px, int py) = IsoProjection.TileCenter(x, y, h);
                Vector2 screen = ((new Vector2(px, py) - camera.Position) * camera.Zoom.X) + half;
                if (!WorldPicker.TryPickTile(view, screen, out int tx, out int ty) || tx != x || ty != y)
                {
                    return $"picking: tile {x},{y} (height {h}) picked as {tx},{ty}";
                }

                samples++;
                if (view.Chunks.Active.TryGetValue(ChunkCoord.OfTile(x, y), out WorldChunk? chunk))
                {
                    Vector2I cell = chunk.Ground.LocalToMap(new Vector2(px, py + (IsoProjection.HeightStep * h)));
                    if (cell != new Vector2I(tx, ty))
                    {
                        return $"picking: picked {tx},{ty} but LocalToMap gives {cell}";
                    }

                    engineSamples++;
                }
            }
        }

        if (samples == 0)
        {
            return "picking: no flat tile near the start to test";
        }

        GD.Print($"Smoke world render: picker matches {samples} flat tiles ({engineSamples} against LocalToMap)");
        return null;
    }

    private static bool IsFlat(TerrainMap terrain, int x, int y, int radius)
    {
        if (!terrain.Contains(x - radius, y - radius) || !terrain.Contains(x + radius, y + radius))
        {
            return false;
        }

        int height = terrain.GetHeight(x, y);
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (terrain.GetHeight(x + dx, y + dy) != height)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? CheckHeroSprite(EntityLayer layer, CoreSimulation sim, GameWorld world, WorldArt art, StartPoint start)
    {
        if (layer.HeroSprite is null || !layer.HasHero)
        {
            return "hero: no sprite was created";
        }

        if (!sim.Positions.TryGet(world.HeroEntity, out Core.Entities.Position position))
        {
            return "hero: the hero entity has no position";
        }

        (int cx, int cy) = IsoProjection.TileCenter(position.TileX, position.TileY);
        if (layer.HeroGroundPoint.DistanceTo(new Vector2(cx, cy)) > 0.5f || position.TileX != start.X || position.TileY != start.Y)
        {
            return $"hero: sprite at {layer.HeroGroundPoint}, tile {position.TileX},{position.TileY} centre ({cx},{cy}), start {start.X},{start.Y}";
        }

        TerrainMap terrain = world.Map.Terrain;
        int lift = IsoProjection.HeightStep * art.VisualHeight(terrain.GetBiome(start.X, start.Y), terrain.GetFeatures(start.X, start.Y), terrain.GetHeight(start.X, start.Y));
        if (!Mathf.IsEqualApprox(layer.HeroSprite.Position.Y, -lift))
        {
            return $"hero: sprite lift {layer.HeroSprite.Position.Y}, tile height lift {lift}";
        }

        return null;
    }

    private static string? CheckHeroOrders(WorldView view, EntityLayer layer, CoreSimulation sim, GameWorld world)
    {
        Core.Pathfinding.PassabilityGrid grid = world.Pathfinder.Grid;
        Core.Entities.EntityId hero = world.HeroEntity;
        Core.Entities.Position before = sim.Positions.Get(hero);

        // Move: the nearest walkable tile 3 to 5 tiles away.
        (int X, int Y)? target = null;
        for (int d = 3; d <= 5 && target is null; d++)
        {
            foreach ((int dx, int dy) in new[] { (d, 0), (0, d), (-d, 0), (0, -d), (d, d), (-d, -d) })
            {
                if (grid.IsPassable(before.TileX + dx, before.TileY + dy))
                {
                    target = (before.TileX + dx, before.TileY + dy);
                    break;
                }
            }
        }

        if (target is null)
        {
            return "hero: no walkable tile near the start";
        }

        sim.Enqueue(new MoveHeroCommand(target.Value.X, target.Value.Y));
        sim.Step(5);
        Core.Entities.Position moved = sim.Positions.Get(hero);
        if (moved == before)
        {
            world.Heroes.TryGet(hero, out Core.Heroes.Hero state);
            return $"hero: no movement after a move order to {target.Value.X},{target.Value.Y} ({state.LastOrder})";
        }

        for (int i = 0; i < 600 && world.Heroes.Get(hero).Activity != Core.Heroes.HeroActivity.Idle; i++)
        {
            sim.Step();
        }

        Core.Entities.Position arrived = sim.Positions.Get(hero);
        layer.Sync();
        (int ax, int ay) = IsoProjection.TileCenter(arrived.TileX, arrived.TileY);
        if (arrived.TileX != target.Value.X || arrived.TileY != target.Value.Y || layer.HeroGroundPoint.DistanceTo(new Vector2(ax, ay)) > 0.5f)
        {
            return $"hero: arrived at {arrived.TileX},{arrived.TileY}, wanted {target.Value.X},{target.Value.Y}; sprite {layer.HeroGroundPoint}";
        }

        // Camera follow and the key panning switch.
        WorldCamera camera = view.Camera;
        if (!camera.KeyPanEnabled)
        {
            return "camera: key panning is off by default";
        }

        camera.FollowProvider = () => layer.HeroFollowPoint;
        camera.Recenter();
        bool following = camera.IsFollowing;
        camera.FollowProvider = null;
        if (!following)
        {
            return "camera: Recenter does not follow the hero";
        }

        return CheckGather(view, sim, world);
    }

    // The hero walks to and fro until a tile becomes a trail; the roads layer of its chunk then holds that overlay cell (FR-WORLD-15).
    private static string? CheckRoads(WorldView view, CoreSimulation sim, GameWorld world)
    {
        Core.Entities.EntityId hero = world.HeroEntity;
        Core.Pathfinding.PassabilityGrid grid = world.Pathfinder.Grid;
        Core.Entities.Position from = sim.Positions.Get(hero);
        (int X, int Y)? far = null;
        for (int d = 8; d >= 3 && far is null; d--)
        {
            foreach ((int dx, int dy) in new[] { (d, 0), (0, d), (-d, 0), (0, -d) })
            {
                bool clear = true;
                for (int i = 0; i <= d && clear; i++)
                {
                    clear = grid.IsPassable(from.TileX + (Math.Sign(dx) * i), from.TileY + (Math.Sign(dy) * i));
                }

                if (clear)
                {
                    far = (from.TileX + dx, from.TileY + dy);
                    break;
                }
            }
        }

        if (far is null)
        {
            return "roads: no straight walkable line near the hero";
        }

        (int X, int Y)[] ends = [(from.TileX, from.TileY), far.Value];
        int trail = -1;
        for (int trip = 0; trip < 200 && trail < 0; trip++)
        {
            sim.Enqueue(new MoveHeroCommand(ends[(trip + 1) % 2].X, ends[(trip + 1) % 2].Y));
            for (int i = 0; i < 600; i++)
            {
                sim.Step();
                if (world.Heroes.Get(hero).Activity == Core.Heroes.HeroActivity.Idle && i > 0)
                {
                    break;
                }
            }

            for (int i = 0; i <= 8 && trail < 0; i++)
            {
                int x = from.TileX + (Math.Sign(far.Value.X - from.TileX) * i);
                int y = from.TileY + (Math.Sign(far.Value.Y - from.TileY) * i);
                if (world.Tiles.GetRoadClass(x, y) != RoadClass.None)
                {
                    trail = (y * world.Size) + x;
                }
            }
        }

        if (trail < 0)
        {
            return "roads: no trail appeared after 200 trips of the hero";
        }

        int tx = trail % world.Size;
        int ty = trail / world.Size;
        RoadClass road = world.Tiles.GetRoadClass(tx, ty);
        view.BuildNow();
        WorldChunk chunk = view.Chunks.Active[ChunkCoord.OfTile(tx, ty)];
        WorldArt art = view.Chunks.Art!;
        var cell = new Vector2I(tx, ty);
        Vector2I? want = art.OverlayTile(road, WorldArt.OverlayVariantOf(tx, ty));
        int expected = view.Chunks.ExpectedRoadCount(chunk.Coord);
        int roadCells = chunk.Roads.GetUsedCells().Count;
        if (want is null || chunk.Roads.GetCellSourceId(cell) != WorldArt.GroundSource || chunk.Roads.GetCellAtlasCoords(cell) != want
            || chunk.Roads.GetCellAlternativeTile(cell) != art.VisualHeight(world.Map.Terrain.GetBiome(tx, ty), world.Map.Terrain.GetFeatures(tx, ty), world.Map.Terrain.GetHeight(tx, ty)) || roadCells != expected || expected == 0)
        {
            return $"roads: {road} at {tx},{ty} is not in the roads layer ({roadCells} cells, want {expected})";
        }

        GD.Print($"Smoke world render: walked tile {tx},{ty} is a {road} and the roads layer shows it ({roadCells} cells)");
        return null;
    }

    private static string? CheckGather(WorldView view, CoreSimulation sim, GameWorld world)
    {
        Core.Entities.EntityId hero = world.HeroEntity;
        Core.Entities.Position at = sim.Positions.Get(hero);
        var trees = new List<(int X, int Y)>();
        for (int y = at.TileY - 30; y <= at.TileY + 30; y++)
        {
            for (int x = at.TileX - 30; x <= at.TileX + 30; x++)
            {
                if (world.Map.Terrain.Contains(x, y) && world.Tiles.GetEffectiveResource(x, y) == ResourceKind.Tree
                    && view.Chunks.Active.ContainsKey(ChunkCoord.OfTile(x, y)))
                {
                    trees.Add((x, y));
                }
            }
        }

        trees.Sort((a, b) => Math.Max(Math.Abs(a.X - at.TileX), Math.Abs(a.Y - at.TileY)).CompareTo(Math.Max(Math.Abs(b.X - at.TileX), Math.Abs(b.Y - at.TileY))));
        foreach ((int x, int y) in trees.Take(20))
        {
            sim.Enqueue(new GatherCommand(x, y));
            sim.Step();
            if (world.Heroes.Get(hero).LastOrder != Core.Heroes.HeroOrderResult.Accepted)
            {
                continue;
            }

            WorldChunk chunk = view.Chunks.Active[ChunkCoord.OfTile(x, y)];
            var cell = new Vector2I(x, y);
            if (chunk.Objects.GetCellSourceId(cell) != WorldArt.ObjectSource)
            {
                return $"gather: the tree at {x},{y} is not drawn before gathering";
            }

            for (int i = 0; i < 3000 && world.Tiles.GetEffectiveResource(x, y) != ResourceKind.None; i++)
            {
                sim.Step();
            }

            if (world.Tiles.GetEffectiveResource(x, y) != ResourceKind.None)
            {
                return $"gather: the tree at {x},{y} was not used up ({world.Heroes.Get(hero).LastStop})";
            }

            view.BuildNow();
            if (world.Tiles.GetFlora(x, y) != FloraState.Stump)
            {
                return $"gather: no stump at {x},{y} after the tree was cut ({world.Tiles.GetFlora(x, y)})";
            }

            WorldArt art = view.Chunks.Art!;
            if (chunk.Objects.GetCellSourceId(cell) != WorldArt.ObjectSource || chunk.Objects.GetCellAtlasCoords(cell) != art.ObjectTile(WorldObject.Stump))
            {
                return $"gather: the tree at {x},{y} is not drawn as a stump after it was cut";
            }

            GD.Print($"Smoke world render: felled tree at {x},{y} shows a stump");
            return CheckRoads(view, sim, world);
        }

        return $"gather: no reachable tree near the hero to test ({trees.Count} candidates, last order {world.Heroes.Get(hero).LastOrder})";
    }
}
