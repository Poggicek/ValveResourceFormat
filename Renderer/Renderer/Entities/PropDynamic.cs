using System.Linq;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
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
public class PropDynamic : BaseModelEntity
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

        if (PlayAnimationByName(animation) >= 0f)
        {
            if (KeyValues.GetBooleanProperty("holdanimation"))
            {
                model.AnimationController.PauseLastFrame();
            }
            else if (!model.AnimationController.Looping)
            {
                // A default animation that does not loop is a performance rather than a pose, and the map
                // has something else in mind for when it happens: a choreographed scene, or a script. Run
                // here it would play itself out at load, unwatched, leaving the entity standing in the pose
                // it ends in - an actor who has already walked off before the player arrives. Its first
                // frame is where that performance starts from, so that is what to hold.
                model.AnimationController.IsPaused = true;
                model.AnimationController.Frame = 0;
            }
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
    protected void InputEnable(EntityInputData data) => IsDrawn = true;

    /// <summary>Hides the prop.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    protected void InputDisable(EntityInputData data) => IsDrawn = false;

    /// <summary>Toggles whether the prop is drawn.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    protected void InputToggle(EntityInputData data) => IsDrawn = !IsDrawn;

    /// <summary>Makes the prop solid again. Only bites when it was authored with a collision shape.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("EnableCollision")]
    protected void InputEnableCollision(EntityInputData data) => IsSolid = true;

    /// <summary>Marks the prop non-solid.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("DisableCollision")]
    protected void InputDisableCollision(EntityInputData data) => IsSolid = false;

    /// <summary>Plays an animation by name, looping if the animation itself loops.</summary>
    /// <param name="data">Carries the animation name.</param>
    [EntityInput("SetAnimation")]
    protected void InputSetAnimation(EntityInputData data) => PlayAnimation(data.Parameter, holdLastFrame: false);

    /// <summary>Plays an animation once and holds on its last frame.</summary>
    /// <param name="data">Carries the animation name.</param>
    [EntityInput("SetAnimationNotLooping")]
    protected void InputSetAnimationNotLooping(EntityInputData data) => PlayAnimation(data.Parameter, holdLastFrame: true);

    private void PlayAnimation(string? animationName, bool holdLastFrame)
    {
        PlayAnimationByName(animationName);

        if (holdLastFrame && ModelNode is { } model)
        {
            model.AnimationController.PauseLastFrame();
        }
    }

    /// <summary>
    /// Plays one of the model's animations and reports how long it lasts, for the entities that drive a
    /// model rather than being one. A <see cref="ScriptedSequence"/> needs both: the animation to start,
    /// and when it will be over so the script can move on.
    /// </summary>
    /// <param name="animationName">The animation to play. An unknown name stops the current one, as
    /// <see cref="SceneNodes.ModelSceneNode.SetAnimationByName"/> does.</param>
    /// <param name="looping">
    /// Whether to keep playing it. Left unset, the animation itself decides: the controller loops whatever
    /// it is given, which for a one-shot means replaying its events for the rest of the map, so a button
    /// told to play its press animation would sound its press once every cycle, forever. An animation graph
    /// clip carries no such flag, and those are the ones authored to loop, so they keep looping.
    /// </param>
    /// <returns>
    /// How long the animation runs in seconds, zero for a single-frame pose, or -1 when the model has no
    /// animation by that name.
    /// </returns>
    public float PlayAnimationByName(string? animationName, bool? looping = null)
    {
        if (string.IsNullOrEmpty(animationName) || ModelNode is not { } model)
        {
            return -1f;
        }

        if (!model.Animations.TryGetValue(animationName, out var animation))
        {
            model.SetAnimation(null);
            return -1f;
        }

        model.AnimationController.Looping = looping ?? animation is not SequenceAnimation { IsLooping: false };

        // Playing means playing: the pause is on the controller rather than the clip, so an entity left
        // holding a frame - by holdanimation, or by a default animation waiting to be performed - would
        // otherwise stay frozen through every animation anything played on it afterwards
        model.AnimationController.IsPaused = false;

        model.SetAnimation(animation);

        return animation.Duration;
    }
}
