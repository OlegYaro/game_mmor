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
[JsonDerivedType(typeof(MoveHeroCommandDto), MoveHeroCommand.KindName)]
[JsonDerivedType(typeof(GatherCommandDto), GatherCommand.KindName)]
[JsonDerivedType(typeof(StopHeroCommandDto), StopHeroCommand.KindName)]
[JsonDerivedType(typeof(GiveItemCommandDto), GiveItemCommand.KindName)]
[JsonDerivedType(typeof(PlaceBuildingCommandDto), PlaceBuildingCommand.KindName)]
[JsonDerivedType(typeof(CancelBuildingCommandDto), CancelBuildingCommand.KindName)]
[JsonDerivedType(typeof(UpgradeBuildingCommandDto), UpgradeBuildingCommand.KindName)]
[JsonDerivedType(typeof(MoveBuildingCommandDto), MoveBuildingCommand.KindName)]
[JsonDerivedType(typeof(DemolishBuildingCommandDto), DemolishBuildingCommand.KindName)]
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
            MoveHeroCommand c => new MoveHeroCommandDto(c.X, c.Y, c.Sprint),
            GatherCommand c => new GatherCommandDto(c.X, c.Y),
            StopHeroCommand => new StopHeroCommandDto(),
            GiveItemCommand c => new GiveItemCommandDto(c.Item, c.Count),
            PlaceBuildingCommand c => new PlaceBuildingCommandDto(c.BuildingId, c.X, c.Y, c.Rotation),
            CancelBuildingCommand c => new CancelBuildingCommandDto(c.Building.Index, c.Building.Generation),
            UpgradeBuildingCommand c => new UpgradeBuildingCommandDto(c.Building.Index, c.Building.Generation),
            MoveBuildingCommand c => new MoveBuildingCommandDto(c.Building.Index, c.Building.Generation, c.X, c.Y, c.Rotation),
            DemolishBuildingCommand c => new DemolishBuildingCommandDto(c.Building.Index, c.Building.Generation),
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

/// <summary>DTO of <see cref="MoveHeroCommand"/>.</summary>
/// <param name="X">Tile column.</param>
/// <param name="Y">Tile row.</param>
/// <param name="Sprint">Sprint while stamina lasts.</param>
public sealed record MoveHeroCommandDto(int X, int Y, bool Sprint) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new MoveHeroCommand(X, Y, Sprint);
}

/// <summary>DTO of <see cref="GatherCommand"/>.</summary>
/// <param name="X">Tile column of the node.</param>
/// <param name="Y">Tile row of the node.</param>
public sealed record GatherCommandDto(int X, int Y) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new GatherCommand(X, Y);
}

/// <summary>DTO of <see cref="StopHeroCommand"/>: no fields, just the kind.</summary>
public sealed record StopHeroCommandDto : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new StopHeroCommand();
}

/// <summary>DTO of <see cref="GiveItemCommand"/>.</summary>
/// <param name="Item">Item definition id.</param>
/// <param name="Count">Number of items.</param>
public sealed record GiveItemCommandDto(string Item, int Count) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new GiveItemCommand(Item, Count);
}

/// <summary>DTO of <see cref="PlaceBuildingCommand"/>.</summary>
/// <param name="Building">Building type id.</param>
/// <param name="X">Column of the footprint's top-left tile.</param>
/// <param name="Y">Row of the footprint's top-left tile.</param>
/// <param name="Rotation">Quarter turns.</param>
public sealed record PlaceBuildingCommandDto(string Building, int X, int Y, int Rotation) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new PlaceBuildingCommand(Building, X, Y, Rotation);
}

/// <summary>DTO of <see cref="CancelBuildingCommand"/>.</summary>
/// <param name="Index">Building entity slot index.</param>
/// <param name="Generation">Building entity generation.</param>
public sealed record CancelBuildingCommandDto(int Index, int Generation) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new CancelBuildingCommand(new EntityId(Index, Generation));
}

/// <summary>DTO of <see cref="UpgradeBuildingCommand"/>.</summary>
/// <param name="Index">Building entity slot index.</param>
/// <param name="Generation">Building entity generation.</param>
public sealed record UpgradeBuildingCommandDto(int Index, int Generation) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new UpgradeBuildingCommand(new EntityId(Index, Generation));
}

/// <summary>DTO of <see cref="MoveBuildingCommand"/>.</summary>
/// <param name="Index">Building entity slot index.</param>
/// <param name="Generation">Building entity generation.</param>
/// <param name="X">Column of the new top-left tile.</param>
/// <param name="Y">Row of the new top-left tile.</param>
/// <param name="Rotation">Quarter turns at the new place.</param>
public sealed record MoveBuildingCommandDto(int Index, int Generation, int X, int Y, int Rotation) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new MoveBuildingCommand(new EntityId(Index, Generation), X, Y, Rotation);
}

/// <summary>DTO of <see cref="DemolishBuildingCommand"/>.</summary>
/// <param name="Index">Building entity slot index.</param>
/// <param name="Generation">Building entity generation.</param>
public sealed record DemolishBuildingCommandDto(int Index, int Generation) : CommandDto
{
    /// <inheritdoc />
    public override ISimCommand ToCommand() => new DemolishBuildingCommand(new EntityId(Index, Generation));
}
