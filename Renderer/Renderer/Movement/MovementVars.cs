namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// The console variables player movement reads, with the game's defaults.
/// </summary>
public sealed class MovementVars
{
    /// <summary>sv_accelerate for movement maps, where air control is far above the default.</summary>
    public const float AirAccelerateMovementMaps = 150f;

    /// <summary>Gets or sets sv_accelerate, the ground acceleration.</summary>
    public float Accelerate { get; set; } = 5.5f;

    /// <summary>Gets or sets sv_accelerate_use_weapon_speed: whether the held weapon scales acceleration.</summary>
    public bool AccelerateUseWeaponSpeed { get; set; } = true;

    /// <summary>Gets or sets sv_air_max_wishspeed, the cap on the speed air acceleration aims for.</summary>
    public float AirMaxWishSpeed { get; set; } = 30f;

    /// <summary>Gets or sets sv_airaccelerate, the air acceleration.</summary>
    public float AirAccelerate { get; set; } = 12f;

    /// <summary>Gets or sets sv_autobunnyhopping: whether holding jump jumps again on landing.</summary>
    public bool AutoBunnyHopping { get; set; }

    /// <summary>Gets or sets sv_bhop_time_window, the time around landing in which a jump press is a bunny hop.</summary>
    public float BhopTimeWindow { get; set; } = 0.0078125f;

    /// <summary>Gets or sets sv_bounce, how much a surf ramp bounces the player off.</summary>
    public float Bounce { get; set; }

    /// <summary>Gets or sets sv_enablebunnyhopping: whether jumping may exceed 1.1 times the max speed.</summary>
    public bool EnableBunnyHopping { get; set; }

    /// <summary>Gets or sets sv_friction, the ground friction.</summary>
    public float Friction { get; set; } = 5.2f;

    /// <summary>Gets or sets sv_gravity.</summary>
    public float Gravity { get; set; } = 800f;

    /// <summary>Gets or sets sv_jump_impulse, the initial upward velocity of a jump.</summary>
    public float JumpImpulse { get; set; } = 301.99338f;

    /// <summary>Gets or sets sv_jump_precision_enable: whether airborne heights snap to a 1/64 grid.</summary>
    public bool JumpPrecision { get; set; } = true;

    /// <summary>Gets or sets sv_jump_spam_penalty_time: presses this soon after the previous one are ignored.</summary>
    public float JumpSpamPenaltyTime { get; set; } = 0.015625f;

    /// <summary>Gets or sets sv_ladder_angle, the cosine past which sideways ladder movement is damped.</summary>
    public float LadderAngle { get; set; } = -0.707f;

    /// <summary>Gets or sets sv_ladder_dampen, how much sideways ladder movement is damped.</summary>
    public float LadderDampen { get; set; } = 0.2f;

    /// <summary>Gets or sets sv_ladder_scale_speed, the scale on ladder climbing speed.</summary>
    public float LadderScaleSpeed { get; set; } = 0.78f;

    /// <summary>Gets or sets sv_legacy_jump: whether to use the jump before sub-tick landing timing.</summary>
    public bool LegacyJump { get; set; }

    /// <summary>Gets or sets sv_maxspeed.</summary>
    public float MaxSpeed { get; set; } = 320f;

    /// <summary>Gets or sets sv_maxvelocity, the cap on each velocity component.</summary>
    public float MaxVelocity { get; set; } = 3500f;

    /// <summary>Gets or sets sv_min_jump_landing_sound, the fall speed a landing must reach to be heard.</summary>
    public float MinJumpLandingSound { get; set; } = 260f;

    /// <summary>Gets or sets sv_noclipaccelerate.</summary>
    public float NoclipAccelerate { get; set; } = 5f;

    /// <summary>Gets or sets sv_noclipspeed.</summary>
    public float NoclipSpeed { get; set; } = 1200f;

    /// <summary>Gets or sets sv_optimizedmovement: whether walking skips the ground check before moving.</summary>
    public bool OptimizedMovement { get; set; } = true;

    /// <summary>Gets or sets sv_quantize_movement_input: whether analog input rounds to -1, 0 or 1.</summary>
    public bool QuantizeMovementInput { get; set; } = true;

    /// <summary>Gets or sets sv_rollangle, the most the view rolls when strafing.</summary>
    public float RollAngle { get; set; }

    /// <summary>Gets or sets sv_rollspeed, the strafe speed of full roll.</summary>
    public float RollSpeed { get; set; } = 200f;

    /// <summary>Gets or sets sv_staminajumpcost, the stamina a jump costs with the legacy jump.</summary>
    public float StaminaJumpCost { get; set; } = 0.08f;

    /// <summary>Gets or sets sv_staminalandcost, the stamina a landing costs with the legacy jump.</summary>
    public float StaminaLandCost { get; set; } = 0.05f;

    /// <summary>Gets or sets sv_staminamax.</summary>
    public float StaminaMax { get; set; } = 80f;

    /// <summary>Gets or sets sv_staminarecoveryrate, in stamina per second.</summary>
    public float StaminaRecoveryRate { get; set; } = 60f;

    /// <summary>Gets or sets sv_standable_normal, the least normal Z the player can stand on.</summary>
    public float StandableNormal { get; set; } = 0.7f;

    /// <summary>Gets or sets sv_step_move_vel_min, the speed a failed step is retried at when moving slower.</summary>
    public float StepMoveVelMin { get; set; } = 64f;

    /// <summary>Gets or sets sv_stepsize, the tallest step the player walks up.</summary>
    public float StepSize { get; set; } = 18f;

    /// <summary>Gets or sets sv_stopspeed, the speed below which friction stops the player at a constant rate.</summary>
    public float StopSpeed { get; set; } = 80f;

    /// <summary>Gets or sets sv_subtick_movement_view_angles: whether view changes inside a tick steer movement.</summary>
    public bool SubtickMovementViewAngles { get; set; } = true;

    /// <summary>Gets or sets sv_timebetweenducks, the time before a new duck can start.</summary>
    public float TimeBetweenDucks { get; set; } = 0.4f;

    /// <summary>Gets or sets sv_walkable_normal, the least normal Z the player can walk up.</summary>
    public float WalkableNormal { get; set; } = 0.7f;

    /// <summary>Gets or sets sv_water_slow_amount, the speed scale in water.</summary>
    public float WaterSlowAmount { get; set; } = 0.9f;

    /// <summary>Gets or sets sv_falldamage_scale.</summary>
    public float FallDamageScale { get; set; } = 1f;

    /// <summary>Gets or sets view_punch_decay, how fast the view punch recovers.</summary>
    public float ViewPunchDecay { get; set; } = 18f;
}
