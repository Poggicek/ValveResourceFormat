using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>How one replayed command compares with what the server networked after running it.</summary>
/// <param name="Index">The command's index in the recording.</param>
/// <param name="ServerTick">The tick the server ran it in.</param>
/// <param name="Origin">Where the replay left the player, rounded as networked.</param>
/// <param name="Velocity">The velocity the replay left the player with, quantized as networked.</param>
/// <param name="Expected">What the server networked, or null when the demo has no state for the tick.</param>
internal sealed record DemoReplayStep(int Index, int ServerTick, Vector3 Origin, Vector3 Velocity, NetworkedMovementState? Expected)
{
    /// <summary>Gets how far the replay is from where the server had the player.</summary>
    public float OriginError => Expected is { } expected ? Vector3.Distance(Origin, expected.Origin) : 0f;

    /// <summary>Gets how far the replayed velocity is from the networked one.</summary>
    public float VelocityError => Expected is { } expected ? Vector3.Distance(Velocity, expected.Velocity) : 0f;

    /// <summary>Gets whether the replay ended the tick exactly where the server did.</summary>
    public bool Matches => Expected is not { } expected || (Origin == expected.Origin && Velocity == expected.Velocity);
}

/// <summary>
/// Runs a demo's user commands through the movement, as the server ran them, to find where the port
/// and the game part ways.
/// </summary>
/// <remarks>
/// Only the start comes from the networked state: the player is placed in it right before the first
/// command, and from there the commands alone move them. Before that the commands leading up to the
/// start are run as well, for the state carried between commands that is not networked, such as the
/// previous command's buttons and movement input, which the start state then overrides.
/// </remarks>
internal sealed class DemoReplay
{
    // Enough earlier commands for every input carried over to come from the demo
    private const int PrimeCommands = 16;

    // How far back a jump is looked for, two minutes of commands
    private const int MaxPrimeCommands = 64 * 120;

    private readonly DemoMovementRecording Recording;
    private readonly EntitySystem? Entities;
    private readonly Func<NetworkedMovementState, float?> WeaponMaxSpeed;

    /// <param name="recording">The demo's movement.</param>
    /// <param name="services">The movement to run the commands through, in the world they were recorded in.</param>
    /// <param name="weaponMaxSpeed">The max speed of the weapon held in a state, null for none.</param>
    public DemoReplay(DemoMovementRecording recording, MovementServices services, Func<NetworkedMovementState, float?> weaponMaxSpeed)
    {
        Recording = recording;
        Services = services;
        Entities = services.Collision.Entities;
        WeaponMaxSpeed = weaponMaxSpeed;
    }

    public MovementServices Services { get; }

    /// <summary>Gets the index of the next command to run.</summary>
    public int Position { get; private set; }

