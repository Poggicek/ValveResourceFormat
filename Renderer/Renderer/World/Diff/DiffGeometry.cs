using System.IO;
using System.Linq;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;
using KVObject = ValveKeyValue.KVObject;
using RnHull = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull;

namespace ValveResourceFormat.Renderer.World.Diff;

/// <summary>Stable 64 bit hashing for comparing builds, which must not change between runs like <see cref="HashCode"/> does.</summary>
internal static class DiffHash
{
    public static ulong Scramble(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public static ulong Combine(ulong seed, ulong value) => Scramble(seed ^ Scramble(value));

    public static ulong Combine(ulong seed, int value) => Combine(seed, (ulong)(uint)value);

    public static ulong String(string value)
    {
        var hash = 14695981039346656037UL;

        foreach (var c in value)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 1099511628211UL;
        }

        return hash;
    }
}

/// <summary>A point snapped to the grid positions are compared at.</summary>
internal readonly record struct GridPoint(int X, int Y, int Z) : IComparable<GridPoint>
{
    public static GridPoint From(Vector3 position, float inversePrecision)
        => new((int)MathF.Round(position.X * inversePrecision), (int)MathF.Round(position.Y * inversePrecision), (int)MathF.Round(position.Z * inversePrecision));

    public int CompareTo(GridPoint other)
    {
        var x = X.CompareTo(other.X);

        if (x != 0)
        {
            return x;
        }

        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    /// <summary>Hashes a triangle the same whichever of its corners it starts at.</summary>
    public static ulong Triangle(GridPoint a, GridPoint b, GridPoint c, ulong seed)
    {
        if (a.CompareTo(b) > 0)
        {
            (a, b) = (b, a);
        }

        if (b.CompareTo(c) > 0)
        {
            (b, c) = (c, b);
        }

        if (a.CompareTo(b) > 0)
        {
            (a, b) = (b, a);
        }

        var hash = seed;

        foreach (var point in (ReadOnlySpan<GridPoint>)[a, b, c])
        {
            hash = DiffHash.Combine(hash, point.X);
            hash = DiffHash.Combine(hash, point.Y);
            hash = DiffHash.Combine(hash, point.Z);
        }

        return hash;
    }
}

/// <summary>One draw call of a mesh, read for comparing.</summary>
internal sealed class DiffDraw
{
    public required string Material { get; init; }

    /// <summary>Gets the positions the indices point into, shared by the draws of a vertex buffer.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>Gets three indices per triangle.</summary>
    public required int[] Indices { get; init; }

    /// <summary>Gets a hash of the triangles in mesh space that ignores their order.</summary>
    public required ulong ContentHash { get; init; }

    /// <summary>
    /// Gets a hash of the triangles relative to the corner of their bounds, the same wherever they are. Geometry the
    /// compiler bakes into world space has no transform, so a prop that moved only matches itself through this.
    /// </summary>
    public required ulong ShapeHash { get; init; }

    public required AABB Bounds { get; init; }

    /// <summary>
    /// Gets the tint the mesh gives this draw, which the renderer multiplies with the tint of whatever places it.
    /// Some compilers bake a prop's tint in here and others into the placement.
    /// </summary>
    public Vector4 Tint { get; init; } = Vector4.One;

    public int TriangleCount => Indices.Length / 3;

    /// <summary>Hashes and bounds triangles given as three indices each into <paramref name="positions"/>.</summary>
    public static DiffDraw Create(string material, Vector3[] positions, int[] indices, float inversePrecision)
    {
        var contentHash = 0UL;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = positions[indices[i]];
            var b = positions[indices[i + 1]];
            var c = positions[indices[i + 2]];

            // Summed so the hash does not depend on the order the compiler wrote the triangles in
            contentHash += GridPoint.Triangle(GridPoint.From(a, inversePrecision), GridPoint.From(b, inversePrecision), GridPoint.From(c, inversePrecision), 0);

            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }

        var shapeHash = 0UL;

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            shapeHash += GridPoint.Triangle(
                GridPoint.From(positions[indices[i]] - min, inversePrecision),
                GridPoint.From(positions[indices[i + 1]] - min, inversePrecision),
                GridPoint.From(positions[indices[i + 2]] - min, inversePrecision),
                0);
        }

        var triangles = indices.Length / 3;

        return new DiffDraw
        {
            Material = material,
            Positions = positions,
            Indices = indices,
            ContentHash = DiffHash.Combine(contentHash, triangles),
            ShapeHash = DiffHash.Combine(shapeHash, triangles),
            Bounds = new AABB(min, max),
        };
    }
}

