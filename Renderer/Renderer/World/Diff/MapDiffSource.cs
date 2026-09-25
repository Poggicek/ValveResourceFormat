using System.Linq;
using System.Threading;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;
using KVObject = ValveKeyValue.KVObject;
using WorldResource = ValveResourceFormat.ResourceTypes.World;

namespace ValveResourceFormat.Renderer.World.Diff;

/// <summary>An entity with where it ends up once its template is applied.</summary>
internal sealed class DiffEntity
{
    public required EntityLump.Entity Entity { get; init; }
    public required Matrix4x4 Transform { get; init; }
    public required string Classname { get; init; }
    public required string TargetName { get; init; }
    public required string Model { get; init; }

    /// <summary>Gets <see cref="Model"/> with the build's own map folder taken out, to compare across builds.</summary>
    public required string ModelKey { get; init; }

    public string? UniqueId { get; init; }

    public Vector3 Origin => Transform.Translation;
}

/// <summary>A placed piece of static geometry: a world node scene object, or one fragment of an aggregate.</summary>
internal sealed class DiffObject
{
    /// <summary>Gets the model or mesh it draws, or the name of the collision group it is.</summary>
    public required string Model { get; init; }

    /// <summary>Gets whether it is collision rather than something drawn.</summary>
    public bool IsCollision { get; init; }

    public required Matrix4x4 Transform { get; init; }

    public List<DiffPlacedDraw> Draws { get; } = [];

    /// <summary>Gets a hash of what it looks like, wherever it is placed and however it is turned.</summary>
    public ulong AppearanceHash { get; set; }

    public AABB Bounds { get; set; }
}

/// <summary>One draw of a <see cref="DiffObject"/>, with what makes it different from any other.</summary>
internal sealed class DiffPlacedDraw
{
    public required DiffObject Owner { get; init; }
    public required DiffDraw Draw { get; init; }
    public required string Material { get; init; }

    /// <summary>Gets the tint it ends up drawn with, its placement's and its mesh's together.</summary>
    public required Vector4 Tint { get; init; }

    /// <summary>Gets the material and tint, which the triangles of the draw are hashed with.</summary>
    public required ulong AppearanceSeed { get; init; }

    /// <summary>Gets a hash of the draw's content, appearance and placement.</summary>
    public required ulong Fingerprint { get; init; }
}

/// <summary>
/// What one build of a map is made of, read without drawing anything: its entities, and every draw of its
/// static geometry placed in the world.
/// </summary>
public sealed class MapDiffSource
{
    internal List<DiffEntity> Entities { get; } = [];
    internal List<DiffObject> Objects { get; } = [];
    internal DiffGeometryCache Geometry { get; }
    internal float Precision { get; }

    /// <summary>Gets the folder the build's compiled files are in, like <c>maps/de_dust2</c>.</summary>
    internal string MapFolder { get; private set; } = string.Empty;

    /// <summary>Gets how many entities were read.</summary>
    public int EntityCount => Entities.Count;

    /// <summary>Gets how many placed draws of static geometry were read.</summary>
    public int DrawCount { get; private set; }

    private MapDiffSource(IFileLoader fileLoader, float precision)
    {
        Precision = precision;
        Geometry = new DiffGeometryCache(fileLoader, precision);
    }

    /// <summary>Reads a build of a map.</summary>
    /// <param name="fileLoader">Loads the build's files.</param>
    /// <param name="world">The build's world.</param>
    /// <param name="options">Tolerances, the same ones <see cref="MapDiffer.Compute"/> is given.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    public static MapDiffSource Load(IFileLoader fileLoader, WorldResource world, MapDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileLoader);
        ArgumentNullException.ThrowIfNull(world);

        options ??= new MapDiffOptions();

        var source = new MapDiffSource(fileLoader, options.VertexPrecision);

        if (world.Resource.FileName?.Replace('\\', '/') is { } worldFile)
        {
            source.MapFolder = worldFile[..Math.Max(0, worldFile.LastIndexOf('/'))];
        }

        foreach (var lumpName in world.GetEntityLumpNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (lumpName != null && fileLoader.LoadFileCompiled(lumpName)?.DataBlock is EntityLump lump)
            {
                source.ReadEntities(lump);
            }
        }

