using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;

namespace ValveResourceFormat.Renderer;

internal static partial class RubikonShapeCast
{
    // Enables the test that reports a box whose centre is inside the closed mesh as start solid
    private const uint MeshFlagClosed = 1;
    private const uint MeshFlagNoInsideTest = 2;

    // The traversal stack holds one entry per tree level
    private const int MeshStackSize = 512;

    private const float MeshFloatMinNormal = 1.17549435e-38f;
    private const float MeshFloatEpsilon = 1.1920929e-7f;

    private enum MeshSatResult
    {
        Miss,
        StartSolid,
        SweepHit,
    }

    private struct MeshTriangleHit
    {
        public Vector3 Normal;
        public float Fraction;
        public bool StartSolid;
    }

    /// <summary>
    /// Sweeps a box against a triangle mesh placed by a rigid transform.
    /// </summary>
    /// <param name="mesh">The triangle mesh, in its own local space.</param>
    /// <param name="transform">Maps mesh-local space to world space, without scale.</param>
    /// <param name="start">The box centre at the start of the sweep, in world space.</param>
    /// <param name="delta">The sweep motion, in world space.</param>
    /// <param name="halfExtents">The half extents of the world axis-aligned box.</param>
    /// <param name="maxFraction">Only hits nearer than this fraction of the sweep are searched for.</param>
    /// <param name="hit">The nearest hit, or <see cref="ShapeCastHit.None"/>.</param>
    /// <returns>Whether the mesh was hit.</returns>
    public static bool CastBoxAgainstMesh(Rubikon.PhysicsMeshData mesh, in Matrix4x4 transform, Vector3 start, Vector3 delta, Vector3 halfExtents, float maxFraction, out ShapeCastHit hit)
    {
        hit = ShapeCastHit.None;

        // Only the diagonal and the translation are tested to pick the untransformed path
        if (1f == transform.M11 && 1f == transform.M22 && 1f == transform.M33
            && 0f == transform.M41 && 0f == transform.M42 && 0f == transform.M43)
        {
            return MeshCastBox(ref hit, mesh, start, delta, halfExtents, maxFraction);
        }

        return MeshCastBoxTransformed(ref hit, mesh, Vector3.One, transform, start, delta, halfExtents, maxFraction);
    }

    private static bool MeshCastBox(ref ShapeCastHit result, Rubikon.PhysicsMeshData mesh, Vector3 start, Vector3 delta, Vector3 halfExtents, float maxFraction)
    {
        var extentsWithSkin = new Vector3(Skin) + halfExtents;
        MeshSweepBounds(start, delta, maxFraction, out var sweepMin, out var sweepMax);
        var length = MathF.Sqrt(((delta.X * delta.X) + (delta.Y * delta.Y)) + (delta.Z * delta.Z));
        var direction = 0f < length ? new Vector3(delta.X / length, delta.Y / length, delta.Z / length) : Vector3.Zero;

        var nodes = mesh.PhysicsTree;
        if (nodes.Length == 0)
        {
            return false;
        }

        Span<int> stack = stackalloc int[MeshStackSize];
        var stackCount = 0;
        var hit = false;
        var best = maxFraction;
        var nodeIndex = 0;

        while (true)
        {
            ref readonly var node = ref nodes[nodeIndex];
            var nodeMax = node.Max + halfExtents;
            var nodeMin = node.Min - halfExtents;

            if (!MeshAabbDisjoint(sweepMin, sweepMax, nodeMin, nodeMax) && MeshSegmentOverlapsBox(start, delta, nodeMin, nodeMax))
            {
                if (node.Type != Mesh.NodeType.Leaf)
                {
                    var splitAxis = (int)node.Type;
                    var firstChild = nodeIndex + 1;
                    var secondChild = nodeIndex + (int)node.ChildOffset;

                    // The near child is picked from the start position alone, not from the sweep direction
                    if (nodes[firstChild].Max[splitAxis] - start[splitAxis] > 0f)
                    {
                        stack[stackCount++] = secondChild;
                        nodeIndex = firstChild;
                    }
                    else
                    {
                        stack[stackCount++] = firstChild;
                        nodeIndex = secondChild;
                    }

                    continue;
                }

                var firstTriangle = (int)node.TriangleOffset;
                var triangleEnd = firstTriangle + (int)node.ChildOffset;

                for (var triangleIndex = firstTriangle; triangleIndex < triangleEnd; triangleIndex++)
                {
                    var triangle = mesh.Triangles[triangleIndex];
                    var v0 = mesh.VertexPositions[triangle.X];
                    var v1 = mesh.VertexPositions[triangle.Y];
                    var v2 = mesh.VertexPositions[triangle.Z];

                    var triangleMax = new Vector3(
                        SseMax(SseMax(v0.X, v1.X), v2.X),
                        SseMax(SseMax(v0.Y, v1.Y), v2.Y),
                        SseMax(SseMax(v0.Z, v1.Z), v2.Z)) + extentsWithSkin;
                    var triangleMin = new Vector3(
                        SseMin(SseMin(v0.X, v1.X), v2.X),
                        SseMin(SseMin(v0.Y, v1.Y), v2.Y),
                        SseMin(SseMin(v0.Z, v1.Z), v2.Z)) - extentsWithSkin;

                    if (MeshAabbDisjoint(sweepMin, sweepMax, triangleMin, triangleMax) || !MeshSegmentOverlapsBox(start, delta, triangleMin, triangleMax))
                    {
                        continue;
                    }

                    var triangleHit = default(MeshTriangleHit);
                    var triangleWasHit = TryGetWingVertices(mesh, triangleIndex, out var w0, out var w1, out var w2)
                        ? SweptBoxTriangleWings(start, direction, halfExtents, length, v0, v1, v2, w0, w1, w2, ref triangleHit)
                        : SweptBoxTriangle(start, direction, halfExtents, length, v0, v1, v2, ref triangleHit);

                    if (!triangleWasHit || !(best > triangleHit.Fraction || (triangleHit.StartSolid && !result.StartSolid)))
                    {
                        continue;
                    }

                    var fraction = triangleHit.Fraction;
                    hit = true;
                    best = fraction;
                    result.Normal = triangleHit.Normal;
                    result.TriangleIndex = triangleIndex;
                    result.StartSolid = triangleHit.StartSolid;
                    result.Fraction = fraction;

                    if (fraction == 0f && triangleHit.StartSolid)
                    {
                        return hit;
                    }

                    MeshSweepBounds(start, delta, fraction, out sweepMin, out sweepMax);
                }
            }

            if (stackCount == 0)
            {
                break;
            }

            nodeIndex = stack[--stackCount];
        }

        if ((mesh.Flags & MeshFlagClosed) != 0 && !(best == 0f) && MeshIsPointInside(mesh, Vector3.One, start))
        {
            SetInsideMeshHit(ref result, delta);
            return true;
        }

        return hit;
    }

