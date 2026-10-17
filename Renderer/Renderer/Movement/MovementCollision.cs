using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// The result of a movement trace.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/server/CGameTrace">CGameTrace</seealso>
internal struct GameTrace
{
    public Vector3 StartPos;

    /// <summary>Where the swept box stops, its origin at <see cref="Fraction"/> of the sweep.</summary>
    public Vector3 EndPos;

    /// <summary>The normal of what was hit, pointing back against the sweep.</summary>
    public Vector3 Normal;

    public float Fraction;
    public bool StartSolid;

    /// <summary>Whether something was hit, which is when a game trace reports an entity.</summary>
    public bool Hit;

    /// <summary>The entity that was hit, the world entity for the map itself. Null without an entity world.</summary>
    public BaseEntity? Entity;

    /// <summary>The hash of the hit surface's property, zero for the default surface.</summary>
    public uint SurfacePropertyHash;

    /// <summary>Whether the hit shape is a ladder.</summary>
    public bool HitLadder;

    public readonly bool DidHit => Fraction < 1f || StartSolid;
}

/// <summary>
/// The shape a trace hit: a mesh and its triangle, or a hull, with the transform placing it in the world.
/// Both shapes are null for a plane.
/// </summary>
internal readonly record struct TraceHitShape(Rubikon.PhysicsMeshData? Mesh, Rubikon.PhysicsHullData? Hull, int TriangleIndex, Matrix4x4 Transform);

/// <summary>
/// Sweeps the player's hull through the world and the solid entities, picking the hit the way the
/// game's physics query does.
/// </summary>
internal sealed class MovementCollision
{
    /// <summary>Gets or sets the static world to trace against.</summary>
    public PhysicsWorld? World { get; set; }

    /// <summary>Gets or sets the entities whose solid shapes are traced too, and that hits report.</summary>
    public EntitySystem? Entities { get; set; }

    /// <summary>
    /// Gets extra solid half-spaces, each with an outward unit normal in XYZ and the plane offset d in W:
    /// points with n.p &lt;= d are solid. Lets tests build simple worlds without physics data.
    /// </summary>
    public List<Vector4> Planes { get; } = [];

    /// <summary>Gets extra solid convex hulls in world space, for tests to build stairs and walls with.</summary>
    public List<Rubikon.PhysicsHullData> Hulls { get; } = [];

    private readonly List<Candidate> Candidates = [];
    private readonly List<int> HullCandidates = [];

    private record struct Candidate(ShapeCastHit Cast, BaseEntity? Entity, uint SurfacePropertyHash, bool IsLadder, TraceHitShape Shape);

    /// <summary>Gets the shape the last <see cref="TraceHull"/> hit, default when it hit nothing.</summary>
    public TraceHitShape LastHitShape { get; private set; }

    /// <summary>Sweeps a box with the given bounds around its origin.</summary>
    /// <param name="start">Origin at the start.</param>
    /// <param name="end">Origin at the end.</param>
    /// <param name="mins">Box minimum relative to the origin.</param>
    /// <param name="maxs">Box maximum relative to the origin.</param>
    /// <param name="collisionName">Which shapes to hit, by interaction name.</param>
    public GameTrace TraceHull(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, string collisionName)
    {
        var delta = new Vector3(end.X - start.X, end.Y - start.Y, end.Z - start.Z);

        var cx = (mins.X + maxs.X) * 0.5f;
        var cy = (mins.Y + maxs.Y) * 0.5f;
        var cz = (mins.Z + maxs.Z) * 0.5f;
        var halfExtents = new Vector3(maxs.X - cx, maxs.Y - cy, maxs.Z - cz);
        var center = new Vector3(cx + start.X, cy + start.Y, cz + start.Z);

        Candidates.Clear();
        LastHitShape = default;

        // The static world is one broadphase tree whose hits collapse into the nearest. A hit at zero
        // ends the whole query, before the entity tree is looked at.
        if (!CastStatic(center, delta, halfExtents, collisionName))
        {
            CastEntities(center, delta, halfExtents, collisionName);
        }

        if (Candidates.Count == 0)
        {
            return Miss(start, delta);
        }

        // Start-solid hits first, then the nearest. Entity filtering never rejects anything here, as
        // the only player is the one moving.
        var best = Candidates[0];

        for (var i = 1; i < Candidates.Count; i++)
        {
            var candidate = Candidates[i];

            if (candidate.Cast.StartSolid == best.Cast.StartSolid
                ? best.Cast.Fraction > candidate.Cast.Fraction
                : candidate.Cast.StartSolid)
            {
                best = candidate;
            }
        }

        // A zero-length trace only counts when it starts solid
        if (!best.Cast.StartSolid && delta == Vector3.Zero)
        {
            return Miss(start, delta);
        }

        var fraction = best.Cast.Fraction;
        LastHitShape = best.Shape;

        return new GameTrace
        {
            StartPos = start,
            EndPos = new Vector3((fraction * delta.X) + start.X, (delta.Y * fraction) + start.Y, (delta.Z * fraction) + start.Z),
            Normal = best.Cast.Normal,
            Fraction = fraction,
            StartSolid = best.Cast.StartSolid,
            Hit = true,
            Entity = best.Entity,
            SurfacePropertyHash = best.SurfacePropertyHash,
            HitLadder = best.IsLadder,
        };
    }