        foreach (var nodeName in world.GetWorldNodeNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (nodeName != null && fileLoader.LoadFile(string.Concat(nodeName, ".vwnod_c"))?.DataBlock is WorldNode node)
            {
                source.ReadWorldNode(node, cancellationToken);
            }
        }

        source.ReadWorldCollision();

        return source;
    }

    private void ReadEntities(EntityLump lump)
    {
        foreach (var traversed in EntityLumpTraversal.EnumerateEntities(lump, Geometry.FileLoader, Matrix4x4.Identity))
        {
            var entity = traversed.Entity;
            var classname = entity.GetStringProperty("classname");

            if (classname == null)
            {
                continue;
            }

            var model = entity.GetStringProperty("model") ?? string.Empty;

            Entities.Add(new DiffEntity
            {
                Entity = entity,
                Transform = EntityTransformHelper.ToTransformationMatrix(entity) * traversed.ParentTransform,
                Classname = classname,
                TargetName = entity.FriendlyTargetName ?? string.Empty,
                Model = model,
                ModelKey = Normalize(model),
                UniqueId = entity.GetStringProperty("hammeruniqueid"),
            });
        }
    }

    /// <summary>
    /// Replaces the build's map folder in a value with a placeholder. Two variants of a map, or a map that was
    /// renamed, compile the same things into differently named folders.
    /// </summary>
    internal string Normalize(string value)
        => MapFolder.Length > 0 ? value.Replace(string.Concat(MapFolder, "/"), "maps/*/", StringComparison.OrdinalIgnoreCase) : value;

    /// <summary>Reads the world's collision, one placed object per collision group, so clips are compared like geometry.</summary>
    private void ReadWorldCollision()
    {
        if (MapFolder.Length == 0)
        {
            return;
        }

        var fileLoader = Geometry.FileLoader;

        // Where the world loader finds it too: its own file, or a physics block of a model
        var phys = fileLoader.LoadFile($"{MapFolder}/world_physics.vphys_c")?.DataBlock as PhysAggregateData
            ?? fileLoader.LoadFile($"{MapFolder}/world_physics.vmdl_c")?.GetBlockByType(BlockType.PHYS) as PhysAggregateData;

        if (phys == null)
        {
            return;
        }

        foreach (var (name, triangles) in DiffGeometryCache.ReadCollision(phys))
        {
            if (triangles.Length == 0)
            {
                continue;
            }

            var draw = DiffDraw.Create(name, triangles, [.. Enumerable.Range(0, triangles.Length)], Geometry.InversePrecision);

            AddObject(name, Matrix4x4.Identity, Vector4.One, [draw], null, isCollision: true);
        }
    }

    private void ReadWorldNode(WorldNode node, CancellationToken cancellationToken)
    {
        foreach (var sceneObject in node.SceneObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = sceneObject.GetStringProperty("m_renderableModel") ?? sceneObject.GetStringProperty("m_renderable");

            if (string.IsNullOrEmpty(name) || Geometry.Get(name) is not { } model)
            {
                continue;
            }

            // Scene objects store their tint from 0 to 1, and one with no alpha is drawn untinted
            var tint = sceneObject.GetSubCollection("m_vTintColor").ToVector4();

            if (tint.W == 0)
            {
                tint = Vector4.One;
            }

            var transform = sceneObject.GetArray("m_vTransform").ToMatrix4x4();
            var skinMaterials = model.GetSkinMaterials(sceneObject.GetStringProperty("m_skin"));

            AddObject(name, transform, tint, model.Draws, skinMaterials);
        }

        foreach (var aggregate in node.AggregateSceneObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = aggregate.GetStringProperty("m_renderableModel");

            if (string.IsNullOrEmpty(name) || Geometry.Get(name) is not { } model)
            {
                continue;
            }

            ReadAggregateFragments(name, model, aggregate);
        }
    }

    private void ReadAggregateFragments(string name, DiffModel model, KVObject aggregate)
    {
        var fragments = aggregate.GetArray("m_aggregateMeshes");
        var draws = model.FirstMeshDraws;

        // Older games list one fragment per draw call instead of pointing each fragment at one
        if (fragments.Count > 0 && !fragments[0].ContainsKey("m_nDrawCallIndex"))
        {
            foreach (var draw in draws)
            {
                AddObject(name, Matrix4x4.Identity, Vector4.One, [draw], null);
            }

            return;
        }

        var transforms = aggregate.GetArray("m_fragmentTransforms");
        var transformIndex = 0;

        foreach (var fragment in fragments)
        {
            var transform = fragment.GetBooleanProperty("m_bHasTransform") && transformIndex < transforms.Count
                ? transforms[transformIndex++].ToMatrix4x4()
                : Matrix4x4.Identity;

            // Coarser levels of detail of the same prop are fragments of their own, only the finest is compared
            var lodGroupMask = fragment.GetUInt32Property("m_nLODGroupMask");

            if (lodGroupMask != 0 && (lodGroupMask & 1) == 0)
            {
                continue;
            }

            var drawIndex = fragment.GetInt32Property("m_nDrawCallIndex");

            if ((uint)drawIndex >= draws.Count)
            {
                continue;
            }

            // Aggregate fragments store theirs from 0 to 255, and a prop can move between the two from one compile to the next
            var tint = new Vector4(fragment.GetSubCollection("m_vTintColor").ToVector3() / 255f, 1f);

            AddObject(name, transform, tint, [draws[drawIndex]], null);
        }
    }

    private void AddObject(string name, Matrix4x4 transform, Vector4 tint, IReadOnlyList<DiffDraw> draws, Dictionary<string, string>? skinMaterials, bool isCollision = false)
    {
        if (draws.Count == 0)
        {
            return;
        }

        var placed = new DiffObject
        {
            Model = name,
            Transform = transform,
            IsCollision = isCollision,
        };

        var kindHash = DiffHash.Combine(isCollision ? 1UL : 0UL, 0UL);
        var placementHash = HashPlacement(transform);
        var appearanceHash = kindHash;
        var bounds = default(AABB);

        for (var i = 0; i < draws.Count; i++)
        {
            var draw = draws[i];
            var material = skinMaterials != null && skinMaterials.TryGetValue(draw.Material, out var replacement) ? replacement : draw.Material;
            var drawTint = tint * draw.Tint;

            // Collision and drawn geometry sharing a name must never cancel each other out
            var appearanceSeed = DiffHash.Combine(DiffHash.String(material), kindHash);

            foreach (var channel in (ReadOnlySpan<float>)[drawTint.X, drawTint.Y, drawTint.Z, drawTint.W])
            {
                appearanceSeed = DiffHash.Combine(appearanceSeed, (int)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f));
            }

            placed.Draws.Add(new DiffPlacedDraw
            {
                Owner = placed,
                Draw = draw,
                Material = material,
                Tint = drawTint,
                AppearanceSeed = appearanceSeed,
                Fingerprint = DiffHash.Combine(DiffHash.Combine(draw.ContentHash, appearanceSeed), placementHash),
            });

            appearanceHash = DiffHash.Combine(DiffHash.Combine(appearanceHash, draw.ShapeHash), appearanceSeed);

            var drawBounds = draw.Bounds.Transform(transform);
            bounds = i == 0 ? drawBounds : bounds.Union(drawBounds);
        }

        placed.AppearanceHash = appearanceHash;
        placed.Bounds = bounds;

        Objects.Add(placed);
        DrawCount += placed.Draws.Count;
    }

    private ulong HashPlacement(Matrix4x4 transform)
    {
        var inversePrecision = Geometry.InversePrecision;
        var hash = 0UL;

        // Rotation and scale compared finely, translation on the grid the vertices are compared on
        foreach (var value in (ReadOnlySpan<float>)[transform.M11, transform.M12, transform.M13, transform.M21, transform.M22, transform.M23, transform.M31, transform.M32, transform.M33])
        {
            hash = DiffHash.Combine(hash, (int)MathF.Round(value * 4096f));
        }

        var translation = GridPoint.From(transform.Translation, inversePrecision);

        hash = DiffHash.Combine(hash, translation.X);
        hash = DiffHash.Combine(hash, translation.Y);
        return DiffHash.Combine(hash, translation.Z);
    }
}
