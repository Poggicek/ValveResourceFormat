using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;

namespace Tests.Renderer
{
    public class RubikonShapeCastMeshTest
    {
        private const float Skin = RubikonShapeCast.Skin;

        private static readonly Vector3 PlayerHalfExtents = new(16f, 16f, 36f);

        // A floor quad at z = 0, one triangle per leaf under a root split on X
        private static Rubikon.PhysicsMeshData CreateFloor(float halfSize = 64f, Matrix4x4? placement = null)
        {
            Vector3[] vertices =
            [
                new(-halfSize, -halfSize, 0f),
                new(halfSize, -halfSize, 0f),
                new(halfSize, halfSize, 0f),
                new(-halfSize, halfSize, 0f),
            ];

            if (placement is { } matrix)
            {
                for (var i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = Vector3.Transform(vertices[i], matrix);
                }
            }

            var triangles = CreateTriangles([0, 1, 2, 0, 2, 3]);
            var (min0, max0) = Bounds(vertices[0], vertices[1], vertices[2]);
            var (min1, max1) = Bounds(vertices[0], vertices[2], vertices[3]);
            var min = Vector3.Min(min0, min1);
            var max = Vector3.Max(max0, max1);

            // Compiled trees pad node bounds by the skin, the cast only grows them by the box
            var padding = new Vector3(Skin);
            min0 -= padding;
            max0 += padding;
            min1 -= padding;
            max1 += padding;

            Mesh.Node[] tree =
            [
                new(min - padding, max + padding, Mesh.NodeType.SplitX, 2, 0),
                new(min0, max0, Mesh.NodeType.Leaf, 1, 0),
                new(min1, max1, Mesh.NodeType.Leaf, 1, 1),
            ];

            return new Rubikon.PhysicsMeshData([], [], vertices, triangles, tree) { Min = min, Max = max };
        }

        // An octahedron with outward facing triangles, all in a single leaf. Its faces do not lie on its
        // bounds, where the inside test ray ends
        private static Rubikon.PhysicsMeshData CreateOctahedron(float radius, uint flags)
        {
            Vector3[] vertices =
            [
                new(radius, 0f, 0f),
                new(-radius, 0f, 0f),
                new(0f, radius, 0f),
                new(0f, -radius, 0f),
                new(0f, 0f, radius),
                new(0f, 0f, -radius),
            ];

            var indices = new List<int>();
            foreach (var x in (int[])[0, 1])
            {
                foreach (var y in (int[])[2, 3])
                {
                    foreach (var z in (int[])[4, 5])
                    {
                        // Counter-clockwise seen from outside when an even number of the axes are negative
                        var negativeAxes = (x & 1) + (y & 1) + (z & 1);
                        indices.AddRange(negativeAxes % 2 == 0 ? [x, y, z] : [x, z, y]);
                    }
                }
            }

            var min = new Vector3(-radius);
            var max = new Vector3(radius);
            Mesh.Node[] tree = [new(min, max, Mesh.NodeType.Leaf, (uint)(indices.Count / 3), 0)];

            return new Rubikon.PhysicsMeshData([], [], vertices, CreateTriangles([.. indices]), tree)
            {
                Min = min,
                Max = max,
                Flags = flags,
            };
        }

        private static Mesh.Triangle[] CreateTriangles(int[] indices) => MemoryMarshal.Cast<int, Mesh.Triangle>(indices).ToArray();

        private static (Vector3 Min, Vector3 Max) Bounds(Vector3 a, Vector3 b, Vector3 c)
            => (Vector3.Min(Vector3.Min(a, b), c), Vector3.Max(Vector3.Max(a, b), c));

        private static bool Cast(Rubikon.PhysicsMeshData mesh, Vector3 start, Vector3 delta, out ShapeCastHit hit)
            => RubikonShapeCast.CastBoxAgainstMesh(mesh, Matrix4x4.Identity, start, delta, PlayerHalfExtents, 1f, out hit);

