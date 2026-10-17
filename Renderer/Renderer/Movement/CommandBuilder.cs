namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// Builds one user command per tick from input sampled every rendered frame, the way the game's client
/// does: each button edge is timed within the tick and sent as a sub-tick step with the view angles
/// of its frame, and the command carries the input as of the last frame that went into it.
/// </summary>
internal sealed class CommandBuilder
{
    // Only these buttons are timed within the tick; the rest only change the command's button state
    private static readonly InputButtons[] SubtickButtons =
    [
        InputButtons.Attack, InputButtons.Jump, InputButtons.Duck, InputButtons.Forward,
        InputButtons.Back, InputButtons.MoveLeft, InputButtons.MoveRight, InputButtons.Attack2,
    ];

    private const int MaxSubtickMoves = 32;

    // A command's button states from the previous command's held state and this one's edges, indexed by
    // previously held * 8 + released * 4 + pressed * 2 + held
    private static ReadOnlySpan<byte> ButtonStateTable => [0, 3, 0, 3, 0, 3, 4, 7, 2, 1, 2, 1, 2, 1, 6, 5];

    private record struct Step(float When, InputButtons Button, bool Pressed, int EdgeIndex, Vector3 ViewAngles);

    private sealed class FrameEntry
    {
        public float When;
        public InputButtons Held;
        public InputButtons Pressed;
        public InputButtons Released;
        public float ForwardMove;
        public float LeftMove;
        public Vector3 ViewAngles;
        public List<Step> Steps { get; } = [];
    }

    private readonly List<FrameEntry> Entries = [];
    private readonly List<Step> PendingSteps = [];

    // What the command being built has taken in so far
    private InputButtons Held;
    private InputButtons Pressed;
    private InputButtons Released;
    private InputButtons PreviousHeld;
    private float ForwardMove;
    private float LeftMove;
    private Vector3 ViewAngles;
    private readonly List<Step> Steps = [];

    // The buttons down as of the last frame, for edges
    private InputButtons LastFrameHeld;
    private int CommandNumber;

    /// <summary>Starts over with the given buttons down and view angles, and nothing queued.</summary>
    public void Reset(InputButtons held, Vector3 viewAngles)
    {
        Entries.Clear();
        PendingSteps.Clear();
        Steps.Clear();
        Held = held;
        PreviousHeld = held;
        LastFrameHeld = held;
        Pressed = Released = InputButtons.None;
        ForwardMove = LeftMove = 0f;
        ViewAngles = viewAngles;
    }

    /// <summary>Takes one rendered frame's input.</summary>
    /// <param name="when">When the input happened, in ticks since the start of the tick being built.</param>
    /// <param name="held">The buttons down.</param>
    /// <param name="taps">Buttons pressed and released at this moment, as mouse wheel binds are.</param>
    /// <param name="viewAngles">The view angles, as (pitch, yaw, roll) degrees.</param>
    public void AddFrame(float when, InputButtons held, InputButtons taps, Vector3 viewAngles)
    {
        // Edge times are quantized to 1/64 of a tick
        when = TickFraction.RoundSubtickFraction(when);

        var entry = new FrameEntry
        {
            When = Entries.Count > 0 ? Entries[^1].When : 0f,
            Held = held,
            ViewAngles = viewAngles,
            ForwardMove = Axis(held, InputButtons.Forward, InputButtons.Back),
            LeftMove = Axis(held, InputButtons.MoveLeft, InputButtons.MoveRight),
        };

        for (var bit = 1ul; bit != 0; bit <<= 1)
        {
            var button = (InputButtons)bit;
            var wasDown = (LastFrameHeld & button) != 0;
            var isDown = (held & button) != 0;
            var tapped = (taps & button) != 0 && !wasDown && !isDown;

            if (wasDown == isDown && !tapped)
            {
                continue;
            }

            if (tapped)
            {
                entry.Pressed |= button;
                entry.Released |= button;
            }
            else if (isDown)
            {
                entry.Pressed |= button;
            }
            else
            {
                entry.Released |= button;
            }

            if (Array.IndexOf(SubtickButtons, button) < 0)
            {
                continue;
            }

            // A tap is two edges at the same time, a press then a release
            var edges = tapped ? 2 : 1;

            for (var k = 0; k < edges; k++)
            {
                entry.Steps.Add(new Step(when, button, wasDown ^ (k % 2 == 0), k, viewAngles));
            }
        }

        LastFrameHeld = held;

        if (entry.Steps.Count > 0)
        {
            entry.Steps.Sort(static (a, b) => a.When != b.When ? a.When.CompareTo(b.When)
                : a.Button != b.Button ? a.Button.CompareTo(b.Button)
                : a.EdgeIndex.CompareTo(b.EdgeIndex));

            if (entry.Steps.Count > MaxSubtickMoves)
            {
                entry.Steps.RemoveRange(MaxSubtickMoves, entry.Steps.Count - MaxSubtickMoves);
            }

            entry.When = entry.Steps[0].When;
        }

        Entries.Add(entry);
    }