    private static GameTrace Miss(Vector3 start, Vector3 delta) => new()
    {
        StartPos = start,
        EndPos = new Vector3(start.X + delta.X, start.Y + delta.Y, start.Z + delta.Z),
        Fraction = 1f,
    };

    // Query state: the static tree collapses its hits into the nearest, the entity tree keeps them all
    private float MaxFraction;
    private int StaticBestIndex;
    private bool CollapseHits;
    private BaseEntity? CurrentEntity;

    /// <returns>Whether a hit at fraction zero ended the query.</returns>
    private bool CastStatic(Vector3 center, Vector3 delta, Vector3 halfExtents, string collisionName)
    {
        MaxFraction = 1f;
        StaticBestIndex = -1;
        CollapseHits = true;
        CurrentEntity = Entities?.World;

        if (World is { } world)
        {
            for (var i = 0; i < world.BodyCount; i++)
            {
                var (shape, transform, inverse) = world.GetBody(i);

                if (CastShape(shape, transform, inverse, center, delta, halfExtents, collisionName))
                {
                    return true;
                }
            }

            if (world.GroundPlane is { } height && !Rubikon.SkipsCollision(collisionName, [], [])
                && Consider(CastPlane(Vector3.UnitZ, height, center, delta, halfExtents), 0, [], default))
            {
                return true;
            }
        }

        foreach (var plane in Planes)
        {
            if (Consider(CastPlane(plane.AsVector3(), plane.W, center, delta, halfExtents), 0, [], default))
            {
                return true;
            }
        }

        foreach (var hull in Hulls)
        {
            if (RubikonShapeCast.CastBoxAgainstHull(hull, Matrix4x4.Identity, center, delta, halfExtents, MaxFraction, out var cast)
                && Consider(cast, hull.SurfacePropertyHash, hull.InteractAs, new TraceHitShape(null, hull, -1, Matrix4x4.Identity)))
            {
                return true;
            }
        }

        return false;
    }

    private void CastEntities(Vector3 center, Vector3 delta, Vector3 halfExtents, string collisionName)
    {
        if (Entities is not { } entitySystem)
        {
            return;
        }

        MaxFraction = 1f;
        CollapseHits = false;

        var end = center + delta;
        var reach = halfExtents + new Vector3(RubikonShapeCast.Skin);

        foreach (var entity in entitySystem.Entities)
        {
            if (!entity.IsCollidable || entity.Collider is not { } collider || !collider.MightHit(center, end, reach))
            {
                continue;
            }

            CurrentEntity = entity;

            if (CastShape(collider.Shape, collider.Transform, collider.InverseTransform, center, delta, halfExtents, collisionName))
            {
                return;
            }
        }
    }

