using System.Globalization;
using Core.Content;
using Core.Sim;
using Core.WorldGen;
using Game.Sim;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.World;

/// <summary>
/// Windowed map rendering benchmark (NFR-01, M1 acceptance), run by <c>tools/bench-render.sh</c>:
/// <c>godot --path src/Game res://World/RenderBench.tscn -- --bench-render [--bench-shots=DIR] [--bench-size=512]</c>.
/// Creates a new game (seed 1), reveals the whole map, then scrolls corner to corner at every zoom step and sweeps the
/// whole map at the widest zoom with vsync off, and prints
/// <c>BENCH avg_fps=.. min_fps=.. p1_low=..</c>. Exit code 0 when the average is at least <see cref="TargetFps"/>.
/// With <c>--bench-shots=DIR</c> it first saves review screenshots (fog by day and night, revealed map at every zoom,
/// hills, water) into DIR. With <c>--bench-shots-only</c> it stops after the screenshots.
/// </summary>
public partial class RenderBench : Node2D
{
    /// <summary>Required average frames per second.</summary>
    public const double TargetFps = 60;

    // macOS composites windowed apps at the display refresh even with vsync off, so a 60 Hz display yields at most
    // 60.0 FPS with ±0.1 jitter; the pass check tolerates 0.5 FPS below the target. uncapped_fps_est (main-thread CPU,
    // render CPU and GPU time per frame) shows the real headroom.
    private const double RefreshJitterFps = 0.5;

    private const ulong Seed = 1;
    private const double PhaseSeconds = 6.0;
    private const double SweepSpeed = 4000.0; // world px per second (1000 screen px/s at the widest zoom)
    private const double WarmupSeconds = 1.0;
    private const double MaxSweepSeconds = 90.0;
    private const double TimeLimitSeconds = 170.0;
    private const int FailureExitCode = 1;
    private const int ErrorExitCode = 2;

    private readonly List<double> _frameMs = [];
    private readonly List<(double Process, double RenderCpu, double Gpu, double Chunks, int Builds)> _work = [];
    private readonly List<List<(double Process, double RenderCpu, double Gpu, double Chunks, int Builds)>> _phaseWork = [];
    private WorldView _view = null!;
    private MinimapView _minimap = null!;
    private CoreSimulation _sim = null!;
    private readonly HashSet<int> _gcAt = [];
    private int _lastGcCount;
    private ulong _processStartUsec;
    private double _lastCpuMs;
    private ulong _lastUsec;
    private ulong _startUsec;
    private bool _measuring;

