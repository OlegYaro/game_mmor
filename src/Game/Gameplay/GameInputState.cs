namespace Game.Gameplay;

/// <summary>
/// Shared flags between the input handlers of the world (hero control, build mode, inspector) so that only one of
/// them reacts to a mouse click or key at a time. Lives on <see cref="GameRoot"/>.
/// </summary>
public sealed class GameInputState
{
    /// <summary>True while the build mode owns the mouse (preview, placement, move); hero clicks are ignored.</summary>
    public bool BuildModeActive { get; set; }

    /// <summary>True while a modal panel is open and the world must not react to the keyboard or mouse.</summary>
    public bool WorldInputBlocked { get; set; }
}