    /// <remarks>
    /// The tree is walked in the mesh's scaled local frame, but every triangle is moved to world space
    /// and swept with the original world box. The world sweep bounds only shrink when a leaf is entered.
    /// </remarks>
    private static bool MeshCastBoxTransformed(ref ShapeCastHit result, Rubikon.PhysicsMeshData mesh, Vector3 scale, in Matrix4x4 transform,
        Vector3 start, Vector3 delta, Vector3 halfExtents, float maxFraction)
    {
        // Rows 1 to 3 are the images of the local axes, so a local coordinate is the dot product with its row
        var negTx = -transform.M41;
        var negTy = -transform.M42;
        var negTz = -transform.M43;

        var localStart = new Vector3(
            (((start.Y * transform.M12) + (start.X * transform.M11)) + (transform.M13 * start.Z)) + (((transform.M12 * negTy) + (transform.M11 * negTx)) + (transform.M13 * negTz)),
            (((negTx * transform.M21) + (transform.M22 * negTy)) + (transform.M23 * negTz)) + ((transform.M23 * start.Z) + ((start.Y * transform.M22) + (start.X * transform.M21))),
            (((negTx * transform.M31) + (transform.M32 * negTy)) + (transform.M33 * negTz)) + ((transform.M33 * start.Z) + ((start.Y * transform.M32) + (transform.M31 * start.X))));
        var localDelta = new Vector3(
            ((transform.M11 * delta.X) + (delta.Y * transform.M12)) + (transform.M13 * delta.Z),
            (transform.M23 * delta.Z) + ((delta.Y * transform.M22) + (transform.M21 * delta.X)),
            (transform.M33 * delta.Z) + ((delta.Y * transform.M32) + (transform.M31 * delta.X)));
        var localExtents = new Vector3(
            ((MathF.Abs(transform.M11) * halfExtents.X) + (MathF.Abs(transform.M12) * halfExtents.Y)) + (MathF.Abs(transform.M13) * halfExtents.Z),
            (MathF.Abs(transform.M23) * halfExtents.Z) + ((MathF.Abs(transform.M22) * halfExtents.Y) + (MathF.Abs(transform.M21) * halfExtents.X)),
            (MathF.Abs(transform.M33) * halfExtents.Z) + ((MathF.Abs(transform.M32) * halfExtents.Y) + (MathF.Abs(transform.M31) * halfExtents.X)));

        MeshSweepBounds(localStart, localDelta, maxFraction, out var localMin, out var localMax);
        var unscaledLocalStart = localStart / scale;

        var length = MathF.Sqrt(((delta.X * delta.X) + (delta.Y * delta.Y)) + (delta.Z * delta.Z));
        var direction = 0f < length ? new Vector3(delta.X / length, delta.Y / length, delta.Z / length) : Vector3.Zero;
        MeshSweepBounds(start, delta, maxFraction, out var worldMin, out var worldMax);

        var axisX = scale.X * new Vector3(transform.M11, transform.M12, transform.M13);
        var axisY = scale.Y * new Vector3(transform.M21, transform.M22, transform.M23);
        var axisZ = scale.Z * new Vector3(transform.M31, transform.M32, transform.M33);
        var origin = transform.Translation;

        var nodes = mesh.PhysicsTree;
        if (nodes.Length == 0)
        {
            return false;
        }

        Span<int> stack = stackalloc int[MeshStackSize];
        var stackCount = 0;
        var hit = false;
        var best = maxFraction;
        var worldBoundsFraction = maxFraction;
        var nodeIndex = 0;

        while (true)
        {
            ref readonly var node = ref nodes[nodeIndex];
            var nodeMax = (node.Max * scale) + localExtents;
            var nodeMin = (node.Min * scale) - localExtents;

            if (!MeshAabbDisjoint(localMin, localMax, nodeMin, nodeMax) && MeshSegmentOverlapsBox(localStart, localDelta, nodeMin, nodeMax))
            {
                if (node.Type != Mesh.NodeType.Leaf)
                {
                    var splitAxis = (int)node.Type;
                    var firstChild = nodeIndex + 1;
                    var secondChild = nodeIndex + (int)node.ChildOffset;

                    if (nodes[firstChild].Max[splitAxis] - unscaledLocalStart[splitAxis] > 0f)
                    {
                        stack[stackCount++] = secondChild;
                        nodeIndex = firstChild;
                    }
                    else
                    {
                        stack[stackCount++] = firstChild;
                        nodeIndex = secondChild;
                    }

                    continue;
                }

                if (!(worldBoundsFraction == best))
                {
                    worldBoundsFraction = best;
                    MeshSweepBounds(start, delta, best, out worldMin, out worldMax);
                }

                var firstTriangle = (int)node.TriangleOffset;
                var triangleEnd = firstTriangle + (int)node.ChildOffset;

                for (var triangleIndex = firstTriangle; triangleIndex < triangleEnd; triangleIndex++)
                {
                    var triangle = mesh.Triangles[triangleIndex];
                    var v0 = MeshTransformPoint(axisX, axisY, axisZ, origin, mesh.VertexPositions[triangle.X]);
                    var v1 = MeshTransformPoint(axisX, axisY, axisZ, origin, mesh.VertexPositions[triangle.Y]);
                    var v2 = MeshTransformPoint(axisX, axisY, axisZ, origin, mesh.VertexPositions[triangle.Z]);

                    var triangleMin = (new Vector3(
                        SseMin(v0.X, SseMin(v1.X, v2.X)),
                        SseMin(v0.Y, SseMin(v1.Y, v2.Y)),
                        SseMin(v0.Z, SseMin(v1.Z, v2.Z))) - new Vector3(Skin)) - halfExtents;
                    var triangleMax = (new Vector3(
                        SseMax(v0.X, SseMax(v1.X, v2.X)),
                        SseMax(v0.Y, SseMax(v1.Y, v2.Y)),
                        SseMax(v0.Z, SseMax(v1.Z, v2.Z))) + new Vector3(Skin)) + halfExtents;

                    if (MeshAabbDisjoint(worldMin, worldMax, triangleMin, triangleMax))
                    {
                        continue;
                    }

                    var triangleHit = default(MeshTriangleHit);
                    bool triangleWasHit;

                    if (TryGetWingVertices(mesh, triangleIndex, out var w0, out var w1, out var w2))
                    {
                        w0 = MeshTransformPoint(axisX, axisY, axisZ, origin, w0);
                        w1 = MeshTransformPoint(axisX, axisY, axisZ, origin, w1);
                        w2 = MeshTransformPoint(axisX, axisY, axisZ, origin, w2);
                        triangleWasHit = SweptBoxTriangleWings(start, direction, halfExtents, length, v0, v1, v2, w0, w1, w2, ref triangleHit);
                    }
                    else
                    {
                        triangleWasHit = SweptBoxTriangle(start, direction, halfExtents, length, v0, v1, v2, ref triangleHit);
                    }

                    if (!triangleWasHit || !(best > triangleHit.Fraction || (triangleHit.StartSolid && !result.StartSolid)))
                    {
                        continue;
                    }

                    var fraction = triangleHit.Fraction;
                    best = fraction;
                    hit = true;
                    result.Fraction = fraction;
                    result.Normal = triangleHit.Normal;
                    result.TriangleIndex = triangleIndex;
                    result.StartSolid = triangleHit.StartSolid;

                    if (fraction == 0f && triangleHit.StartSolid)
                    {
                        return hit;
                    }

                    MeshSweepBounds(localStart, localDelta, fraction, out localMin, out localMax);
                }
            }

            if (stackCount == 0)
            {
                break;
            }

            nodeIndex = stack[--stackCount];
        }

        if ((mesh.Flags & MeshFlagClosed) != 0 && MeshIsPointInsideTransformed(mesh, scale, start, best, transform))
        {
            SetInsideMeshHit(ref result, delta);
            return true;
        }

        return hit;
    }

