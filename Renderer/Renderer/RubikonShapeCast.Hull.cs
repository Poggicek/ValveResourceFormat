using System.Runtime.CompilerServices;

namespace ValveResourceFormat.Renderer;

// Convex shapes (hulls, spheres, capsules) run GJK on the swept configuration space object
// C = (box + sweep segment) - shape. When the origin is inside C, the exit distance of the ray
// along the sweep direction is found by portal refinement, then pushed out to C inflated by
// radius + skin. The time of impact is the sweep length minus that exit distance.
internal static partial class RubikonShapeCast
{
    private const float GjkFltMinNormal = 1.1754944e-38f;
    private const float GjkNormalizeOrZeroEpsilon = 1.1754944e-35f;
    private const float GjkDuplicateEpsilonSqr = 1.1920929e-7f;
    private const float GjkEpsilonSqr = 1e-8f;
    private const float GjkNormalizeMin = 9.9999998e-18f;
    private const float GjkNormalizeMax = 9.9999998e16f;
    private const float GjkLongSegment = 512f;
    private const float GjkPortalTolerance = 1f / 256f;
    private const float GjkPortalNoHit = 1e24f;
    private const float GjkRayMiss = float.MaxValue;
    private const int GjkMaxIterations = 100;
    private const int GjkPortalMaxIterations = 100;
    private const int GjkMarginMaxIterations = 20;
    private const int GjkFeatureMaxIterations = 32;

    /// <summary>Sweeps a box against a convex hull placed by a rigid transform.</summary>
    /// <param name="hull">The hull, of which only <see cref="Rubikon.PhysicsHullData.VertexPositions"/> is read.</param>
    /// <param name="transform">Rigid transform from hull space to world space.</param>
    /// <param name="start">Box center at the start of the sweep.</param>
    /// <param name="delta">Sweep from start to end.</param>
    /// <param name="halfExtents">Box half extents.</param>
    /// <param name="maxFraction">Current nearest hit, which hull casts do not clip the sweep to.</param>
    /// <param name="hit">The hit, or <see cref="ShapeCastHit.None"/>.</param>
    /// <returns>Whether the box hits the hull.</returns>
    public static bool CastBoxAgainstHull(Rubikon.PhysicsHullData hull, in Matrix4x4 transform, Vector3 start, Vector3 delta, Vector3 halfExtents, float maxFraction, out ShapeCastHit hit)
    {
        return CastBoxAgainstHull(hull.VertexPositions, hull.Centroid, transform, start, delta, halfExtents, maxFraction, out hit);
    }

    /// <summary>Sweeps a box against a convex hull placed by a rigid transform.</summary>
    /// <param name="vertices">Hull vertices in hull space.</param>
    /// <param name="centroid">Hull centroid in hull space, the reference point the cast is computed relative to.</param>
    /// <param name="transform">Rigid transform from hull space to world space.</param>
    /// <param name="start">Box center at the start of the sweep.</param>
    /// <param name="delta">Sweep from start to end.</param>
    /// <param name="halfExtents">Box half extents.</param>
    /// <param name="maxFraction">Current nearest hit, which hull casts do not clip the sweep to.</param>
    /// <param name="hit">The hit, or <see cref="ShapeCastHit.None"/>.</param>
    /// <returns>Whether the box hits the hull.</returns>
    public static bool CastBoxAgainstHull(ReadOnlySpan<Vector3> vertices, Vector3 centroid, in Matrix4x4 transform, Vector3 start, Vector3 delta, Vector3 halfExtents, float maxFraction, out ShapeCastHit hit)
    {
        hit = ShapeCastHit.None;

        if (vertices.Length <= 0)
        {
            return false;
        }

        // The rotation rows and translation of the matrix are used directly. They are the same 3x3 rotation and
        // origin the cast works with, so no quaternion round trip changes the products below.
        var center = GjkTransformPoint(transform, centroid);
        var offsetX = transform.M41 - center.X;
        var offsetY = transform.M42 - center.Y;
        var offsetZ = transform.M43 - center.Z;

        Span<Vector3> points = vertices.Length <= 256 ? stackalloc Vector3[vertices.Length] : new Vector3[vertices.Length];

        for (var i = 0; i < vertices.Length; i++)
        {
            var p = vertices[i];
            points[i] = new Vector3(
                transform.M21 * p.Y + transform.M11 * p.X + transform.M31 * p.Z + offsetX,
                transform.M22 * p.Y + transform.M12 * p.X + transform.M32 * p.Z + offsetY,
                transform.M23 * p.Y + transform.M13 * p.X + transform.M33 * p.Z + offsetZ);
        }

        var castStart = new Vector3(start.X - center.X, start.Y - center.Y, start.Z - center.Z);
        var segment = GjkCastSegmentInit(castStart, delta);
        var cast = new GjkCast(GjkShapes.ForCloud(halfExtents, points), segment, castStart, 0f);

        return GjkCastExecute(ref cast, ref hit);
    }

    /// <summary>Sweeps a box against a sphere placed by a rigid transform.</summary>
    /// <param name="center">Sphere center in shape space.</param>
    /// <param name="radius">Sphere radius.</param>
    /// <param name="transform">Rigid transform from shape space to world space.</param>
    /// <param name="start">Box center at the start of the sweep.</param>
    /// <param name="delta">Sweep from start to end.</param>
    /// <param name="halfExtents">Box half extents.</param>
    /// <param name="maxFraction">Current nearest hit; the sweep is clipped a little beyond it.</param>
    /// <param name="hit">The hit, or <see cref="ShapeCastHit.None"/>.</param>
    /// <returns>Whether the box hits the sphere.</returns>
    public static bool CastBoxAgainstSphere(Vector3 center, float radius, in Matrix4x4 transform, Vector3 start, Vector3 delta, Vector3 halfExtents, float maxFraction, out ShapeCastHit hit)
    {
        hit = ShapeCastHit.None;

        var segment = GjkCastSegmentInit(start, delta);
        GjkCastSegmentClip(ref segment, maxFraction);

        var point = GjkTransformPoint(transform, center);
        var cast = new GjkCast(GjkShapes.ForPoint(halfExtents, point), segment, start, radius);

        return GjkCastExecute(ref cast, ref hit);
    }

    /// <summary>Sweeps a box against a capsule placed by a rigid transform.</summary>
    /// <param name="a">First end of the capsule axis in shape space.</param>
    /// <param name="b">Second end of the capsule axis in shape space.</param>
    /// <param name="radius">Capsule radius.</param>
    /// <param name="transform">Rigid transform from shape space to world space.</param>
    /// <param name="start">Box center at the start of the sweep.</param>
    /// <param name="delta">Sweep from start to end.</param>
    /// <param name="halfExtents">Box half extents.</param>
    /// <param name="maxFraction">Current nearest hit; the sweep is clipped a little beyond it.</param>
    /// <param name="hit">The hit, or <see cref="ShapeCastHit.None"/>.</param>
    /// <returns>Whether the box hits the capsule.</returns>
    public static bool CastBoxAgainstCapsule(Vector3 a, Vector3 b, float radius, in Matrix4x4 transform, Vector3 start, Vector3 delta, Vector3 halfExtents, float maxFraction, out ShapeCastHit hit)
    {
        hit = ShapeCastHit.None;

        var segment = GjkCastSegmentInit(start, delta);
        GjkCastSegmentClip(ref segment, maxFraction);

        var pointB = GjkTransformPoint(transform, b);
        var pointA = GjkTransformPoint(transform, a);
        var cast = new GjkCast(GjkShapes.ForCapsule(halfExtents, pointA, pointB), segment, start, radius);

        return GjkCastExecute(ref cast, ref hit);
    }

    private static Vector3 GjkTransformPoint(in Matrix4x4 transform, Vector3 v)
    {
        return new Vector3(
            v.X * transform.M11 + v.Y * transform.M21 + v.Z * transform.M31 + transform.M41,
            v.X * transform.M12 + v.Y * transform.M22 + v.Z * transform.M32 + transform.M42,
            v.X * transform.M13 + v.Y * transform.M23 + v.Z * transform.M33 + transform.M43);
    }

    // Vector3's unary minus may compute 0 - x, which turns -0 into +0, and the box support reads the sign bit
    private static Vector3 GjkNeg(Vector3 a) => new(-a.X, -a.Y, -a.Z);

    private static float GjkDotLastX(Vector3 a, Vector3 b) => a.Z * b.Z + a.Y * b.Y + a.X * b.X;

    private static float GjkVectorNormalizeSlow(ref Vector3 v)
    {
        var x = (double)v.X;
        var y = (double)v.Y;
        var z = (double)v.Z;
        var length = Math.Sqrt(y * y + x * x + z * z);
        var inverse = 1.0 / length;
        v.X = (float)(inverse * x);
        v.Z = (float)(inverse * z);
        v.Y = (float)(inverse * y);

        return (float)(length < float.MaxValue ? length : float.MaxValue);
    }

    // The squared length is passed in because every caller sums the squares in its own order
    private static float GjkNormalizeGuarded(ref Vector3 v, float lengthSqr)
    {
        var length = MathF.Sqrt(lengthSqr);

        if (GjkNormalizeMin > length || length > GjkNormalizeMax)
        {
            if (length == 0f)
            {
                v = Vector3.Zero;
                return 0f;
            }

            return GjkVectorNormalizeSlow(ref v);
        }

        var inverse = 1f / length;
        v.X *= inverse;
        v.Y *= inverse;
        v.Z *= inverse;

        return length;
    }

    private static float GjkVectorNormalize(ref Vector3 v)
    {
        return GjkNormalizeGuarded(ref v, v.Y * v.Y + v.Z * v.Z + v.X * v.X);
    }

    private static Vector3 GjkNormalizeOrZero(Vector3 v, float lengthSqr)
    {
        if (lengthSqr > GjkNormalizeOrZeroEpsilon)
        {
            var inverse = 1f / MathF.Sqrt(lengthSqr);
            return new Vector3(v.X * inverse, v.Y * inverse, v.Z * inverse);
        }

        return Vector3.Zero;
    }

    private static Vector3 GjkVectorPerpendicular(Vector3 n)
    {
        var result = new Vector3((1f - n.Z) * (n.Y * n.Y - 0f) + n.Z, 0f, -n.X);
        GjkVectorNormalize(ref result);

        var d = result.Z * n.Z + n.Y * result.Y + result.X * n.X;
        result.X -= d * n.X;
        result.Y -= n.Y * d;
        result.Z -= n.Z * d;
        GjkVectorNormalize(ref result);

        return result;
    }

    private enum GjkShapeKind
    {
        Cloud,
        Point,
        Capsule,
    }

