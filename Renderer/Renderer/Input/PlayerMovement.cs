using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.Renderer.Movement;
using ValveResourceFormat.Renderer.Movement.Demo;

namespace ValveResourceFormat.Renderer.Input;

/// <summary>
/// The player walking the map: samples the input every frame, simulates the game's movement one user
/// command per 64 Hz tick, and places the camera between the last two ticks.
/// </summary>
public sealed class PlayerMovement : IPlayerController
{
    private const float TickInterval = MovementServices.TickInterval;

    // Frame times are clamped to this, so a stall is not owed back
    private const float MaxFrameTime = 0.1f;

    // cl_pitchup and cl_pitchdown, in degrees
    private const float PitchUp = 89f;
    private const float PitchDown = 89f;

    // The landing dip: how far the eye sinks and comes back, and over how long
    private const float LandingDipDepth = 3f;
    private const float LandingDipTime = 0.2f;

    private readonly UserInput Input;
    private readonly MovementCollision Collision = new();
    private readonly MovementServices Services;
    private readonly CommandBuilder Commands = new();
    private readonly Effects SoundEffects;

    // Time owed since the last tick, which is how far into the tick being built the frame is
    private float Accumulator;

    private bool InLandingDip;
    private float LandingDipStart;
    private float LastFrameFallVelocity;

    // Where the last two ticks left the player, for drawing them in between
    private Vector3 PreviousOrigin;
    private float PreviousViewOffset;
    private float PreviousDuckAmount;

    private Vector3 WeaponPunch;
    private SurfacePropertyTable? surfaces;
    private WeaponSpeedTable? weaponSpeeds;

    // The playback the player was last placed for, and the aim of the command it last played
    private DemoPlayback? StartedPlayback;
    private Vector3 PlaybackViewAngles;

    // Buttons seen since the entity tick last collected them, and what it collected that time
    private TrackedKeys PendingButtons;
    private TrackedKeys HeldButtons;
    private TrackedKeys LastCollectedButtons;

    /// <summary>Initializes the player movement for an input source.</summary>
    /// <param name="input">The input to read keys, mouse and the physics world from.</param>
    public PlayerMovement(UserInput input)
    {
        Input = input;
        Recorder = new MovementRecorder(Vars);
        Services = new MovementServices(Vars, Collision) { Recorder = Recorder };
        SoundEffects = new Effects(this);
        Services.Effects = SoundEffects;
    }

    /// <summary>Gets the console variables the movement reads, with the game's defaults.</summary>
    public MovementVars Vars { get; } = new();

    /// <summary>Gets the recorder keeping the last ten seconds of movement in detail, for export.</summary>
    public MovementRecorder Recorder { get; }

    /// <summary>Gets or sets whether the player is placed at the camera on the next frame.</summary>
    public bool Initialize { get; set; }

    /// <summary>
    /// Gets or sets a demo whose commands move the player instead of the input, or null to walk.
    /// The player is placed where the demo starts on the next frame.
    /// </summary>
    public DemoPlayback? Playback { get; set; }

    /// <summary>
    /// Plays the demo on without placing the camera, for watching it from a camera flying freely.
    /// </summary>
    /// <param name="deltaTime">Seconds since the last frame.</param>
    public void UpdateDetachedPlayback(float deltaTime)
    {
        if (Playback is not { } playback)
        {
            return;
        }

        Collision.World = Input.PhysicsWorld;
        Collision.Entities = Input.EntitySystem;

        ProcessPlayback(null, deltaTime, playback);
    }

    /// <summary>Gets or sets the held weapon's max speed, or null when holding nothing.</summary>
    public float? WeaponMaxSpeed
    {
        get => Services.WeaponMaxSpeed;
        set => Services.WeaponMaxSpeed = value;
    }

    /// <summary>Gets whether the player is simulated, the viewer's walk mode.</summary>
    public bool IsActive => Input.WalkMode;

    /// <summary>Gets the origin at the feet as of the last tick.</summary>
    public Vector3 Position => Services.AbsOrigin;

    /// <summary>Gets the origin at the feet as drawn this frame, between the last two ticks.</summary>
    public Vector3 RenderPosition { get; private set; }

    /// <summary>Gets the velocity as of the last tick, in units per second.</summary>
    public Vector3 Velocity => Services.AbsVelocity;

    /// <summary>Gets whether the player stands on something.</summary>
    public bool OnGround => Services.OnGround;

    /// <summary>Gets whether the player is on a ladder.</summary>
    public bool OnLadder => Services.MoveType == MoveType.Ladder;