    /// <summary>Builds the command for the tick that just ended, from every input that happened in it.</summary>
    public UserCmd CreateMove(bool subtickViewAngles)
    {
        Steps.Clear();
        Consume();

        var cmd = new UserCmd
        {
            CommandNumber = ++CommandNumber,
            ViewAngles = ViewAngles,
            ForwardMove = ForwardMove,
            LeftMove = LeftMove,
        };

        // Each step's angles are sent as the change from the one before, starting from the command's own
        var runningPitch = ViewAngles.X;
        var runningYaw = ViewAngles.Y;

        foreach (var step in Steps)
        {
            var move = new SubtickMoveStep { When = step.When, Button = step.Button, Pressed = step.Pressed };

            if (subtickViewAngles)
            {
                var pitchDelta = step.ViewAngles.X - runningPitch;

                if (pitchDelta != 0f)
                {
                    move.PitchDelta = pitchDelta;
                    runningPitch = step.ViewAngles.X;
                }

                var yawDelta = step.ViewAngles.Y - runningYaw;

                if (yawDelta != 0f)
                {
                    move.YawDelta = yawDelta;
                    runningYaw = step.ViewAngles.Y;
                }
            }

            cmd.SubtickMoves.Add(move);
        }

        var buttons = new ButtonState();

        for (var bit = 1ul; bit != 0; bit <<= 1)
        {
            var index = ((PreviousHeld & (InputButtons)bit) != 0 ? 8 : 0)
                | ((Released & (InputButtons)bit) != 0 ? 4 : 0)
                | ((Pressed & (InputButtons)bit) != 0 ? 2 : 0)
                | ((Held & (InputButtons)bit) != 0 ? 1 : 0);

            var state = ButtonStateTable[index];

            if ((state & 1) != 0)
            {
                buttons.Held |= bit;
            }

            if ((state & 2) != 0)
            {
                buttons.Changed |= bit;
            }

            if ((state & 4) != 0)
            {
                buttons.ChangedTwice |= bit;
            }
        }

        cmd.Buttons = buttons;

        // The next command starts where this one ended, and everything still queued moves a tick earlier
        PreviousHeld = (InputButtons)buttons.Held;
        Pressed = Released = InputButtons.None;

        foreach (var entry in Entries)
        {
            entry.When -= 1f;

            for (var i = 0; i < entry.Steps.Count; i++)
            {
                entry.Steps[i] = entry.Steps[i] with { When = entry.Steps[i].When - 1f };
            }
        }

        for (var i = 0; i < PendingSteps.Count; i++)
        {
            PendingSteps[i] = PendingSteps[i] with { When = PendingSteps[i].When - 1f };
        }

        return cmd;
    }

    // Takes in the frames that happened in this tick. A frame belongs whole to the tick of its earliest
    // edge, but a step of it that happened after the tick ended waits for the next command.
    private void Consume()
    {
        while (Entries.Count > 0 && Entries[0].When < 1f)
        {
            var entry = Entries[0];
            Entries.RemoveAt(0);

            Held = entry.Held;
            Pressed |= entry.Pressed;
            Released |= entry.Released;
            ForwardMove = entry.ForwardMove;
            LeftMove = entry.LeftMove;
            ViewAngles = entry.ViewAngles;
            PendingSteps.AddRange(entry.Steps);
        }

        while (PendingSteps.Count > 0 && PendingSteps[0].When < 1f)
        {
            // Steps past the cap are dropped, not deferred
            if (Steps.Count < MaxSubtickMoves)
            {
                Steps.Add(PendingSteps[0]);
            }

            PendingSteps.RemoveAt(0);
        }
    }

    private static float Axis(InputButtons held, InputButtons positive, InputButtons negative)
        => ((held & positive) != 0 ? 1f : 0f) - ((held & negative) != 0 ? 1f : 0f);
}
