using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;

namespace Tests.Renderer
{
    public class RubikonMeshWingsTest
    {
        private static Mesh.Triangle[] Triangles(params int[] indices) => MemoryMarshal.Cast<int, Mesh.Triangle>(indices).ToArray();

        private static (int X, int Y, int Z) AsTuple(Mesh.Wing wing) => (wing.X, wing.Y, wing.Z);

        [Test]
        public async Task TwoTrianglesSeeEachOtherAcrossTheirSharedEdge()
        {
            // 0-1-2 and 2-1-3 share the edge 1-2
            var wings = RubikonMeshWings.Build(Triangles(0, 1, 2, 2, 1, 3), 4);

            await Assert.That(wings.Length).IsEqualTo(2);
            await Assert.That(AsTuple(wings[0])).IsEqualTo((-1, 3, -1));
            await Assert.That(AsTuple(wings[1])).IsEqualTo((0, -1, -1));
        }

        [Test]
        public async Task AnEdgeOfThreeTrianglesHasNoNeighbour()
        {
            var wings = RubikonMeshWings.Build(Triangles(0, 1, 2, 1, 0, 3, 0, 1, 4), 5);

            await Assert.That(wings[0].X).IsEqualTo(-2);
            await Assert.That(wings[1].X).IsEqualTo(-2);
            await Assert.That(wings[2].X).IsEqualTo(-2);
        }

        [Test]
        public async Task EdgesMatchByIndexNotPosition()
        {
            // Both triangles have a vertex at the same place, but under different indices
            var wings = RubikonMeshWings.Build(Triangles(0, 1, 2, 3, 1, 4), 5);

            await Assert.That(AsTuple(wings[0])).IsEqualTo((-1, -1, -1));
            await Assert.That(AsTuple(wings[1])).IsEqualTo((-1, -1, -1));
        }

        [Test]
        public async Task ClosedMeshHasEveryNeighbour()
        {
            // A tetrahedron, each edge shared by two faces
            var wings = RubikonMeshWings.Build(Triangles(0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3), 4);

            await Assert.That(AsTuple(wings[0])).IsEqualTo((3, 3, 3));
            await Assert.That(AsTuple(wings[1])).IsEqualTo((2, 2, 2));
            await Assert.That(AsTuple(wings[2])).IsEqualTo((0, 0, 0));
            await Assert.That(AsTuple(wings[3])).IsEqualTo((1, 1, 1));
        }

        /// <summary>
        /// A box surfing a ramp in a CS2 map crossed the flat seam between two of its triangles. Without
        /// wings the cast reports the seam's edge crossed with the box's Z axis, which threw the player off
        /// the ramp. With the wings the game builds, it reports the ramp's face.
        /// </summary>
        [Test]
        public async Task WingsKeepABoxFromCatchingOnARampSeam()
        {
            Vector3[] vertices =
            [
                new(390.144f, -1864.1752f, 1652.3577f),
                new(708.90466f, -1974.0103f, 1202.1089f),
                new(390.07578f, -1802.872f, 1633.5232f),
                new(708.9871f, -2042.7191f, 1224.8855f),
                new(708.8279f, -1904.9983f, 1180.9058f),
                new(358.14404f, -1864.1826f, 1652.4412f),
            ];

            var min = Vector3.Min(vertices[0], Vector3.Min(vertices[1], vertices[2]));
            var max = Vector3.Max(vertices[0], Vector3.Max(vertices[1], vertices[2]));
            Mesh.Node[] tree = [new(min, max, Mesh.NodeType.Leaf, 1, 0)];

            var withoutWings = new Rubikon.PhysicsMeshData([], [], vertices, Triangles(0, 1, 2), tree) { Min = min, Max = max };
            var withWings = withoutWings with { Wings = [new Mesh.Wing(3, 4, 5)] };

            var start = new Vector3(595.82367f, -1883.9426f, 1375.4076f);
            var end = new Vector3(596.3052f, -1908.1348f, 1381.6406f);
            var center = start + new Vector3(0f, 0f, 36f);
            var halfExtents = new Vector3(16f, 16f, 36f);

            RubikonShapeCast.CastBoxAgainstMesh(withoutWings, Matrix4x4.Identity, center, end - start, halfExtents, 1f, out var seam);
            RubikonShapeCast.CastBoxAgainstMesh(withWings, Matrix4x4.Identity, center, end - start, halfExtents, 1f, out var face);

            await Assert.That(seam.Fraction).IsEqualTo(0.19826207f);
            await Assert.That(seam.Normal).IsEqualTo(new Vector3(0.47294512f, 0.88109183f, 0f));

            await Assert.That(face.Fraction).IsEqualTo(0.19826207f);
            await Assert.That(face.Normal).IsEqualTo(new Vector3(0.8234452f, 0.16747518f, 0.5421163f));
        }
    }
}
