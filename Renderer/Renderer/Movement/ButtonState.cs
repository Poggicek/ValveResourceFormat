namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// The state of every button over one command, three bits per button: whether it ended down, and two
/// more that together say which edges it went through, see <see cref="ButtonEdgeState"/>.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/client/CInButtonState">CInButtonState</seealso>
public struct ButtonState : IEquatable<ButtonState>
{
    /// <summary>Gets or sets the buttons that are down at the end of the command.</summary>
    public ulong Held { readonly get; set; }

    /// <summary>Gets or sets the second bit of each button's <see cref="ButtonEdgeState"/>.</summary>
    public ulong Changed { readonly get; set; }

    /// <summary>Gets or sets the third bit of each button's <see cref="ButtonEdgeState"/>.</summary>
    public ulong ChangedTwice { readonly get; set; }

    // New state after one more edge, indexed by state * 2 + pressed. A release after a release, or a
    // press after a press, changes nothing.
    private static ReadOnlySpan<byte> AddEdgeTable => [0, 3, 2, 1, 2, 5, 4, 3, 4, 7, 6, 5, 6, 5, 4, 7];

    // The history collapsed to one edge into the forced state, keeping the state the command started
    // in, indexed by state * 2 + down
    private static ReadOnlySpan<byte> ForceTable => [0, 3, 2, 1, 2, 1, 0, 3, 0, 3, 2, 1, 2, 1, 0, 3];

    // A command's history continued from the state the previous one ended in, indexed by
    // command state * 2 + previously down
    private static ReadOnlySpan<byte> ChainTable => [0, 2, 3, 1, 4, 2, 3, 5, 4, 6, 7, 5, 4, 6, 7, 5];

    // A toggle button flips on every press in the history, indexed by state + 8 * started down
    private static ReadOnlySpan<byte> ToggleTable => [0, 0, 0, 3, 3, 3, 3, 4, 1, 1, 1, 2, 2, 2, 2, 5];

    /// <summary>Whether any of <paramref name="buttons"/> ends the command down.</summary>
    public readonly bool IsHeld(InputButtons buttons) => (Held & (ulong)buttons) != 0;

    /// <summary>Gets the history of one button.</summary>
    public readonly ButtonEdgeState GetState(InputButtons button) => (ButtonEdgeState)GetStateBits((ulong)button);

    /// <summary>Whether any of <paramref name="buttons"/> went down during the command.</summary>
    public readonly bool WasPressed(InputButtons buttons)
    {
        for (var mask = (ulong)buttons; mask != 0; mask &= mask - 1)
        {
            var bit = mask & (~mask + 1);
            var state = GetStateBits(bit);

            if (state != 0 && state - 1 >= 2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any of <paramref name="buttons"/> came up during the command.</summary>
    public readonly bool WasReleased(InputButtons buttons)
    {
        for (var mask = (ulong)buttons; mask != 0; mask &= mask - 1)
        {
            var bit = mask & (~mask + 1);
            var state = GetStateBits(bit);

            if (state != 0 && state != 1 && state != 3)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any of <paramref name="buttons"/> was pressed twice during the command.</summary>
    public readonly bool WasPressedTwice(InputButtons buttons)
    {
        for (var mask = (ulong)buttons; mask != 0; mask &= mask - 1)
        {
            var bit = mask & (~mask + 1);
            if (GetStateBits(bit) == (int)ButtonEdgeState.UpDownUpDown)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Appends a press or release to the history of <paramref name="buttons"/>.</summary>
    public void AddButtonEdge(InputButtons buttons, bool pressed)
        => SetStateBits((ulong)buttons, AddEdgeTable[GetStateBits((ulong)buttons) * 2 + (pressed ? 1 : 0)]);

    /// <summary>
    /// Forces <paramref name="buttons"/> to end the command down or up. The state they started the
    /// command in is kept, so the history becomes at most one edge.
    /// </summary>
    public void ForceButtonState(InputButtons buttons, bool down)
        => SetStateBits((ulong)buttons, ForceTable[GetStateBits((ulong)buttons) * 2 + (down ? 1 : 0)]);

    /// <summary>Forces every button, down when it is in <paramref name="buttonsDown"/>.</summary>
    public void ForceButtons(ulong buttonsDown)
    {
        for (var bit = 1ul; bit != 0; bit <<= 1)
        {
            SetStateBits(bit, ForceTable[GetStateBits(bit) * 2 + ((buttonsDown & bit) != 0 ? 1 : 0)]);
        }
    }

    /// <summary>Forces the buttons in <paramref name="changeMask"/>, down when they are in <paramref name="downMask"/>.</summary>
    public void ForceButtonsMasked(ulong changeMask, ulong downMask)
    {
        for (var mask = changeMask; mask != 0; mask &= mask - 1)
        {
            var bit = mask & (~mask + 1);
            SetStateBits(bit, ForceTable[GetStateBits(bit) * 2 + ((downMask & bit) != 0 ? 1 : 0)]);
        }
    }

    /// <summary>
    /// Re-derives a toggle button from its history: it starts down when <paramref name="startDown"/>
    /// and flips on every press.
    /// </summary>
    public void ApplyToggleButton(InputButtons button, bool startDown)
        => SetStateBits((ulong)button, ToggleTable[GetStateBits((ulong)button) + (startDown ? 8 : 0)]);

    /// <summary>
    /// Takes a new command's buttons. The command reports what happened during it, so each history is
    /// continued from the state the previous command ended in.
    /// </summary>
    /// <param name="command">The buttons the command carries.</param>
    /// <param name="toggleButtons">Buttons that toggle on each press instead of following the key.</param>
    /// <param name="toggleDownMask">Which toggle buttons are currently down.</param>
    public void UpdateFromCommand(in ButtonState command, ulong toggleButtons, ulong toggleDownMask)
    {
        var previousHeld = Held;
        this = command;

        var startDown = (toggleDownMask & toggleButtons) | (previousHeld & ~toggleButtons);

        for (var bit = 1ul; bit != 0; bit <<= 1)
        {
            var state = GetStateBits(bit);
            var wasDown = (startDown & bit) != 0;

            var next = (toggleButtons & bit) != 0
                ? ToggleTable[state + (wasDown ? 8 : 0)]
                : ChainTable[state * 2 + (wasDown ? 1 : 0)];

            SetStateBits(bit, next);
        }
    }

    // Any bit of the mask set counts, as the state of a group of buttons is read as one
    private readonly int GetStateBits(ulong buttons)
        => ((Held & buttons) != 0 ? 1 : 0)
            | ((Changed & buttons) != 0 ? 2 : 0)
            | ((ChangedTwice & buttons) != 0 ? 4 : 0);

    private void SetStateBits(ulong buttons, int state)
    {
        Held = (Held & ~buttons) | ((state & 1) != 0 ? buttons : 0);
        Changed = (Changed & ~buttons) | ((state & 2) != 0 ? buttons : 0);
        ChangedTwice = (ChangedTwice & ~buttons) | ((state & 4) != 0 ? buttons : 0);
    }

    /// <inheritdoc/>
    public readonly bool Equals(ButtonState other)
        => Held == other.Held && Changed == other.Changed && ChangedTwice == other.ChangedTwice;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is ButtonState other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(Held, Changed, ChangedTwice);

    /// <summary>Compares two button states.</summary>
    public static bool operator ==(ButtonState left, ButtonState right) => left.Equals(right);

    /// <summary>Compares two button states.</summary>
    public static bool operator !=(ButtonState left, ButtonState right) => !left.Equals(right);
}