    /// <summary>Gets the normal of the ladder being climbed, pointing away from it.</summary>
    public Vector3 LadderNormal => Services.LadderNormal;

    /// <summary>Gets whether a jump left the ground since the last frame.</summary>
    public bool Jumped { get; private set; }

    /// <summary>Gets how far ducked the player is drawn, from 0 standing to 1 ducked.</summary>
    public float CrouchBlend { get; private set; }

    // How far between the last tick and the next the frame is drawn
    private float TickFraction => Accumulator / TickInterval;

    // The time the frame is drawn at, on the movement's clock
    private float RenderTime => (Services.TickBase * TickInterval) + (TickFraction * TickInterval);

    /// <summary>Gets the jump stamina penalty, from 0 to the max.</summary>
    public float Stamina => Services.Stamina;

    /// <inheritdoc/>
    public Vector3 HullHalfExtents => (Services.PlayerMaxs - Services.PlayerMins) * 0.5f;

    /// <inheritdoc/>
    public Vector3 EyePosition { get; private set; }

    /// <inheritdoc/>
    public Vector3 ViewForward => Input.Camera.Forward;

    /// <inheritdoc/>
    public BaseEntity? GroundEntity => Services.OnGround ? Services.GroundEntity : null;

    /// <inheritdoc/>
    public Vector3 HullCenter => Services.AbsOrigin + new Vector3(0f, 0f, HullHalfExtents.Z);

    /// <summary>
    /// Gets the view punch from landings and weapon fire, as (pitch, yaw) degrees, added to the aim for
    /// drawing only.
    /// </summary>
    public Vector2 ViewPunchDegrees
    {
        get
        {
            var landing = Services.GetViewPunchAngle(RenderTime);
            return new Vector2(landing.X + WeaponPunch.X, landing.Y + WeaponPunch.Y);
        }
    }

    /// <summary>
    /// Gets extra solid half-spaces for tests, each an outward unit normal in XYZ and the plane offset
    /// in W: points with n.p &lt;= d are solid.
    /// </summary>
    public List<Vector4> DebugCollisionPlanes => Collision.Planes;

    /// <summary>Adds to the view punch, which decays at the view punch decay rate.</summary>
    /// <param name="degrees">Punch as (pitch, yaw) degrees.</param>
    public void AddViewPunch(Vector2 degrees) => WeaponPunch += new Vector3(degrees, 0f);

    /// <summary>Pre-decodes the sounds movement plays, so the first step does not decode mid-frame.</summary>
    public static void CacheSounds()
    {
        foreach (var soundEvent in Effects.CommonSounds)
        {
            Sound.Cache(soundEvent);
        }
    }

    /// <summary>
    /// Runs the ticks this frame completes and places the camera.
    /// </summary>
    /// <param name="camera">The camera, whose angles are the view; its location is set to the eyes.</param>
    /// <param name="deltaTime">Seconds since the last frame.</param>
    public void ProcessMovement(Camera camera, float deltaTime)
    {
        Collision.World = Input.PhysicsWorld;
        Collision.Entities = Input.EntitySystem;

        if (Playback is { } playback)
        {
            Initialize = false;
            ProcessPlayback(camera, deltaTime, playback);
            return;
        }

        StartedPlayback = null;

        var held = MapButtons(Input.Keys);

        // The aim itself is clamped, so mouse movement past the limit is lost rather than wound back first
        camera.Pitch = Math.Clamp(camera.Pitch, float.DegreesToRadians(-PitchUp), float.DegreesToRadians(PitchDown));
        var viewAngles = camera.GetQAngle();

        if (Initialize)
        {
            Initialize = false;
            ResetFromCamera(camera, held, viewAngles);
        }

        SampleEntityButtons();

        WeaponPunch *= MathF.Exp(-Vars.ViewPunchDecay * deltaTime);
        Jumped = false;

        Accumulator += Math.Clamp(deltaTime, 0.0001f, MaxFrameTime);
        var ticks = (int)(Accumulator / TickInterval);
        Accumulator = MathF.Max(0f, Accumulator - (float)(ticks * TickInterval));

        // This frame's input happened now, after the ticks it completes: their commands are built
        // first, from earlier frames, and it goes to the tick still accumulating
        var taps = (Input.Keys & (TrackedKeys.MouseWheelUp | TrackedKeys.MouseWheelDown)) != 0 ? InputButtons.Jump : InputButtons.None;
        Commands.AddFrame((float)(Accumulator + ((double)ticks * TickInterval)) / TickInterval, held, taps, viewAngles);

        // Each command is predicted as soon as it is built
        for (var i = 0; i < ticks; i++)
        {
            RunTick();
        }

        PlaceCamera(camera);
        SoundEffects.Update();
    }

