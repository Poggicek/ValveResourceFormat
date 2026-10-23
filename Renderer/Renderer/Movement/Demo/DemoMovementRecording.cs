using System.IO;
using System.Linq;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>A user command of the recorded player and the server tick it ran in.</summary>
internal sealed record DemoCommand(int ServerTick, UserCmd Command);

/// <summary>A player's movement state as the server networked it at the end of a tick.</summary>
/// <param name="ServerTick">The tick it is the end of.</param>
/// <param name="Origin">Where the player's feet are.</param>
/// <param name="Velocity">The velocity, quantized as networked.</param>
/// <param name="GroundEntityIndex">What the player stands on, -1 for nothing and 0 for the world.</param>
/// <param name="WeaponSubclass">The string token of the held weapon's class in <c>weapons.vdata</c>, 0 for none.</param>
/// <param name="WeaponMode">Which of the weapon's firing modes is in use: 1 when scoped.</param>
/// <param name="Values">The rest, named and typed as <see cref="MovementServices.VisitState"/> names them.</param>
internal sealed record NetworkedMovementState(int ServerTick, Vector3 Origin, Vector3 Velocity, int GroundEntityIndex, uint WeaponSubclass, int WeaponMode, IReadOnlyDictionary<string, object> Values);

/// <summary>A player in a demo who sent user commands.</summary>
internal sealed record DemoPlayer(int Slot, string Name, int CommandCount);

/// <summary>
/// One player's movement read from a demo: the user commands the server ran, as it received them,
/// and the movement state it networked after each tick.
/// </summary>
/// <remarks>
/// The states are only for starting a replay and for checking it: a replay runs on the commands alone,
/// as the server does.
/// </remarks>
internal sealed class DemoMovementRecording
{
    // Networked fields of the pawn, and the movement state each one is
    private static readonly (string Field, string State)[] PawnFields =
    [
        ("m_vecBaseVelocity", "BaseVelocity"),
        ("m_fFlags", "Flags"),
        ("m_MoveType", "MoveType"),
        ("m_flWaterLevel", "WaterLevelFraction"),
        ("m_flVelocityModifier", "VelocityModifier"),
        ("m_bIsWalking", "IsWalking"),
        ("m_flGravityScale", "GravityScale"),
        ("m_flFriction", "EntityFriction"),
        ("m_pMovementServices.m_flMaxspeed", "MaxSpeed"),
        ("m_pMovementServices.m_flFallVelocity", "FallVelocity"),
        ("m_pMovementServices.m_bDucked", "Ducked"),
        ("m_pMovementServices.m_flDuckAmount", "DuckAmount"),
        ("m_pMovementServices.m_flDuckSpeed", "DuckSpeed"),
        ("m_pMovementServices.m_bDesiresDuck", "DesiresDuck"),
        ("m_pMovementServices.m_bDucking", "Ducking"),
        ("m_pMovementServices.m_flDuckRootOffset", "DuckRootOffset"),
        ("m_pMovementServices.m_flDuckViewOffset", "DuckViewOffset"),
        ("m_pMovementServices.m_flLastDuckTime", "LastDuckTime"),
        ("m_pMovementServices.m_fStashGrenadeParameterWhen", "StashGrenadeParameterWhen"),
        ("m_pMovementServices.m_bUseFrictionStashedSpeed", "UseFrictionStashedSpeed"),
        ("m_pMovementServices.m_flUseFrictionStashedSpeedUntilFrac", "UseFrictionStashedSpeedUntilFrac"),
        ("m_pMovementServices.m_flFrictionStashedSpeed", "FrictionStashedSpeed"),
        ("m_pMovementServices.m_flStamina", "Stamina"),
        ("m_pMovementServices.m_LegacyJump.m_bOldJumpPressed", "OldJumpPressed"),
        ("m_pMovementServices.m_ModernJump.m_nLastActualJumpPressTick", "LastActualJumpPressTick"),
        ("m_pMovementServices.m_ModernJump.m_flLastActualJumpPressFrac", "LastActualJumpPressFrac"),
        ("m_pMovementServices.m_ModernJump.m_nLastUsableJumpPressTick", "LastUsableJumpPressTick"),
        ("m_pMovementServices.m_ModernJump.m_flLastUsableJumpPressFrac", "LastUsableJumpPressFrac"),
        ("m_pMovementServices.m_ModernJump.m_nLastLandedTick", "LastLandedTick"),
        ("m_pMovementServices.m_ModernJump.m_flLastLandedFrac", "LastLandedFrac"),
        ("m_pMovementServices.m_ModernJump.m_flLastLandedVelocityX", "LastLandedVelocityX"),
        ("m_pMovementServices.m_ModernJump.m_flLastLandedVelocityY", "LastLandedVelocityY"),
        ("m_pMovementServices.m_ModernJump.m_flLastLandedVelocityZ", "LastLandedVelocityZ"),
        ("m_pMovementServices.m_nLastJumpTick", "LastJumpTick"),
        ("m_pMovementServices.m_flLastJumpFrac", "LastJumpFrac"),
        ("m_pMovementServices.m_flLastJumpVelocityZ", "LastJumpVelocityZ"),
        ("m_pMovementServices.m_bJumpApexPending", "JumpApexPending"),
    ];

