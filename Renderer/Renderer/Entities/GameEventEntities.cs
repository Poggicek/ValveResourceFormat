using Microsoft.Extensions.Logging;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_achievement</c>. Tells the achievement system that something happened.
/// </summary>
/// <remarks>
/// A viewer awards nothing, so the event is logged and the wiring carries on. It is registered rather than
/// left out because an unimplemented classname swallows the whole chain that fires it, and a map often
/// hangs a relay off the same output.
/// </remarks>
public sealed class LogicAchievement : BaseEntity
{
    /// <summary>Gets whether the entity is listening.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>
    /// Initializes a <c>logic_achievement</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicAchievement(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        IsEnabled = !KeyValues.GetBooleanProperty("startdisabled");
    }

    /// <summary>Reports the achievement event.</summary>
    /// <param name="data">The input's parameter and sender; the activator is passed along.</param>
    [EntityInput("FireEvent")]
    private void InputFireEvent(EntityInputData data)
    {
        if (!IsEnabled)
        {
            return;
        }

        EntitySystem.Logger.LogDebug("Achievement event from '{TargetName}': {Achievement}",
            TargetName, KeyValues.GetStringProperty("achievementevent"));

        EntitySystem.TriggerOutput(this, "OnFired", data.Activator);
    }

    /// <summary>Starts listening.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => IsEnabled = true;

    /// <summary>Stops listening.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => IsEnabled = false;
}

/// <summary>
/// <c>logic_autosave</c>. Asks the game to save. A viewer has nothing to save, so the request is noted and
/// the chain around it runs on.
/// </summary>
public sealed class LogicAutosave : BaseEntity
{
    /// <summary>
    /// Initializes a <c>logic_autosave</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicAutosave(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Notes the save point.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Save")]
    private void InputSave(EntityInputData data)
        => EntitySystem.Logger.LogDebug("Autosave requested by '{TargetName}'", TargetName);

    /// <summary>Notes a save point that only happens when the player is healthy enough.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("SaveDangerous")]
    private void InputSaveDangerous(EntityInputData data)
        => EntitySystem.Logger.LogDebug("Dangerous autosave requested by '{TargetName}'", TargetName);
}

/// <summary>
/// <c>point_clientcommand</c>. Runs a console command as if the player typed it. Nothing is run here: a
/// map handing a viewer arbitrary commands is not something to honour, and most of them address a game
/// this is not.
/// </summary>
public sealed class PointClientCommand : BaseEntity
{
    /// <summary>
    /// Initializes a <c>point_clientcommand</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PointClientCommand(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>
    /// Runs the one console command a viewer can meaningfully run - the one that changes level - and notes
    /// the rest.
    /// </summary>
    /// <remarks>
    /// This is how a Half-Life Alyx level actually ends. There is no <c>trigger_changelevel</c> in its
    /// maps: the map fires <c>map a1_intro_world_2</c> at a <c>point_clientcommand</c> and lets the console
    /// do it, so a viewer that ignores console commands never reaches the next map.
    /// </remarks>
    /// <param name="data">Carries the command line.</param>
    [EntityInput("Command")]
    private void InputCommand(EntityInputData data)
    {
        var command = data.Parameter?.Trim();

        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        // "map <name>" and "changelevel <name>" both open a level; the rest of the console is not here
        var separator = command.IndexOf(' ', StringComparison.Ordinal);
        var verb = separator < 0 ? command : command[..separator];

        if (separator > 0
            && (verb.Equals("map", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("changelevel", StringComparison.OrdinalIgnoreCase)))
        {
            var mapName = command[(separator + 1)..].Trim();

            if (mapName.Length > 0)
            {
                EntitySystem.Logger.LogInformation("Level change from '{TargetName}': {Command}", TargetName, command);
                EntitySystem.RequestLevelChange(mapName);
                return;
            }
        }

        EntitySystem.Logger.LogDebug("Ignoring console command from '{TargetName}': {Command}", TargetName, command);
    }
}

/// <summary>
/// <c>point_instructor_event</c>. Shows one of the game's instruction hints. There is no hint system here,
/// so the request is noted and whatever the map wired around it still runs.
/// </summary>
public sealed class PointInstructorEvent : BaseEntity
{
    /// <summary>
    /// Initializes a <c>point_instructor_event</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PointInstructorEvent(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Notes the hint.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("ShowLesson")]
    private void InputShowLesson(EntityInputData data)
        => EntitySystem.Logger.LogDebug("Instructor lesson from '{TargetName}': {Lesson}",
            TargetName, KeyValues.GetStringProperty("hint_name"));
}