    private void ProcessPlayback(Camera? camera, float deltaTime, DemoPlayback playback)
    {
        if (StartedPlayback != playback && !BeginPlayback(playback))
        {
            Playback = null;
            return;
        }

        WeaponPunch *= MathF.Exp(-Vars.ViewPunchDecay * deltaTime);
        Jumped = false;

        if (!playback.Paused)
        {
            Accumulator += Math.Clamp(deltaTime, 0.0001f, MaxFrameTime);
            var ticks = (int)(Accumulator / TickInterval);
            Accumulator = MathF.Max(0f, Accumulator - (float)(ticks * TickInterval));

            for (var i = 0; i < ticks && !playback.Finished; i++)
            {
                PreviousOrigin = Services.AbsOrigin;
                PreviousViewOffset = Services.ViewOffsetZ;
                PreviousDuckAmount = Services.DuckAmount;

                if (playback.Step() is { } command)
                {
                    PlaybackViewAngles = command.ViewAngles;
                    Jumped |= Services.Jumped;
                }
            }

            // Held on the last tick once it is over, not between it and the one before
            if (playback.Finished)
            {
                Accumulator = 0f;
                PreviousOrigin = Services.AbsOrigin;
                PreviousViewOffset = Services.ViewOffsetZ;
                PreviousDuckAmount = Services.DuckAmount;
            }
        }

        // The view is the recorded player's
        camera?.SetFromQAngle(PlaybackViewAngles);

        PlaceCamera(camera);
        SoundEffects.Update();
    }

    private bool BeginPlayback(DemoPlayback playback)
    {
        surfaces ??= SurfacePropertyTable.Load(Input.RendererContext.FileLoader);
        weaponSpeeds ??= WeaponSpeedTable.Load(Input.RendererContext.FileLoader);
        Services.Surfaces = surfaces;

        if (playback.Begin(Services, weaponSpeeds.Get) is not { } viewAngles)
        {
            return false;
        }

        StartedPlayback = playback;
        PlaybackViewAngles = viewAngles;
        Recorder.Mark("demo");

        Accumulator = 0f;
        InLandingDip = false;
        LastFrameFallVelocity = 0f;
        PreviousOrigin = Services.AbsOrigin;
        PreviousViewOffset = Services.ViewOffsetZ;
        PreviousDuckAmount = Services.DuckAmount;
        WeaponPunch = Vector3.Zero;

        CacheSounds();
        return true;
    }

    private void RunTick()
    {
        PreviousOrigin = Services.AbsOrigin;
        PreviousViewOffset = Services.ViewOffsetZ;
        PreviousDuckAmount = Services.DuckAmount;

        Services.RunCommand(Commands.CreateMove(Vars.SubtickMovementViewAngles));

        Jumped |= Services.Jumped;
    }

    /// <summary>
    /// Draws the player one tick behind the newest simulated state, blended between the last two ticks by
    /// how far the frame is into the next. The view angles are the newest ones, not blended.
    /// </summary>
    private void PlaceCamera(Camera? camera)
    {
        var fraction = TickFraction;

        RenderPosition = Vector3.Lerp(PreviousOrigin, Services.AbsOrigin, fraction);
        CrouchBlend = float.Lerp(PreviousDuckAmount, Services.DuckAmount, fraction);

        var viewOffset = float.Lerp(PreviousViewOffset, Services.ViewOffsetZ, fraction);
        var eye = RenderPosition + new Vector3(0f, 0f, viewOffset);

        ApplyLandingDip(ref eye);
        ClampEyeUnderCeiling(ref eye);

        EyePosition = eye;

        // Without a camera the player is only placed for drawing
        if (camera == null)
        {
            return;
        }

        camera.Location = eye;

        var punch = Services.GetViewPunchAngle(RenderTime);
        camera.Roll = float.DegreesToRadians(punch.Z);
    }