    private static Vector3 MeshTransformPoint(Vector3 axisX, Vector3 axisY, Vector3 axisZ, Vector3 origin, Vector3 point)
        => origin + ((axisZ * point.Z) + ((axisY * point.Y) + (axisX * point.X)));

    private static bool TryGetWingVertices(Rubikon.PhysicsMeshData mesh, int triangleIndex, out Vector3 w0, out Vector3 w1, out Vector3 w2)
    {
        if (mesh.Wings.Length == 0)
        {
            w0 = w1 = w2 = default;
            return false;
        }

        var wing = mesh.Wings[triangleIndex];
        if (wing.X < 0 || wing.Y < 0 || wing.Z < 0)
        {
            w0 = w1 = w2 = default;
            return false;
        }

        w0 = mesh.VertexPositions[wing.X];
        w1 = mesh.VertexPositions[wing.Y];
        w2 = mesh.VertexPositions[wing.Z];
        return true;
    }

    private static void SetInsideMeshHit(ref ShapeCastHit result, Vector3 delta)
    {
        var direction = MeshNormalizedOrDefault(delta, Vector3.UnitZ);
        result.StartSolid = true;
        result.Fraction = 0f;
        result.Normal = new Vector3(-direction.X, -direction.Y, -direction.Z);
        result.TriangleIndex = -1;
    }

