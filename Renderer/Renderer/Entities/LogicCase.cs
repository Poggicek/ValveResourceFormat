using System.Globalization;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_case</c>. Picks one of up to sixteen outputs, either by matching a value against the authored
/// cases or at random. What a map uses to make something happen differently each time.
/// </summary>
/// <remarks>
/// The random picks are what maps mostly want it for, minigames choosing a winner or a prize; the value
/// match is the switch statement of entity I/O. Random draws come from the entity system's own generator,
/// so a map's randomness is not tied to framerate.
/// </remarks>
public sealed class LogicCase : BaseEntity
{
    /// <summary>How many cases the entity supports, matching the FGD's <c>Case01</c> to <c>Case16</c>.</summary>
    private const int CaseCount = 16;

    private readonly string?[] cases = new string?[CaseCount];
    private readonly List<int> available = [];
    private readonly List<int> shuffle = [];
    private int lastShuffleCase = -1;

    /// <summary>
    /// Initializes a <c>logic_case</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicCase(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        for (var i = 0; i < CaseCount; i++)
        {
            // Compiled keyvalues are lowercased, and the case number is one-based and zero-padded
            var value = KeyValues.GetStringProperty($"case{i + 1:00}");

            cases[i] = string.IsNullOrEmpty(value) ? null : value;

            // A case counts as available because something is wired to its output, which is what Source's
            // BuildCaseMap tests. The authored value only decides what InValue matches, so a case picked at
            // random needs no value at all - and maps that only ever pick randomly author none.
            if (HasOutput($"OnCase{i + 1:00}"))
            {
                available.Add(i);
            }
        }
    }

    /// <summary>Whether anything is wired to the named output.</summary>
    private bool HasOutput(string outputName)
    {
        if (Data?.Connections == null)
        {
            return false;
        }

        foreach (var connection in Data.Connections)
        {
            if (connection.OutputName.Equals(outputName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fires the case matching the value, or <c>OnDefault</c> when none does.
    /// </summary>
    /// <param name="data">Carries the value to match, compared as text and then as a number.</param>
    [EntityInput("InValue")]
    private void InputInValue(EntityInputData data)
    {
        var value = data.Parameter;

        for (var i = 0; i < CaseCount; i++)
        {
            if (cases[i] is not { } authored || !Matches(authored, value))
            {
                continue;
            }

            FireCase(i, data.Activator);
            return;
        }

        EntitySystem.TriggerOutput(this, "OnDefault", data.Activator);
    }

    /// <summary>Fires one of the defined cases at random, repeats allowed.</summary>
    /// <param name="data">The input's parameter is unused; its activator is passed along.</param>
    [EntityInput("PickRandom")]
    private void InputPickRandom(EntityInputData data)
    {
        if (available.Count == 0)
        {
            return;
        }

        FireCase(available[Random.Shared.Next(available.Count)], data.Activator);
    }

    /// <summary>
    /// Fires one of the defined cases at random, without repeating until every case has come up.
    /// </summary>
    /// <param name="data">The input's parameter is unused; its activator is passed along.</param>
    [EntityInput("PickRandomShuffle")]
    private void InputPickRandomShuffle(EntityInputData data)
    {
        if (shuffle.Count == 0)
        {
            if (available.Count == 0)
            {
                return;
            }

            shuffle.AddRange(available);

            // A fresh batch may not open with the case the last one closed on, so that a repeat cannot
            // straddle the boundary. Source swaps it to the end and shortens the draw by one.
            if (shuffle.Count > 1 && lastShuffleCase != -1)
            {
                shuffle.Remove(lastShuffleCase);

                var reopened = Random.Shared.Next(shuffle.Count);
                var first = shuffle[reopened];

                shuffle.RemoveAt(reopened);
                shuffle.Add(lastShuffleCase);

                Fire(first);
                return;
            }
        }

        Fire(shuffle[Random.Shared.Next(shuffle.Count)]);

        void Fire(int picked)
        {
            shuffle.Remove(picked);
            lastShuffleCase = picked;

            FireCase(picked, data.Activator);
        }
    }

    /// <summary>Fires <c>OnUser1</c>, the pass-through the map wires for its own purposes.</summary>
    /// <param name="data">The input's parameter and sender, passed along.</param>
    [EntityInput("FireUser1")]
    private void InputFireUser1(EntityInputData data) => EntitySystem.TriggerOutput(this, "OnUser1", data.Activator);

    /// <summary>Fires <c>OnUser2</c>.</summary>
    /// <param name="data">The input's parameter and sender, passed along.</param>
    [EntityInput("FireUser2")]
    private void InputFireUser2(EntityInputData data) => EntitySystem.TriggerOutput(this, "OnUser2", data.Activator);

    /// <summary>Fires <c>OnUser3</c>.</summary>
    /// <param name="data">The input's parameter and sender, passed along.</param>
    [EntityInput("FireUser3")]
    private void InputFireUser3(EntityInputData data) => EntitySystem.TriggerOutput(this, "OnUser3", data.Activator);

    /// <summary>Fires <c>OnUser4</c>.</summary>
    /// <param name="data">The input's parameter and sender, passed along.</param>
    [EntityInput("FireUser4")]
    private void InputFireUser4(EntityInputData data) => EntitySystem.TriggerOutput(this, "OnUser4", data.Activator);

    private void FireCase(int index, BaseEntity? activator)
        => EntitySystem.TriggerOutput(this, $"OnCase{index + 1:00}", activator);

    /// <summary>
    /// Whether an authored case matches the incoming value: as text first, then as a number so that
    /// <c>1</c> and <c>1.0</c> are the same case.
    /// </summary>
    private static bool Matches(string authored, string? value)
    {
        if (string.Equals(authored, value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return float.TryParse(authored, NumberStyles.Float, CultureInfo.InvariantCulture, out var authoredNumber)
            && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var valueNumber)
            && authoredNumber == valueNumber;
    }
}
