using System.Linq;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>prop_dynamic</c>. A model a map can show, hide, repaint and animate. The most numerous thing a map
/// wires up, and almost always the far end of the wiring rather than the source of it.
/// </summary>
/// <remarks>
/// <para>
/// A prop collides when the map says it does: <c>solid</c> is Source's <c>SOLID_</c> enum, and a prop
/// authored <c>SOLID_NONE</c> skips building a collision shape at all rather than building one nothing
/// will ever trace against.
/// </para>
/// <para>
/// Props are the reason <see cref="AllowsSceneParenting"/> exists: they are the usual child of a
/// <c>parentname</c>, and unlike a brush that moves itself, a prop never writes its own transform after
/// it spawns, so the scene's parenting can drive it without the two fighting.
/// </para>
/// </remarks>
public sealed class PropDynamic : BaseModelEntity
{
    /// <summary>Source's <c>SOLID_NONE</c>: the prop is scenery and nothing traces against it.</summary>
    private const int SolidNone = 0;

    /// <inheritdoc/>
    protected override bool CreatesCollider => IsAuthoredSolid;

    /// <summary>Whether the map authored this prop as something to collide with.</summary>
    private bool IsAuthoredSolid => Data?.GetInt32Property("solid") != SolidNone;

    /// <inheritdoc/>
    public override bool AllowsSceneParenting => true;

    /// <summary>
    /// Initializes a <c>prop_dynamic</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PropDynamic(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
        IsSolid = IsAuthoredSolid;
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        if (ModelNode is not { } model)
        {
            return;
        }

        // The same setup the loader gives a prop it loads itself, so routing one through the entity
        // system does not quietly cost it its animation or its body group
        var animation = KeyValues.GetStringProperty("defaultanim") ?? KeyValues.GetStringProperty("idleanim");

        if (!string.IsNullOrEmpty(animation)
            && model.SetAnimationForWorldPreview(animation)
            && KeyValues.GetBooleanProperty("holdanimation"))
        {
            model.AnimationController.PauseLastFrame();
        }

        var body = KeyValues.GetInt32Property("body");

        if (body > 0)
        {
            model.SetActiveMeshGroups(model.GetMeshGroups().Skip(body).Take(1));
        }

        if (KeyValues.GetBooleanProperty("startdisabled"))
        {
            IsDrawn = false;
        }
    }

    /// <summary>Draws the prop again.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => IsDrawn = true;

    /// <summary>Hides the prop.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => IsDrawn = false;

    /// <summary>Toggles whether the prop is drawn.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data) => IsDrawn = !IsDrawn;

    /// <summary>Makes the prop solid again. Only bites when it was authored with a collision shape.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("EnableCollision")]
    private void InputEnableCollision(EntityInputData data) => IsSolid = true;

    /// <summary>Marks the prop non-solid.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("DisableCollision")]
    private void InputDisableCollision(EntityInputData data) => IsSolid = false;

    /// <summary>Plays an animation by name, looping if the animation itself loops.</summary>
    /// <param name="data">Carries the animation name.</param>
    [EntityInput("SetAnimation")]
    private void InputSetAnimation(EntityInputData data) => PlayAnimation(data.Parameter, holdLastFrame: false);

    /// <summary>Plays an animation once and holds on its last frame.</summary>
    /// <param name="data">Carries the animation name.</param>
    [EntityInput("SetAnimationNotLooping")]
    private void InputSetAnimationNotLooping(EntityInputData data) => PlayAnimation(data.Parameter, holdLastFrame: true);

    private void PlayAnimation(string? animationName, bool holdLastFrame)
    {
        if (string.IsNullOrEmpty(animationName) || ModelNode is not { } model)
        {
            return;
        }

        model.SetAnimationByName(animationName);

        if (holdLastFrame)
        {
            model.AnimationController.PauseLastFrame();
        }
    }
}
