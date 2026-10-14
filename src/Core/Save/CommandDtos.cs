using System.Text.Json.Serialization;
using Core.Entities;
using Core.Sim;

namespace Core.Save;

/// <summary>
/// Serialized form of an <see cref="ISimCommand"/>, shared by saves and replays. The JSON object starts with a
/// <c>kind</c> property equal to <see cref="ISimCommand.Kind"/>, followed by the command's fields.
/// </summary>
/// <remarks>
/// To add a command: a sealed record deriving from <see cref="CommandDto"/>, a <see cref="JsonDerivedTypeAttribute"/>
/// line here with the command's <c>KindName</c>, and an arm in <see cref="FromCommand"/>. A test fails for any
/// <see cref="ISimCommand"/> in Core without a DTO. Kinds are never renamed; a changed payload is a format change.
/// </remarks>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = KindProperty,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SpawnMarkerCommandDto), SpawnMarkerCommand.KindName)]
[JsonDerivedType(typeof(DestroyEntityCommandDto), DestroyEntityCommand.KindName)]
[JsonDerivedType(typeof(RevealAllCommandDto), RevealAllCommand.KindName)]
public abstract record CommandDto
{
    /// <summary>JSON name of the discriminator property.</summary>
    public const string KindProperty = "kind";

    /// <summary>Creates the simulation command this DTO describes.</summary>
    /// <exception cref="ArgumentException">The DTO holds values the command rejects.</exception>
    public abstract ISimCommand ToCommand();

    /// <summary>The DTO for <paramref name="command"/>.</summary>
    /// <exception cref="NotSupportedException">The command type has no DTO (a bug: see the remarks).</exception>
    public static CommandDto FromCommand(ISimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            SpawnMarkerCommand c => new SpawnMarkerCommandDto(c.Position.X, c.Position.Y),
            DestroyEntityCommand c => new DestroyEntityCommandDto(c.Entity.Index, c.Entity.Generation),
            RevealAllCommand => new RevealAllCommandDto(),
            _ => throw new NotSupportedException($"Command kind '{command.Kind}' ({command.GetType().Name}) has no CommandDto."),
        };
    }
}

/// <summary>DTO of <see cref="SpawnMarkerCommand"/>.</summary>
/// <param name="X">X in 1/256 tile.</param>
/// <param name="Y">Y in 1/256 tile.</param>
public sealed record SpawnMarkerCommandDto(int X, int Y) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new SpawnMarkerCommand(new Position(X, Y));
}

/// <summary>DTO of <see cref="DestroyEntityCommand"/>.</summary>
/// <param name="Index">Entity slot index.</param>
/// <param name="Generation">Entity generation.</param>
public sealed record DestroyEntityCommandDto(int Index, int Generation) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new DestroyEntityCommand(new EntityId(Index, Generation));
}

/// <summary>DTO of <see cref="RevealAllCommand"/>: no fields, just the kind.</summary>
public sealed record RevealAllCommandDto : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new RevealAllCommand();
}
