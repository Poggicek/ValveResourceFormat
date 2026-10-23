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

        foreach (var (name, weapon) in kv3.Data.Root)
        {
            // One speed per firing mode, the second for when scoped
            if (weapon.ValueType == KVValueType.Collection && weapon.GetArray<float>("m_flMaxSpeed") is { Length: > 0 } speeds)
            {
                table.SpeedsBySubclass[StringToken.Get(name)] = speeds;
            }
        }

        return table;
    }

    /// <summary>Gets the max speed of the weapon held in a state, or null when there is none or it is unknown.</summary>
    public float? Get(NetworkedMovementState state)
    {
        if (!SpeedsBySubclass.TryGetValue(state.WeaponSubclass, out var speeds))
        {
            return null;
        }

        return speeds[Math.Clamp(state.WeaponMode, 0, speeds.Length - 1)];
    }
}