    /// <summary>
    /// Landing from a fall sinks the eye a little and brings it back, eased in and out.
    /// </summary>
    private void ApplyLandingDip(ref Vector3 eye)
    {
        var fallVelocity = Services.FallVelocity;

        if (!InLandingDip && fallVelocity <= 0.1f && LastFrameFallVelocity > 250f && LastFrameFallVelocity <= 1024f)
        {
            InLandingDip = true;
            LandingDipStart = RenderTime;
        }

        if (InLandingDip)
        {
            var before = eye.Z;
            var progress = Math.Clamp(MathF.Max(RenderTime - LandingDipStart, 0f) / LandingDipTime, 0f, 1f);

            // Down for the first half, back up for the second
            var depth = progress < 0.5f ? 2f * progress : Math.Clamp(1f - (2f * (progress - 0.5f)), 0f, 1f);
            var eased = depth < 0.5f ? 4f * depth * depth * depth : 1f - (0.5f * MathF.Pow(2f - (2f * depth), 3f));

            eye.Z -= LandingDipDepth * eased;
            InLandingDip = progress < 1f && !(eye.Z - 0.001f >= before);
        }

        if (!InLandingDip)
        {
            LastFrameFallVelocity = fallVelocity;
        }
    }

    /// <summary>Keeps the eye 8 units under the top of the hull, which a duck animating can push it past.</summary>
    private void ClampEyeUnderCeiling(ref Vector3 eye)
    {
        var top = RenderPosition.Z + Services.PlayerMaxs.Z;

        if (eye.Z + 8f <= top)
        {
            return;
        }

        var start = eye with { Z = top - 8f };
        eye = Collision.TraceHull(start, eye, new Vector3(-8f), new Vector3(8f), Rubikon.Cs2PlayerCollisionFilter).EndPos;
    }

    private void ResetFromCamera(Camera camera, InputButtons held, Vector3 viewAngles)
    {
        var origin = camera.Location - new Vector3(0f, 0f, MovementServices.ViewHeight);

        Services.Reset(origin, viewAngles);
        Services.AbsVelocity = Input.Velocity;
        Unstick();
        Recorder.Mark("reset");

        Commands.Reset(held, viewAngles);
        Accumulator = 0f;
        InLandingDip = false;
        LastFrameFallVelocity = 0f;

        surfaces ??= SurfacePropertyTable.Load(Input.RendererContext.FileLoader);
        Services.Surfaces = surfaces;

        PreviousOrigin = Services.AbsOrigin;
        PreviousViewOffset = Services.ViewOffsetZ;
        PreviousDuckAmount = 0f;
        WeaponPunch = Vector3.Zero;

        // Nothing pressed while flying the camera was pressed at anything in the world
        PendingButtons = Input.Keys;
        HeldButtons = Input.Keys;
        LastCollectedButtons = Input.Keys;

        CacheSounds();
    }

    // A camera inside geometry would start the player stuck, so look for room nearby, upwards first
    private void Unstick()
    {
        if (!IsHullStuck(HullCenter))
        {
            return;
        }

        Span<Vector3> directions = [Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY];

        for (var distance = 1f; distance < 100f; distance += 4f)
        {
            foreach (var direction in directions)
            {
                var center = HullCenter + direction * distance;

                if (!IsHullStuck(center))
                {
                    Services.AbsOrigin += direction * distance;
                    return;
                }
            }
        }
    }

    private static InputButtons MapButtons(TrackedKeys keys)
    {
        var buttons = InputButtons.None;

        void Map(TrackedKeys key, InputButtons button)
        {
            if ((keys & key) != 0)
            {
                buttons |= button;
            }
        }

        Map(TrackedKeys.W, InputButtons.Forward);
        Map(TrackedKeys.S, InputButtons.Back);
        Map(TrackedKeys.A, InputButtons.MoveLeft);
        Map(TrackedKeys.D, InputButtons.MoveRight);
        Map(TrackedKeys.Space, InputButtons.Jump);
        Map(TrackedKeys.Control, InputButtons.Duck);
        Map(TrackedKeys.Shift, InputButtons.Speed);
        Map(TrackedKeys.E, InputButtons.Use);

        return buttons;
    }

    /// <inheritdoc/>
    public Rubikon.TraceResult TraceHull(Vector3 startCenter, Vector3 endCenter)
    {
        var offset = new Vector3(0f, 0f, HullHalfExtents.Z);
        var trace = Collision.TraceHull(startCenter - offset, endCenter - offset, Services.PlayerMins, Services.PlayerMaxs, Rubikon.Cs2PlayerCollisionFilter);

        if (!trace.DidHit)
        {
            return new Rubikon.TraceResult();
        }

        return new Rubikon.TraceResult(true, trace.EndPos + offset, trace.Normal, trace.Fraction * Vector3.Distance(startCenter, endCenter), -1)
        {
            StartSolid = trace.StartSolid,
            HitEntity = trace.Entity,
            SurfacePropertyHash = trace.SurfacePropertyHash,
        };
    }