    /// <summary>The moving box and the static shape, as support mappings.</summary>
    private readonly ref struct GjkShapes
    {
        public readonly Vector3 BoxMin;
        public readonly Vector3 BoxMax;
        public readonly GjkShapeKind Kind;
        public readonly ReadOnlySpan<Vector3> Cloud;
        public readonly Vector3 PointA;
        public readonly Vector3 PointB;

        private GjkShapes(Vector3 halfExtents, GjkShapeKind kind, ReadOnlySpan<Vector3> cloud, Vector3 pointA, Vector3 pointB)
        {
            BoxMin = GjkNeg(halfExtents);
            BoxMax = halfExtents;
            Kind = kind;
            Cloud = cloud;
            PointA = pointA;
            PointB = pointB;
        }

        public static GjkShapes ForCloud(Vector3 halfExtents, ReadOnlySpan<Vector3> points) => new(halfExtents, GjkShapeKind.Cloud, points, default, default);
        public static GjkShapes ForPoint(Vector3 halfExtents, Vector3 point) => new(halfExtents, GjkShapeKind.Point, default, point, default);
        public static GjkShapes ForCapsule(Vector3 halfExtents, Vector3 a, Vector3 b) => new(halfExtents, GjkShapeKind.Capsule, default, a, b);

        // A direction component of -0 selects the min side
        public Vector3 SupportBox(Vector3 direction) => new(
            float.IsNegative(direction.X) ? BoxMin.X : BoxMax.X,
            float.IsNegative(direction.Y) ? BoxMin.Y : BoxMax.Y,
            float.IsNegative(direction.Z) ? BoxMin.Z : BoxMax.Z);

        public Vector3 SupportShape(Vector3 direction)
        {
            switch (Kind)
            {
                case GjkShapeKind.Point:
                    return PointA;

                case GjkShapeKind.Capsule:
                {
                    var d0 = direction.Z * PointA.Z + PointA.Y * direction.Y + direction.X * PointA.X;
                    var d1 = direction.Z * PointB.Z + PointB.Y * direction.Y + direction.X * PointB.X;
                    return d0 > d1 ? PointA : PointB;
                }

                default:
                {
                    // The first point with the strictly largest dot product wins
                    var best = 0;
                    var bestDot = direction.Z * Cloud[0].Z + Cloud[0].Y * direction.Y + direction.X * Cloud[0].X;

                    for (var i = 1; i < Cloud.Length; i++)
                    {
                        var d = direction.Z * Cloud[i].Z + Cloud[i].Y * direction.Y + direction.X * Cloud[i].X;

                        if (d > bestDot)
                        {
                            bestDot = d;
                            best = i;
                        }
                    }

                    return Cloud[best];
                }
            }
        }
    }

    /// <summary>The sweep of the box center.</summary>
    private struct GjkSegment
    {
        public Vector3 Start;
        public Vector3 End;
        public Vector3 Delta;
        public Vector3 Direction;
        public float Length;
        public float FullLength;
        public float InverseLength;
        public float MaxFraction;
    }

    private static GjkSegment GjkCastSegmentInit(Vector3 start, Vector3 delta)
    {
        var segment = new GjkSegment
        {
            Start = start,
            End = new Vector3(delta.X + start.X, delta.Y + start.Y, delta.Z + start.Z),
            Delta = delta,
            Direction = delta,
            MaxFraction = 1f,
        };

        var length = GjkVectorNormalize(ref segment.Direction);
        segment.Length = length;
        segment.InverseLength = 0f;
        segment.FullLength = length;

        if (length > 0f)
        {
            segment.InverseLength = 1f / length;
        }

        return segment;
    }

    // Clips the sweep to maxFraction plus 10 skins of look-ahead
    private static void GjkCastSegmentClip(ref GjkSegment segment, float maxFraction)
    {
        var fraction = SseMin(segment.InverseLength * 0.3125f + maxFraction, 1f);
        segment.MaxFraction = fraction;
        segment.Length = fraction * segment.FullLength;
        segment.End.X = segment.Delta.X * fraction + segment.Start.X;
        segment.End.Y = segment.Delta.Y * fraction + segment.Start.Y;
        segment.End.Z = segment.Delta.Z * fraction + segment.Start.Z;
    }

    private ref struct GjkCast(GjkShapes shapes, GjkSegment segment, Vector3 searchStart, float radius)
    {
        /// <summary>The GJK search vector, and at the end the contact normal.</summary>
        public Vector3 Search = searchStart;

        /// <summary>Unit contact normal, pointing from the shape towards the box.</summary>
        public Vector3 Normal;

        public readonly GjkShapes Shapes = shapes;
        public GjkSegment Segment = segment;

        /// <summary>Time of impact as a distance along the sweep.</summary>
        public float Distance;

        public float Fraction = 1f;
        public readonly float Radius = radius;
        public readonly float Skin = RubikonShapeCast.Skin;
        public bool StartSolid;

        /// <summary>Whether the sweep was already shortened once after an exact touch.</summary>
        public bool Shrunk;

        /// <summary>Whether a support point was found within radius + skin.</summary>
        public bool InMargin;
    }

    // ( supportA(d) + ( dot(delta, d) > 0 ? end : start ) ) - supportB(-d)
    private static Vector3 GjkCastSupport(in GjkCast cast, Vector3 d)
    {
        ref readonly var segment = ref cast.Segment;
        var p = GjkDotLastX(segment.Delta, d) > 0f ? segment.End : segment.Start;
        var sa = cast.Shapes.SupportBox(d);
        var sb = cast.Shapes.SupportShape(GjkNeg(d));

        return new Vector3(sa.X + p.X - sb.X, p.Y + sa.Y - sb.Y, p.Z + sa.Z - sb.Z);
    }

    [InlineArray(4)]
    private struct GjkPoints
    {
        private Vector3 Element;
    }

    private struct GjkSimplex
    {
        public GjkPoints Points;
        public int Count;
    }

    private static bool GjkSimplexContains(in GjkSimplex simplex, Vector3 w)
    {
        for (var i = 0; i < simplex.Count; i++)
        {
            var dx = simplex.Points[i].X - w.X;
            var dy = simplex.Points[i].Y - w.Y;
            var dz = simplex.Points[i].Z - w.Z;

            if (GjkDuplicateEpsilonSqr >= dy * dy + dx * dx + dz * dz)
            {
                return true;
            }
        }

        return false;
    }

    // d x (0,0,1), or d x (1,0,0) when that is ~0. The products with 0 are kept for their effect on signed zeros and NaN.
    private static Vector3 GjkDegenerateSegmentDir(float dx, float dy, float dz)
    {
        var dz0 = dz * 0f;
        var x = dy - dz0;
        var y = dz0 - dx;
        var dx0 = dx * 0f;
        var dy0 = dy * 0f;
        var z = dx0 - dy0;

        if (GjkEpsilonSqr >= x * x + y * y + z * z)
        {
            return new Vector3(dy0 - dz0, dz - dx0, dx0 - dy);
        }

        return new Vector3(x, y, z);
    }

    // Simplex { B, C } plus the new point A = w. v receives the new search vector, the caller searches along -v.
    private static bool GjkSimplexSolveTriangle(ref GjkSimplex simplex, Vector3 w, ref Vector3 v)
    {
        var abx = simplex.Points[0].X - w.X;
        var aby = simplex.Points[0].Y - w.Y;
        var abz = simplex.Points[0].Z - w.Z;
        var acy = simplex.Points[1].Y - w.Y;
        var acz = simplex.Points[1].Z - w.Z;
        var acx = simplex.Points[1].X - w.X;

        var nx = acz * aby - acy * abz;
        var ny = acx * abz - acz * abx;
        var nz = acy * abx - acx * aby;

        var t1 = (nz * acx - nx * acz) * w.Y + (nx * acy - ny * acx) * w.Z + (ny * acz - nz * acy) * w.X;

        if (0f > t1)
        {
            var dAc = w.Y * acy + w.Z * acz + acx * w.X;

            if (0f > dAc)
            {
                var cx = w.Z * acy - w.Y * acz;
                var cy = acz * w.X - w.Z * acx;
                var cz = w.Y * acx - acy * w.X;
                v = new Vector3(cy * acz - cz * acy, cz * acx - cx * acz, cx * acy - cy * acx);
                simplex.Count = 2;
                simplex.Points[0] = w;

                if (GjkEpsilonSqr < cy * cy + cx * cx + cz * cz)
                {
                    return false;
                }

                v = GjkDegenerateSegmentDir(
                    simplex.Points[0].X - simplex.Points[1].X,
                    simplex.Points[0].Y - simplex.Points[1].Y,
                    simplex.Points[0].Z - simplex.Points[1].Z);
                return false;
            }
        }
        else
        {
            var t2 = (nx * abz - nz * abx) * w.Y + (ny * abx - nx * aby) * w.Z + (nz * aby - ny * abz) * w.X;

            if (t2 > 0f)
            {
                // Inside the triangle prism: keep all three, wound towards the origin
                simplex.Count = 3;
                var dn = w.Y * ny + w.Z * nz + nx * w.X;

                if (0f > dn)
                {
                    simplex.Points[2] = w;
                    v = new Vector3(-nx, -ny, -nz);
                }
                else
                {
                    simplex.Points[2] = simplex.Points[1];
                    simplex.Points[1] = w;
                    v = new Vector3(nx, ny, nz);
                }

                return false;
            }
        }

        var dAb = w.Z * abz + w.Y * aby + w.X * abx;

        if (0f > dAb)
        {
            var cx = w.Z * aby - w.Y * abz;
            var cz = w.Y * abx - w.X * aby;
            var cy = w.X * abz - w.Z * abx;
            v = new Vector3(cy * abz - cz * aby, cz * abx - cx * abz, cx * aby - cy * abx);
            simplex.Count = 2;
            simplex.Points[1] = w;

            if (GjkEpsilonSqr < cx * cx + cy * cy + cz * cz)
            {
                return false;
            }

            v = GjkDegenerateSegmentDir(simplex.Points[0].X - w.X, simplex.Points[0].Y - w.Y, simplex.Points[0].Z - w.Z);
            return false;
        }

        simplex.Count = 1;
        simplex.Points[0] = w;
        v = w;
        return false;
    }

    // Edge A-X where A = w goes to slot slotA and X stays in the other slot
    private static void GjkTetraEdge(ref GjkSimplex simplex, Vector3 w, float ex, float ey, float ez, int slotA, ref Vector3 v)
    {
        var cx = w.Z * ey - w.Y * ez;
        var cy = ez * w.X - w.Z * ex;
        var cz = w.Y * ex - ey * w.X;
        v = new Vector3(cy * ez - cz * ey, cz * ex - cx * ez, cx * ey - cy * ex);
        var nonDegenerate = cx * cx + cy * cy + cz * cz > GjkEpsilonSqr;
        simplex.Count = 2;
        simplex.Points[slotA] = w;

        if (nonDegenerate)
        {
            return;
        }

        v = GjkDegenerateSegmentDir(
            simplex.Points[0].X - simplex.Points[1].X,
            simplex.Points[0].Y - simplex.Points[1].Y,
            simplex.Points[0].Z - simplex.Points[1].Z);
    }

