namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// Button bits of a user command.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/client/InputBitMask_t">InputBitMask_t</seealso>
[Flags]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public enum InputButtons : ulong
#pragma warning restore CA1711
{
    /// <summary>No buttons.</summary>
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

    /// <summary>Use.</summary>
    Use = 1 << 5,

    /// <summary>Turn left.</summary>
    TurnLeft = 1 << 7,

    /// <summary>Turn right.</summary>
    TurnRight = 1 << 8,

    /// <summary>Strafe left.</summary>
    MoveLeft = 1 << 9,

    /// <summary>Strafe right.</summary>
    MoveRight = 1 << 10,

    /// <summary>Secondary attack.</summary>
    Attack2 = 1 << 11,

    /// <summary>Reload.</summary>
    Reload = 1 << 13,

    /// <summary>Walk.</summary>
    Speed = 1 << 16,
}

/// <summary>
/// The history of one button within a command: its state at the start, every edge it went through,
/// and so its state at the end.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/client/EInButtonState">EInButtonState</seealso>
public enum ButtonEdgeState
{
    /// <summary>Up the whole time.</summary>
    Up = 0,

    /// <summary>Down the whole time.</summary>
    Down = 1,

    /// <summary>Released.</summary>
    DownUp = 2,

    /// <summary>Pressed.</summary>
    UpDown = 3,

    /// <summary>Pressed, then released.</summary>
    UpDownUp = 4,

    /// <summary>Released, then pressed again.</summary>
    DownUpDown = 5,

    /// <summary>Released, pressed and released again.</summary>
    DownUpDownUp = 6,

    /// <summary>Pressed, released and pressed again.</summary>
    UpDownUpDown = 7,
}