    private const string ForceSubtickMoveWhenField = "m_pMovementServices.m_arrForceSubtickMoveWhen";

    private DemoMovementRecording(string? mapName, DemoPlayer player, IReadOnlyList<DemoPlayer> players, List<DemoCommand> commands, Dictionary<int, NetworkedMovementState> states)
    {
        MapName = mapName;
        Player = player;
        Players = players;
        Commands = commands;
        States = states;
    }

    public string? MapName { get; }

    /// <summary>Gets the player whose movement this is.</summary>
    public DemoPlayer Player { get; }

    /// <summary>Gets every player in the demo who sent commands.</summary>
    public IReadOnlyList<DemoPlayer> Players { get; }

    /// <summary>Gets the player's commands in the order the server ran them.</summary>
    public IReadOnlyList<DemoCommand> Commands { get; }

    /// <summary>Gets the player's networked state at the end of each server tick, by tick.</summary>
    public IReadOnlyDictionary<int, NetworkedMovementState> States { get; }

    /// <summary>Reads a player's movement from a demo.</summary>
    /// <param name="stream">The demo file.</param>
    /// <param name="playerSlot">The player to read, or null for the one who sent the most commands.</param>
    public static DemoMovementRecording Load(Stream stream, int? playerSlot = null)
    {
        var decoder = new UserCommandDecoder();
        var commandsBySlot = new Dictionary<int, List<DemoCommand>>();
        var statesBySlot = new Dictionary<int, Dictionary<int, NetworkedMovementState>>();
        var names = new Dictionary<int, string>();
        DemoParser? parser = null;

        parser = new DemoParser
        {
            UserCommand = command =>
            {
                if (!commandsBySlot.TryGetValue(command.PlayerSlot, out var commands))
                {
                    commands = [];
                    commandsBySlot[command.PlayerSlot] = commands;
                }

                commands.Add(new DemoCommand(command.ServerTickExecuted, decoder.Decode(command)));
            },
            EntitiesUpdated = tick =>
            {
                foreach (var slot in commandsBySlot.Keys)
                {
                    if (CaptureState(parser!, slot, tick, names) is not { } state)
                    {
                        continue;
                    }

                    if (!statesBySlot.TryGetValue(slot, out var states))
                    {
                        states = [];
                        statesBySlot[slot] = states;
                    }

                    states[tick] = state;
                }
            },
        };

        parser.Parse(stream);

        var players = commandsBySlot
            .Select(pair => new DemoPlayer(pair.Key, names.GetValueOrDefault(pair.Key, $"Player {pair.Key}"), pair.Value.Count))
            .OrderByDescending(static player => player.CommandCount)
            .ToList();

        var player = playerSlot is { } wanted
            ? players.Find(p => p.Slot == wanted) ?? throw new InvalidDataException($"Player slot {wanted} sent no commands in this demo.")
            : players.FirstOrDefault() ?? throw new InvalidDataException("The demo has no user commands. Only demos recorded on the server since CS2 records them have.");

        // The server runs a player's commands in their order, which is also the order they are recorded
        var playerCommands = commandsBySlot[player.Slot];
        playerCommands.Sort(static (a, b) => a.Command.CommandNumber.CompareTo(b.Command.CommandNumber));

        return new DemoMovementRecording(parser.MapName, player, players, playerCommands, statesBySlot.GetValueOrDefault(player.Slot) ?? []);
    }