/// <summary>The triangles of the collision shapes of one collision attribute group.</summary>
/// <param name="Name">The group's name, as the viewer's physics group list shows it.</param>
/// <param name="Triangles">Three corners per triangle.</param>
internal readonly record struct DiffCollisionGroup(string Name, Vector3[] Triangles);

/// <summary>The finest level of detail of a model or mesh, read for comparing.</summary>
internal sealed class DiffModel
{
    public required IReadOnlyList<DiffDraw> Draws { get; init; }

    /// <summary>Gets the draws of the first mesh, which aggregate fragments index into.</summary>
    public required IReadOnlyList<DiffDraw> FirstMeshDraws { get; init; }

    public required IReadOnlyList<(string Name, string[] Materials)> MaterialGroups { get; init; }

    public ulong ContentHash { get; init; }

    public AABB Bounds { get; init; }

    /// <summary>Gets a hash of the collision shapes, 0 when it has none.</summary>
    public ulong CollisionHash { get; init; }

    public int CollisionTriangleCount { get; init; }

    /// <summary>Gets the bounds of the collision shapes, for brushes like triggers that draw nothing.</summary>
    public AABB? CollisionBounds { get; init; }

    /// <summary>Gets the materials a skin replaces, or <see langword="null"/> for the default skin.</summary>
    public Dictionary<string, string>? GetSkinMaterials(string? skin)
    {
        if (string.IsNullOrEmpty(skin) || MaterialGroups.Count < 2)
        {
            return null;
        }

        var defaultGroup = MaterialGroups[0].Materials;

        foreach (var (name, materials) in MaterialGroups)
        {
            if (!name.Equals(skin, StringComparison.OrdinalIgnoreCase) || materials == defaultGroup)
            {
                continue;
            }

            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < Math.Min(defaultGroup.Length, materials.Length); i++)
            {
                remap.TryAdd(defaultGroup[i], materials[i]);
            }

            return remap;
        }

        return null;
    }
}

/// <summary>Reads and caches the geometry of models and meshes one build of a map uses.</summary>
internal sealed class DiffGeometryCache(IFileLoader fileLoader, float precision)
{
    private readonly Dictionary<string, DiffModel?> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly float inversePrecision = 1f / precision;

    public IFileLoader FileLoader => fileLoader;

    public float InversePrecision => inversePrecision;

    /// <summary>Gets a model (<c>.vmdl</c>) or a mesh (<c>.vmesh</c>) by name.</summary>
    public DiffModel? Get(string name)
    {
        if (models.TryGetValue(name, out var cached))
        {
            return cached;
        }

        DiffModel? model = null;

        try
        {
            model = fileLoader.LoadFileCompiled(name)?.DataBlock switch
            {
                Model modelData => ReadModel(modelData),
                Mesh mesh => ReadMeshes([mesh], []),
                _ => null,
            };
        }
        catch (Exception e) when (e is InvalidDataException or NotImplementedException or UnexpectedMagicException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // A model that fails to read is compared as having no geometry, the same in both builds
        }

        models[name] = model;
        return model;
    }

    private DiffModel ReadModel(Model model)
    {
        var level = model.LodInfo.LowestLevel;
        var meshes = model.GetEmbeddedMeshesForLod(level).Select(static embedded => embedded.Mesh).ToList();

        foreach (var reference in model.GetReferenceMeshNamesForLod(level))
        {
            if (fileLoader.LoadFileCompiled(reference.MeshName)?.DataBlock is Mesh mesh)
            {
                meshes.Add(mesh);
            }
        }

        var read = ReadMeshes(meshes, [.. model.GetMaterialGroups()]);

        if (model.Resource.GetBlockByType(BlockType.PHYS) is not PhysAggregateData phys)
        {
            return read;
        }

        var groups = ReadCollision(phys);
        var triangles = groups.SelectMany(static group => group.Triangles).ToArray();

        if (triangles.Length == 0)
        {
            return read;
        }

        var collision = DiffDraw.Create(string.Empty, triangles, [.. Enumerable.Range(0, triangles.Length)], inversePrecision);

        return new DiffModel
        {
            Draws = read.Draws,
            FirstMeshDraws = read.FirstMeshDraws,
            MaterialGroups = read.MaterialGroups,
            ContentHash = read.ContentHash,
            Bounds = read.Bounds,
            CollisionHash = collision.ContentHash,
            CollisionTriangleCount = collision.TriangleCount,
            CollisionBounds = collision.Bounds,
        };
    }

