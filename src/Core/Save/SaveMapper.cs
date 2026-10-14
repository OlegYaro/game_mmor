using Core.Determinism;
using Core.Entities;
using Core.Fog;
using Core.Sim;

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

    /// <summary>Header of a state: the world's generator version for a game, the running generator's otherwise.</summary>
    public static SaveHeader ToHeader(SimulationState state, string codec) =>
        new(SaveFormat.CurrentVersion, GameVersion, state.World?.GeneratorVersion ?? SaveFormat.WorldGenVersion, state.Seed, codec);

    /// <summary>Version of this build as written into headers.</summary>
    public static string GameVersion => CoreInfo.Version.ToString(3);

    public static SaveBodyDto ToBody(SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
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

        return new SaveBodyDto(
            state.Tick,
            rng,
            new EntitiesDto(state.Entities.Generations.ToArray(), state.Entities.Alive.ToArray()),
            new ComponentsDto(positions, revealers),
            new CommandQueueDto(state.NextCommandSequence, pending),
            new GameDto(DifficultyNames.ToName(state.Difficulty), world?.MapSize ?? 0, world?.MapHash ?? 0),
            new FogDto(exploredRuns));
    }

    /// <summary>Builds the simulation state from a parsed header and body.</summary>
    /// <exception cref="InvalidDataException">The body is structurally invalid (null entries, unknown or missing RNG streams).</exception>
    public static SimulationState ToState(SaveHeader header, SaveBodyDto body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);

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
            ToWorld(header, body));
    }

    private static WorldState? ToWorld(SaveHeader header, SaveBodyDto body)
    {
        int size = body.Game.MapSize;
        if (size == 0)
        {
            if (body.Game.MapHash != 0 || body.Fog.ExploredRuns.Count != 0 || body.Components.Revealer.Count != 0)
            {
                throw new InvalidDataException("A save without a world (mapSize 0) must have mapHash 0, no fog runs and no revealers.");
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
        return new WorldState(size, header.WorldGenVersion, body.Game.MapHash, explored, revealers);
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
