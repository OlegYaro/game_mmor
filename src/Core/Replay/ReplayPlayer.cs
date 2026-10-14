using Core.Content;
using Core.Logging;
using Core.Sim;

namespace Core.Replay;

/// <summary>
/// Runs a simulation from a <see cref="ReplayFile"/>, enqueuing each command one tick before it executes, and records
/// the subsystem hashes after every tick (ADR 0005: hash every tick in debug and CI).
/// </summary>
/// <remarks>
/// A player can also continue a simulation restored from a save made during a replay run: commands at or before the
/// simulation's tick are skipped (they are already in the restored state). Save between steps only.
/// </remarks>
public sealed class ReplayPlayer
{
    private readonly ReplayFile _replay;
    private int _next;

    /// <summary>Plays <paramref name="replay"/> on <paramref name="simulation"/>, starting at its current tick.</summary>
    /// <exception cref="ArgumentException">Seed, content hash, map size or difficulty differ from the replay's.</exception>
    public ReplayPlayer(ReplayFile replay, Simulation simulation)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(simulation);
        if (simulation.Seed != replay.Seed)
        {
            throw new ArgumentException($"Replay seed {replay.Seed} differs from simulation seed {simulation.Seed}.", nameof(simulation));
        }

        if (simulation.Content.ContentHash != replay.ContentHash)
        {
            throw new ArgumentException(
                $"Replay was recorded with content {ContentHash.Format(replay.ContentHash)}, but the simulation runs content " +
                $"{ContentHash.Format(simulation.Content.ContentHash)}. If data/ changed on purpose, regenerate the replay and its hashes.",
                nameof(simulation));
        }

        int mapSize = simulation.World?.Size ?? 0;
        if (mapSize != replay.MapSize || simulation.Difficulty != replay.Difficulty)
        {
            throw new ArgumentException(
                $"Replay is for map size {replay.MapSize} ({DifficultyNames.ToName(replay.Difficulty)}), the simulation has map size {mapSize} ({DifficultyNames.ToName(simulation.Difficulty)}).",
                nameof(simulation));
        }

        _replay = replay;
        Simulation = simulation;
        while (_next < replay.Commands.Count && replay.Commands[_next].Tick <= simulation.Tick)
        {
            _next++;
        }
    }

    /// <summary>The simulation being driven.</summary>
    public Simulation Simulation { get; }

    /// <summary>Hashes after every step this player ran.</summary>
    public HashTrace Hashes { get; } = new();

    /// <summary>True once the simulation reached <see cref="ReplayFile.EndTick"/>.</summary>
    public bool IsFinished => Simulation.Tick >= _replay.EndTick;

    /// <summary>
    /// Starts a new simulation with the replay's seed (a new game with its map size and difficulty, or a core
    /// simulation) and plays from tick 0.
    /// </summary>
    /// <exception cref="ArgumentException">The content hash differs from the replay's.</exception>
    public static ReplayPlayer Start(ReplayFile replay, ContentCatalog content, SimLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        NewGameOptions? newGame = replay.NewGame;
        Simulation simulation = newGame is null ? new Simulation(replay.Seed, content, log) : Simulation.CreateNewGame(content, newGame, log);
        return new ReplayPlayer(replay, simulation);
    }

    /// <summary>Enqueues the commands of the next tick, steps once and records the hashes.</summary>
    /// <exception cref="InvalidOperationException">The simulation was stepped outside the player and commands were missed.</exception>
    public void Step()
    {
        long nextTick = Simulation.Tick + 1;
        while (_next < _replay.Commands.Count && _replay.Commands[_next].Tick <= nextTick)
        {
            ReplayCommand command = _replay.Commands[_next];
            if (command.Tick < nextTick)
            {
                throw new InvalidOperationException($"Command for tick {command.Tick} was missed: the simulation is already at tick {Simulation.Tick}.");
            }

            Simulation.Enqueue(command.Command);
            _next++;
        }

        Simulation.Step();
        Hashes.Add(Simulation.Tick, Simulation.ComputeHashes());
    }

    /// <summary>Steps until the simulation reaches <paramref name="tick"/> (no-op when already there).</summary>
    public void RunTo(long tick)
    {
        while (Simulation.Tick < tick)
        {
            Step();
        }
    }

    /// <summary>Steps until <see cref="ReplayFile.EndTick"/>.</summary>
    public void RunToEnd() => RunTo(_replay.EndTick);
}