    /// <summary>
    /// Triangulates the hulls and meshes of physics data, one group per collision attribute. Spheres and capsules
    /// are left out, maps build their collision from the other two.
    /// </summary>
    public static List<DiffCollisionGroup> ReadCollision(PhysAggregateData phys)
    {
        var groups = new List<DiffCollisionGroup>(phys.CollisionAttributes.Count);
        var bindPose = phys.BindPose;
        var parts = phys.Parts;

        for (var attributeIndex = 0; attributeIndex < phys.CollisionAttributes.Count; attributeIndex++)
        {
            var triangles = new List<Vector3>();

            for (var p = 0; p < parts.Length; p++)
            {
                var pose = bindPose.Length == 0 ? Matrix4x4.Identity : bindPose[p];
                var shape = parts[p].Shape;

                foreach (var hull in shape.Hulls)
                {
                    if (hull.CollisionAttributeIndex != attributeIndex)
                    {
                        continue;
                    }

                    var vertices = hull.Shape.GetVertexPositions();
                    var edges = hull.Shape.GetEdges();

                    foreach (var face in hull.Shape.GetFaces())
                    {
                        foreach (var (a, b, c) in RnHull.GetFaceTriangles(edges, face))
                        {
                            triangles.Add(Vector3.Transform(vertices[a], pose));
                            triangles.Add(Vector3.Transform(vertices[b], pose));
                            triangles.Add(Vector3.Transform(vertices[c], pose));
                        }
                    }
                }

                foreach (var mesh in shape.Meshes)
                {
                    if (mesh.CollisionAttributeIndex != attributeIndex)
                    {
                        continue;
                    }

                    var vertices = mesh.Shape.GetVertices();

                    foreach (var triangle in mesh.Shape.GetTriangles())
                    {
                        triangles.Add(Vector3.Transform(vertices[triangle.X], pose));
                        triangles.Add(Vector3.Transform(vertices[triangle.Y], pose));
                        triangles.Add(Vector3.Transform(vertices[triangle.Z], pose));
                    }
                }
            }

            groups.Add(new DiffCollisionGroup(PhysSceneNode.GetGroupName(phys.CollisionAttributes[attributeIndex]), [.. triangles]));
        }

        return groups;
    }

    private DiffModel ReadMeshes(List<Mesh> meshes, IReadOnlyList<(string Name, string[] Materials)> materialGroups)
    {
        var draws = new List<DiffDraw>();
        IReadOnlyList<DiffDraw> firstMeshDraws = [];

        for (var i = 0; i < meshes.Count; i++)
        {
            var meshDraws = ReadDraws(meshes[i]);

            if (i == 0)
            {
                firstMeshDraws = meshDraws;
            }

            draws.AddRange(meshDraws);
        }

        var contentHash = 0UL;
        var bounds = default(AABB);

        for (var i = 0; i < draws.Count; i++)
        {
            contentHash = DiffHash.Combine(DiffHash.Combine(contentHash, draws[i].ContentHash), DiffHash.String(draws[i].Material));
            bounds = i == 0 ? draws[i].Bounds : bounds.Union(draws[i].Bounds);
        }

        return new DiffModel
        {
            Draws = draws,
            FirstMeshDraws = firstMeshDraws,
            MaterialGroups = materialGroups,
            ContentHash = contentHash,
            Bounds = bounds,
        };
    }

