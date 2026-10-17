using System.Threading.Tasks;
using ValveResourceFormat.Renderer;

namespace Tests.Renderer
{
    public class RubikonShapeCastHullTest
    {
        private const float Skin = RubikonShapeCast.Skin;
        private static readonly Vector3 PlayerHalfExtents = new(16f, 16f, 36f);

        private readonly record struct CastResult(bool Hit, float Fraction, Vector3 Normal, bool StartSolid)
        {
            public CastResult(bool hit, ShapeCastHit result)
                : this(hit, result.Fraction, result.Normal, result.StartSolid)
            {
            }
        }

        private static CastResult CastHull(Rubikon.PhysicsHullData hull, Matrix4x4 transform, Vector3 start, Vector3 delta, Vector3 halfExtents)
            => new(RubikonShapeCast.CastBoxAgainstHull(hull, transform, start, delta, halfExtents, 1f, out var hit), hit);

        private static CastResult CastHull(Rubikon.PhysicsHullData hull, Vector3 start, Vector3 delta)
            => CastHull(hull, Matrix4x4.Identity, start, delta, PlayerHalfExtents);

        private static Rubikon.PhysicsHullData CreateBoxHull(Vector3 min, Vector3 max)
        {
            // The cast only reads the vertices
            Vector3[] vertices =
            [
                new(min.X, min.Y, min.Z),
                new(max.X, min.Y, min.Z),
                new(min.X, max.Y, min.Z),
                new(max.X, max.Y, min.Z),
                new(min.X, min.Y, max.Z),
                new(max.X, min.Y, max.Z),
                new(min.X, max.Y, max.Z),
                new(max.X, max.Y, max.Z),
            ];

            return new Rubikon.PhysicsHullData([], [], min, max, vertices, [], [], []);
        }

        // A large floor whose top face is at z = 0
        private static readonly Rubikon.PhysicsHullData Floor = CreateBoxHull(new Vector3(-512f, -512f, -64f), new Vector3(512f, 512f, 0f));

        private static async Task AssertVector(Vector3 actual, Vector3 expected, float tolerance)
        {
            await Assert.That(actual.X).IsEqualTo(expected.X).Within(tolerance);
            await Assert.That(actual.Y).IsEqualTo(expected.Y).Within(tolerance);
            await Assert.That(actual.Z).IsEqualTo(expected.Z).Within(tolerance);
        }

        [Test]
        public async Task FallingBoxStopsOneSkinAboveFloor()
        {
            const float Gap = 10f;
            var start = new Vector3(3f, -5f, PlayerHalfExtents.Z + Gap);
            var delta = new Vector3(0f, 0f, -20f);

            var result = CastHull(Floor, start, delta);

            await Assert.That(result.Hit).IsTrue();
            await Assert.That(result.StartSolid).IsFalse();
            await Assert.That(result.Fraction).IsEqualTo((Gap - Skin) / 20f).Within(1e-5f);
            await AssertVector(result.Normal, Vector3.UnitZ, 1e-5f);
        }

        [Test]
        public async Task OverlappingBoxStartsSolid()
        {
            var start = new Vector3(0f, 0f, PlayerHalfExtents.Z - 1f);
            var delta = new Vector3(0f, 0f, -8f);

            var result = CastHull(Floor, start, delta);

            await Assert.That(result.Hit).IsTrue();
            await Assert.That(result.StartSolid).IsTrue();
            await Assert.That(result.Fraction).IsEqualTo(0f);
            await AssertVector(result.Normal, -Vector3.Normalize(delta), 1e-5f);
        }

        [Test]
        public async Task StartInsideSkinMovingCloserIsNotStartSolid()
        {
            var start = new Vector3(0f, 0f, PlayerHalfExtents.Z + 1f / 64f);
            var delta = new Vector3(0f, 0f, -4f);

            var result = CastHull(Floor, start, delta);

            await Assert.That(result.Hit).IsTrue();
            await Assert.That(result.StartSolid).IsFalse();
            await Assert.That(result.Fraction).IsEqualTo(0f);
            await AssertVector(result.Normal, Vector3.UnitZ, 1e-5f);
        }

        [Test]
        public async Task ParallelSlideInsideSkinIsNotHit()
        {
            var start = new Vector3(0f, 0f, PlayerHalfExtents.Z + 1f / 64f);
            var delta = new Vector3(24f, 10f, 0f);

            var result = CastHull(Floor, start, delta);

            await Assert.That(result.Hit).IsFalse();
            await Assert.That(result.Fraction).IsEqualTo(1f);
        }

        [Test]
        public async Task EdgeHitNormalPointsFromEdgeToBox()
        {
            // The box's +X+Y vertical edge runs diagonally into the hull's -X-Y vertical edge.
            // Relative to the hull, the box center then touches the edge x = y = -24 of the
            // Minkowski difference, rounded by the skin.
            var hull = CreateBoxHull(new Vector3(-16f), new Vector3(16f));
            var halfExtents = new Vector3(8f);
            var start = new Vector3(-64f, -64f, 0f);
            var delta = new Vector3(60f, 60f, 0f);

            var result = CastHull(hull, Matrix4x4.Identity, start, delta, halfExtents);

            var expectedCenter = -24f - Skin / MathF.Sqrt(2f);
            var expectedFraction = (expectedCenter - start.X) / delta.X;

            await Assert.That(result.Hit).IsTrue();
            await Assert.That(result.StartSolid).IsFalse();
            await Assert.That(result.Fraction).IsEqualTo(expectedFraction).Within(1e-5f);
            await AssertVector(result.Normal, Vector3.Normalize(new Vector3(-1f, -1f, 0f)), 1e-4f);
        }