    private static bool SweptBoxTriangle(Vector3 start, Vector3 direction, Vector3 halfExtents, float length,
        Vector3 v0, Vector3 v1, Vector3 v2, ref MeshTriangleHit hit)
    {
        var result = SweptBoxTriangleSat(start, direction, halfExtents, length, v0, v1, v2, wingVariant: false, ref hit, out _, out _, out var fraction);
        if (result == MeshSatResult.Miss)
        {
            return false;
        }

        if (result == MeshSatResult.SweepHit)
        {
            hit.Fraction = fraction;
            hit.StartSolid = false;
        }

        return true;
    }

    /// <remarks>
    /// When the hit axis comes from an edge, its normal is clamped between this face's normal and the
    /// neighbour's face normal across that edge, so boxes sliding over a seam between flat or convex
    /// triangles are not stopped by edge normals that point sideways or out of the surface.
    /// </remarks>
    private static bool SweptBoxTriangleWings(Vector3 start, Vector3 direction, Vector3 halfExtents, float length,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 w0, Vector3 w1, Vector3 w2, ref MeshTriangleHit hit)
    {
        var result = SweptBoxTriangleSat(start, direction, halfExtents, length, v0, v1, v2, wingVariant: true, ref hit, out var faceNormal, out var axis, out var fraction);
        if (result == MeshSatResult.Miss)
        {
            return false;
        }

        if (result == MeshSatResult.StartSolid)
        {
            return true;
        }

        if (axis > 3)
        {
            var n = hit.Normal;

            // Lane 3 repeats edge 0
            var lane = axis & 3;
            if (lane > 2)
            {
                lane = 0;
            }

            var (edgeStart, opposite, edgeEnd, wing) = lane switch
            {
                0 => (v0, v2, v1, w0),
                1 => (v1, v0, v2, w1),
                _ => (v2, v1, v0, w2),
            };

            var dotOpposite = ((n.Y * opposite.Y) + (n.X * opposite.X)) + (n.Z * opposite.Z);
            var dotEdge = (((n.Y * edgeStart.Y) + (n.X * edgeStart.X)) + (n.Z * edgeStart.Z)) + Skin;

            // Only when the triangle lies behind the edge along the normal
            if (dotEdge > dotOpposite)
            {
                var cosFace = (faceNormal.X * n.X) + ((faceNormal.Y * n.Y) + (faceNormal.Z * n.Z));

                var u = edgeStart - edgeEnd;
                var w = edgeEnd - wing;
                var c = new Vector3(
                    (u.Z * w.Y) - (u.Y * w.Z),
                    (u.X * w.Z) - (u.Z * w.X),
                    (u.Y * w.X) - (u.X * w.Y));
                var cLength = MathF.Sqrt(((c.X * c.X) + (c.Y * c.Y)) + (c.Z * c.Z));
                var neighbourNormal = 0f < cLength ? new Vector3(c.X / cLength, c.Y / cLength, c.Z / cLength) : Vector3.Zero;

                var cosNeighbour = ((faceNormal.X * neighbourNormal.X) + ((faceNormal.Y * neighbourNormal.Y) + (faceNormal.Z * neighbourNormal.Z))) - 0.001f;
                if (cosNeighbour > cosFace)
                {
                    hit.Normal = neighbourNormal;
                }
                else
                {
                    var dotNeighbour = ((neighbourNormal.Z * n.Z) + (neighbourNormal.Y * n.Y)) + (n.X * neighbourNormal.X);
                    if (cosNeighbour > dotNeighbour)
                    {
                        hit.Normal = faceNormal;
                    }
                }
            }
        }

        hit.Fraction = fraction;
        hit.StartSolid = false;
        return true;
    }