    /// <returns>Whether the hit ended the query.</returns>
    private bool Consider(in ShapeCastHit cast, uint surfacePropertyHash, string[] interactAs, in TraceHitShape shape)
    {
        if (cast.Fraction >= 1f)
        {
            return false;
        }

        var candidate = new Candidate(cast, CurrentEntity, surfacePropertyHash, IsLadder(interactAs), shape);

        if (!CollapseHits || StaticBestIndex < 0)
        {
            if (CollapseHits)
            {
                StaticBestIndex = Candidates.Count;
            }

            Candidates.Add(candidate);
        }
        else if (Candidates[StaticBestIndex].Cast.Fraction > cast.Fraction)
        {
            Candidates[StaticBestIndex] = candidate;
        }

        if (cast.Fraction == 0f)
        {
            return true;
        }

        if (!(cast.Fraction <= 0f) && !(MaxFraction <= cast.Fraction))
        {
            MaxFraction = cast.Fraction;
        }

        return false;
    }

    /// <returns>Whether a hit ended the query.</returns>
    private bool CastShape(Rubikon shape, in Matrix4x4 transform, in Matrix4x4 inverse, Vector3 center, Vector3 delta,
        Vector3 halfExtents, string collisionName)
    {
        // Sweep bounds in the shape's own space, padded by the skin, to skip shapes it cannot reach
        var reach = halfExtents + new Vector3(RubikonShapeCast.Skin);
        var localStart = Vector3.Transform(center, inverse);
        var localEnd = Vector3.Transform(center + delta, inverse);
        var localReach = transform.IsIdentity ? reach : RotatedExtents(reach, inverse);
        var sweepMin = Vector3.Min(localStart, localEnd) - localReach;
        var sweepMax = Vector3.Max(localStart, localEnd) + localReach;

        foreach (var compound in shape.Compounds)
        {
            if (Rubikon.SkipsCollision(collisionName, compound.InteractAs, compound.InteractExclude))
            {
                continue;
            }

            foreach (var mesh in compound.Meshes)
            {
                if (CastMesh(mesh, transform, center, delta, halfExtents, sweepMin, sweepMax, compound.InteractAs))
                {
                    return true;
                }
            }

            foreach (var hull in compound.Hulls)
            {
                if (CastHull(hull, transform, center, delta, halfExtents, sweepMin, sweepMax, compound.InteractAs))
                {
                    return true;
                }
            }
        }

        foreach (var mesh in shape.Meshes)
        {
            if (!Rubikon.SkipsCollision(collisionName, mesh.InteractAs, mesh.InteractExclude)
                && CastMesh(mesh, transform, center, delta, halfExtents, sweepMin, sweepMax, mesh.InteractAs))
            {
                return true;
            }
        }

        HullCandidates.Clear();
        shape.CollectHulls(sweepMin, sweepMax, HullCandidates);

        // In order, so ties between hulls resolve the same way whatever the tree visited first
        HullCandidates.Sort();

        foreach (var index in HullCandidates)
        {
            var hull = shape.Hulls[index];

            if (!Rubikon.SkipsCollision(collisionName, hull.InteractAs, hull.InteractExclude)
                && CastHull(hull, transform, center, delta, halfExtents, sweepMin, sweepMax, hull.InteractAs))
            {
                return true;
            }
        }

        return false;
    }

    private bool CastMesh(Rubikon.PhysicsMeshData mesh, in Matrix4x4 transform, Vector3 center, Vector3 delta, Vector3 halfExtents,
        Vector3 sweepMin, Vector3 sweepMax, string[] interactAs)
    {
        if (mesh.PhysicsTree.Length == 0)
        {
            return false;
        }

        ref readonly var root = ref mesh.PhysicsTree[0];

        if (!Overlaps(sweepMin, sweepMax, root.Min, root.Max)
            || !RubikonShapeCast.CastBoxAgainstMesh(mesh, transform, center, delta, halfExtents, MaxFraction, out var cast))
        {
            return false;
        }

        var surface = cast.TriangleIndex >= 0 ? mesh.GetSurfacePropertyHash(cast.TriangleIndex) : mesh.SurfacePropertyHash;
        return Consider(cast, surface, interactAs, new TraceHitShape(mesh, null, cast.TriangleIndex, transform));
    }

