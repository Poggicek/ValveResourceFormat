using System.Globalization;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The NPCs, as far as anything here can be one: <c>generic_actor</c>, <c>npc_furniture</c> and the
/// citizens a Half-Life Alyx level is populated with.
/// </summary>
/// <remarks>
/// <para>
/// There is no AI and no navigation, so an actor is a model that something else animates - which is what
/// the choreographed parts of a map need it to be. Three things drive one: a
/// <see cref="ScriptedSequence"/>, which names the animation outright; a
/// <see cref="LogicChoreographedScene"/>, which names it on a timeline; and the model's own animation
/// graph, which is handed named parameters and works it out for itself.
/// </para>
/// <para>
/// That last one is why this class exists rather than the actors simply being props. Half-Life Alyx drives
/// its NPCs almost entirely through <c>SetAnimGraphParameter</c>, and a map firing
/// <c>bActionA=true</c> at an actor is describing an animation without ever naming one. See
/// <see cref="AnimationGraphRuntime"/> for how much of the graph is understood.
/// </para>
/// <para>
/// Everything else an actor answers comes from <see cref="PropDynamic"/>: it is drawn, hidden, repainted,
/// killed and animated the same way. What it does not answer are the inputs about where it is looking,
/// which describe behaviour rather than state and have nothing here to act on.
/// </para>
/// </remarks>
public class BaseActor : PropDynamic
{
    /// <summary>Gets the model's animation graph, when it has one this can run.</summary>
    public AnimationGraphRuntime? AnimationGraph { get; private set; }

    /// <summary>
    /// Initializes an actor from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public BaseActor(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        if (string.IsNullOrEmpty(ModelName) || ModelNode == null)
        {
            return;
        }

        // Already loaded and cached by the model this entity is drawn as; asked for again here only to
        // reach the resource's external references, which is where the graph is named
        var modelResource = EntitySystem.FileLoader.LoadFileCompiled(ModelName);

        if (modelResource?.DataBlock is not Model)
        {
            return;
        }

        AnimationGraph = AnimationGraphRuntime.Load(modelResource, EntitySystem.FileLoader);

        if (AnimationGraph != null)
        {
            // The graph decides frame by frame, so it is asked every tick rather than only when the map
            // sets something: a state may be waiting on its animation to finish, or on time passing
            SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
        }
    }

    /// <inheritdoc/>
    public override void Think()
    {
        EvaluateGraph();

        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>
    /// Sets one of the model's animation graph parameters, which is how a Half-Life Alyx map animates an
    /// NPC. The parameter is written as <c>name=value</c>.
    /// </summary>
    /// <param name="data">Carries the assignment.</param>
    [EntityInput("SetAnimGraphParameter")]
    protected void InputSetAnimGraphParameter(EntityInputData data)
    {
        if (AnimationGraph == null || string.IsNullOrEmpty(data.Parameter))
        {
            return;
        }

        var separator = data.Parameter.IndexOf('=', StringComparison.Ordinal);

        if (separator < 0)
        {
            EntitySystem.Logger.LogWarning(
                "{Classname} '{TargetName}' was set an animation graph parameter with no value: '{Parameter}'",
                Classname, TargetName, data.Parameter);
            return;
        }

        var name = data.Parameter[..separator].Trim();
        var text = data.Parameter[(separator + 1)..].Trim();

        // Written as the mapper thinks of it: true and false for the booleans, a number for the rest
        var value = text switch
        {
            _ when text.Equals("true", StringComparison.OrdinalIgnoreCase) => 1f,
            _ when text.Equals("false", StringComparison.OrdinalIgnoreCase) => 0f,
            _ => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0f,
        };

        if (!AnimationGraph.SetParameter(name, value))
        {
            EntitySystem.Logger.LogWarning(
                "{Classname} '{TargetName}' animation graph has no parameter '{Parameter}'",
                Classname, TargetName, name);
            return;
        }

        // Acted on at once rather than at the next tick, so the animation starts on the same instant the
        // rest of the map's wiring did
        EvaluateGraph();
    }

    /// <summary>Advances the graph and plays whatever it has decided on, if that has changed.</summary>
    private void EvaluateGraph()
    {
        if (AnimationGraph == null || ModelNode is not { } model)
        {
            return;
        }

        if (!AnimationGraph.Update(EntitySystem.TickInterval, model.AnimationController.ActiveClipFinished))
        {
            return;
        }

        if (!string.IsNullOrEmpty(AnimationGraph.CurrentSequence))
        {
            PlayAnimationByName(AnimationGraph.CurrentSequence, AnimationGraph.CurrentSequenceLoops);
        }
    }
}