    /// <summary>
    /// Swept separating axis test of a box against one triangle over 20 axes, in 5 groups of 4 lanes:
    /// the face normal, world X, Y and Z, then Z, Y and X crossed with each edge, then each edge crossed
    /// with the sweep direction. Lane 3 of the edge groups repeats edge 0.
    /// </summary>
    /// <remarks>
    /// Only the face normal and the edge cross direction axes are unit length, the skin is scaled by the
    /// axis length instead. A box that starts outside and never gets deeper than 7/8 of the skin misses.
    /// For a sweep hit only the normal is written to <paramref name="hit"/>, with the axis and fraction returned.
    /// </remarks>
    private static MeshSatResult SweptBoxTriangleSat(Vector3 start, Vector3 direction, Vector3 halfExtents, float length,
        Vector3 v0, Vector3 v1, Vector3 v2, bool wingVariant, ref MeshTriangleHit hit, out Vector3 faceNormal, out int axis, out float fraction)
    {
        var p = start - v0;
        var e2 = v2 - v0;
        var e1 = v1 - v0;
        var negE2 = Vector3.Zero - e2;
        var f = e2 - e1;

        // Both spellings of the cross product only differ in the sign of an exact zero
        Vector3 n;
        if (!wingVariant)
        {
            n = new Vector3(
                (e1.Z * negE2.Y) - (e1.Y * negE2.Z),
                (e1.X * negE2.Z) - (e1.Z * negE2.X),
                (e1.Y * negE2.X) - (e1.X * negE2.Y));
        }
        else
        {
            var e2Again = Vector3.Zero - negE2;
            n = new Vector3(
                (e2Again.Z * e1.Y) - (e2Again.Y * e1.Z),
                (e2Again.X * e1.Z) - (e2Again.Z * e1.X),
                (e2Again.Y * e1.X) - (e2Again.X * e1.Y));
        }

        var nLength = MathF.Sqrt(((n.X * n.X) + (n.Y * n.Y)) + (n.Z * n.Z));
        faceNormal = 0f < nLength ? new Vector3(n.X / nLength, n.Y / nLength, n.Z / nLength) : Vector3.Zero;

        var motion = length * direction;
        var pEnd = motion + p;

        hit.Normal = Vector3.Zero;
        hit.Fraction = 1f;
        hit.StartSolid = false;
        axis = 0;
        fraction = 0f;

        var allOverlapAtStart = true;
        Span<float> bestEnter = [float.MinValue, float.MinValue, float.MinValue, float.MinValue];
        Span<float> bestExit = [float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue];
        Span<int> bestAxis = [0, 0, 0, 0];
        Span<int> touchAxis = [1000, 1000, 1000, 1000];

        // Any lane that misses ends the test, so the lanes of a group can be evaluated one at a time
        for (var group = 0; group < 5; group++)
        {
            for (var lane = 0; lane < 4; lane++)
            {
                var index = group * 4 + lane;
                var a = GetSatAxis(index, faceNormal, direction, e1, f, negE2);

                var s0 = (p.Z * a.Z) + ((p.Y * a.Y) + (p.X * a.X));
                var s1 = (pEnd.Z * a.Z) + ((pEnd.Y * a.Y) + (pEnd.X * a.X));
                var q1 = (e1.Z * a.Z) + ((e1.Y * a.Y) + (e1.X * a.X));
                var q2 = (e2.Z * a.Z) + ((e2.Y * a.Y) + (e2.X * a.X));
                var radius = MathF.Abs(halfExtents.Z * a.Z) + (MathF.Abs(halfExtents.Y * a.Y) + MathF.Abs(halfExtents.X * a.X));
                var axisLength = MathF.Sqrt(((a.Y * a.Y) + (a.Z * a.Z)) + (a.X * a.X));
                var skin = axisLength * Skin;

                // The triangle interval, with v0 at 0, grown by the box radius
                var lo = SseMin(0f, SseMin(q1, q2)) - radius;
                var hi = SseMax(0f, SseMax(q1, q2)) + radius;

                var ds = s1 - s0;
                var inverse = MeshGuardedReciprocal(ds);

                var below = s0 < lo;
                var above = hi < s0;

                if (below || above)
                {
                    allOverlapAtStart = false;

                    var graze = skin * 0.875f;
                    var loGraze = lo - graze;
                    var hiGraze = graze + hi;

                    if ((below && s1 <= loGraze) || (above && s0 <= s1) || (above && hiGraze <= s1) || (below && s1 <= s0))
                    {
                        return MeshSatResult.Miss;
                    }

                    // Starts and ends outside the interval without the skin
                    var touching = (below && s1 <= lo) || (above && hi <= s1);
                    var candidate = touching ? index : touchAxis[lane];
                    touchAxis[lane] = touchAxis[lane] < candidate ? touchAxis[lane] : candidate;
                }

                var hiSkin = hi + skin;
                var loSkin = lo - skin;
                var moving = MeshFloatEpsilon < MathF.Abs(ds);
                var tHi = inverse * (hiSkin - s0);
                var tLo = inverse * (loSkin - s0);
                var enter = moving ? SseMin(tLo, tHi) : float.MinValue;
                var exit = moving ? SseMax(tLo, tHi) : float.MaxValue;

                if (exit < enter)
                {
                    return MeshSatResult.Miss;
                }

                if (bestEnter[lane] < enter)
                {
                    bestAxis[lane] = index;
                }

                bestExit[lane] = SseMin(exit, bestExit[lane]);
                bestEnter[lane] = SseMax(enter, bestEnter[lane]);

                if (bestExit[lane] <= bestEnter[lane])
                {
                    return MeshSatResult.Miss;
                }
            }
        }

        var minExit = SseMin(SseMin(bestExit[0], bestExit[1]), SseMin(bestExit[2], bestExit[3]));

        // Ties keep the lower lane
        var axis01 = bestEnter[0] < bestEnter[1] ? bestAxis[1] : bestAxis[0];
        var enter01 = SseMax(bestEnter[0], bestEnter[1]);
        var axis23 = bestEnter[2] < bestEnter[3] ? bestAxis[3] : bestAxis[2];
        var enter23 = SseMax(bestEnter[2], bestEnter[3]);
        var hitAxis = enter01 < enter23 ? axis23 : axis01;
        var maxEnter = SseMax(enter01, enter23);

        var enterNegative = 0f > maxEnter;
        var t = SseMax(0f, maxEnter);

        if (!(t < minExit) || !(t < 1f))
        {
            return MeshSatResult.Miss;
        }

        if (length == 0f && !allOverlapAtStart)
        {
            return MeshSatResult.Miss;
        }

        if (allOverlapAtStart)
        {
            hit.Normal = length > 0f ? new Vector3(0f - direction.X, 0f - direction.Y, 0f - direction.Z) : Vector3.UnitX;
            hit.Fraction = 0f;
            hit.StartSolid = true;
            return MeshSatResult.StartSolid;
        }

        // Started inside the skin on every separating axis: the lowest touching axis gives the normal
        if (enterNegative)
        {
            var touch01 = touchAxis[0] < touchAxis[1] ? touchAxis[0] : touchAxis[1];
            var touch23 = touchAxis[2] < touchAxis[3] ? touchAxis[2] : touchAxis[3];
            var touch = touch01 < touch23 ? touch01 : touch23;
            if ((uint)touch < 20)
            {
                hitAxis = touch;
            }
        }

        var hitAxisVector = GetSatAxis(hitAxis, faceNormal, direction, e1, f, negE2);
        var lengthSquared = ((hitAxisVector.Y * hitAxisVector.Y) + (hitAxisVector.X * hitAxisVector.X)) + (hitAxisVector.Z * hitAxisVector.Z);
        var normal = Vector3.Zero;
        if (lengthSquared > 1.1754944e-35f)
        {
            var inverseLength = 1f / MeshSqrt(lengthSquared);
            normal = new Vector3(hitAxisVector.X * inverseLength, hitAxisVector.Y * inverseLength, hitAxisVector.Z * inverseLength);
        }

        var dot = (motion.X * normal.X) + ((motion.Z * normal.Z) + (motion.Y * normal.Y));
        if (dot > 0f)
        {
            normal = new Vector3(normal.X * -1f, normal.Y * -1f, normal.Z * -1f);
        }

        hit.Normal = normal;
        axis = hitAxis;
        fraction = t;
        return MeshSatResult.SweepHit;
    }

