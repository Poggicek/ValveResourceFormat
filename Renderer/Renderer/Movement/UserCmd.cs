namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// One input change inside a tick: a button edge, or a change of analog movement, at a fraction of
/// the tick, with how far the view turned since the previous step.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/client/CSubtickMoveStep">CSubtickMoveStep</seealso>
public record struct SubtickMoveStep
{
    /// <summary>Gets or sets the button that changed, or <see cref="InputButtons.None"/> for an analog change.</summary>
    public InputButtons Button { get; set; }

    /// <summary>Gets or sets whether <see cref="Button"/> went down rather than up.</summary>
    public bool Pressed { get; set; }

    /// <summary>Gets or sets when in the tick the change happened, from 0 to 1.</summary>
    public float When { get; set; }

    /// <summary>Gets or sets the change of the forward move input, for an analog step.</summary>
    public float AnalogForwardDelta { get; set; }

    /// <summary>Gets or sets the change of the left move input, for an analog step.</summary>
    public float AnalogLeftDelta { get; set; }

    /// <summary>Gets or sets how far the view pitch changed since the previous step, in degrees.</summary>
    public float PitchDelta { get; set; }

    /// <summary>Gets or sets how far the view yaw changed since the previous step, in degrees.</summary>
    public float YawDelta { get; set; }
}

/// <summary>
/// What the player did during one tick: the view angles and movement input at its end, the buttons,
/// and when inside the tick each input changed.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/client/CUserCmd">CUserCmd</seealso>
public sealed class UserCmd
{
    /// <summary>Gets or sets the sequence number of the command.</summary>
    public int CommandNumber { get; set; }

    /// <summary>Gets or sets the view angles at the end of the tick, as (pitch, yaw, roll) degrees.</summary>
    public Vector3 ViewAngles { get; set; }

    /// <summary>Gets or sets the forward move input at the end of the tick, from -1 to 1.</summary>
    public float ForwardMove { get; set; }

    /// <summary>Gets or sets the left move input at the end of the tick, from -1 to 1.</summary>
    public float LeftMove { get; set; }

    /// <summary>Gets or sets the up move input at the end of the tick, from -1 to 1.</summary>
    public float UpMove { get; set; }

    /// <summary>Gets or sets the buttons, as what happened to each during the tick.</summary>
    public ButtonState Buttons { get; set; }

    /// <summary>Gets or sets the impulse command, 0 for none.</summary>
    public int Impulse { get; set; }

    /// <summary>Gets the input changes inside the tick, in time order.</summary>
    public List<SubtickMoveStep> SubtickMoves { get; } = [];
}