    private List<DiffDraw> ReadDraws(Mesh mesh)
    {
        var draws = new List<DiffDraw>();
        var vbib = mesh.VBIB;
        var positionsByBuffer = new Dictionary<int, Vector3[]>();

        foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
        {
            foreach (var drawCall in sceneObject.GetArray("m_drawCalls"))
            {
                var material = Mesh.GetMaterialName(drawCall);

                // Skipped the same way the renderer skips them, which aggregate fragments index around
                if (material == null && Mesh.IsOccluder(drawCall))
                {
                    continue;
                }

                var draw = ReadDraw(drawCall, material ?? string.Empty, vbib, positionsByBuffer);

                if (draw != null)
                {
                    draws.Add(draw);
                }
            }
        }

        return draws;
    }

    private DiffDraw? ReadDraw(KVObject drawCall, string material, VBIB vbib, Dictionary<int, Vector3[]> positionsByBuffer)
    {
        if (drawCall.GetEnumValue<RenderPrimitiveType>("m_nPrimitiveType") != RenderPrimitiveType.RENDER_PRIM_TRIANGLES)
        {
            return null;
        }

        Vector3[]? positions = null;

        foreach (var vertexBufferObject in drawCall.GetArray("m_vertexBuffers"))
        {
            var bufferIndex = vertexBufferObject.GetInt32Property("m_hBuffer");

            if (positionsByBuffer.TryGetValue(bufferIndex, out positions))
            {
                break;
            }

            var vertexBuffer = vbib.VertexBuffers[bufferIndex];
            var positionField = Array.FindIndex(vertexBuffer.InputLayoutFields, static field => field.SemanticName == "POSITION");

            if (positionField < 0)
            {
                continue;
            }

            positions = VBIB.GetVector3AttributeArray(vertexBuffer, vertexBuffer.InputLayoutFields[positionField]);
            positionsByBuffer[bufferIndex] = positions;
            break;
        }

        if (positions == null)
        {
            return null;
        }

        var indexBufferObject = drawCall.GetSubCollection("m_indexBuffer");
        var indexBuffer = vbib.IndexBuffers[indexBufferObject.GetInt32Property("m_hBuffer")];
        var startIndex = drawCall.GetInt32Property("m_nStartIndex");
        var indexCount = drawCall.GetInt32Property("m_nIndexCount") / 3 * 3;
        var baseVertex = drawCall.GetInt32Property("m_nBaseVertex");
        var indexSize = (int)indexBuffer.ElementSizeInBytes;
        var indexData = indexBuffer.Data.AsSpan();

        var indices = new int[indexCount];
        var used = 0;

        for (var i = 0; i < indexCount; i += 3)
        {
            var offset = (startIndex + i) * indexSize;

            if (offset + indexSize * 3 > indexData.Length)
            {
                break;
            }

            var a = ReadIndex(indexData, offset, indexSize) + baseVertex;
            var b = ReadIndex(indexData, offset + indexSize, indexSize) + baseVertex;
            var c = ReadIndex(indexData, offset + indexSize * 2, indexSize) + baseVertex;

            if ((uint)a >= positions.Length || (uint)b >= positions.Length || (uint)c >= positions.Length)
            {
                continue;
            }

            indices[used++] = a;
            indices[used++] = b;
            indices[used++] = c;
        }

        if (used == 0)
        {
            return null;
        }

        if (used < indices.Length)
        {
            Array.Resize(ref indices, used);
        }

        var draw = DiffDraw.Create(material, positions, indices, inversePrecision);
        var tint = Vector4.One;

        // Read the way the renderer reads it for the draw call
        if (drawCall.ContainsKey("m_vTintColor"))
        {
            tint = new Vector4(ColorSpace.SrgbLinearToGamma(drawCall.GetSubCollection("m_vTintColor").ToVector3()), 1f);
        }

        if (drawCall.ContainsKey("m_flAlpha"))
        {
            tint.W = drawCall.GetFloatProperty("m_flAlpha");
        }

        return tint == Vector4.One ? draw : new DiffDraw
        {
            Material = draw.Material,
            Positions = draw.Positions,
            Indices = draw.Indices,
            ContentHash = draw.ContentHash,
            ShapeHash = draw.ShapeHash,
            Bounds = draw.Bounds,
            Tint = tint,
        };
    }

    private static int ReadIndex(ReadOnlySpan<byte> data, int offset, int size)
        => size == 2 ? BitConverter.ToUInt16(data[offset..]) : (int)BitConverter.ToUInt32(data[offset..]);
}