        [Test]
        public async Task FallingBoxStopsOneSkinAboveFloor()
        {
            var start = new Vector3(0f, 0f, 100f);
            var delta = new Vector3(0f, 0f, -200f);

            var wasHit = Cast(CreateFloor(), start, delta, out var hit);

            // The box centre projects to 100 at the start and -100 at the end, the floor plus box radius plus skin to 36 + 1/32
            var expected = (1f / (-100f - 100f)) * ((PlayerHalfExtents.Z + Skin) - 100f);
            var bottom = start.Z + hit.Fraction * delta.Z - PlayerHalfExtents.Z;

            using (Assert.Multiple())
            {
                await Assert.That(wasHit).IsTrue();
                await Assert.That(hit.Fraction).IsEqualTo(expected);
                await Assert.That(bottom).IsEqualTo(Skin).Within(1e-4f);
                await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
                await Assert.That(hit.StartSolid).IsFalse();
                await Assert.That(hit.TriangleIndex).IsEqualTo(0);
            }
        }

        [Test]
        public async Task TouchingFloorIsStartSolid()
        {
            var delta = new Vector3(0f, 0f, -10f);

            var wasHit = Cast(CreateFloor(), new Vector3(0f, 0f, PlayerHalfExtents.Z), delta, out var hit);

            using (Assert.Multiple())
            {
                await Assert.That(wasHit).IsTrue();
                await Assert.That(hit.StartSolid).IsTrue();
                await Assert.That(hit.Fraction).IsEqualTo(0f);
                await Assert.That(hit.Normal).IsEqualTo(-Vector3.Normalize(delta));
            }
        }

        [Test]
        public async Task MovingCloserInsideSkinStopsWithoutStartSolid()
        {
            var wasHit = Cast(CreateFloor(), new Vector3(0f, 0f, PlayerHalfExtents.Z + Skin / 2f), new Vector3(0f, 0f, -10f), out var hit);

            using (Assert.Multiple())
            {
                await Assert.That(wasHit).IsTrue();
                await Assert.That(hit.StartSolid).IsFalse();
                await Assert.That(hit.Fraction).IsEqualTo(0f);
                await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
            }
        }

        [Test]
        public async Task SlidingInsideSkinIsNotAHit()
        {
            var wasHit = Cast(CreateFloor(), new Vector3(0f, 0f, PlayerHalfExtents.Z + Skin / 2f), new Vector3(50f, 0f, 0f), out var hit);

            using (Assert.Multiple())
            {
                await Assert.That(wasHit).IsFalse();
                await Assert.That(hit.Fraction).IsEqualTo(1f);
                await Assert.That(hit.StartSolid).IsFalse();
            }
        }

        [Test]
        public async Task GrazingLessThanSevenEighthsOfSkinIsNotAHit()
        {
            var start = new Vector3(0f, 0f, PlayerHalfExtents.Z + 1f);
            var floor = CreateFloor();

            // Ends 15/16 of the skin above the floor, inside the skin but not past 7/8 of it
            var grazed = Cast(floor, start, new Vector3(0f, 0f, -(1f - Skin * 15f / 16f)), out var grazeHit);

            // Ends 3/4 of the skin above the floor
            var entered = Cast(floor, start, new Vector3(0f, 0f, -(1f - Skin * 3f / 4f)), out var enterHit);

            using (Assert.Multiple())
            {
                await Assert.That(grazed).IsFalse();
                await Assert.That(grazeHit.Fraction).IsEqualTo(1f);
                await Assert.That(entered).IsTrue();
                await Assert.That(enterHit.Fraction).IsLessThan(1f);
                await Assert.That(enterHit.Normal).IsEqualTo(Vector3.UnitZ);
            }
        }

        [Test]
        public async Task WallHitReturnsWallNormal()
        {
            // The floor quad stood up as a wall facing -X at x = 100
            var wallPlacement = Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2f) * Matrix4x4.CreateTranslation(100f, 0f, 0f);
            var wall = CreateFloor(placement: wallPlacement);

            var start = Vector3.Zero;
            var delta = new Vector3(200f, 0f, 0f);
            var wasHit = Cast(wall, start, delta, out var hit);

            var expected = (100f - PlayerHalfExtents.X - Skin) / delta.X;