    private enum GjkTetraRegion
    {
        FaceAbc,
        FaceAcd,
        FaceAdb,
        EdgeAb,
        EdgeAc,
        EdgeAd,
        VertexA,
    }

    // Simplex { B, C, D } plus the new point A = w. Returns true when the origin is inside the tetrahedron.
    private static bool GjkSimplexSolveTetrahedron(ref GjkSimplex simplex, Vector3 w, ref Vector3 v)
    {
        var b = simplex.Points[0];
        var c = simplex.Points[1];
        var d = simplex.Points[2];
        var abx = b.X - w.X;
        var aby = b.Y - w.Y;
        var abz = b.Z - w.Z;
        var acx = c.X - w.X;
        var acy = c.Y - w.Y;
        var acz = c.Z - w.Z;
        var adx = d.X - w.X;
        var ady = d.Y - w.Y;
        var adz = d.Z - w.Z;

        // Face normals: n1 = AB x AC, n2 = AC x AD, n3 = AD x AB
        var n1x = acz * aby - acy * abz;
        var n1y = acx * abz - acz * abx;
        var n1z = acy * abx - acx * aby;
        var n2x = adz * acy - ady * acz;
        var n2y = adx * acz - adz * acx;
        var n2z = ady * acx - adx * acy;
        var n3x = ady * abz - adz * aby;
        var n3y = adz * abx - adx * abz;
        var n3z = adx * aby - ady * abx;

        var mask = 0;

        if (0f > w.Z * n1z + w.Y * n1y + w.X * n1x)
        {
            mask |= 1;
        }

        if (0f > w.Z * n2z + w.Y * n2y + w.X * n2x)
        {
            mask |= 2;
        }

        if (0f > w.Z * n3z + w.Y * n3y + w.X * n3x)
        {
            mask |= 4;
        }

        switch (mask)
        {
            case 0:
                simplex.Points[3] = w;
                simplex.Count = 4;
                return true;
            case 1:
                return GjkSimplexSolveTriangle(ref simplex, w, ref v);
            case 2:
                simplex.Points[0] = simplex.Points[2];
                return GjkSimplexSolveTriangle(ref simplex, w, ref v);
            case 4:
                simplex.Points[1] = simplex.Points[2];
                return GjkSimplexSolveTriangle(ref simplex, w, ref v);
        }

        // Edge plane tests; since w = A, a negative value means the origin is on the positive side
        var e1 = (acy * n1x - acx * n1y) * w.Z + (acx * n1z - acz * n1x) * w.Y + (acz * n1y - acy * n1z) * w.X;
        var e2 = (abz * n1x - abx * n1z) * w.Y + (abx * n1y - aby * n1x) * w.Z + (aby * n1z - abz * n1y) * w.X;
        var e3 = (acx * n2y - acy * n2x) * w.Z + (acz * n2x - acx * n2z) * w.Y + (acy * n2z - acz * n2y) * w.X;
        var e4 = (adx * n2z - adz * n2x) * w.Y + (ady * n2x - adx * n2y) * w.Z + (adz * n2y - ady * n2z) * w.X;
        var e5 = (abx * n3z - abz * n3x) * w.Y + (aby * n3x - abx * n3y) * w.Z + (abz * n3y - aby * n3z) * w.X;
        var e6 = (adx * n3y - ady * n3x) * w.Z + (adz * n3x - adx * n3z) * w.Y + (ady * n3z - adz * n3y) * w.X;
        var dAb = w.Z * abz + w.Y * aby + w.X * abx;
        var dAc = w.Z * acz + w.Y * acy + w.X * acx;
        var dAd = w.Z * adz + w.Y * ady + w.X * adx;

        var edgeAbChecked = dAb >= 0f ? GjkTetraRegion.VertexA : GjkTetraRegion.EdgeAb;
        var edgeAdChecked = dAd >= 0f ? GjkTetraRegion.VertexA : GjkTetraRegion.EdgeAd;

        GjkTetraRegion region;

        switch (mask)
        {
            case 3:
                if (e1 >= 0f)
                {
                    region = e2 >= 0f ? GjkTetraRegion.FaceAbc : edgeAbChecked;
                }
                else if (e3 < 0f)
                {
                    region = dAc >= 0f ? GjkTetraRegion.VertexA : GjkTetraRegion.EdgeAc;
                }
                else
                {
                    region = e4 < 0f ? GjkTetraRegion.EdgeAd : GjkTetraRegion.FaceAcd;
                }

                break;

            case 5:
                if (e5 >= 0f)
                {
                    region = e6 < 0f ? edgeAdChecked : GjkTetraRegion.FaceAdb;
                }
                else if (e2 < 0f)
                {
                    region = edgeAbChecked;
                }
                else
                {
                    region = e1 >= 0f ? GjkTetraRegion.FaceAbc : GjkTetraRegion.EdgeAc;
                }

                break;

            case 6:
                if (e4 >= 0f)
                {
                    if (e3 >= 0f)
                    {
                        region = GjkTetraRegion.FaceAcd;
                    }
                    else
                    {
                        region = dAc >= 0f ? GjkTetraRegion.VertexA : GjkTetraRegion.EdgeAc;
                    }
                }
                else if (e6 >= 0f)
                {
                    region = e5 >= 0f ? GjkTetraRegion.FaceAdb : GjkTetraRegion.EdgeAb;
                }
                else
                {
                    region = edgeAdChecked;
                }

                break;

            default:
            {
                var regionAcd = e3 >= 0f
                    ? (e4 >= 0f ? GjkTetraRegion.FaceAcd : edgeAdChecked)
                    : (dAc < 0f ? GjkTetraRegion.EdgeAc : edgeAdChecked);

                if (e5 >= 0f && e6 >= 0f)
                {
                    region = GjkTetraRegion.FaceAdb;
                }
                else if (e1 < 0f)
                {
                    region = regionAcd;
                }
                else if (e2 >= 0f)
                {
                    region = GjkTetraRegion.FaceAbc;
                }
                else
                {
                    region = dAb < 0f ? GjkTetraRegion.EdgeAb : regionAcd;
                }

                break;
            }
        }

        switch (region)
        {
            case GjkTetraRegion.FaceAbc:
                simplex.Count = 3;
                simplex.Points[2] = w;
                v = new Vector3(-n1x, -n1y, -n1z);
                break;

            case GjkTetraRegion.FaceAcd:
                simplex.Count = 3;
                simplex.Points[0] = w;
                v = new Vector3(-n2x, -n2y, -n2z);
                break;

            case GjkTetraRegion.FaceAdb:
                simplex.Count = 3;
                simplex.Points[1] = w;
                v = new Vector3(-n3x, -n3y, -n3z);
                break;

            case GjkTetraRegion.EdgeAb:
                GjkTetraEdge(ref simplex, w, abx, aby, abz, 1, ref v);
                break;

            case GjkTetraRegion.EdgeAc:
                GjkTetraEdge(ref simplex, w, acx, acy, acz, 0, ref v);
                break;

            case GjkTetraRegion.EdgeAd:
                simplex.Points[0] = simplex.Points[2];
                GjkTetraEdge(ref simplex, w, adx, ady, adz, 1, ref v);
                break;

            default:
                simplex.Count = 1;
                simplex.Points[0] = w;
                v = w;
                break;
        }

        return false;
    }

    // Adds w and reduces the simplex to the feature closest to the origin. Returns true when the search can stop:
    // the origin is inside the tetrahedron, or on the line through a two point simplex.
    private static bool GjkSimplexAddPoint(ref GjkSimplex simplex, Vector3 w, ref Vector3 v)
    {
        switch (simplex.Count)
        {
            case 0:
                simplex.Count = 1;
                simplex.Points[0] = w;
                v = w;
                return false;

            case 1:
            {
                var ex = simplex.Points[0].X - w.X;
                var ez = simplex.Points[0].Z - w.Z;
                var ey = simplex.Points[0].Y - w.Y;

                if (0f > ey * w.Y + ez * w.Z + ex * w.X)
                {
                    simplex.Count = 2;
                    simplex.Points[1] = w;
                    var cx = ey * w.Z - ez * w.Y;
                    var cy = ez * w.X - ex * w.Z;
                    var cz = ex * w.Y - ey * w.X;
                    v = new Vector3(cy * ez - cz * ey, cz * ex - cx * ez, cx * ey - cy * ex);

                    if (GjkEpsilonSqr >= cy * cy + cx * cx + cz * cz)
                    {
                        // Origin on the line: e x (1,0,1)
                        v = new Vector3(ey - ez * 0f, ez - ex, ex * 0f - ey);
                        return true;
                    }

                    return false;
                }

                simplex.Count = 1;
                simplex.Points[0] = w;
                v = w;
                return false;
            }

            case 2:
                return GjkSimplexSolveTriangle(ref simplex, w, ref v);

            case 3:
                return GjkSimplexSolveTetrahedron(ref simplex, w, ref v);
        }

        return true;
    }

    private static bool GjkIntersectRaySphere(Vector3 start, Vector3 delta, Vector3 center, float radius, ref float t0, ref float t1)
    {
        var mx = start.X - center.X;
        var my = start.Y - center.Y;
        var mz = start.Z - center.Z;
        var dd = delta.Z * delta.Z + delta.Y * delta.Y + delta.X * delta.X;
        var r2 = radius * radius;

        if (dd == 0f)
        {
            t1 = 0f;
            t0 = 0f;
            return r2 >= mx * mx + my * my + mz * mz;
        }

        var inverse = 1f / dd;
        var b = mz * delta.Z + my * delta.Y + mx * delta.X;
        var s = -(b + b) * (inverse * 0.5f);
        var qx = delta.X * s + mx;
        var qy = delta.Y * s + my;
        var qz = delta.Z * s + mz;
        var h = r2 - (qx * qx + qy * qy + qz * qz);

        if (0f > h)
        {
            return false;
        }

        var sq = MathF.Sqrt(h * inverse);
        t0 = s - sq;
        t1 = sq + s;
        return true;
    }

    private static bool GjkIntersectSegmentSphere(Vector3 start, Vector3 delta, Vector3 center, float radius, ref float t0, ref float t1)
    {
        if (!GjkIntersectRaySphere(start, delta, center, radius, ref t0, ref t1) || t0 > 1f || 0f > t1)
        {
            return false;
        }

        if (0f > t0)
        {
            t0 = 0f;
        }

        if (t1 > 1f)
        {
            t1 = 1f;
        }

        return true;
    }

    private struct GjkRayHit
    {
        public float Fraction;
        public Vector3 Point;
        public Vector3 Normal;
        public bool StartSolid;
    }