    private static Vector3 GetSatAxis(int index, Vector3 faceNormal, Vector3 direction, Vector3 e1, Vector3 f, Vector3 negE2)
    {
        switch (index)
        {
            case 0:
                return faceNormal;
            case 1:
                return Vector3.UnitX;
            case 2:
                return Vector3.UnitY;
            case 3:
                return Vector3.UnitZ;
        }

        var edge = (index & 3) switch
        {
            1 => f,
            2 => negE2,
            _ => e1,
        };

        switch (index >> 2)
        {
            case 1:
                return new Vector3(0f - edge.Y, edge.X, 0f);
            case 2:
                return new Vector3(edge.Z, 0f, 0f - edge.X);
            case 3:
                return new Vector3(0f, 0f - edge.Z, edge.Y);
        }

        // A degenerate cross product gives a NaN axis, which never separates
        var cx = (direction.Z * edge.Y) - (direction.Y * edge.Z);
        var cy = (direction.X * edge.Z) - (direction.Z * edge.X);
        var cz = (direction.Y * edge.X) - (direction.X * edge.Y);
        var r = MeshReciprocalSqrt((cz * cz) + ((cy * cy) + (cx * cx)));
        return new Vector3(cx * r, cy * r, r * cz);
    }

    /// <summary>Tests the segment from <paramref name="start"/> along <paramref name="delta"/> against a box on the three delta cross axis axes.</summary>
    private static bool MeshSegmentOverlapsBox(Vector3 start, Vector3 delta, Vector3 boxMin, Vector3 boxMax)
    {
        var center = (boxMax + boxMin) * 0.5f;
        var extents = boxMax - center;
        var p = start - center;
        var absDelta = Vector3.Abs(delta);

        var lhs0 = MathF.Abs((p.Z * delta.Y) - (p.Y * delta.Z));
        var lhs1 = MathF.Abs((p.X * delta.Z) - (p.Z * delta.X));
        var lhs2 = MathF.Abs((p.Y * delta.X) - (p.X * delta.Y));
        var rhs0 = (extents.Z * absDelta.Y) + (extents.Y * absDelta.Z);
        var rhs1 = (extents.X * absDelta.Z) + (extents.Z * absDelta.X);
        var rhs2 = (extents.Y * absDelta.X) + (extents.X * absDelta.Y);
        return lhs0 - rhs0 <= 0f && lhs1 - rhs1 <= 0f && lhs2 - rhs2 <= 0f;
    }

    private static bool MeshAabbDisjoint(Vector3 sweepMin, Vector3 sweepMax, Vector3 boxMin, Vector3 boxMax)
        => sweepMax.X < boxMin.X || boxMax.X < sweepMin.X
        || sweepMax.Y < boxMin.Y || boxMax.Y < sweepMin.Y
        || sweepMax.Z < boxMin.Z || boxMax.Z < sweepMin.Z;

    private static void MeshSweepBounds(Vector3 start, Vector3 delta, float fraction, out Vector3 min, out Vector3 max)
    {
        var end = (fraction * delta) + start;
        min = new Vector3(SseMin(start.X, end.X), SseMin(start.Y, end.Y), SseMin(start.Z, end.Z));
        max = new Vector3(SseMax(start.X, end.X), SseMax(start.Y, end.Y), SseMax(start.Z, end.Z));
    }

