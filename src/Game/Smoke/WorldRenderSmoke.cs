using Core.Content;
using Core.Fog;
using Core.Geometry;
using Core.Sim;
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
        return BuildingRenderSmoke.Check(view, sim);
    }
}