    // Degenerate ray against a sphere: c = |m|^2 - r^2, m = start - center, mm = |m|^2
    private static void GjkRaySphereDegenerate(ref GjkRayHit hit, float c, Vector3 start, Vector3 m, float mm)
    {
        if (0f < c)
        {
            hit.Fraction = GjkRayMiss;
            return;
        }

        hit.Fraction = 0f;
        hit.Point = start;

        if (mm > GjkDuplicateEpsilonSqr)
        {
            var inverse = 1f / MathF.Sqrt(mm);
            hit.StartSolid = true;
            hit.Normal = new Vector3(inverse * m.X, inverse * m.Y, inverse * m.Z);
            return;
        }

        hit.Normal = Vector3.UnitZ;
        hit.StartSolid = true;
    }

    private static void GjkCastRaySphere(ref GjkRayHit hit, Vector3 m, Vector3 start, Vector3 delta, float radius)
    {
        var dd = delta.Z * delta.Z + delta.Y * delta.Y + delta.X * delta.X;
        var mm = m.Y * m.Y + m.Z * m.Z + m.X * m.X;
        var c = mm - radius * radius;

        if (1.4210855e-14f > dd)
        {
            GjkRaySphereDegenerate(ref hit, c, start, m, mm);
            return;
        }

        var b = m.Z * delta.Z + delta.Y * m.Y + m.X * delta.X;
        var disc = b * b - c * dd;

        if (0f > disc)
        {
            hit.Fraction = GjkRayMiss;
            return;
        }

        var t = (-b - MathF.Sqrt(disc)) / dd;

        if (0f > t)
        {
            GjkRaySphereDegenerate(ref hit, c, start, m, mm);
            return;
        }

        hit.Fraction = t;
        var tx = t * delta.X;
        var ty = t * delta.Y;
        var tz = t * delta.Z;
        hit.Point = new Vector3(tx + start.X, ty + start.Y, tz + start.Z);
        var inverseRadius = 1f / radius;
        hit.Normal = new Vector3((tx + m.X) * inverseRadius, (ty + m.Y) * inverseRadius, (tz + m.Z) * inverseRadius);
    }

    // A radius below 1e-5 leaves the hit untouched
    private static void GjkCastRayCapsule(ref GjkRayHit hit, Vector3 start, Vector3 delta, Vector3 p0, Vector3 p1, float radius)
    {
        var axx = p1.X - p0.X;
        var axy = p1.Y - p0.Y;
        var axz = p1.Z - p0.Z;
        var m = new Vector3(start.X - p0.X, start.Y - p0.Y, start.Z - p0.Z);
        var length = MathF.Sqrt(axy * axy + axx * axx + axz * axz);

        if (1e-5f > radius)
        {
            return;
        }

        if (0.001f > length)
        {
            GjkCastRaySphere(ref hit, m, start, delta, radius);
            return;
        }

        var dd = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
        var inverseLength = 1f / length;
        var a = new Vector3(axx * inverseLength, axy * inverseLength, inverseLength * axz);
        var deltaLength = MathF.Sqrt(dd);

        if (deltaLength > 1e-4f)
        {
            // t is the hit parameter along delta, s the one along the axis for the normal
            var t = GjkRayMiss;
            var s = 0f;

            var inverseDelta = 1f / deltaLength;
            var dirX = inverseDelta * delta.X;
            var dirY = inverseDelta * delta.Y;
            var dirZ = inverseDelta * delta.Z;
            var cx = dirZ * a.Y - dirY * a.Z;
            var cy = dirX * a.Z - dirZ * a.X;
            var cz = dirY * a.X - dirX * a.Y;
            var cc = cy * cy + cx * cx + cz * cz;
            var dDotA = delta.Z * a.Z + delta.Y * a.Y + delta.X * a.X;

            if (cc > 9.3132257e-10f)
            {
                var inverseC = 1f / MathF.Sqrt(cc);
                var ny = inverseC * cy;
                var nx = inverseC * cx;
                var nz = inverseC * cz;
                var distance = m.Z * nz + m.Y * ny + m.X * nx;
                var h = radius * radius - distance * distance;

                if (!(0f < h))
                {
                    hit.Fraction = GjkRayMiss;
                    return;
                }

                // q = n x a
                var qx = ny * a.Z - nz * a.Y;
                var qy = nz * a.X - nx * a.Z;
                var qz = nx * a.Y - ny * a.X;
                var projection = m.Z * a.Z + m.Y * a.Y + m.X * a.X;
                var mq = m.Z * qz + m.Y * qy + m.X * qx;
                var dq = qz * delta.Z + qy * delta.Y + delta.X * qx;
                var mq2 = mq * mq;
                s = SseMin(SseMax(projection, 0f), length);
                var e = projection - s;

                if (h <= e * e + mq2)
                {
                    // Starts outside, the infinite cylinder first
                    var inverse4 = 1f / (dq * dq + dDotA * dDotA);
                    var dqmq = dq * mq;
                    var tc = (-MathF.Sqrt(h) - mq) / dq;
                    s = SseMax(0f, tc) * dDotA + projection;
                    var hitsCylinder = false;

                    if (0f > s)
                    {
                        s = 0f;
                    }
                    else if (length > s)
                    {
                        if (tc >= 0f)
                        {
                            t = tc;
                        }

                        hitsCylinder = true;
                    }
                    else
                    {
                        s = length;
                        projection -= length;
                    }

                    if (!hitsCylinder)
                    {
                        var capB = (projection * dDotA + dqmq) * inverse4;
                        var capC = (projection * projection + mq2 - h) * inverse4;
                        var disc = capB * capB - capC;

                        if (disc >= 0f)
                        {
                            var ts = -capB - MathF.Sqrt(disc);

                            if (ts >= 0f)
                            {
                                t = ts;
                            }
                        }
                    }
                }
                else
                {
                    t = 0f;
                }
            }
            else
            {
                // Ray parallel to the axis
                var projection = m.Z * a.Z + m.Y * a.Y + m.X * a.X;
                var px = m.X - a.X * projection;
                var pz = m.Z - a.Z * projection;
                var py = m.Y - a.Y * projection;
                var pp = py * py + px * px + pz * pz;
                var reach = radius;
                var misses = false;

                if (!(1.4210855e-14f > pp))
                {
                    var inverseP = 1f / MathF.Sqrt(pp);
                    var upz = pz * inverseP;
                    var upx = inverseP * px;
                    var upy = py * inverseP;

                    // (p x a) . m
                    var pam = (upz * a.X - upx * a.Z) * m.Y + (upx * a.Y - upy * a.X) * m.Z + (upy * a.Z - upz * a.Y) * m.X;
                    var h = radius * radius - pp - pam * pam;

                    if (h > 0f)
                    {
                        reach = MathF.Sqrt(h);
                    }
                    else
                    {
                        misses = true;
                    }
                }

                if (!misses)
                {
                    var front = projection + reach;

                    if (0f > front)
                    {
                        var negative = -front;
                        s = 0f;

                        if (dDotA >= negative)
                        {
                            t = negative / dDotA;
                        }
                    }
                    else
                    {
                        var back = length + reach;

                        if (back > projection)
                        {
                            t = 0f;
                            s = SseMin(SseMax(projection, 0f), length);
                        }
                        else
                        {
                            var negativeDDotA = -dDotA;
                            var over = projection - back;
                            s = length;

                            if (negativeDDotA >= over)
                            {
                                t = over / negativeDDotA;
                            }
                        }
                    }
                }
            }

            var f = SseMin(1f, t);
            hit.Fraction = f;
            hit.StartSolid = 0f >= t;
            hit.Point = new Vector3(f * delta.X + start.X, f * delta.Y + start.Y, f * delta.Z + start.Z);

            if (t == GjkRayMiss)
            {
                hit.Normal = Vector3.UnitZ;
                return;
            }

            var n = new Vector3(
                hit.Point.X - (s * a.X + p0.X),
                hit.Point.Y - (s * a.Y + p0.Y),
                hit.Point.Z - (s * a.Z + p0.Z));
            GjkNormalizeGuarded(ref n, n.Z * n.Z + n.Y * n.Y + n.X * n.X);
            hit.Normal = n;
            return;
        }

        // (Almost) zero length ray: point against capsule
        {
            var projection = m.Z * a.Z + m.Y * a.Y + m.X * a.X;

            if (!(0f > projection))
            {
                if (projection > length)
                {
                    m = new Vector3(start.X - p1.X, start.Y - p1.Y, start.Z - p1.Z);
                }
                else
                {
                    m.X -= projection * a.X;
                    m.Y -= projection * a.Y;
                    m.Z -= projection * a.Z;
                }
            }

            var mm = m.Y * m.Y + m.X * m.X + m.Z * m.Z;

            if (mm > radius * radius)
            {
                hit.StartSolid = false;
                hit.Fraction = 1f;
                return;
            }

            hit.StartSolid = true;
            hit.Fraction = 0f;

            if (mm > GjkEpsilonSqr)
            {
                var inverse = 1f / MathF.Sqrt(mm);
                hit.Normal = new Vector3(inverse * m.X, inverse * m.Y, inverse * m.Z);
            }
            else
            {
                hit.Normal = GjkVectorPerpendicular(a);
            }

            hit.Point = start;
        }
    }

    // Ray from the origin along dir against the plane through p with the unnormalized normal n
    private static float GjkRayPlaneExit(Vector3 dir, Vector3 p, float nx, float ny, float nz)
    {
        var den = dir.Z * nz + dir.Y * ny + dir.X * nx;

        if (den > 0f)
        {
            return SseMax(0f, p.Z * nz + p.Y * ny + p.X * nx) / den;
        }

        return GjkPortalNoHit;
    }

