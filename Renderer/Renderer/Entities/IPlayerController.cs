namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The physical state of a player, as much of it as the entity world needs: where they stand, how fast,
/// how big, and how to move them somewhere else.
/// </summary>
/// <remarks>
/// Declared here, next to its consumer, so the entity world does not depend on whatever drives the player.
/// The player is simulated per rendered frame rather than on the entity tick, so <see cref="PlayerEntity"/>
/// observes an implementation of this rather than owning the state itself.
/// </remarks>
public interface IPlayerController
{
    /// <summary>Gets the position of the player's feet, which is the entity origin.</summary>
    Vector3 Position { get; }

    /// <summary>Gets the current velocity in units per second.</summary>
    Vector3 Velocity { get; }

    /// <summary>Gets the half-extents of the player's collision hull, which shrink when ducking.</summary>
    Vector3 HullHalfExtents { get; }

    /// <summary>Gets the eye position that a use or aim trace starts from.</summary>
    Vector3 EyePosition { get; }

    /// <summary>Gets the direction the player is looking.</summary>
    Vector3 ViewForward { get; }

    /// <summary>
    /// Takes everything the player has done with their buttons since this was last called, clearing the
    /// record of it.
    /// </summary>
    /// <remarks>
    /// Accumulated rather than polled: input is sampled every rendered frame while entities run on a
    /// slower fixed tick, so a tap that starts and ends between two ticks would be missed by a "what is
    /// held right now" question. This is the engine's usercmd button bits, on a clock that differs here.
    /// </remarks>
    /// <returns>What is held, and what went down or came up since the last call.</returns>
    PlayerButtonState ConsumeButtons();

    /// <summary>
    /// Moves the player somewhere else outright, keeping their velocity.
    /// </summary>
    /// <param name="feetPosition">Where the feet arrive.</param>
    /// <param name="angles">View angles to adopt, or <see langword="null"/> to keep the current ones.</param>
    void Teleport(Vector3 feetPosition, Vector3? angles);
}