    private bool CastHull(Rubikon.PhysicsHullData hull, in Matrix4x4 transform, Vector3 center, Vector3 delta, Vector3 halfExtents,
        Vector3 sweepMin, Vector3 sweepMax, string[] interactAs)
    {
        if (!Overlaps(sweepMin, sweepMax, hull.Min, hull.Max)
            || !RubikonShapeCast.CastBoxAgainstHull(hull, transform, center, delta, halfExtents, MaxFraction, out var cast))
        {
            return false;
        }

        return Consider(cast, hull.SurfacePropertyHash, interactAs, new TraceHitShape(null, hull, -1, transform));
    }

    private static bool Overlaps(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB)
        => minA.X <= maxB.X && maxA.X >= minB.X
            && minA.Y <= maxB.Y && maxA.Y >= minB.Y
            && minA.Z <= maxB.Z && maxA.Z >= minB.Z;

    // Half extents of a world-axis box seen in a rotated frame
    private static Vector3 RotatedExtents(Vector3 extents, in Matrix4x4 rotation) => new(
        (MathF.Abs(rotation.M11) * extents.X) + (MathF.Abs(rotation.M21) * extents.Y) + (MathF.Abs(rotation.M31) * extents.Z),
        (MathF.Abs(rotation.M12) * extents.X) + (MathF.Abs(rotation.M22) * extents.Y) + (MathF.Abs(rotation.M32) * extents.Z),
        (MathF.Abs(rotation.M13) * extents.X) + (MathF.Abs(rotation.M23) * extents.Y) + (MathF.Abs(rotation.M33) * extents.Z));

    private static bool IsLadder(string[] interactAs) => Array.IndexOf(interactAs, Rubikon.LadderCollisionName) >= 0;

    /// <summary>
    /// Sweeps a box against a solid half-space with the same skin and start-solid rules as the
    /// shape casts: exact contact is start solid, and a start inside the skin moving closer stops at once.
    /// </summary>
    private static ShapeCastHit CastPlane(Vector3 normal, float offset, Vector3 center, Vector3 delta, Vector3 halfExtents)
    {
        var extent = (MathF.Abs(normal.X) * halfExtents.X) + (MathF.Abs(normal.Y) * halfExtents.Y) + (MathF.Abs(normal.Z) * halfExtents.Z);
        var gapStart = Vector3.Dot(normal, center) - offset - extent;
        var gapEnd = gapStart + Vector3.Dot(normal, delta);

        if (gapStart <= 0f)
        {
            var length = delta.Length();
            return new ShapeCastHit
            {
                Fraction = 0f,
                Normal = length > 0f ? -delta / length : Vector3.UnitX,
                StartSolid = true,
                TriangleIndex = -1,
            };
        }

        if (gapEnd >= gapStart || gapEnd > RubikonShapeCast.Skin)
        {
            return ShapeCastHit.None;
        }

        var fraction = MathF.Max(0f, gapStart - RubikonShapeCast.Skin) / (gapStart - gapEnd);

        return fraction < 1f
            ? new ShapeCastHit { Fraction = fraction, Normal = normal, TriangleIndex = -1 }
            : ShapeCastHit.None;
    }

    /// <summary>
    /// Traces a line against water, for how submerged the player is.
    /// </summary>
    /// <returns>How far along the line the water surface is, 1 for no water, 0 when it starts in water.</returns>
    public float TraceWater(Vector3 top, Vector3 bottom)
    {
        var length = Vector3.Distance(top, bottom);

        if (length <= 0f)
        {
            return 1f;
        }

        var fraction = 1f;

        if (World?.TraceRay(top, bottom, Rubikon.WaterCollisionName) is { Hit: true } worldWater)
        {
            fraction = MathF.Min(fraction, worldWater.Distance / length);
        }

        if (Entities is { } entitySystem)
        {
            foreach (var entity in entitySystem.Entities)
            {
                if (entity is not FuncWater { IsRemoved: false, IsInQueryWorld: true, Collider: { IsEmpty: false } collider })
                {
                    continue;
                }

                if (collider.ContainsPoint(top))
                {
                    return 0f;
                }

                var hit = collider.TraceRay(top, bottom, Rubikon.WaterCollisionName);
                hit.MinimizeWith(collider.TraceRay(top, bottom, Rubikon.DefaultGeometry));

                if (hit.Hit)
                {
                    fraction = MathF.Min(fraction, hit.Distance / length);
                }
            }
        }

        return fraction;
    }
}