    /// <summary>Gets the index of the first command the demo has a state to start from, or -1 for none.</summary>
    public int FirstStartIndex
    {
        get
        {
            for (var i = 0; i < Recording.Commands.Count; i++)
            {
                if (StateBefore(i) != null)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>Gets whether every command has been run.</summary>
    public bool Finished => Position >= Recording.Commands.Count;

    /// <summary>
    /// Puts the player where the server had them right before the command at <paramref name="index"/>.
    /// </summary>
    /// <returns>False when the demo has no state to start that command from.</returns>
    public bool Seek(int index)
    {
        var start = StateBefore(index);

        if (start == null)
        {
            return false;
        }

        // Nothing carries over from wherever the player was before
        Services.Reset(start.Origin, Recording.Commands[index].Command.ViewAngles);

        // Earlier commands first, each run from its own networked start, for what is not networked. They
        // only set up state, so they make no sound and are not recorded
        var (effects, recorder) = (Services.Effects, Services.Recorder);
        (Services.Effects, Services.Recorder) = (null, null);

        for (var i = PrimeStart(index); i < index; i++)
        {
            if (StateBefore(i) is { } primeState)
            {
                Restore(primeState);
                ApplyConsoleState(i);
                Services.RunCommand(Recording.Commands[i].Command);
            }
        }

        (Services.Effects, Services.Recorder) = (effects, recorder);

        Restore(start);
        Position = index;
        return true;
    }

    /// <summary>
    /// Finds where to start running commands to set up for the one at <paramref name="index"/>: a fixed
    /// number back, or further to the last jump. The rounding error airborne heights carry is only reset
    /// by jumping and is not networked, so it is only right when run from there.
    /// </summary>
    private int PrimeStart(int index)
    {
        var start = Math.Max(0, index - PrimeCommands);
        var lastJump = StateBefore(index)?.Values.GetValueOrDefault("LastJumpTick");

        if (lastJump == null)
        {
            return start;
        }

        var limit = Math.Max(0, index - MaxPrimeCommands);

        for (var i = index - 1; i >= limit; i--)
        {
            // The jump happened in the command that started before the last jump tick was set
            if (StateBefore(i) is not { } before || !Equals(before.Values.GetValueOrDefault("LastJumpTick"), lastJump))
            {
                return Math.Min(start, i);
            }
        }

        return start;
    }

    /// <summary>Runs the next command.</summary>
    /// <param name="resync">
    /// Whether to start it from the networked state instead of where the last command left the player,
    /// keeping only what is not networked. Each command is then checked on its own.
    /// </param>
    public DemoReplayStep Step(bool resync = false)
    {
        var index = Position++;
        var command = Recording.Commands[index];

        if (StateBefore(index) is { } before)
        {
            if (resync)
            {
                Restore(before);
            }
            else
            {
                ApplyOutsideState(before);
            }
        }

        ApplyConsoleState(index);

        Services.RunCommand(command.Command);

        var velocity = new Vector3(
            QuantizedFloat.Velocity.Quantize(Services.AbsVelocity.X),
            QuantizedFloat.Velocity.Quantize(Services.AbsVelocity.Y),
            QuantizedFloat.Velocity.Quantize(Services.AbsVelocity.Z));

        return new DemoReplayStep(index, command.ServerTick, NetworkOrigin.Round(Services.AbsOrigin), velocity, ExpectedAfter(index));
    }

    /// <summary>
    /// Gets what the server networked after the command at <paramref name="index"/>, or null when another
    /// command of the player ran later in the same tick or the demo has no state for the tick.
    /// </summary>
    public NetworkedMovementState? ExpectedAfter(int index)
    {
        var tick = Recording.Commands[index].ServerTick;

        if (index + 1 < Recording.Commands.Count && Recording.Commands[index + 1].ServerTick == tick)
        {
            return null;
        }

        return Recording.States.GetValueOrDefault(tick);
    }

    /// <summary>Gets the networked state the command at <paramref name="index"/> started from.</summary>
    public NetworkedMovementState? StateBefore(int index)
    {
        var tick = Recording.Commands[index].ServerTick;

        // Commands sharing a tick start from the one before them, which is not networked
        if (index > 0 && Recording.Commands[index - 1].ServerTick == tick)
        {
            return null;
        }

        return Recording.States.GetValueOrDefault(tick - 1);
    }

    private void Restore(NetworkedMovementState state)
    {
        Services.VisitState(new NetworkedStateVisitor(state));
        ApplyOutsideState(state);

        Services.AbsOrigin = state.Origin;
        Services.AbsVelocity = state.Velocity;

        // Only the world can be found again; standing on anything else replays as finding it again
        Services.GroundEntity = state.GroundEntityIndex == 0 ? Entities?.World : null;
    }

    /// <summary>
    /// Takes what changes outside the commands from the state a command starts from: the weapon in hand,
    /// which sets how fast the player can run, and noclip, which console commands toggle.
    /// </summary>
    private void ApplyOutsideState(NetworkedMovementState state)
    {
        Services.WeaponMaxSpeed = WeaponMaxSpeed(state);
        Services.NoClip = state.Values.GetValueOrDefault(nameof(MovementServices.NoClip)) is true;
    }

    /// <summary>
    /// Takes noclip from the tick the command ran in rather than the one before: console commands run
    /// before the tick's user commands, so a toggle already applies to the command of its own tick.
    /// </summary>
    private void ApplyConsoleState(int index)
    {
        if (Recording.States.GetValueOrDefault(Recording.Commands[index].ServerTick) is { } during)
        {
            Services.NoClip = during.Values.GetValueOrDefault(nameof(MovementServices.NoClip)) is true;
        }
    }

    /// <summary>Overwrites the movement state with what was networked, leaving the rest as it is.</summary>
    private sealed class NetworkedStateVisitor(NetworkedMovementState state) : IMovementStateVisitor
    {
        // The flags the movement reads; the rest are not movement's
        private const ulong MovementFlags = (ulong)(PlayerFlags.OnGround | PlayerFlags.Ducking | PlayerFlags.WaterJump
            | PlayerFlags.Frozen | PlayerFlags.AtControls | PlayerFlags.BaseVelocity);

        public void Visit(string name, ref float value)
        {
            if (state.Values.GetValueOrDefault(name) is float networked)
            {
                value = networked;
            }
        }

        public void Visit(string name, ref int value)
        {
            switch (state.Values.GetValueOrDefault(name))
            {
                case int networked:
                    value = networked;
                    break;
                case uint networked:
                    value = (int)networked;
                    break;
            }
        }

        public void Visit(string name, ref bool value)
        {
            if (state.Values.GetValueOrDefault(name) is bool networked)
            {
                value = networked;
            }
        }

        public void Visit(string name, ref ulong value)
        {
            if (state.Values.GetValueOrDefault(name) is uint networked)
            {
                value = name == nameof(MovementServices.Flags) ? (value & ~MovementFlags) | (networked & MovementFlags) : networked;
            }
        }

        public void Visit(string name, ref Vector2 value)
        {
        }

        public void Visit(string name, ref Vector3 value)
        {
            if (state.Values.GetValueOrDefault(name) is Vector3 networked)
            {
                value = networked;
            }
        }
    }
}
