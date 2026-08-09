namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The buttons a player can work, Source's <c>IN_</c> bits from a usercmd. The values are the engine's, so
/// the set can be filled in as more of it is needed.
/// </summary>
[Flags]
public enum PlayerButton : uint
{
    /// <summary>Nothing held.</summary>
    None = 0,

    /// <summary>Primary attack.</summary>
    Attack = 1 << 0,

    /// <summary>Jump.</summary>
    Jump = 1 << 1,

    /// <summary>Duck.</summary>
    Duck = 1 << 2,

    /// <summary>Move forward.</summary>
    Forward = 1 << 3,

    /// <summary>Move back.</summary>
    Back = 1 << 4,

    /// <summary>Use, which presses whatever the player is looking at.</summary>
    Use = 1 << 5,

    /// <summary>Strafe left.</summary>
    MoveLeft = 1 << 9,

    /// <summary>Strafe right.</summary>
    MoveRight = 1 << 10,

    /// <summary>Secondary attack.</summary>
    Attack2 = 1 << 11,

    /// <summary>Reload.</summary>
    Reload = 1 << 13,

    /// <summary>Walk, the speed modifier.</summary>
    Speed = 1 << 17,
}

/// <summary>
/// One tick's worth of button activity: what is held, and what changed since it was last collected.
/// Source's <c>m_nButtons</c>, <c>m_afButtonPressed</c> and <c>m_afButtonReleased</c>.
/// </summary>
/// <param name="Held">The buttons held as of the most recent input sample.</param>
/// <param name="Pressed">The buttons that went down since the last collection.</param>
/// <param name="Released">The buttons that came up since the last collection.</param>
public readonly record struct PlayerButtonState(PlayerButton Held, PlayerButton Pressed, PlayerButton Released);
