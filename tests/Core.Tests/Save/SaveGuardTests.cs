using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Core.Save;
using Core.Sim;

namespace Core.Tests.Save;

/// <summary>
/// Reflection guard (ADR 0007, FR-SAVE-04): every member of <see cref="SimulationState"/> and of the state types it
/// contains is either mapped to a save DTO member or excluded with a reason, and every DTO member is accounted for.
/// A new field in the state fails here until it is saved (DTO + SaveMapper) or deliberately excluded.
/// </summary>
public sealed class SaveGuardTests
{
    // State member -> DTO members ("Type" for a flattened composite, "Type.jsonName" for a property). Keep in sync with SaveMapper.
    private static readonly Dictionary<string, string[]> _mapped = new(StringComparer.Ordinal)
    {
        ["SimulationState.Seed"] = ["SaveHeader.seed"],
        ["SimulationState.Tick"] = ["SaveBodyDto.tick"],
        ["SimulationState.RngStates"] = ["SaveBodyDto.rng"],
        ["SimulationState.Entities"] = ["SaveBodyDto.entities"],
        ["SimulationState.Positions"] = ["ComponentsDto.position"],
        ["SimulationState.NextCommandSequence"] = ["CommandQueueDto.nextSequence"],
        ["SimulationState.PendingCommands"] = ["CommandQueueDto.pending"],
        ["SimulationState.Difficulty"] = ["GameDto.difficulty"],
        ["SimulationState.World"] = ["SaveBodyDto.game", "SaveBodyDto.fog"],
        ["WorldState.MapSize"] = ["GameDto.mapSize"],
        ["WorldState.GeneratorVersion"] = ["SaveHeader.worldGenVersion"],
        ["WorldState.MapHash"] = ["GameDto.mapHash"],
        ["WorldState.ExploredBits"] = ["FogDto.exploredRuns"],
        ["WorldState.Revealers"] = ["ComponentsDto.revealer"],
        ["ComponentEntry<Revealer>.Entity"] = ["RevealerEntryDto"],
        ["ComponentEntry<Revealer>.Value"] = ["RevealerEntryDto"],
        ["Revealer.Kind"] = ["RevealerEntryDto.kind"],
        ["Xoshiro256State.S0"] = ["RngStreamDto.s0"],
        ["Xoshiro256State.S1"] = ["RngStreamDto.s1"],
        ["Xoshiro256State.S2"] = ["RngStreamDto.s2"],
        ["Xoshiro256State.S3"] = ["RngStreamDto.s3"],
        ["EntityRegistryState.Generations"] = ["EntitiesDto.generations"],
        ["EntityRegistryState.Alive"] = ["EntitiesDto.alive"],
        ["ComponentEntry<Position>.Entity"] = ["PositionEntryDto"],
        ["ComponentEntry<Position>.Value"] = ["PositionEntryDto"],
        ["EntityId.Index"] = ["PositionEntryDto.index", "RevealerEntryDto.index", "DestroyEntityCommandDto.index"],
        ["EntityId.Generation"] = ["PositionEntryDto.generation", "RevealerEntryDto.generation", "DestroyEntityCommandDto.generation"],
        ["Position.X"] = ["PositionEntryDto.x", "SpawnMarkerCommandDto.x"],
        ["Position.Y"] = ["PositionEntryDto.y", "SpawnMarkerCommandDto.y"],
        ["PendingCommand.ExecuteTick"] = ["PendingCommandDto.executeTick"],
        ["PendingCommand.Sequence"] = ["PendingCommandDto.sequence"],
        ["PendingCommand.Command"] = ["PendingCommandDto.command"],
        ["SpawnMarkerCommand.Kind"] = ["CommandDto.kind"],
        ["SpawnMarkerCommand.Position"] = ["SpawnMarkerCommandDto"],
        ["DestroyEntityCommand.Kind"] = ["CommandDto.kind"],
        ["DestroyEntityCommand.Entity"] = ["DestroyEntityCommandDto"],
        ["RevealAllCommand.Kind"] = ["CommandDto.kind"],
    };