    // The origin is inside the tetrahedron. Reduces the simplex to the face the ray along dir leaves through
    // and returns its unnormalized outward normal, or returns false when no face is hit and the tetrahedron is flat.
    private static bool GjkPortalSelectFromTetrahedron(ref GjkSimplex simplex, Vector3 dir, out Vector3 normal)
    {
        var p0 = simplex.Points[0];
        var p1 = simplex.Points[1];
        var p2 = simplex.Points[2];
        var p3 = simplex.Points[3];
        var ax = p0.X - p3.X;
        var ay = p0.Y - p3.Y;
        var az = p0.Z - p3.Z;
        var bx = p1.X - p3.X;
        var by = p1.Y - p3.Y;
        var bz = p1.Z - p3.Z;
        var cx = p2.X - p3.X;
        var cy = p2.Y - p3.Y;
        var cz = p2.Z - p3.Z;
        var e1x = p1.X - p0.X;
        var e1y = p1.Y - p0.Y;
        var e1z = p1.Z - p0.Z;
        var e2x = p2.X - p0.X;
        var e2y = p2.Y - p0.Y;
        var e2z = p2.Z - p0.Z;

        // f0 = a x b (p3 p0 p1), f1 = b x c (p3 p1 p2), f2 = c x a (p3 p2 p0), f3 = e1 x e2 (p0 p1 p2)
        var f0 = new Vector3(bz * ay - by * az, bx * az - bz * ax, by * ax - bx * ay);
        var f1 = new Vector3(cz * by - cy * bz, cx * bz - cz * bx, cy * bx - cx * by);
        var f2 = new Vector3(cy * az - cz * ay, cz * ax - cx * az, cx * ay - cy * ax);
        var f3 = new Vector3(e2y * e1z - e2z * e1y, e2z * e1x - e2x * e1z, e2x * e1y - e2y * e1x);

        var t0 = GjkRayPlaneExit(dir, p3, f0.X, f0.Y, f0.Z);
        var t1 = GjkRayPlaneExit(dir, p3, f1.X, f1.Y, f1.Z);
        var t2 = GjkRayPlaneExit(dir, p3, f2.X, f2.Y, f2.Z);
        var t3 = GjkRayPlaneExit(dir, p0, f3.X, f3.Y, f3.Z);

        var best = t3;

        if (t3 > t0)
        {
            best = t0;
        }

        var face = t3 <= t0 ? 3 : 0;

        if (best > t1)
        {
            face = 1;
            best = t1;
        }

        if (best > t2)
        {
            face = 2;
            best = t2;
        }

        if (best >= GjkPortalNoHit)
        {
            var d3 = p3.Z * f3.Z + p3.Y * f3.Y + f3.X * p3.X;
            var d0 = p0.Z * f3.Z + p0.Y * f3.Y + f3.X * p0.X;
            var d1 = p1.Z * f3.Z + p1.Y * f3.Y + f3.X * p1.X;

            if (1e-6f > MathF.Abs(d3 - (d0 + d1) * 0.5f))
            {
                normal = Vector3.Zero;
                return false;
            }
        }

        simplex.Count = 3;

        switch (face)
        {
            case 0:
                simplex.Points[2] = p3;
                normal = f0;
                break;

            case 1:
                simplex.Points[0] = p3;
                normal = f1;
                break;

            case 2:
                simplex.Points[1] = p3;
                normal = f2;
                break;

            default:
                simplex.Points[1] = p2;
                simplex.Points[2] = p1;
                simplex.Points[3] = p1;
                normal = f3;
                break;
        }

        return true;
    }

    // Portal p0 p1 p2 plus the new support point p3: reduces the simplex to the new face the ray passes through
    private static Vector3 GjkPortalRefine(ref GjkSimplex simplex, Vector3 dir)
    {
        var p3 = simplex.Points[3];
        var ax = simplex.Points[0].X - p3.X;
        var ay = simplex.Points[0].Y - p3.Y;
        var az = simplex.Points[0].Z - p3.Z;
        var bx = simplex.Points[1].X - p3.X;
        var by = simplex.Points[1].Y - p3.Y;
        var bz = simplex.Points[1].Z - p3.Z;
        var cx = simplex.Points[2].X - p3.X;
        var cy = simplex.Points[2].Y - p3.Y;
        var cz = simplex.Points[2].Z - p3.Z;

        var g0 = new Vector3(bz * ay - by * az, bx * az - bz * ax, by * ax - bx * ay);
        var g1 = new Vector3(cz * by - cy * bz, cx * bz - cz * bx, cy * bx - cx * by);
        var g2 = new Vector3(cy * az - cz * ay, cz * ax - cx * az, cx * ay - cy * ax);

        var t0 = GjkRayPlaneExit(dir, p3, g0.X, g0.Y, g0.Z);
        var t1 = GjkRayPlaneExit(dir, p3, g1.X, g1.Y, g1.Z);
        var t2 = GjkRayPlaneExit(dir, p3, g2.X, g2.Y, g2.Z);

        var best = t2;

        if (t2 > t0)
        {
            best = t0;
        }

        simplex.Count = 3;
        var face = t2 <= t0 ? 2 : 0;

        if (best > t1)
        {
            face = 1;
        }

        switch (face)
        {
            case 0:
                simplex.Points[2] = p3;
                return g0;

            case 1:
                simplex.Points[0] = p3;
                return g1;

            default:
                simplex.Points[1] = p3;
                return g2;
        }
    }

    // Flat case: the origin is inside the triangle. Exit through the nearest of its three side planes, pushed out by skin.
    private static float GjkPortalTriangleExit(in GjkSimplex simplex, Vector3 dir, float skin)
    {
        var p0 = simplex.Points[0];
        var p1 = simplex.Points[1];
        var p2 = simplex.Points[2];
        var az = p0.Z - p2.Z;
        var ay = p0.Y - p2.Y;
        var ax = p0.X - p2.X;
        var bx = p1.X - p2.X;
        var by = p1.Y - p2.Y;
        var bz = p1.Z - p2.Z;
        var ez = p1.Z - p0.Z;
        var ey = p1.Y - p0.Y;
        var ex = p1.X - p0.X;

        var n = new Vector3(bz * ay - by * az, bx * az - bz * ax, by * ax - bx * ay);
        GjkNormalizeGuarded(ref n, n.Z * n.Z + n.Y * n.Y + n.X * n.X);

        // Side normals: s0 = a x n (edge p2-p0), s1 = n x b (edge p2-p1), s2 = e x n (edge p0-p1)
        var s0 = new Vector3(n.Z * ay - n.Y * az, az * n.X - n.Z * ax, n.Y * ax - ay * n.X);
        var s1 = new Vector3(n.Y * bz - n.Z * by, n.Z * bx - n.X * bz, n.X * by - n.Y * bx);
        var s2 = new Vector3(n.Z * ey - n.Y * ez, ez * n.X - n.Z * ex, n.Y * ex - ey * n.X);
        GjkNormalizeGuarded(ref s0, s0.Z * s0.Z + s0.Y * s0.Y + s0.X * s0.X);
        GjkNormalizeGuarded(ref s1, s1.Z * s1.Z + s1.Y * s1.Y + s1.X * s1.X);
        GjkNormalizeGuarded(ref s2, s2.Z * s2.Z + s2.Y * s2.Y + s2.X * s2.X);

        var t0 = GjkRayPlaneExit(dir, p0, s0.X, s0.Y, s0.Z);
        var t1 = GjkRayPlaneExit(dir, p1, s1.X, s1.Y, s1.Z);
        var t2 = GjkRayPlaneExit(dir, p1, s2.X, s2.Y, s2.Z);

        var best = t0;

        if (t0 > t1)
        {
            best = t1;
        }

        var index = t0 > t1 ? 1 : 0;

        if (best > t2)
        {
            index = 2;
            best = t2;
        }

        var side = index switch
        {
            0 => s0,
            1 => s1,
            _ => s2,
        };
        var dd = dir.Z * side.Z + side.Y * dir.Y + dir.X * side.X;

        if (dd > 0f)
        {
            return skin / dd + best;
        }

        return GjkPortalNoHit;
    }

    // The origin is inside C: finds the distance t at which the ray along the sweep direction leaves it
    private static bool GjkCastPortalExit(ref GjkCast cast, ref GjkSimplex simplex, out float t)
    {
        t = 0f;
        var count = simplex.Count;

        if (count < 4)
        {
            if (count == 2)
            {
                var v = cast.Search;

                if (v.X * v.X + v.Y * v.Y + v.Z * v.Z <= 0f)
                {
                    return false;
                }

                var w = GjkCastSupport(cast, GjkNeg(v));
                count = simplex.Count;

                if (!GjkSimplexContains(simplex, w))
                {
                    simplex.Points[count] = w;
                    ++count;
                    simplex.Count = count;
                }
            }

            if (count != 3)
            {
                return false;
            }

            t = GjkPortalTriangleExit(simplex, cast.Segment.Direction, cast.Skin);
            return true;
        }

        if (!GjkPortalSelectFromTetrahedron(ref simplex, cast.Segment.Direction, out var n))
        {
            simplex.Count = 3;
            t = GjkPortalTriangleExit(simplex, cast.Segment.Direction, cast.Skin);
            return true;
        }

        for (var iteration = 0; ;)
        {
            n = GjkNormalizeOrZero(n, n.Y * n.Y + n.X * n.X + n.Z * n.Z);
            var w = GjkCastSupport(cast, n);
            var portalPoint = simplex.Points[0];

            if (portalPoint.Z * n.Z + portalPoint.Y * n.Y + portalPoint.X * n.X + GjkPortalTolerance >= n.Z * w.Z + n.Y * w.Y + w.X * n.X)
            {
                break;
            }

            simplex.Points[simplex.Count] = w;
            simplex.Count++;
            n = GjkPortalRefine(ref simplex, cast.Segment.Direction);

            if (++iteration >= GjkPortalMaxIterations)
            {
                return false;
            }
        }

        // Ray against the portal plane
        var p0 = simplex.Points[0];
        var e2x = simplex.Points[2].X - p0.X;
        var e1z = simplex.Points[1].Z - p0.Z;
        var e1x = simplex.Points[1].X - p0.X;
        var e1y = simplex.Points[1].Y - p0.Y;
        var e2z = simplex.Points[2].Z - p0.Z;
        var e2y = simplex.Points[2].Y - p0.Y;
        var ny = e2x * e1z - e2z * e1x;
        var nx = e2z * e1y - e2y * e1z;
        var nz = e2y * e1x - e2x * e1y;
        var dir = cast.Segment.Direction;
        var den = dir.Z * nz + dir.Y * ny + nx * dir.X;

        if (den <= 0f)
        {
            t = GjkPortalNoHit;
        }
        else
        {
            t = SseMax(0f, p0.Z * nz + p0.Y * ny + nx * p0.X) / den;
        }

        if (simplex.Count == 3)
        {
            // Keep the triangle wound so that its normal faces away from the origin
            var lhs = p0.Z * nz + p0.Y * ny + nx * p0.X;
            var rhs = nz * 0f + ny * 0f + nx * 0f;

            if (lhs > rhs)
            {
                (simplex.Points[1], simplex.Points[2]) = (simplex.Points[2], simplex.Points[1]);
            }
        }

        return true;
    }

    // Normal when radius + skin <= 0, which box casts never reach since the skin is always positive
    private static void GjkFinalizeNormalNoMargin(ref GjkCast cast, in GjkSimplex simplex)
    {
        var p0 = simplex.Points[0];
        var dir = cast.Segment.Direction;

        if (simplex.Count >= 3)
        {
            var ax = simplex.Points[1].X - p0.X;
            var ay = simplex.Points[1].Y - p0.Y;
            var az = simplex.Points[1].Z - p0.Z;
            var bx = simplex.Points[2].X - p0.X;
            var by = simplex.Points[2].Y - p0.Y;
            var bz = simplex.Points[2].Z - p0.Z;
            var ny = ax * bz - az * bx;
            var nx = az * by - ay * bz;
            var nz = ay * bx - ax * by;
            cast.Normal = GjkNormalizeOrZero(new Vector3(nx, ny, nz), ny * ny + nx * nx + nz * nz);
            return;
        }

        if (simplex.Count == 2)
        {
            var ex = simplex.Points[1].X - p0.X;
            var ez = simplex.Points[1].Z - p0.Z;
            var ey = simplex.Points[1].Y - p0.Y;

            // c = e x dir, m = e x c
            var cx = ey * dir.Z - dir.Y * ez;
            var cy = dir.X * ez - ex * dir.Z;
            var cz = ex * dir.Y - ey * dir.X;
            var mx = ey * cz - cy * ez;
            var my = cx * ez - ex * cz;
            var mz = ex * cy - ey * cx;
            var m = GjkNormalizeOrZero(new Vector3(mx, my, mz), my * my + mx * mx + mz * mz);
            var dd = dir.Z * m.Z + dir.Y * m.Y + m.X * dir.X;
            var n = dd > 0f ? GjkNeg(m) : m;

            if (dd == 0f)
            {
                n = GjkNeg(dir);
            }

            cast.Normal = n;
            return;
        }

        cast.Normal = GjkNeg(dir);
    }

