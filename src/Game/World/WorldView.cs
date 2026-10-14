using Core.DevConsole;
using Core.Sim;
using Godot;

namespace Game.World;

/// <summary>
/// Shows the world of a simulation (ADR 0010): chunked ground and object layers around <see cref="Camera"/>, the fog
/// of war and the day/night tint. Reads Core state only; it never changes the simulation.
/// Scene: <c>res://World/WorldView.tscn</c>. Usage: instance it, then <see cref="Bind(Simulation)"/> (or
/// <see cref="Bind(SimulationHolder)"/> to follow console <c>load</c>), <see cref="Unbind"/> when leaving the game.
/// </summary>
/// <remarks>
/// Tree: <c>Ground</c> (flat chunk layers, drawn back to front by z-index), <c>Objects</c> (the one shared y-sorted
/// parent: chunk object layers now, creature sprites later go here via <see cref="ObjectsRoot"/>), <c>Fog</c> (the
/// multiplicative fog/tint pass) and <c>Camera</c>.
/// </remarks>
public partial class WorldView : Node2D
{
    /// <summary>Path of the scene.</summary>
    public const string ScenePath = "res://World/WorldView.tscn";

    private const int MaxChunkBuildsPerFrame = 6;
    private const ulong ChunkBuildBudgetUsec = 4000;

    private ChunkManager? _chunks;
    private SimulationHolder? _holder;
    private Simulation? _pending;

    /// <summary>The camera of the world.</summary>
    public WorldCamera Camera { get; private set; } = null!;

    /// <summary>Shared y-sorted parent of everything standing on the map (chunk object layers, creatures).</summary>
    public Node2D ObjectsRoot { get; private set; } = null!;

    /// <summary>The fog/tint pass.</summary>
    public FogOverlay Fog { get; private set; } = null!;

    /// <summary>Chunk layers (null until the node is ready).</summary>
    public ChunkManager Chunks => _chunks!;

    /// <summary>The bound simulation, or null.</summary>
    public Simulation? Simulation { get; private set; }

    /// <summary>True while a simulation with a world is shown.</summary>
    public bool IsBound => Simulation?.World is not null;

    /// <summary>When true the day/night tint follows the clock; when false it stays neutral.</summary>
    public bool DayNightEnabled { get; set; } = true;

    public override void _Ready()
    {
        Camera = GetNode<WorldCamera>("Camera");
        ObjectsRoot = GetNode<Node2D>("Objects");
        Fog = GetNode<FogOverlay>("Fog");
        _chunks = new ChunkManager(GetNode<Node2D>("Ground"), ObjectsRoot);
        if (_pending is not null)
        {
            Simulation pending = _pending;
            _pending = null;
            BindSimulation(pending);
        }
    }

    /// <summary>
    /// Shows the world of <paramref name="simulation"/> and centres the camera on its start tile. A simulation without
    /// a world (the M0 core) shows nothing. Replaces any earlier binding. May be called before the node enters the tree.
    /// </summary>
    public void Bind(Simulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        _holder = null;
        BindSimulation(simulation);
    }

    /// <summary>Like <see cref="Bind(Simulation)"/>, and rebinds by itself when the holder's simulation is replaced (console <c>load</c>).</summary>
    public void Bind(SimulationHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        _holder = holder;
        BindSimulation(holder.Current);
    }

    /// <summary>Stops showing the world and returns all chunks to the pool.</summary>
    public void Unbind()
    {
        _holder = null;
        UnbindSimulation();
    }

    /// <summary>Builds all chunks needed for the current camera view at once, refreshes fog and tint (tests, benchmark).</summary>
    public void BuildNow()
    {
        if (!IsBound)
        {
            return;
        }

        _chunks!.BuildAll(Camera.VisibleWorldRect);
        Fog.Refresh();
        UpdateTint();
    }

    public override void _Process(double delta)
    {
        if (_holder is not null && !ReferenceEquals(_holder.Current, Simulation))
        {
            BindSimulation(_holder.Current);
        }

        if (!IsBound)
        {
            return;
        }

        _chunks!.Update(Camera.VisibleWorldRect, MaxChunkBuildsPerFrame, ChunkBuildBudgetUsec);
        Fog.Refresh();
        UpdateTint();
    }

    private void BindSimulation(Simulation simulation)
    {
        if (_chunks is null)
        {
            // Bound before entering the tree: finish in _Ready.
            _pending = simulation;
            return;
        }

        UnbindSimulation();
        Simulation = simulation;
        GameWorld? world = simulation.World;
        if (world is null)
        {
            GD.Print("WorldView: the simulation has no world; nothing to show.");
            return;
        }

        if (!WorldArt.TryGetShared(out WorldArt art, out string error))
        {
            GD.PushError($"WorldView: {error}");
            Simulation = null;
            return;
        }

        _chunks!.Bind(world, art);
        Fog.Bind(world, art);
        Camera.SetMap(world.Size, new Vector2I(world.Map.Start.X, world.Map.Start.Y));
        Camera.MakeCurrent();
        Camera.CenterOnTile(world.Map.Start.X, world.Map.Start.Y);
        UpdateTint();
    }

    private void UnbindSimulation()
    {
        _pending = null;
        _chunks?.Unbind();
        Fog?.Unbind();
        Simulation = null;
    }

    private void UpdateTint() =>
        Fog.SetTint(DayNightEnabled && Simulation is not null ? DayNightTint.At(Simulation) : Colors.White);
}
