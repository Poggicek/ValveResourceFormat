using System.IO;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>What movement reads from a surface property.</summary>
/// <param name="Friction">The physics friction; the player's surface friction is 1.25 times this, at most 1.</param>
/// <param name="MaxSpeedFactor">The scale on the max speed while standing on it.</param>
/// <param name="JumpFactor">The scale on the jump impulse when jumping off it.</param>
/// <param name="IsLadder">Whether it can be climbed.</param>
/// <param name="FootstepSound">The footstep sound event, or null for none.</param>
internal sealed record SurfaceProperty(float Friction, float MaxSpeedFactor, float JumpFactor, bool IsLadder, string? FootstepSound);

/// <summary>
/// The surface properties of the game, by name hash, with each one's unset values taken from its base.
/// </summary>
internal sealed class SurfacePropertyTable
{
    private const string DefaultSurface = "default";
    private const string LadderSurface = "ladder";

    /// <summary>Gets a table with only the default surface, for when no game files are loaded.</summary>
    public static SurfacePropertyTable Empty { get; } = new();

    private readonly Dictionary<uint, SurfaceProperty> resolved = [];
    private readonly Dictionary<string, KVObject> physics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KVObject> game = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KVObject> footsteps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> namesByHash = [];

    private SurfacePropertyTable()
    {
    }

    /// <summary>Gets the default surface.</summary>
    public SurfaceProperty Default => Get(0);

    /// <summary>Gets the ladder surface, which climbing steps sound like.</summary>
    public SurfaceProperty Ladder => Resolve(LadderSurface);

    /// <summary>Loads the surface properties the game ships.</summary>
    public static SurfacePropertyTable Load(GameFileLoader fileLoader)
    {
        var table = new SurfacePropertyTable();

        using (var resource = fileLoader.LoadFileCompiled("surfaceproperties/surfaceproperties.vsurf"))
        {
            if (resource?.DataBlock is BinaryKV3 kv3)
            {
                foreach (var surface in kv3.Data.Root.GetArray("SurfacePropertiesList") ?? [])
                {
                    if (surface.GetStringProperty("surfacePropertyName") is not { } name)
                    {
                        continue;
                    }

                    table.namesByHash[StringToken.Store(name)] = name;
                    table.physics[name] = surface;

                    if (surface.GetStringProperty("base") is { } baseName)
                    {
                        table.bases[name] = baseName;
                    }
                }
            }
        }

        foreach (var surface in ReadList(fileLoader, "scripts/surfaceproperties_game.txt", null))
        {
            if (surface.GetStringProperty("surfacePropertyName") is { } name)
            {
                table.game[name] = surface;
            }
        }

        // The player's footsteps; the viewer plays the counter-terrorist ones
        foreach (var surface in ReadList(fileLoader, "scripts/surfaceproperties_footsteps.txt", "ct_player"))
        {
            if (surface.GetStringProperty("surfacePropertyName") is { } name)
            {
                table.footsteps[name] = surface;
            }
        }

        return table;
    }

    private static IEnumerable<KVObject> ReadList(GameFileLoader fileLoader, string path, string? group)
    {
        using var stream = fileLoader.GetFileStream(path);

        if (stream == null)
        {
            return [];
        }

        var root = KVDocumentExtensions.ParseKV3(stream).Root;

        if (group != null)
        {
            root = root.GetSubCollection(group);
        }

        return root?.GetArray("SurfacePropertiesList") ?? [];
    }

    /// <summary>Gets a surface by the hash of its name; unknown hashes are the default surface.</summary>
    public SurfaceProperty Get(uint nameHash)
    {
        if (resolved.TryGetValue(nameHash, out var surface))
        {
            return surface;
        }

        surface = Resolve(namesByHash.GetValueOrDefault(nameHash, DefaultSurface));
        resolved[nameHash] = surface;
        return surface;
    }

    private SurfaceProperty Resolve(string name) => new(
        Find(name, physics, s => s.GetSubCollection("physics")?.ContainsKey("friction") == true)?.GetSubCollection("physics")!.GetFloatProperty("friction") ?? 0.8f,
        Find(name, game, s => s.ContainsKey("maxspeedfactor"))?.GetFloatProperty("maxspeedfactor") ?? 1f,
        Find(name, game, s => s.ContainsKey("jumpfactor"))?.GetFloatProperty("jumpfactor") ?? 1f,
        Find(name, game, s => s.ContainsKey("climbable"))?.GetBooleanProperty("climbable") ?? false,
        Find(name, footsteps, s => s.GetStringProperty("walkleft") is { Length: > 0 })?.GetStringProperty("walkleft"));

    // A value comes from the surface itself, else its base chain, else the default surface
    private KVObject? Find(string name, Dictionary<string, KVObject> source, Func<KVObject, bool> hasValue)
    {
        for (var current = name; ;)
        {
            if (source.TryGetValue(current, out var surface) && hasValue(surface))
            {
                return surface;
            }

            if (!bases.TryGetValue(current, out var baseName) || baseName == current)
            {
                break;
            }

            current = baseName;
        }

        return source.TryGetValue(DefaultSurface, out var fallback) && hasValue(fallback) ? fallback : null;
    }
}