    // Checks whether vertex p is the outermost point of C along its normal n, and walks to the edge or face beyond it if not
    private static float GjkFinalizeRefineVertex(ref GjkCast cast, Vector3 p, float t, float margin, Vector3 n)
    {
        var pn = p.Y * n.Y + n.Z * p.Z + p.X * n.X;
        var w = GjkCastSupport(cast, n);

        if (!(1e-4f < w.Z * n.Z + w.Y * n.Y + w.X * n.X - pn))
        {
            return t;
        }

        var dir = cast.Segment.Direction;
        var ex = w.X - p.X;
        var ey = w.Y - p.Y;
        var ez = w.Z - p.Z;
        var local = new GjkSimplex { Count = 2 };
        local.Points[0] = p;
        local.Points[1] = w;

        var tx = t * dir.X;
        var tz = t * dir.Z;
        var ty = t * dir.Y;
        var qx = tx - p.X;
        var qz = tz - p.Z;
        var qy = ty - p.Y;

        // c = q x e, m = c x e: perpendicular to the edge, in the plane of the edge and T
        var cx = qy * ez - ey * qz;
        var cy = ex * qz - ez * qx;
        var cz = ey * qx - ex * qy;
        var c = GjkNormalizeOrZero(new Vector3(cx, cy, cz), cy * cy + cx * cx + cz * cz);
        var mx = ez * c.Y - ey * c.Z;
        var my = ex * c.Z - ez * c.X;
        var mz = ey * c.X - ex * c.Y;
        var m = GjkNormalizeOrZero(new Vector3(mx, my, mz), my * my + mx * mx + mz * mz);

        if (0f > m.Y * ty + m.Z * tz + m.X * tx)
        {
            m = GjkNeg(m);
        }

        var w2 = GjkCastSupport(cast, m);
        var dW = m.Z * w2.Z + m.Y * w2.Y + m.X * w2.X;
        var dP = m.Z * p.Z + m.Y * p.Y + m.X * p.X;

        if (1e-4f > MathF.Abs(dW - dP))
        {
            return GjkFinalizeEdge(ref cast, local, t, margin, top: false);
        }

        for (var iteration = 0; iteration < GjkFeatureMaxIterations; iteration++)
        {
            var gx = w2.X - p.X;
            var gy = w2.Y - p.Y;
            var gz = w2.Z - p.Z;

            // k = g x e, the normal of the triangle p, w, w2
            var k = new Vector3(ez * gy - ey * gz, ex * gz - ez * gx, ey * gx - ex * gy);
            GjkNormalizeGuarded(ref k, k.Z * k.Z + k.Y * k.Y + k.X * k.X);

            if (0f > tz * k.Z + ty * k.Y + k.X * tx)
            {
                k = GjkNeg(k);
            }

            var w3 = GjkCastSupport(cast, k);
            var d2 = w2.Z * k.Z + w2.Y * k.Y + w2.X * k.X;
            var d3 = k.Z * w3.Z + k.Y * w3.Y + k.X * w3.X;

            if (1e-4f > MathF.Abs(d3 - d2) || d2 > d3 || 0f > n.Z * k.Z + n.Y * k.Y + n.X * k.X)
            {
                break;
            }

            w2 = w3;
        }

        // Triangle p, w, w2 wound so that its normal points away from T
        local.Points[2] = w2;
        local.Count = 3;

        var ax = w2.X - p.X;
        var ay = w2.Y - p.Y;
        var az = w2.Z - p.Z;
        var bx = w.X - p.X;
        var by = w.Y - p.Y;
        var bz = w.Z - p.Z;
        var nx = az * by - ay * bz;
        var ny = ax * bz - az * bx;
        var nz = ay * bx - ax * by;
        var rhs = t * dir.Z * nz + t * dir.Y * ny + t * dir.X * nx;
        var lhs = p.Z * nz + p.Y * ny + p.X * nx;

        if (lhs > rhs)
        {
            local.Points[2] = w;
            local.Points[1] = w2;
        }

        return GjkFinalizeFace(ref cast, local, t, margin, top: false);
    }

    // Single vertex p: exit of the ray from T = t * dir through the sphere (p, margin)
    private static float GjkFinalizeVertex(ref GjkCast cast, in GjkSimplex simplex, float t, float margin)
    {
        var dir = cast.Segment.Direction;
        var p = simplex.Points[0];
        var tp = new Vector3(t * dir.X, t * dir.Y, t * dir.Z);
        var length = dir.Y * p.Y + dir.Z * p.Z + p.X * dir.X + t + (margin + margin);
        var d = new Vector3(length * dir.X + tp.X - tp.X, length * dir.Y + tp.Y - tp.Y, length * dir.Z + tp.Z - tp.Z);
        var t0 = 0f;
        var t1 = 1f;

        if (!GjkIntersectSegmentSphere(tp, d, p, margin, ref t0, ref t1))
        {
            cast.Normal = GjkNeg(dir);
            return t + margin;
        }

        var s = SseMax(t0, t1);
        var hx = d.X * s + tp.X - p.X;
        var hy = d.Y * s + tp.Y - p.Y;
        var hz = d.Z * s + tp.Z - p.Z;
        var h = GjkNormalizeOrZero(new Vector3(hx, hy, hz), hx * hx + hy * hy + hz * hz);
        cast.Normal = GjkNeg(h);
        var lengthD = MathF.Sqrt(d.Z * d.Z + d.Y * d.Y + d.X * d.X);

        return GjkFinalizeRefineVertex(ref cast, p, SseMax(lengthD * s, t), margin, h);
    }

    // Edge pA-pB with outward normal n: searches for a support point beyond the edge, and finishes as a face if found
    private static float GjkFinalizeRefineEdge(ref GjkCast cast, Vector3 pA, Vector3 pB, float t, float margin, Vector3 n)
    {
        var found = false;
        var dA0 = n.Z * pA.Z + n.Y * pA.Y + n.X * pA.X;
        var d = n;
        var ex = pA.X - pB.X;
        var ey = pA.Y - pB.Y;
        var ez = pA.Z - pB.Z;
        var best = Vector3.Zero;
        var dir = cast.Segment.Direction;
        var tx = dir.X * t;
        var ty = dir.Y * t;
        var tz = dir.Z * t;
        var dCurrent = dA0;

        for (var iteration = 0; iteration < GjkFeatureMaxIterations; iteration++)
        {
            var w = GjkCastSupport(cast, d);

            if (1e-4f >= d.Z * w.Z + d.Y * w.Y + w.X * d.X - dCurrent)
            {
                break;
            }

            if ((pB.X == w.X && pB.Y == w.Y && pB.Z == w.Z) || (pA.X == w.X && pA.Y == w.Y && pA.Z == w.Z))
            {
                break;
            }

            if (n.Y * w.Y + n.Z * w.Z + n.X * w.X - dA0 >= -1e-4f)
            {
                best = w;
                found = true;
            }
            else if (found)
            {
                break;
            }

            // Rotate the search direction around the edge towards w
            var qx = w.X - pB.X;
            var qy = w.Y - pB.Y;
            var qz = w.Z - pB.Z;
            var kx = qz * ey - qy * ez;
            var ky = qx * ez - qz * ex;
            var kz = qy * ex - qx * ey;
            d = GjkNormalizeOrZero(new Vector3(kx, ky, kz), ky * ky + kx * kx + kz * kz);
            dCurrent = pB.Z * d.Z + pB.Y * d.Y + d.X * pB.X;

            if (dCurrent > tz * d.Z + ty * d.Y + tx * d.X)
            {
                dCurrent = -dCurrent;
                d.Y *= -1f;
                d.Z *= -1f;
                d.X *= -1f;
            }

            if (dA0 > n.Z * w.Z + n.Y * w.Y + n.X * w.X)
            {
                break;
            }
        }

        if (!found)
        {
            return t;
        }

        var local = new GjkSimplex { Count = 3 };
        local.Points[0] = pB;
        local.Points[1] = pA;
        local.Points[2] = best;

        var ax = best.X - pB.X;
        var ay = best.Y - pB.Y;
        var az = best.Z - pB.Z;
        var bx = pA.X - pB.X;
        var by = pA.Y - pB.Y;
        var bz = pA.Z - pB.Z;
        var ny = ax * bz - az * bx;
        var nx = az * by - ay * bz;
        var nz = ay * bx - ax * by;
        var lhs = pB.Z * nz + pB.Y * ny + nx * pB.X;
        var rhs = t * dir.Z * nz + t * dir.Y * ny + t * dir.X * nx;

        if (lhs > rhs)
        {
            local.Points[2] = pA;
            local.Points[1] = best;
        }

        return GjkFinalizeFace(ref cast, local, t, margin, top: false);
    }

    // Segment p0-p1: casts a ray back from (|p1 - p0| + margin + t) * dir to t * dir against the capsule (p0, p1, margin)
    private static float GjkFinalizeEdge(ref GjkCast cast, in GjkSimplex simplex, float t, float margin, bool top)
    {
        var p0 = simplex.Points[0];
        var p1 = simplex.Points[1];
        var dir = cast.Segment.Direction;
        var ex = p1.X - p0.X;
        var ey = p1.Y - p0.Y;
        var ez = p1.Z - p0.Z;
        var length = MathF.Sqrt(ez * ez + ey * ey + ex * ex);
        var reach = length + margin;
        var reachT = reach + t;

        var hit = new GjkRayHit { Fraction = 1f };
        var start = new Vector3(dir.X * reachT, dir.Y * reachT, dir.Z * reachT);
        var delta = new Vector3(dir.X * t - start.X, dir.Y * t - start.Y, dir.Z * t - start.Z);
        GjkCastRayCapsule(ref hit, start, delta, p0, p1, margin);

        var f = hit.StartSolid ? 0f : hit.Fraction;
        f = 0f > f ? 0f : SseMin(1f, f);

        if (!(1f > f))
        {
            cast.Normal = GjkNeg(dir);
            return t;
        }

        cast.Normal = GjkNeg(hit.Normal);
        var tHit = (1f - f) * reach + t;
        var inverse = 1f / length;
        var ez1 = ez * inverse;
        var ex1 = ex * inverse;
        var ey1 = ey * inverse;
        var projectionHit = ez1 * hit.Point.Z + ey1 * hit.Point.Y + ex1 * hit.Point.X;

        if (ez1 * p0.Z + ey1 * p0.Y + ex1 * p0.X > projectionHit)
        {
            if (!top)
            {
                return t;
            }

            return GjkFinalizeRefineVertex(ref cast, p0, tHit, margin, hit.Normal);
        }

        if (projectionHit > ey1 * p1.Y + ez1 * p1.Z + ex1 * p1.X)
        {
            return GjkFinalizeRefineVertex(ref cast, p1, tHit, margin, hit.Normal);
        }

        // Interior of the edge: n = (dir x e) x e, oriented along dir
        var cz = ey * dir.X - dir.Y * ex;
        var cx = dir.Y * ez - dir.Z * ey;
        var cy = dir.Z * ex - ez * dir.X;
        var nx = cy * ez - ey * cz;
        var nz = ey * cx - cy * ex;
        var ny = cz * ex - cx * ez;
        var n = GjkNormalizeOrZero(new Vector3(nx, ny, nz), ny * ny + nx * nx + nz * nz);
        var dn = dir.Z * n.Z + dir.Y * n.Y + n.X * dir.X;

        if (dn == 0f)
        {
            return tHit;
        }

        if (0f > dn)
        {
            (p0, p1) = (p1, p0);
            n = GjkNeg(n);
        }

        return GjkFinalizeRefineEdge(ref cast, p0, p1, tHit, margin, n);
    }