    /// <inheritdoc/>
    public bool IsHullStuck(Vector3 center)
    {
        var origin = center - new Vector3(0f, 0f, HullHalfExtents.Z);
        return Collision.TraceHull(origin, origin, Services.PlayerMins, Services.PlayerMaxs, Rubikon.Cs2PlayerCollisionFilter).StartSolid;
    }

    /// <inheritdoc/>
    public void Push(Vector3 delta) => Services.AbsOrigin += delta;

    /// <inheritdoc/>
    public void Teleport(Vector3 feetPosition, Vector3? angles)
    {
        var moved = feetPosition - Services.AbsOrigin;

        Services.AbsOrigin = feetPosition;
        Recorder.Mark("teleport");
        PreviousOrigin += moved;
        RenderPosition += moved;
        EyePosition += moved;
        Input.Camera.Location += moved;

        if (angles is { } viewAngles)
        {
            Input.Camera.SetFromQAngle(viewAngles);
            Commands.Reset(MapButtons(Input.Keys), viewAngles);
        }
    }

    // Folds this frame's buttons into what the next entity tick collects, as that tick is slower than frames
    private void SampleEntityButtons()
    {
        HeldButtons = Input.Keys;
        PendingButtons |= HeldButtons;
    }

    /// <inheritdoc/>
    public PlayerButtonState ConsumeButtons()
    {
        var held = PendingButtons;
        var changed = held ^ LastCollectedButtons;

        LastCollectedButtons = held;

        // Only what is still down survives, so a tap between two ticks reports pressed then released
        PendingButtons = HeldButtons;

        return new PlayerButtonState(held, changed & held, changed & ~held);
    }

    /// <summary>Plays the sounds of movement, following the player as they move.</summary>
    private sealed class Effects(PlayerMovement movement) : IMovementEffects
    {
        private const string JumpSound = "Default.WalkJump";
        private const string GearSound = "Gear.JumpLand.CT";
        private const string FallDamageSound = "Player.DamageFall";
        private const string DefaultFootstep = "CT_Concrete.StepLeft";
        private const string LadderFootstep = "CT_Ladder.StepLeft";

        public static readonly string[] CommonSounds = [JumpSound, GearSound, FallDamageSound, DefaultFootstep, LadderFootstep];

        private readonly List<Audio.SoundHandle> Playing = [];

        public void PlayStepSound(Vector3 origin, uint surfacePropertyHash, float volume, bool onLadder)
            => Play(FootstepFor(surfacePropertyHash, onLadder), origin, volume);

        public void PlayJumpSound(Vector3 origin) => Play(JumpSound, origin, null);

        public void OnLand(Vector3 origin, float fallVelocity)
        {
            if (!(movement.Vars.MinJumpLandingSound < fallVelocity))
            {
                return;
            }

            // The landing is the surface's footstep with its team prefix swapped for "land_"
            var footstep = FootstepFor(movement.Services.LastGroundSurface, onLadder: false);
            var name = footstep.StartsWith("CT_", StringComparison.OrdinalIgnoreCase) ? footstep[3..] : footstep;

            Play($"Land_{name}", origin, null);
            Play(GearSound, origin, null);
        }

        public void OnFallDamage(Vector3 origin, float damage) => Play(FallDamageSound, origin, null);

        public void PlayWaterSound(Vector3 origin, string soundEvent) => Play(soundEvent, origin, null);

        private string FootstepFor(uint surfacePropertyHash, bool onLadder)
        {
            var surfaces = movement.Services.Surfaces;
            var surface = onLadder ? surfaces.Ladder : surfaces.Get(surfacePropertyHash);

            return surface.FootstepSound ?? (onLadder ? LadderFootstep : DefaultFootstep);
        }

        private void Play(string soundEvent, Vector3 origin, float? volume)
        {
            var handle = Sound.Play(soundEvent, origin, volume: volume);

            if (handle.IsValid)
            {
                Playing.Add(handle);
            }
        }

        /// <summary>
        /// Keeps the sounds at the player, as they are attached to them in the game. Left where they
        /// started they would trail behind at run speed.
        /// </summary>
        public void Update()
        {
            for (var i = Playing.Count - 1; i >= 0; i--)
            {
                if (!Playing[i].Playing)
                {
                    Playing.RemoveAt(i);
                    continue;
                }

                var handle = Playing[i];
                handle.Position = movement.RenderPosition;
            }
        }
    }
}