            using (Assert.Multiple())
            {
                await Assert.That(wasHit).IsTrue();
                await Assert.That(hit.StartSolid).IsFalse();
                await Assert.That(hit.Fraction).IsEqualTo(expected).Within(1e-5f);
                await Assert.That(hit.Normal.X).IsEqualTo(-1f).Within(1e-6f);
                await Assert.That(hit.Normal.Y).IsEqualTo(0f).Within(1e-6f);
                await Assert.That(hit.Normal.Z).IsEqualTo(0f).Within(1e-6f);
            }
        }

        [Test]
        public async Task TransformedMeshMatchesPreTransformedMesh()
        {
            // Local +X maps to world +Y and local +Y to world -X, then translated
            var placement = new Matrix4x4(
                0f, 1f, 0f, 0f,
                -1f, 0f, 0f, 0f,
                0f, 0f, 1f, 0f,
                10f, 20f, 30f, 1f);

            var localFloor = CreateFloor();
            var worldFloor = CreateFloor(placement: placement);

            (Vector3 Start, Vector3 Delta)[] sweeps =
            [
                (new Vector3(15f, 23f, 130f), new Vector3(40f, -25f, -200f)),
                (new Vector3(-30f, 50f, 66.5f), new Vector3(10f, 5f, -1f)),
                (new Vector3(10f, 20f, 30f + PlayerHalfExtents.Z), new Vector3(0f, 0f, -10f)),
                (new Vector3(100f, 20f, 40f), new Vector3(-150f, 0f, -50f)),
            ];

            foreach (var (start, delta) in sweeps)
            {
                var transformedHit = RubikonShapeCast.CastBoxAgainstMesh(localFloor, placement, start, delta, PlayerHalfExtents, 1f, out var transformed);
                var identityHit = Cast(worldFloor, start, delta, out var identity);

                using (Assert.Multiple())
                {
                    await Assert.That(transformedHit).IsTrue();
                    await Assert.That(transformedHit).IsEqualTo(identityHit);
                    await Assert.That(transformed.StartSolid).IsEqualTo(identity.StartSolid);
                    await Assert.That(transformed.TriangleIndex).IsEqualTo(identity.TriangleIndex);
                    await Assert.That(transformed.Fraction).IsEqualTo(identity.Fraction).Within(1e-5f);
                    await Assert.That(transformed.Normal.X).IsEqualTo(identity.Normal.X).Within(1e-5f);
                    await Assert.That(transformed.Normal.Y).IsEqualTo(identity.Normal.Y).Within(1e-5f);
                    await Assert.That(transformed.Normal.Z).IsEqualTo(identity.Normal.Z).Within(1e-5f);
                }
            }
        }

        [Test]
        public async Task BoxCentreInsideClosedMeshIsStartSolid()
        {
            var start = new Vector3(5f, 3f, -2f);
            var delta = new Vector3(0f, 0f, -10f);
            var halfExtents = new Vector3(1f);

            var closedHit = RubikonShapeCast.CastBoxAgainstMesh(CreateOctahedron(100f, flags: 1), Matrix4x4.Identity, start, delta, halfExtents, 1f, out var closed);
            var openHit = RubikonShapeCast.CastBoxAgainstMesh(CreateOctahedron(100f, flags: 0), Matrix4x4.Identity, start, delta, halfExtents, 1f, out _);
            var disabledHit = RubikonShapeCast.CastBoxAgainstMesh(CreateOctahedron(100f, flags: 3), Matrix4x4.Identity, start, delta, halfExtents, 1f, out _);

            using (Assert.Multiple())
            {
                await Assert.That(closedHit).IsTrue();
                await Assert.That(closed.StartSolid).IsTrue();
                await Assert.That(closed.Fraction).IsEqualTo(0f);
                await Assert.That(closed.Normal).IsEqualTo(Vector3.UnitZ);
                await Assert.That(closed.TriangleIndex).IsEqualTo(-1);
                await Assert.That(openHit).IsFalse();
                await Assert.That(disabledHit).IsFalse();
            }
        }
    }
}