        [Test]
        public async Task RotatedHullMatchesAxisAlignedHull()
        {
            var local = CreateBoxHull(new Vector3(-32f, -16f, -8f), new Vector3(32f, 16f, 8f));
            var translation = new Vector3(100f, 200f, 50f);
            var transform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(translation);
            var world = CreateBoxHull(new Vector3(-16f, -32f, -8f) + translation, new Vector3(16f, 32f, 8f) + translation);

            (Vector3 Start, Vector3 Delta)[] sweeps =
            [
                (translation + new Vector3(0f, 0f, 80f), new Vector3(0f, 0f, -60f)),
                (translation + new Vector3(-80f, 5f, 0f), new Vector3(70f, 0f, 0f)),
                (translation + new Vector3(10f, 90f, 20f), new Vector3(-5f, -50f, -10f)),
                (translation + new Vector3(-60f, -80f, 30f), new Vector3(50f, 60f, -25f)),
            ];

            foreach (var (start, delta) in sweeps)
            {
                var rotated = CastHull(local, transform, start, delta, PlayerHalfExtents);
                var aligned = CastHull(world, Matrix4x4.Identity, start, delta, PlayerHalfExtents);

                await Assert.That(rotated.Hit).IsTrue();
                await Assert.That(rotated.Hit).IsEqualTo(aligned.Hit);
                await Assert.That(rotated.StartSolid).IsEqualTo(aligned.StartSolid);
                await Assert.That(rotated.Fraction).IsEqualTo(aligned.Fraction).Within(1e-4f);
                await AssertVector(rotated.Normal, aligned.Normal, 1e-4f);
            }
        }

        [Test]
        public async Task LongSweepIsClippedAndStillHits()
        {
            const float Height = 900f;
            var start = new Vector3(40f, -70f, PlayerHalfExtents.Z + Height);
            var delta = new Vector3(0f, 0f, -2000f);

            var result = CastHull(Floor, start, delta);

            await Assert.That(result.Hit).IsTrue();
            await Assert.That(result.StartSolid).IsFalse();
            await Assert.That(result.Fraction).IsEqualTo((Height - Skin) / 2000f).Within(1e-6f);
            await AssertVector(result.Normal, Vector3.UnitZ, 1e-5f);

            var stop = start.Z + result.Fraction * delta.Z - PlayerHalfExtents.Z;
            await Assert.That(stop).IsEqualTo(Skin).Within(2e-3f);
        }

        [Test]
        public async Task LongSweepThatMissesIsNotHit()
        {
            var start = new Vector3(0f, 0f, PlayerHalfExtents.Z + 100f);
            var delta = new Vector3(2000f, 0f, 0f);

            var result = CastHull(Floor, start, delta);

            await Assert.That(result.Hit).IsFalse();
        }

        [Test]
        public async Task FallingBoxStopsAboveSphere()
        {
            const float Radius = 10f;
            var center = new Vector3(5f, 5f, 0f);
            var start = new Vector3(5f, 5f, Radius + PlayerHalfExtents.Z + 20f);
            var delta = new Vector3(0f, 0f, -40f);

            var result = new CastResult(RubikonShapeCast.CastBoxAgainstSphere(center, Radius, Matrix4x4.Identity, start, delta, PlayerHalfExtents, 1f, out var hit), hit);

            await Assert.That(result.Hit).IsTrue();
            await Assert.That(result.StartSolid).IsFalse();
            await Assert.That(result.Fraction).IsEqualTo((20f - Skin) / 40f).Within(1e-5f);
            await AssertVector(result.Normal, Vector3.UnitZ, 1e-5f);
        }

        [Test]
        public async Task SideMovingBoxStopsBesideCapsule()
        {
            const float Radius = 4f;
            var start = new Vector3(-60f, 0f, 0f);
            var delta = new Vector3(50f, 0f, 0f);

            var a = new Vector3(0f, 0f, -20f);
            var b = new Vector3(0f, 0f, 20f);
            var result = new CastResult(RubikonShapeCast.CastBoxAgainstCapsule(a, b, Radius, Matrix4x4.Identity, start, delta, PlayerHalfExtents, 1f, out var hit), hit);

            var expectedCenter = -Radius - Skin - PlayerHalfExtents.X;

            await Assert.That(result.Hit).IsTrue();
            await Assert.That(result.StartSolid).IsFalse();
            await Assert.That(result.Fraction).IsEqualTo((expectedCenter - start.X) / delta.X).Within(1e-5f);
            await AssertVector(result.Normal, -Vector3.UnitX, 1e-5f);
        }
    }
}
