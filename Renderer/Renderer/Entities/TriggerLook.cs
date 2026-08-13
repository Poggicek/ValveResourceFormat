using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>trigger_look</c>. Fires when the player, standing in the volume, looks at a named target for long
/// enough. How a map notices that you have seen something.
/// </summary>
/// <remarks>
/// The look test is the engine's: the dot product of the player's view direction against the direction to
/// the target, compared to <c>FieldOfView</c>, where 1 is straight ahead and 0 is anywhere in front. The
/// timer resets on leaving the volume or looking away, so a glance does not accumulate.
/// </remarks>
public sealed class TriggerLook : TriggerMultiple
{
    /// <summary>Gets how closely the player must be looking at the target, as a dot product.</summary>
    public float FieldOfView { get; private set; } = 0.9f;

    /// <summary>Gets how long the player must keep looking before it fires, in seconds.</summary>
    public float LookTime { get; private set; } = 0.5f;

    /// <summary>Gets how long after entering it gives up, or zero to wait forever.</summary>
    public float Timeout { get; private set; }

    private Vector3? targetPosition;
    private float lookingSince = -1f;
    private float enteredAt = -1f;
    private bool isLooking;
    private bool hasFired;

    /// <summary>
    /// Initializes a <c>trigger_look</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public TriggerLook(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        FieldOfView = KeyValues.GetFloatProperty("fieldofview", 0.9f);
        LookTime = KeyValues.GetFloatProperty("looktime", 0.5f);
        Timeout = KeyValues.GetFloatProperty("timeout");
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        base.Activate();

        var targetName = KeyValues.GetStringProperty("target");

        if (string.IsNullOrEmpty(targetName))
        {
            return;
        }

        foreach (var target in EntitySystem.FindTargets(targetName, caller: this))
        {
            targetPosition = target.WorldOrigin;
            return;
        }

        // The look target is usually an info_target, which nothing simulates but the loader still placed
        if (Scene.FindNodeByTargetName(targetName) is { } node)
        {
            targetPosition = node.Transform.Translation;
        }
    }

    /// <inheritdoc/>
    protected override void OnStartTouch(BaseEntity other)
    {
        base.OnStartTouch(other);

        if (other is PlayerEntity)
        {
            enteredAt = EntitySystem.CurrentTime;
        }
    }

    /// <inheritdoc/>
    protected override void OnTouch(BaseEntity other)
    {
        base.OnTouch(other);

        if (hasFired || other is not PlayerEntity player || targetPosition is not { } target)
        {
            return;
        }

        if (Timeout > 0f && enteredAt >= 0f && EntitySystem.CurrentTime - enteredAt >= Timeout)
        {
            hasFired = true;

            EntitySystem.TriggerOutput(this, "OnTimeout", other);
            return;
        }

        var toTarget = target - player.Controller.EyePosition;

        if (toTarget.LengthSquared() < float.Epsilon)
        {
            return;
        }

        var looking = Vector3.Dot(Vector3.Normalize(toTarget), player.Controller.ViewForward) >= FieldOfView;

        if (!looking)
        {
            StopLooking(other);
            return;
        }

        if (!isLooking)
        {
            isLooking = true;
            lookingSince = EntitySystem.CurrentTime;

            EntitySystem.TriggerOutput(this, "OnStartLook", other);
        }

        if (EntitySystem.CurrentTime - lookingSince < LookTime)
        {
            return;
        }

        hasFired = true;

        EntitySystem.TriggerOutput(this, "OnTrigger", other);
    }

    /// <inheritdoc/>
    protected override void OnEndTouch(BaseEntity other)
    {
        base.OnEndTouch(other);

        if (other is PlayerEntity)
        {
            StopLooking(other);
            enteredAt = -1f;
        }
    }

    /// <summary>Ends a look in progress, so the next one starts its clock again.</summary>
    private void StopLooking(BaseEntity other)
    {
        if (!isLooking)
        {
            return;
        }

        isLooking = false;
        lookingSince = -1f;

        EntitySystem.TriggerOutput(this, "OnEndLook", other);
    }
}
