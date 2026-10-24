using System.Globalization;
using ValveKeyValue;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>
/// How fast each weapon lets the player run, <c>m_flMaxSpeed</c> in <c>scripts/weapons.vdata</c>, by
/// the string token a weapon entity networks as its subclass.
/// </summary>
internal sealed class WeaponSpeedTable
{
    private readonly Dictionary<uint, float[]> SpeedsBySubclass = [];

    public static WeaponSpeedTable Load(GameFileLoader fileLoader)
    {
        var table = new WeaponSpeedTable();
        using var resource = fileLoader.LoadFileCompiled("scripts/weapons.vdata");

        if (resource?.DataBlock is not BinaryKV3 kv3)
        {
            return table;
        }

        var speedsByName = new Dictionary<string, float[]>();

        foreach (var (name, weapon) in kv3.Data.Root)
        {
            // One speed per firing mode, the second for when scoped
            if (weapon.ValueType == KVValueType.Collection && weapon.GetArray<float>("m_flMaxSpeed") is { Length: > 0 } speeds)
            {
                speedsByName[name] = speeds;
                table.SpeedsBySubclass[StringToken.Get(name)] = speeds;
            }
        }

        table.AddItemDefinitionIndices(fileLoader, speedsByName);

        return table;
    }

    /// <summary>Gets the max speed of the weapon held in a state, or null when there is none or it is unknown.</summary>
    public float? Get(NetworkedMovementState state) => Get(state.WeaponSubclass, state.WeaponMode);

    /// <summary>Gets the max speed of a weapon in a firing mode, or null when the weapon is unknown.</summary>
    public float? Get(uint subclass, int mode)
    {
        if (!SpeedsBySubclass.TryGetValue(subclass, out var speeds))
        {
            return null;
        }

        return speeds[Math.Clamp(mode, 0, speeds.Length - 1)];
    }

    /// <summary>
    /// Older builds keyed <c>weapons.vdata</c> by item definition index, so demos recorded on them
    /// network the token of the index as the subclass. <c>items_game.txt</c> maps each index to a name.
    /// </summary>
    private void AddItemDefinitionIndices(GameFileLoader fileLoader, Dictionary<string, float[]> speedsByName)
    {
        using var stream = fileLoader.GetFileStream("scripts/items/items_game.txt");

        if (stream == null)
        {
            return;
        }

        KVObject root;

        try
        {
            root = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
        }
        catch (KeyValueException)
        {
            return;
        }

        // The file repeats the items block, once per group of items
        foreach (var (section, items) in root)
        {
            if (section != "items" || items.ValueType != KVValueType.Collection)
            {
                continue;
            }

            foreach (var (index, item) in items)
            {
                if (item.ValueType == KVValueType.Collection && item.TryGetValue("name", out var name)
                    && speedsByName.TryGetValue(name.ToString(CultureInfo.InvariantCulture), out var speeds))
                {
                    SpeedsBySubclass.TryAdd(StringToken.Get(index), speeds);
                }
            }
        }
    }
}