    public override void _Ready()
    {
        _startUsec = Time.GetTicksUsec();
        ProcessPriority = int.MinValue; // first _Process of the frame: start of the main-thread work
        RenderingServer.FramePreDraw += () => _lastCpuMs = (Time.GetTicksUsec() - _processStartUsec) / 1000.0;
        RunGuarded();
    }

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        _processStartUsec = now;
        if (_measuring && _lastUsec != 0)
        {
            _frameMs.Add((now - _lastUsec) / 1000.0);
            int gcs = GC.CollectionCount(0);
            if (gcs != _lastGcCount)
            {
                _gcAt.Add(_frameMs.Count - 1);
                _lastGcCount = gcs;
            }
            Rid viewport = GetViewport().GetViewportRid();
            _work.Add((
                _lastCpuMs,
                RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport) + RenderingServer.GetFrameSetupTimeCpu(),
                RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport),
                _view.Chunks.LastUpdateUsec / 1000.0,
                _view.Chunks.LastBuilds));
        }

        _lastUsec = now;
        if ((now - _startUsec) / 1e6 > TimeLimitSeconds)
        {
            GD.Print($"BENCH_FAIL time limit {TimeLimitSeconds}s exceeded");
            GetTree().Quit(ErrorExitCode);
        }
    }

    private static string? Arg(string name)
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == name)
            {
                return string.Empty;
            }

            if (arg.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return arg[(name.Length + 1)..];
            }
        }

        return null;
    }

    private static (double Avg, double Min, double P1Low, double WorstMs) Stats(IReadOnlyList<double> frames)
    {
        if (frames.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        double total = frames.Sum();
        double worst = frames.Max();
        var sorted = frames.OrderByDescending(f => f).ToList();
        int onePercent = Math.Max(1, sorted.Count / 100);
        double p1Ms = sorted.Take(onePercent).Average();
        return (1000.0 * frames.Count / total, 1000.0 / worst, 1000.0 / p1Ms, worst);
    }

    // Main-thread CPU (avg/max) of frames whose previous frame built chunks (or did not): tile updates run at its end.
    private static string CpuOf(List<(double Process, double RenderCpu, double Gpu, double Chunks, int Builds)> work, bool afterBuilds)
    {
        var cpu = Enumerable.Range(1, Math.Max(work.Count - 1, 0)).Where(i => (work[i - 1].Builds > 0) == afterBuilds).Select(i => work[i].Process).ToList();
        return cpu.Count == 0 ? "-" : $"{F(cpu.Average())}/{F(cpu.Max())}";
    }

    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private async void RunGuarded()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            GD.PushError($"BENCH_FAIL {e}");
            GD.Print($"BENCH_FAIL {e.Message}");
            GetTree().Quit(ErrorExitCode);
        }
    }

    private async Task Run()
    {
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        int size = int.TryParse(Arg("--bench-size"), out int s) ? s : NewGameOptions.DefaultMapSize;

        if (!GameContent.TryLoad(out ContentCatalog content, out string error))
        {
            throw new InvalidOperationException(error);
        }

        ulong genStart = Time.GetTicksUsec();
        _sim = CoreSimulation.CreateNewGame(content, new NewGameOptions(Seed, size, Difficulty.Normal));
        GD.Print($"BENCH world {size}x{size} generated in {(Time.GetTicksUsec() - genStart) / 1000} ms");
        LogMapStats(_sim.World!);

        _view = GD.Load<PackedScene>(WorldView.ScenePath).Instantiate<WorldView>();
        AddChild(_view);
        var ui = new CanvasLayer();
        AddChild(ui);
        _minimap = GD.Load<PackedScene>(MinimapView.ScenePath).Instantiate<MinimapView>();
        ui.AddChild(_minimap);
        _view.Bind(_sim);
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        _minimap.Bind(_sim, _view.Camera);
        _view.Camera.InputEnabled = false;

        Vector2 viewport = GetViewportRect().Size;
        GD.Print($"BENCH vsync {DisplayServer.WindowGetVsyncMode()} window {DisplayServer.WindowGetSize()} viewport {viewport} renderer {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterApiVersion()})");

        string? shots = Arg("--bench-shots") ?? System.Environment.GetEnvironmentVariable("BENCH_SHOTS_DIR");
        if (!string.IsNullOrEmpty(shots))
        {
            await TakeShots(shots);
            if (Arg("--bench-shots-only") is not null)
            {
                GD.Print("BENCH shots done");
                GetTree().Quit(0);
                return;
            }
        }

        if (_sim.World!.Fog.CountStates().Unknown != 0)
        {
            _sim.Enqueue(new RevealAllCommand());
            _sim.Step();
        }

        await Measure(size);
    }

    private async Task Measure(int size)
    {
        WorldCamera camera = _view.Camera;
        var phases = new List<(string Name, List<double> Frames)>();

        // Warm-up: first frames compile shaders and fill the first chunks.
        camera.SetZoomIndex(WorldCamera.ZoomLevels.Length - 1);
        camera.CenterOnTile(16, 16);
        await Seconds(WarmupSeconds);

        // Corner to corner (top tile to bottom tile) at every zoom, closest first.
        (int ax, int ay) = Core.Geometry.IsoProjection.TileCenter(8, 8);
        (int bx, int by) = Core.Geometry.IsoProjection.TileCenter(size - 9, size - 9);
        for (int z = WorldCamera.ZoomLevels.Length - 1; z >= 0; z--)
        {
            camera.SetZoomIndex(z);
            phases.Add(($"diagonal_zoom_{WorldCamera.ZoomLevels[z].ToString(CultureInfo.InvariantCulture)}",
                await Scroll(t => new Vector2(ax, ay).Lerp(new Vector2(bx, by), (float)(t / PhaseSeconds)), PhaseSeconds)));
        }

        // Lawn-mower sweep over the whole map at the widest zoom.
        camera.SetZoomIndex(0);
        Vector2 view = camera.VisibleWorldRect.Size;
        (int minX, _, int maxX, int maxY) = Core.Geometry.IsoProjection.MapBounds(size);
        var points = new List<Vector2>();
        bool leftToRight = true;
        for (float y = view.Y / 2; y < maxY + (view.Y / 2); y += view.Y)
        {
            float row = Math.Min(y, maxY - (view.Y / 2));
            var a = new Vector2(minX + (view.X / 2), row);
            var b = new Vector2(maxX - (view.X / 2), row);
            points.Add(leftToRight ? a : b);
            points.Add(leftToRight ? b : a);
            leftToRight = !leftToRight;
        }

        double length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += points[i].DistanceTo(points[i - 1]);
        }

        // Large maps would not fit the time limit at the base speed: the sweep then runs faster, which only makes it harsher.
        double sweepSpeed = Math.Max(SweepSpeed, length / MaxSweepSeconds);
        phases.Add(($"sweep_zoom_{WorldCamera.ZoomLevels[0].ToString(CultureInfo.InvariantCulture)}", await Scroll(t => AlongPath(points, t * sweepSpeed), length / sweepSpeed)));

        var all = phases.SelectMany(p => p.Frames).ToList();
        for (int i = 0; i < phases.Count; i++)
        {
            (string name, List<double> frames) = phases[i];
            (double avg, double min, double p1, double worst) = Stats(frames);
            var work = _phaseWork[i];
            double busy = work.Average(w => Math.Max(w.Process + w.RenderCpu, w.Gpu));
            GD.Print($"BENCH_PHASE {name} avg_fps={F(avg)} min_fps={F(min)} p1_low={F(p1)} worst_ms={F(worst)} frames={frames.Count} " +
                $"hitches_over_17ms={frames.Count(f => f > 17.5)} main_cpu_ms(avg/max)={F(work.Average(w => w.Process))}/{F(work.Max(w => w.Process))} " +
                $"render_cpu_ms={F(work.Average(w => w.RenderCpu))}/{F(work.Max(w => w.RenderCpu))} gpu_ms={F(work.Average(w => w.Gpu))}/{F(work.Max(w => w.Gpu))} " +
                $"chunk_ms={F(work.Average(w => w.Chunks))}/{F(work.Max(w => w.Chunks))} builds={work.Sum(w => w.Builds)} " +
                $"cpu_ms_build_frames={CpuOf(work, true)} cpu_ms_other_frames={CpuOf(work, false)} uncapped_fps_est={F(1000.0 / busy)}");
        }

        // Worst frames of the last phase with the chunk builds of the frame before (tile updates run at its end).
        List<double> last = phases[^1].Frames;
        var lastWork = _phaseWork[^1];
        var worstFrames = Enumerable.Range(1, last.Count - 1).OrderByDescending(i => last[i]).Take(12)
            .Select(i => $"{F(last[i])}ms(prev_builds={lastWork[i - 1].Builds},prev_chunk_ms={F(lastWork[i - 1].Chunks)},gc={_gcAt.Contains(i)})");
        GD.Print($"BENCH_WORST {string.Join(" ", worstFrames)}");
        GD.Print($"BENCH gc_gen0={GC.CollectionCount(0)} gc_gen1={GC.CollectionCount(1)} gc_gen2={GC.CollectionCount(2)}");

        (double totalAvg, double totalMin, double totalP1, double totalWorst) = Stats(all);
        var allWork = _phaseWork.SelectMany(w => w).ToList();
        double totalBusy = allWork.Average(w => Math.Max(w.Process + w.RenderCpu, w.Gpu));
        GD.Print($"BENCH avg_fps={F(totalAvg)} min_fps={F(totalMin)} p1_low={F(totalP1)} worst_ms={F(totalWorst)} uncapped_fps_est={F(1000.0 / totalBusy)} frames={all.Count} " +
            $"seconds={F(all.Sum() / 1000)} chunks_built={_view.Chunks.BuildCount} active={_view.Chunks.Active.Count} pooled={_view.Chunks.PooledCount}");
        bool pass = totalAvg >= TargetFps - RefreshJitterFps;
        GD.Print(pass ? "BENCH_PASS" : $"BENCH_FAIL avg_fps below {TargetFps}");
        GetTree().Quit(pass ? 0 : FailureExitCode);
    }

    private static Vector2 AlongPath(List<Vector2> points, double distance)
    {
        for (int i = 1; i < points.Count; i++)
        {
            float segment = points[i].DistanceTo(points[i - 1]);
            if (distance <= segment)
            {
                return points[i - 1].Lerp(points[i], segment <= 0 ? 1f : (float)(distance / segment));
            }

            distance -= segment;
        }

        return points[^1];
    }

    // Moves the camera along `path(t)` for `seconds`, recording frame times.
    private async Task<List<double>> Scroll(Func<double, Vector2> path, double seconds)
    {
        _frameMs.Clear();
        _work.Clear();
        _gcAt.Clear();
        _lastGcCount = GC.CollectionCount(0);
        _lastUsec = 0;
        _measuring = true;
        ulong start = Time.GetTicksUsec();
        double t = 0;
        while (t < seconds)
        {
            _view.Camera.CenterOnWorld(path(t));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            t = (Time.GetTicksUsec() - start) / 1e6;
        }

        _measuring = false;
        _phaseWork.Add([.. _work]);
        return [.. _frameMs];
    }

    private async Task Seconds(double seconds)
    {
        ulong start = Time.GetTicksUsec();
        while ((Time.GetTicksUsec() - start) / 1e6 < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task TakeShots(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        GameWorld world = _sim.World!;
        WorldCamera camera = _view.Camera;
        StartPoint start = world.Map.Start;

        // Fog at day: visible circle around the start, everything else unknown.
        await Shot(dir, "fog_day_z1", start.X, start.Y, 2);

        // Night: smaller circle, the day circle stays explored (dimmed), bluish tint.
        while (!_sim.Now.IsNight)
        {
            _sim.Step();
        }

        _sim.Step(_sim.Calendar.TicksPerDay / 24);
        await Shot(dir, "fog_night_z1", start.X, start.Y, 2);
        await Shot(dir, "fog_night_z2", start.X, start.Y, 3);

        // Back to the day, whole map explored.
        while (_sim.Now.IsNight || _sim.Now.Hour < 6)
        {
            _sim.Step();
        }

        _sim.Enqueue(new RevealAllCommand());
        _sim.Step();
        for (int z = WorldCamera.ZoomLevels.Length - 1; z >= 0; z--)
        {
            await Shot(dir, $"revealed_z{z}", start.X, start.Y, z);
        }

        (int hx, int hy) = FindHills(world);
        await Shot(dir, "hills_z3", hx, hy, 3);
        await Shot(dir, "hills_z2", hx, hy, 2);
        (int wx, int wy) = FindRiver(world);
        await Shot(dir, "river_z2", wx, wy, 2);
        camera.SetZoomIndex(2);
    }

    private async Task Shot(string dir, string name, int tileX, int tileY, int zoomIndex)
    {
        _view.Camera.SetZoomIndex(zoomIndex);
        _view.Camera.CenterOnTile(tileX, tileY);
        _view.BuildNow();
        for (int i = 0; i < 4; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }

        Image image = GetViewport().GetTexture().GetImage();
        string path = System.IO.Path.Combine(dir, name + ".png");
        Error result = image.SavePng(path);
        GD.Print($"BENCH shot {path} ({result}) tile {tileX},{tileY} zoom {WorldCamera.ZoomLevels[zoomIndex].ToString(CultureInfo.InvariantCulture)} hour {_sim.Now.Hour}");
    }

    // Land tile with the most height-3 tiles around it (relief check).
    private static (int X, int Y) FindHills(GameWorld world)
    {
        TerrainMap t = world.Map.Terrain;
        int best = -1;
        (int, int) at = (world.Size / 2, world.Size / 2);
        for (int y = 8; y < world.Size - 8; y += 4)
        {
            for (int x = 8; x < world.Size - 8; x += 4)
            {
                int score = 0;
                for (int dy = -4; dy <= 4; dy++)
                {
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int h = t.GetHeight(x + dx, y + dy);
                        score += h == 3 ? 3 : h == 2 ? 1 : 0;
                    }
                }

                // Prefer mixed relief: penalise all-flat-top plateaus.
                score -= Math.Abs(score - 120);
                if (score > best)
                {
                    best = score;
                    at = (x, y);
                }
            }
        }

        return at;
    }

    // A river tile with the most land relief around it.
    private static (int X, int Y) FindRiver(GameWorld world)
    {
        TerrainMap t = world.Map.Terrain;
        int best = -1;
        (int, int) at = (world.Map.Start.X, world.Map.Start.Y);
        for (int y = 6; y < world.Size - 6; y += 3)
        {
            for (int x = 6; x < world.Size - 6; x += 3)
            {
                if (!t.IsRiver(x, y))
                {
                    continue;
                }

                int score = 0;
                for (int dy = -3; dy <= 3; dy++)
                {
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        score += t.GetHeight(x + dx, y + dy);
                    }
                }

                if (score > best)
                {
                    best = score;
                    at = (x, y);
                }
            }
        }

        return at;
    }

    private static void LogMapStats(GameWorld world)
    {
        TerrainMap t = world.Map.Terrain;
        int[] riverByHeight = new int[TerrainMap.MaxHeight + 1];
        int[] landByHeight = new int[TerrainMap.MaxHeight + 1];
        for (int y = 0; y < world.Size; y++)
        {
            for (int x = 0; x < world.Size; x++)
            {
                if (t.IsRiver(x, y))
                {
                    riverByHeight[t.GetHeight(x, y)]++;
                }
                else if (!t.IsWater(x, y))
                {
                    landByHeight[t.GetHeight(x, y)]++;
                }
            }
        }

        GD.Print($"BENCH map land by height [{string.Join(", ", landByHeight)}], river by height [{string.Join(", ", riverByHeight)}], " +
            $"pois {world.Map.PointsOfInterest.Count}, sites {world.Map.SettlementSites.Count}");
    }
}