    private static NetworkedMovementState? CaptureState(DemoParser parser, int slot, int tick, Dictionary<int, string> names)
    {
        // A player's controller is the entity after their slot
        if (!parser.Entities.TryGetValue(slot + 1, out var controller)
            || !controller.TryGet<uint>("m_hPlayerPawn", out var pawnHandle)
            || !parser.Entities.TryGetValue((int)(pawnHandle & DemoEntity.HandleIndexMask), out var pawn))
        {
            return null;
        }

        if (controller.TryGet<string>("m_iszPlayerName", out var name))
        {
            names[slot] = name;
        }

        var values = new Dictionary<string, object>();

        foreach (var (field, state) in PawnFields)
        {
            if (pawn.Class.FindPath(field) is { } path && pawn.Values.TryGetValue(path, out var value))
            {
                values[state] = value;
            }
        }

        if (pawn.Class.FindPath(ForceSubtickMoveWhenField) is { } forceWhenPath)
        {
            for (var i = 0; i < 4; i++)
            {
                if (pawn.Values.TryGetValue(forceWhenPath.Push(i), out var when))
                {
                    values[$"ForceSubtickMoveWhen{i}"] = when;
                }
            }
        }

        if (controller.TryGet<uint>("m_nTickBase", out var tickBase))
        {
            values["TickBase"] = (int)tickBase;
        }

        // Noclip is the controller's, and only overrides the pawn's move type while on
        values["NoClip"] = controller.Get("m_bNoClipEnabled", false);

        var origin = new Vector3(
            CellCoordinate(pawn, "X"),
            CellCoordinate(pawn, "Y"),
            CellCoordinate(pawn, "Z"));

        var velocity = new Vector3(
            pawn.Get("m_vecVelocity.m_vecX", 0f),
            pawn.Get("m_vecVelocity.m_vecY", 0f),
            pawn.Get("m_vecVelocity.m_vecZ", 0f));

        var groundHandle = pawn.Get("m_hGroundEntity", uint.MaxValue);
        var groundIndex = groundHandle == uint.MaxValue || (groundHandle & DemoEntity.HandleIndexMask) == DemoEntity.HandleIndexMask
            ? -1
            : (int)(groundHandle & DemoEntity.HandleIndexMask);

        var weaponSubclass = 0u;
        var weaponMode = 0;

        if (pawn.TryGet<uint>("m_pWeaponServices.m_hActiveWeapon", out var weaponHandle)
            && parser.Entities.TryGetValue((int)(weaponHandle & DemoEntity.HandleIndexMask), out var weapon))
        {
            weaponSubclass = weapon.Get("m_nSubclassID", 0u);
            weaponMode = (int)weapon.Get("m_weaponMode", 0u);
        }

        return new NetworkedMovementState(tick, origin, velocity, groundIndex, weaponSubclass, weaponMode, values);
    }

    // Positions are sent as a cell of the world and an offset inside it
    private static float CellCoordinate(DemoEntity pawn, string axis)
    {
        const int CellBits = 9;
        const float MaxCoord = 1 << 14;

        var cell = pawn.Get($"CBodyComponent.m_skeletonInstance.m_vecOrigin.m_cell{axis}", 0u);
        var offset = pawn.Get($"CBodyComponent.m_skeletonInstance.m_vecOrigin.m_vec{axis}", 0f);

        return ((cell << CellBits) - MaxCoord) + offset;
    }
}