    // State members deliberately not saved.
    private static readonly Dictionary<string, string> _excluded = new(StringComparer.Ordinal)
    {
        ["Xoshiro256State.IsZero"] = "Derived from S0..S3.",
        ["EntityId.IsValid"] = "Derived from Index and Generation.",
        ["Position.TileX"] = "Derived from X.",
        ["Position.TileY"] = "Derived from Y.",
    };

    // DTO members that have no state member behind them.
    private static readonly Dictionary<string, string> _dtoOnly = new(StringComparer.Ordinal)
    {
        ["SaveHeader.formatVersion"] = "Container metadata: format version.",
        ["SaveHeader.gameVersion"] = "Container metadata: writer build, informational.",
        ["SaveHeader.codec"] = "Container metadata: body encoding.",
        ["SaveBodyDto.components"] = "Grouping of the component stores.",
        ["SaveBodyDto.commands"] = "Grouping of the command queue.",
        ["RngStreamDto.stream"] = "Stream name; the state keeps streams by index.",
    };

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void EveryStateMemberIsSavedOrExcluded()
    {
        HashSet<string> members = CollectStateMembers();

        string[] unaccounted = members.Where(m => !_mapped.ContainsKey(m) && !_excluded.ContainsKey(m)).Order(StringComparer.Ordinal).ToArray();
        string[] stale = _mapped.Keys.Concat(_excluded.Keys).Where(k => !members.Contains(k)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(unaccounted.Length == 0,
            "State members not in the save: " + string.Join(", ", unaccounted) +
            ". Add them to the save DTOs and SaveMapper (bump SaveFormat.CurrentVersion with a migration) and map them here, or exclude them with a reason.");
        Assert.True(stale.Length == 0, "Guard entries for members that no longer exist: " + string.Join(", ", stale));
        Assert.All(_excluded.Values.Concat(_dtoOnly.Values), reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void EveryMappingTargetExistsAndEveryDtoMemberIsAccountedFor()
    {
        Dictionary<string, JsonTypeInfo> dtoTypes = CollectDtoTypes();
        HashSet<string> dtoMembers = dtoTypes
            .SelectMany(t => t.Value.Properties.Select(p => $"{t.Key}.{p.Name}"))
            .Append($"{nameof(CommandDto)}.{CommandDto.KindProperty}")
            .ToHashSet(StringComparer.Ordinal);

        string[] targets = _mapped.Values.SelectMany(t => t).Distinct(StringComparer.Ordinal).ToArray();
        string[] missingTargets = targets.Where(t => !dtoMembers.Contains(t) && !dtoTypes.ContainsKey(t)).ToArray();
        Assert.True(missingTargets.Length == 0, "Mapped to DTO members that do not exist: " + string.Join(", ", missingTargets));

        HashSet<string> flattened = targets.Where(dtoTypes.ContainsKey).ToHashSet(StringComparer.Ordinal);
        string[] orphans = dtoMembers
            .Where(m => !targets.Contains(m, StringComparer.Ordinal) && !_dtoOnly.ContainsKey(m))
            .Where(m => !flattened.Contains(m[..m.IndexOf('.', StringComparison.Ordinal)]))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(orphans.Length == 0, "DTO members with no state member behind them: " + string.Join(", ", orphans));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void EveryCommandHasADtoWithItsKind()
    {
        JsonTypeInfo baseInfo = SaveJsonContext.Default.GetTypeInfo(typeof(CommandDto))!;
        Dictionary<string, Type> dtoByKind = baseInfo.PolymorphismOptions!.DerivedTypes
            .ToDictionary(d => (string)d.TypeDiscriminator!, d => d.DerivedType, StringComparer.Ordinal);
        Type[] commandTypes = typeof(Simulation).Assembly.GetTypes()
            .Where(t => typeof(ISimCommand).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        Assert.NotEmpty(commandTypes);
        Assert.Equal(commandTypes.Length, dtoByKind.Count);
        foreach (Type type in commandTypes)
        {
            string kind = (string)type.GetField("KindName", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            Assert.True(dtoByKind.ContainsKey(kind), $"Command {type.Name} (kind '{kind}') has no CommandDto.");
        }
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void CommandsRoundTripThroughDtos()
    {
        ISimCommand[] commands =
        [
            new SpawnMarkerCommand(new Core.Entities.Position(-5, 9)),
            new DestroyEntityCommand(new Core.Entities.EntityId(3, 4)),
            new RevealAllCommand(),
        ];

        foreach (ISimCommand command in commands)
        {
            Assert.Equal(command, CommandDto.FromCommand(command).ToCommand());
        }

        Assert.Throws<NotSupportedException>(() => CommandDto.FromCommand(new UnknownCommand()));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void RngStreamNamesCoverEveryStream()
    {
        Assert.Equal(Core.Determinism.RngStreams.Count, SaveMapper.RngStreamNames.Count);
        Assert.Equal(SaveMapper.RngStreamNames.Count, SaveMapper.RngStreamNames.Distinct(StringComparer.Ordinal).Count());
    }

    private static HashSet<string> CollectStateMembers()
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        var queue = new Queue<Type>([typeof(SimulationState)]);
        Type[] commandTypes = typeof(Simulation).Assembly.GetTypes()
            .Where(t => typeof(ISimCommand).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        while (queue.Count > 0)
        {
            Type type = queue.Dequeue();
            if (!visited.Add(type))
            {
                continue;
            }

            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m is PropertyInfo or FieldInfo))
            {
                members.Add($"{Name(type)}.{member.Name}");
                Type memberType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
                foreach (Type next in Expand(memberType, commandTypes))
                {
                    queue.Enqueue(next);
                }
            }

            // Non-public instance fields of state records would be state too; there must be none beyond backing fields.
            FieldInfo[] hidden = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => !f.Name.Contains("k__BackingField", StringComparison.Ordinal))
                .ToArray();
            Assert.True(hidden.Length == 0, $"{Name(type)} has non-public fields: {string.Join(", ", hidden.Select(f => f.Name))}");
        }

        return members;
    }

    // Element and command types worth descending into: Core value/record types, not primitives or strings.
    private static Type[] Expand(Type type, Type[] commandTypes)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            type = type.GetGenericArguments()[0];
        }

        if (type == typeof(ISimCommand))
        {
            return commandTypes;
        }

        return type.Namespace?.StartsWith("Core", StringComparison.Ordinal) == true && !type.IsEnum ? [type] : [];
    }

    private static Dictionary<string, JsonTypeInfo> CollectDtoTypes()
    {
        var result = new Dictionary<string, JsonTypeInfo>(StringComparer.Ordinal);
        var queue = new Queue<Type>([typeof(SaveHeader), typeof(SaveBodyDto)]);
        while (queue.Count > 0)
        {
            Type type = queue.Dequeue();
            if (result.ContainsKey(type.Name))
            {
                continue;
            }

            JsonTypeInfo info = SaveJsonContext.Default.GetTypeInfo(type)
                ?? throw new InvalidOperationException($"No JSON metadata for {type.Name}.");
            result.Add(type.Name, info);
            foreach (JsonPropertyInfo property in info.Properties)
            {
                Type propertyType = property.PropertyType;
                if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                {
                    propertyType = propertyType.GetGenericArguments()[0];
                }

                if (propertyType.Namespace == typeof(SaveHeader).Namespace)
                {
                    queue.Enqueue(propertyType);
                }
            }

            foreach (JsonDerivedType derived in info.PolymorphismOptions?.DerivedTypes ?? [])
            {
                queue.Enqueue(derived.DerivedType);
            }
        }

        return result;
    }

    private static string Name(Type type) => type.IsGenericType
        ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(",", type.GetGenericArguments().Select(Name))}>"
        : type.Name;

    private sealed record UnknownCommand : ISimCommand
    {
        public string Kind => "unknown";

        public void Execute(Simulation sim)
        {
        }

        public void WriteState(Core.Determinism.StateWriter writer)
        {
        }
    }
}