    /// <summary>
    /// Reports whether a point is inside a closed mesh, by casting a ray to the nearest face of the mesh
    /// bounds: the point is inside when the first triangle hit faces away from the ray.
    /// </summary>
    private static bool MeshIsPointInside(Rubikon.PhysicsMeshData mesh, Vector3 scale, Vector3 point)
    {
        if ((mesh.Flags & MeshFlagNoInsideTest) != 0)
        {
            return false;
        }

        var min = scale * mesh.Min;
        var max = scale * mesh.Max;
        if (!(point.X >= min.X) || !(point.Y >= min.Y) || !(point.Z >= min.Z)
            || !(max.X >= point.X) || !(max.Y >= point.Y) || !(max.Z >= point.Z))
        {
            return false;
        }

        var dyLo = point.Y - min.Y;
        var dxHi = max.X - point.X;
        var dxLo = point.X - min.X;
        var dyHi = max.Y - point.Y;
        var dzHi = max.Z - point.Z;
        var dzLo = point.Z - min.Z;

        // The axis order of these comparisons is deliberately not symmetric
        int loAxis, hiAxis;
        float lo, hi;
        if (MathF.Abs(dyLo) > MathF.Abs(dxLo))
        {
            (loAxis, lo) = MathF.Abs(dzLo) > MathF.Abs(dxLo) ? (0, dxLo) : (2, dzLo);
        }
        else
        {
            (loAxis, lo) = MathF.Abs(dzLo) > MathF.Abs(dyLo) ? (1, dyLo) : (2, dzLo);
        }

        if (MathF.Abs(dyHi) > MathF.Abs(dxHi))
        {
            (hiAxis, hi) = MathF.Abs(dzHi) > MathF.Abs(dxHi) ? (0, dxHi) : (2, dzHi);
        }
        else
        {
            (hiAxis, hi) = MathF.Abs(dzHi) > MathF.Abs(dyHi) ? (1, dyHi) : (2, dzHi);
        }

        var rayDelta = Vector3.Zero;
        if (hi > lo)
        {
            rayDelta[loAxis] = -lo;
        }
        else
        {
            rayDelta[hiAxis] = hi;
        }

        if (!MeshCastRay(mesh, scale, point, rayDelta, 1f, out var normal))
        {
            return false;
        }

        var dot = ((rayDelta.Y * normal.Y) + (rayDelta.X * normal.X)) + (rayDelta.Z * normal.Z);
        return dot >= 0f;
    }

    private static bool MeshIsPointInsideTransformed(Rubikon.PhysicsMeshData mesh, Vector3 scale, Vector3 start, float fraction, in Matrix4x4 transform)
    {
        if (fraction == 0f)
        {
            return false;
        }

        var negTx = -transform.M41;
        var negTy = -transform.M42;
        var negTz = -transform.M43;
        var localPoint = new Vector3(
            (((negTy * transform.M12) + (negTx * transform.M11)) + (negTz * transform.M13)) + (((transform.M11 * start.X) + (start.Y * transform.M12)) + (start.Z * transform.M13)),
            (((start.Y * transform.M22) + (transform.M21 * start.X)) + (transform.M23 * start.Z)) + (((negTx * transform.M21) + (negTy * transform.M22)) + (transform.M23 * negTz)),
            (((negTx * transform.M31) + (negTy * transform.M32)) + (transform.M33 * negTz)) + (((start.Y * transform.M32) + (transform.M31 * start.X)) + (start.Z * transform.M33)));

        return MeshIsPointInside(mesh, scale, localPoint);
    }