    // Edge pA-pB of a face: ray from tp back to the origin against the capsule (pA, pB, margin)
    private static float GjkFinalizeFaceEdge(ref GjkCast cast, Vector3 pA, Vector3 pB, Vector3 tp, float t, float margin, Vector3 edge)
    {
        var hit = new GjkRayHit { Fraction = 1f };
        GjkCastRayCapsule(ref hit, tp, GjkNeg(tp), pA, pB, margin);

        var f = hit.StartSolid ? 0f : hit.Fraction;
        f = 0f > f ? 0f : SseMin(1f, f);

        if (!(1f > f))
        {
            return t;
        }

        var tNew = MathF.Sqrt(tp.Y * tp.Y + tp.Z * tp.Z + tp.X * tp.X) * (1f - f);

        if (t >= tNew)
        {
            return t;
        }

        cast.Normal = GjkNeg(hit.Normal);
        var e = edge;
        GjkNormalizeGuarded(ref e, e.Z * e.Z + e.Y * e.Y + e.X * e.X);
        var hitProjection = hit.Point.Z * e.Z + hit.Point.Y * e.Y + hit.Point.X * e.X;

        if (pA.Y * e.Y + e.Z * pA.Z + pA.X * e.X > hitProjection)
        {
            return GjkFinalizeRefineVertex(ref cast, pA, tNew, margin, hit.Normal);
        }

        if (hitProjection > pB.Y * e.Y + e.Z * pB.Z + pB.X * e.X)
        {
            return GjkFinalizeRefineVertex(ref cast, pB, tNew, margin, hit.Normal);
        }

        return GjkFinalizeRefineEdge(ref cast, pA, pB, tNew, margin, hit.Normal);
    }