    /// <summary>
    /// Casts a ray against the mesh for the inside test. Triangles are hit from either side, and the
    /// normal is the geometric normal of the scaled triangle, not oriented towards the ray.
    /// </summary>
    private static bool MeshCastRay(Rubikon.PhysicsMeshData mesh, Vector3 scale, Vector3 origin, Vector3 delta, float maxFraction, out Vector3 normal)
    {
        normal = Vector3.Zero;

        var nodes = mesh.PhysicsTree;
        if (nodes.Length == 0)
        {
            return false;
        }

        var o = origin / scale;
        var d = delta / scale;
        MeshSweepBounds(o, d, maxFraction, out var rayMin, out var rayMax);

        Span<int> stack = stackalloc int[MeshStackSize];
        var stackCount = 0;
        var best = maxFraction;
        var bestTriangle = -1;
        var nodeIndex = 0;

        while (true)
        {
            ref readonly var node = ref nodes[nodeIndex];

            if (!MeshAabbDisjoint(rayMin, rayMax, node.Min, node.Max) && MeshSegmentOverlapsBox(o, d, node.Min, node.Max))
            {
                if (node.Type != Mesh.NodeType.Leaf)
                {
                    var firstChild = nodeIndex + 1;
                    var secondChild = nodeIndex + (int)node.ChildOffset;

                    if (d[(int)node.Type] > 0f)
                    {
                        stack[stackCount++] = secondChild;
                        nodeIndex = firstChild;
                    }
                    else
                    {
                        stack[stackCount++] = firstChild;
                        nodeIndex = secondChild;
                    }

                    continue;
                }

                var firstTriangle = (int)node.TriangleOffset;
                var triangleEnd = firstTriangle + (int)node.ChildOffset;

                for (var triangleIndex = firstTriangle; triangleIndex < triangleEnd; triangleIndex++)
                {
                    var triangle = mesh.Triangles[triangleIndex];
                    var v0 = mesh.VertexPositions[triangle.X];
                    var v1 = mesh.VertexPositions[triangle.Y];
                    var v2 = mesh.VertexPositions[triangle.Z];

                    var e01 = v1 - v0;
                    var e20 = v0 - v2;
                    var e12 = v2 - v1;
                    var m12 = ((v2 + v1) * 0.5f) - o;
                    var m20 = ((v2 + v0) * 0.5f) - o;
                    var m01 = ((v1 + v0) * 0.5f) - o;

                    var side12 = MeshEdgeSide(m12, e12, d);
                    var side20 = MeshEdgeSide(m20, e20, d);
                    var side01 = MeshEdgeSide(m01, e01, d);

                    var anyNegative = side12 < 0f || side20 < 0f || side01 < 0f;
                    var anyPositive = 0f < side12 || 0f < side20 || 0f < side01;

                    float t;
                    if (anyNegative && anyPositive)
                    {
                        t = 1f;
                    }
                    else
                    {
                        var e02 = v2 - v0;
                        var w = v0 - o;
                        var nn = new Vector3(
                            (e02.Z * e01.Y) - (e02.Y * e01.Z),
                            (e02.X * e01.Z) - (e02.Z * e01.X),
                            (e02.Y * e01.X) - (e02.X * e01.Y));
                        var numerator = ((w.X * nn.X) + (w.Y * nn.Y)) + (w.Z * nn.Z);
                        var denominator = ((nn.X * d.X) + (nn.Y * d.Y)) + (nn.Z * d.Z);
                        t = numerator / denominator;
                        t = t <= 0f ? 1f : SseMin(t, 1f);
                    }

                    if (best > t)
                    {
                        bestTriangle = triangleIndex;
                        best = t;
                        MeshSweepBounds(o, d, t, out rayMin, out rayMax);
                    }
                }
            }

            if (stackCount == 0)
            {
                break;
            }

            nodeIndex = stack[--stackCount];
        }

        if (!(maxFraction > best))
        {
            return false;
        }

        var hitTriangle = mesh.Triangles[bestTriangle];
        var a = mesh.VertexPositions[hitTriangle.X];
        var e1 = (mesh.VertexPositions[hitTriangle.Y] - a) * scale;
        var e2 = (mesh.VertexPositions[hitTriangle.Z] - a) * scale;
        var nx = (e2.Z * e1.Y) - (e2.Y * e1.Z);
        var ny = (e2.X * e1.Z) - (e2.Z * e1.X);
        var nz = (e2.Y * e1.X) - (e2.X * e1.Y);
        var inverseLength = 1f / MeshSqrt(((nz * nz) + (ny * ny)) + (nx * nx));

        normal = new Vector3(nx * inverseLength, ny * inverseLength, nz * inverseLength);
        return true;
    }

    // Which side of the edge the ray passes, as the ray direction dotted with edge x (midpoint - origin)
    private static float MeshEdgeSide(Vector3 midpoint, Vector3 edge, Vector3 rayDelta)
    {
        var cx = (midpoint.Z * edge.Y) - (midpoint.Y * edge.Z);
        var cy = (midpoint.X * edge.Z) - (midpoint.Z * edge.X);
        var cz = (midpoint.Y * edge.X) - (midpoint.X * edge.Y);
        return (rayDelta.Z * cz) + ((rayDelta.Y * cy) + (rayDelta.X * cx));
    }

    private static Vector3 MeshNormalizedOrDefault(Vector3 vector, Vector3 fallback)
    {
        var length = MeshSqrt(((vector.Z * vector.Z) + (vector.Y * vector.Y)) + (vector.X * vector.X));

        if (1e-17f > length || length > 1e17f)
        {
            if (length == 0f)
            {
                return fallback;
            }

            // Tiny and huge vectors are normalized in double precision to avoid underflow and overflow
            var x = (double)vector.X;
            var y = (double)vector.Y;
            var z = (double)vector.Z;
            var lengthDouble = Math.Sqrt(((y * y) + (x * x)) + (z * z));
            var inverse = 1.0 / lengthDouble;
            vector = new Vector3((float)(inverse * x), (float)(inverse * y), (float)(inverse * z));

            var clampedLength = (float)(lengthDouble < float.MaxValue ? lengthDouble : float.MaxValue);
            return clampedLength == 0f ? fallback : vector;
        }

        var inverseLength = 1f / length;
        return new Vector3(vector.X * inverseLength, vector.Y * inverseLength, vector.Z * inverseLength);
    }

    // A denominator below the smallest normal float gets 2^-23 or'd into its bits, keeping its sign
    private static float MeshGuardedReciprocal(float d)
    {
        var mask = MathF.Abs(d) < MeshFloatMinNormal ? 0x34000000u : 0u;
        return 1f / BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(d) | mask);
    }

    // The hardware estimate differs between CPU vendors, so results can differ in the last bits across machines
    private static float MeshReciprocalSqrt(float x)
    {
        var r = Sse.IsSupported
            ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalarUnsafe(x)).ToScalar()
            : 1f / MathF.Sqrt(x);
        return (r * (3f - ((r * r) * x))) * 0.5f;
    }

    // Negative input yields 0 instead of NaN
    private static float MeshSqrt(float x) => 0f > x ? 0f : MathF.Sqrt(x);
}