    // Triangle p0 p1 p2: distance to the face plane offset by margin, unless the ray leaves through one of its rounded edges
    private static float GjkFinalizeFace(ref GjkCast cast, in GjkSimplex simplex, float t, float margin, bool top)
    {
        var p0 = simplex.Points[0];
        var p1 = simplex.Points[1];
        var p2 = simplex.Points[2];
        var segmentLength = cast.Segment.Length;
        var dir = cast.Segment.Direction;

        var e1 = new Vector3(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
        var e2 = new Vector3(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
        var e3 = new Vector3(p0.X - p2.X, p0.Y - p2.Y, p0.Z - p2.Z);
        var tx = dir.X * t;
        var ty = dir.Y * t;
        var tz = dir.Z * t;

        // Face normal e3 x e1, or e1 x e2 if that degenerates
        var n = new Vector3(e3.Y * e1.Z - e3.Z * e1.Y, e3.Z * e1.X - e3.X * e1.Z, e3.X * e1.Y - e3.Y * e1.X);
        GjkNormalizeGuarded(ref n, n.Z * n.Z + n.Y * n.Y + n.X * n.X);

        if (0.5f > n.X * n.X + n.Y * n.Y + n.Z * n.Z)
        {
            n = new Vector3(e2.Z * e1.Y - e2.Y * e1.Z, e2.X * e1.Z - e1.X * e2.Z, e1.X * e2.Y - e2.X * e1.Y);
            GjkNormalizeGuarded(ref n, n.Y * n.Y + n.Z * n.Z + n.X * n.X);
        }

        var deferredNormal = false;
        var pn = p0.Z * n.Z + p0.Y * n.Y + n.X * p0.X;
        var dn = n.Y * dir.Y + n.Z * dir.Z + n.X * dir.X;
        var tFace = t;
        var usePlane = false;

        if (MathF.Abs(dn) > 0f)
        {
            var tPlane = (0f > dn ? pn - margin : pn + margin) / dn;

            // A nearly parallel plane that is hit far beyond the sweep is not trusted
            usePlane = !(tPlane > segmentLength + segmentLength) || 0.001f < MathF.Abs(dn);

            if (usePlane)
            {
                tFace = tPlane;

                if (top)
                {
                    cast.Normal = 0f > dn ? n : GjkNeg(n);
                }
                else
                {
                    deferredNormal = true;
                }
            }
        }

        var diff = tFace - t;

        if (!usePlane || 0f > diff)
        {
            var d0 = p0.Z * dir.Z + p0.Y * dir.Y + p0.X * dir.X;
            var d1 = p1.Z * dir.Z + p1.Y * dir.Y + p1.X * dir.X;
            var d2 = p2.Z * dir.Z + p2.Y * dir.Y + p2.X * dir.X;
            diff = SseMax(SseMax(SseMax(segmentLength, d0), d1), d2) + margin - t;
        }

        tFace = SseMax(tFace, t);

        var v = SseMax(0f, diff) + t;
        var tp = new Vector3(dir.X * v, dir.Y * v, dir.Z * v);
        var edge = -1;
        var bestS = 2f;

        for (var i = 0; i < 3; i++)
        {
            var (e, p) = i switch
            {
                0 => (e1, p0),
                1 => (e2, p1),
                _ => (e3, p2),
            };

            // Outward side plane of the edge: e x n
            var side = new Vector3(n.Z * e.Y - n.Y * e.Z, e.Z * n.X - n.Z * e.X, n.Y * e.X - e.Y * n.X);
            GjkNormalizeGuarded(ref side, side.Z * side.Z + side.Y * side.Y + side.X * side.X);
            var dP = p.Z * side.Z + p.Y * side.Y + side.X * p.X;
            var dTp = tp.Z * side.Z + tp.Y * side.Y + tp.X * side.X;

            if (!(dTp > dP))
            {
                continue;
            }

            var dT = side.Z * tz + side.Y * ty + tx * side.X;
            var fraction = 0f;

            if (i == 0)
            {
                if (dP > dT)
                {
                    fraction = (dP - dT) / (dTp - dT);

                    if (!(2f > fraction))
                    {
                        continue;
                    }
                }

                edge = 0;
                bestS = SseMax(fraction, 0f);
            }
            else
            {
                if (dP > dT)
                {
                    fraction = (dP - dT) / (dTp - dT);
                }

                if (bestS > fraction)
                {
                    edge = i;
                    bestS = SseMax(fraction, 0f);
                }
            }
        }

        switch (edge)
        {
            case 0:
                return GjkFinalizeFaceEdge(ref cast, p0, p1, tp, t, margin, e1);
            case 1:
                return GjkFinalizeFaceEdge(ref cast, p1, p2, tp, t, margin, e2);
            case 2:
                return GjkFinalizeFaceEdge(ref cast, p2, p0, tp, t, margin, e3);
        }

        if (deferredNormal)
        {
            cast.Normal = 0f > dn ? n : GjkNeg(n);
        }

        return tFace;
    }

    private static float GjkFinalizeFeature(ref GjkCast cast, in GjkSimplex simplex, float t, float margin)
    {
        if (simplex.Count >= 3)
        {
            return GjkFinalizeFace(ref cast, simplex, t, margin, top: true);
        }

        if (simplex.Count == 2)
        {
            return GjkFinalizeEdge(ref cast, simplex, t, margin, top: true);
        }

        return GjkFinalizeVertex(ref cast, simplex, t, margin);
    }

    // Turns the polytope exit distance t into the final distance, normal and start solid flag. A box that starts within
    // the margin is start solid, unless it is still clear of the reduced margin max(radius, skin / 4).
    private static bool GjkCastFinalize(ref GjkCast cast, in GjkSimplex simplex, float t)
    {
        var force = false;
        var tExit = t;
        var margin = cast.Radius + cast.Skin;

        if (0f >= margin)
        {
            GjkFinalizeNormalNoMargin(ref cast, simplex);
            var dir = cast.Segment.Direction;
            var dn = cast.Normal.Y * dir.Y + dir.Z * cast.Normal.Z + dir.X * cast.Normal.X;

            if (-1e-5f > dn)
            {
                tExit = SseMin(t - cast.Skin / dn, cast.Segment.Length);
            }
            else if (dn >= 0f)
            {
                cast.Distance = cast.Segment.Length;
                return false;
            }
            else
            {
                force = true;
            }
        }

        if (margin > 0f || force)
        {
            tExit = GjkFinalizeFeature(ref cast, simplex, t, margin);
        }

        cast.Search = cast.Normal;
        var distance = cast.Segment.Length - tExit;
        cast.Distance = distance;

        if (0f > distance)
        {
            var reducedMargin = SseMax(cast.Radius, cast.Skin * 0.25f);
            cast.StartSolid = true;
            cast.Distance = 0f;
            var reducedExit = GjkFinalizeFeature(ref cast, simplex, t, reducedMargin);

            if (cast.Segment.Length > reducedExit)
            {
                cast.StartSolid = false;
            }
        }

        return cast.Segment.Length > cast.Distance;
    }

    // GJK found a support point behind the search direction by less than radius + skin: the sweep misses the shape
    // itself but may pass within the margin. Keeps iterating to tell a near miss from a grazing hit.
    private static bool GjkCastMargin(ref GjkCast cast, ref GjkSimplex simplex, float separation, Vector3 firstPoint)
    {
        var near = false;
        var radius = cast.Radius;
        var margin = radius + cast.Skin;
        var maxSeparation = separation;
        var finalSeparation = separation;

        if (separation > 0f)
        {
            var w = firstPoint;

            if (!GjkSimplexContains(simplex, w))
            {
                for (var iteration = 0; ;)
                {
                    var previous = w;
                    GjkSimplexAddPoint(ref simplex, w, ref cast.Search);
                    var d = GjkNeg(cast.Search);
                    d = GjkNormalizeOrZero(d, d.Y * d.Y + d.X * d.X + d.Z * d.Z);
                    w = GjkCastSupport(cast, d);
                    var s = -(d.Z * w.Z + d.Y * w.Y + d.X * w.X);
                    maxSeparation = SseMax(maxSeparation, s);

                    if (s >= radius)
                    {
                        near = true;

                        if (s >= margin || 0f >= cast.Segment.Length)
                        {
                            return false;
                        }
                    }

                    var previousSeparation = -(previous.Z * d.Z + previous.Y * d.Y + previous.X * d.X);

                    if (1e-4f > MathF.Abs(s - previousSeparation) || GjkSimplexContains(simplex, w) || ++iteration >= GjkMarginMaxIterations)
                    {
                        break;
                    }
                }

                finalSeparation = maxSeparation;
            }
        }

        var nearMiss = near || finalSeparation >= radius;

        if (simplex.Count == 4)
        {
            cast.InMargin = false;

            if (cast.Segment.Length != 0f
                && GjkCastPortalExit(ref cast, ref simplex, out var t)
                && cast.Segment.Length > t)
            {
                GjkCastFinalize(ref cast, simplex, t);
                return true;
            }

            if (!nearMiss)
            {
                cast.StartSolid = true;
                cast.Distance = 0f;
            }

            return true;
        }

        var n = cast.Search;
        GjkNormalizeGuarded(ref n, n.Z * n.Z + n.Y * n.Y + n.X * n.X);

        if (cast.InMargin)
        {
            var p0 = simplex.Points[0];

            if (p0.Z * n.Z + p0.Y * n.Y + n.X * p0.X >= 0f)
            {
                n = GjkNeg(n);
            }
        }

        var dir = cast.Segment.Direction;

        if (nearMiss)
        {
            if (0f >= cast.Segment.Length || 0f >= dir.Z * n.Z + dir.Y * n.Y + dir.X * n.X)
            {
                return false;
            }
        }
        else
        {
            var dd = dir.Z * n.Z + dir.Y * n.Y + dir.X * n.X;

            if (!(-0.001f < dd))
            {
                // Moving into it while already within the radius
                cast.StartSolid = true;
                cast.Distance = 0f;
                cast.Normal = dir;
                return true;
            }
        }

        if (cast.Segment.Length > 0f)
        {
            var result = GjkCastFinalize(ref cast, simplex, 0f);

            if (nearMiss)
            {
                cast.StartSolid = false;
            }

            return result;
        }

        cast.Distance = 0f;
        cast.Fraction = 0f;
        cast.StartSolid = true;
        cast.Normal = dir;
        return true;
    }

    // GJK on C. Returns false for no hit; otherwise Distance, Normal and StartSolid are set.
    private static bool GjkCastSolve(ref GjkCast cast)
    {
        var simplex = default(GjkSimplex);

        var vx = cast.Search.X;
        var vy = cast.Search.Y;
        var vz = cast.Search.Z;

        if (GjkFltMinNormal > vx * vx + vy * vy + vz * vz)
        {
            cast.Search = Vector3.UnitX;
            vx = 1f;
            vy = 0f;
            vz = 0f;
        }

        var d = new Vector3(-vx, -vy, -vz);
        d = GjkNormalizeOrZero(d, d.Y * d.Y + d.X * d.X + d.Z * d.Z);

        for (var iteration = 0; ;)
        {
            var w = GjkCastSupport(cast, d);
            var dw = d.Z * w.Z + d.Y * w.Y + w.X * d.X;

            if (GjkEpsilonSqr > w.Y * w.Y + w.X * w.X + w.Z * w.Z)
            {
                // Exactly touching: shorten the sweep a little once and start over, if moving along d
                ref var segment = ref cast.Segment;

                if (cast.Shrunk || !(segment.Delta.Z * d.Z + segment.Delta.Y * d.Y + segment.Delta.X * d.X > 0f))
                {
                    return StartSolid(ref cast);
                }

                var length = SseMax(segment.Length - cast.Skin * 0.05f, 0f);
                segment.Length = length;
                segment.End.X = length * segment.Direction.X + segment.Start.X;
                segment.End.Y = length * segment.Direction.Y + segment.Start.Y;
                segment.End.Z = length * segment.Direction.Z + segment.Start.Z;
                cast.Shrunk = true;

                return GjkCastSolve(ref cast);
            }

            if (0f > dw)
            {
                if (!(cast.Radius + cast.Skin > MathF.Abs(dw)))
                {
                    return false;
                }

                if (!(cast.Segment.Length > 0f) && !(cast.Radius > 0f))
                {
                    return false;
                }

                cast.InMargin = true;

                if (simplex.Count > 0)
                {
                    return GjkCastMargin(ref cast, ref simplex, MathF.Abs(dw), w);
                }
            }

            if (GjkSimplexContains(simplex, w))
            {
                // No progress: only a flat triangle through (almost) the origin is accepted
                if (simplex.Count != 3)
                {
                    return false;
                }

                var p0 = simplex.Points[0];
                var e2y = simplex.Points[2].Y - p0.Y;
                var e2z = simplex.Points[2].Z - p0.Z;
                var e2x = simplex.Points[2].X - p0.X;
                var e1z = simplex.Points[1].Z - p0.Z;
                var e1y = simplex.Points[1].Y - p0.Y;
                var e1x = simplex.Points[1].X - p0.X;
                var n = new Vector3(e2y * e1z - e2z * e1y, e2z * e1x - e2x * e1z, e2x * e1y - e2y * e1x);
                GjkNormalizeGuarded(ref n, n.Z * n.Z + n.Y * n.Y + n.X * n.X);
                var planeOffset = p0.Z * n.Z + p0.Y * n.Y + p0.X * n.X;

                if (!(0.001f > MathF.Abs(planeOffset)))
                {
                    return false;
                }

                d = n.Z * d.Z + n.Y * d.Y + n.X * d.X > 0f ? GjkNeg(n) : n;
                var w2 = GjkCastSupport(cast, d);

                if (!GjkSimplexContains(simplex, w2))
                {
                    simplex.Points[simplex.Count] = w2;
                    simplex.Count++;
                }

                return Portal(ref cast, ref simplex);
            }

            var stop = GjkSimplexAddPoint(ref simplex, w, ref cast.Search);
            var v = cast.Search;
            var vv = v.X * v.X + v.Y * v.Y + v.Z * v.Z;

            if (GjkEpsilonSqr > vv || stop)
            {
                break;
            }

            d = GjkNeg(GjkNormalizeOrZero(v, vv));

            if (++iteration >= GjkMaxIterations)
            {
                return false;
            }
        }

        // The origin is inside C or on its boundary
        if (cast.Segment.Length == 0f)
        {
            return StartSolid(ref cast);
        }

        return Portal(ref cast, ref simplex);

        static bool Portal(ref GjkCast cast, ref GjkSimplex simplex)
        {
            if (GjkCastPortalExit(ref cast, ref simplex, out var t) && cast.Segment.Length > t)
            {
                GjkCastFinalize(ref cast, simplex, t);
                return true;
            }

            return StartSolid(ref cast);
        }

        static bool StartSolid(ref GjkCast cast)
        {
            cast.StartSolid = true;
            cast.Distance = 0f;
            return true;
        }
    }

    private static void GjkCastFixStartSolidNormal(ref GjkCast cast)
    {
        var n = cast.Normal;

        if (0.001f > n.X * n.X + n.Y * n.Y + n.Z * n.Z)
        {
            n = GjkNeg(cast.Segment.Direction);

            if (0.001f > n.X * n.X + n.Y * n.Y + n.Z * n.Z)
            {
                n = Vector3.UnitX;
            }

            cast.Normal = n;
        }
    }

    // Sweeps longer than 512 units are first clipped to the slab in which the box can touch the shape along the
    // sweep direction, and solved as a fresh cast on that part.
    private static bool GjkCastRun(ref GjkCast cast)
    {
        ref readonly var segment = ref cast.Segment;

        if (!(segment.FullLength > GjkLongSegment))
        {
            if (!GjkCastSolve(ref cast))
            {
                return false;
            }

            cast.Fraction = cast.Segment.InverseLength * cast.Distance;

            if (cast.StartSolid)
            {
                GjkCastFixStartSolidNormal(ref cast);
            }

            return true;
        }

        var d = segment.Direction;
        GjkNormalizeGuarded(ref d, d.Z * d.Z + d.Y * d.Y + d.X * d.X);
        var saNeg = cast.Shapes.SupportBox(GjkNeg(d));
        var saPos = cast.Shapes.SupportBox(d);
        var sbNeg = cast.Shapes.SupportShape(GjkNeg(d));
        var sbPos = cast.Shapes.SupportShape(d);
        var start = segment.Start;

        var margin = cast.Skin * 8f + cast.Radius;
        var tEnter = ((sbNeg.Y - (saPos.Y + start.Y)) * d.Y + (sbNeg.Z - (saPos.Z + start.Z)) * d.Z
            + (sbNeg.X - (start.X + saPos.X)) * d.X - margin) * segment.InverseLength;
        var tExit = ((sbPos.Y - (saNeg.Y + start.Y)) * d.Y + (sbPos.Z - (start.Z + saNeg.Z)) * d.Z
            + (sbPos.X - (start.X + saNeg.X)) * d.X + margin) * segment.InverseLength;

        if (tEnter > segment.MaxFraction)
        {
            return false;
        }

        var t0 = 0f > tEnter ? 0f : SseMin(1f, tEnter);
        var t1 = 0f > tExit ? 0f : SseMin(1f, tExit);

        var subStart = new Vector3(t0 * segment.Delta.X + start.X, segment.Delta.Y * t0 + start.Y, segment.Delta.Z * t0 + start.Z);
        var subEnd = new Vector3(segment.Delta.X * t1 + start.X, segment.Delta.Y * t1 + start.Y, segment.Delta.Z * t1 + start.Z);
        var subSegment = GjkCastSegmentInit(subStart, new Vector3(subEnd.X - subStart.X, subEnd.Y - subStart.Y, subEnd.Z - subStart.Z));
        var subCast = new GjkCast(cast.Shapes, subSegment, cast.Search, cast.Radius);

        if (!GjkCastSolve(ref subCast))
        {
            return false;
        }

        cast.Fraction = subCast.Segment.InverseLength * subCast.Distance * (t1 - t0) + t0;
        cast.Normal = subCast.Normal;
        cast.StartSolid = subCast.StartSolid;

        if (subCast.StartSolid)
        {
            cast.Fraction = 0f;
            GjkCastFixStartSolidNormal(ref cast);
        }

        return true;
    }

    private static bool GjkCastExecute(ref GjkCast cast, ref ShapeCastHit hit)
    {
        if (!GjkCastRun(ref cast))
        {
            return false;
        }

        hit.Fraction = cast.Fraction;
        hit.Normal = cast.Normal;
        hit.StartSolid = cast.StartSolid;
        return true;
    }
}
