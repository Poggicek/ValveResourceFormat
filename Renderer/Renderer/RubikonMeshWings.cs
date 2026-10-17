using static ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Mesh;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Builds a mesh's wings, the neighbour across each triangle edge, the way the physics does when a game
/// loads its meshes with them. Box casts use them to keep boxes from catching on the seams between
/// triangles.
/// </summary>
/// <remarks>
/// Edges are matched by vertex index, never by position. An edge of one triangle has no neighbour (-1), an
/// edge of two has the other triangle's vertex opposite it, and an edge of more has none either (-2). A
/// triangle is only cast against with its wings when all three are neighbours.
/// </remarks>
internal static class RubikonMeshWings
{
    private const int NoNeighbour = -1;
    private const int SharedByMore = -2;

    private sealed class Edge(int min, int max)
    {
        public int Min { get; } = min;
        public int Max { get; } = max;
        public List<int> Triangles { get; } = [];
    }

    /// <returns>A wing per triangle, or none when the mesh's topology is inconsistent.</returns>
    public static Wing[] Build(Triangle[] triangles, int vertexCount)
    {
        var edges = new List<Edge>(triangles.Length * 3 / 2);
        var vertexEdges = new List<int>?[vertexCount];

        Edge? FindEdge(int a, int b)
        {
            var (min, max) = a < b ? (a, b) : (b, a);

            if (vertexEdges[a] is not { } candidates)
            {
                return null;
            }

            foreach (var index in candidates)
            {
                var edge = edges[index];

                if (edge.Min == min && edge.Max == max)
                {
                    return edge;
                }
            }

            return null;
        }

        void AddEdge(int a, int b, int triangle)
        {
            if (FindEdge(a, b) is not { } edge)
            {
                edge = a < b ? new Edge(a, b) : new Edge(b, a);
                edges.Add(edge);

                (vertexEdges[a] ??= []).Add(edges.Count - 1);
                (vertexEdges[b] ??= []).Add(edges.Count - 1);
            }

            edge.Triangles.Add(triangle);
        }

        for (var i = 0; i < triangles.Length; i++)
        {
            var triangle = triangles[i];
            AddEdge(triangle.X, triangle.Y, i);
            AddEdge(triangle.Y, triangle.Z, i);
            AddEdge(triangle.Z, triangle.X, i);
        }

        var wings = new Wing[triangles.Length];

        for (var i = 0; i < triangles.Length; i++)
        {
            var triangle = triangles[i];

            if (Neighbour(triangle.X, triangle.Y, i) is not { } x
                || Neighbour(triangle.Y, triangle.Z, i) is not { } y
                || Neighbour(triangle.Z, triangle.X, i) is not { } z)
            {
                return [];
            }

            wings[i] = new Wing(x, y, z);
        }

        return wings;

        // The vertex of the other triangle on the edge opposite it, or null when the topology does not add up
        int? Neighbour(int a, int b, int triangle)
        {
            if (FindEdge(a, b) is not { } edge)
            {
                return null;
            }

            switch (edge.Triangles.Count)
            {
                case 1:
                    return NoNeighbour;
                case 2:
                    break;
                default:
                    return SharedByMore;
            }

            if (!edge.Triangles.Contains(triangle))
            {
                return null;
            }

            var other = triangles[edge.Triangles[0] == triangle ? edge.Triangles[1] : edge.Triangles[0]];

            if (IsEdge(edge, other.X, other.Y))
            {
                return other.Z;
            }

            if (IsEdge(edge, other.Y, other.Z))
            {
                return other.X;
            }

            if (IsEdge(edge, other.Z, other.X))
            {
                return other.Y;
            }

            return null;
        }
    }

    private static bool IsEdge(Edge edge, int a, int b)
        => edge.Min == Math.Min(a, b) && edge.Max == Math.Max(a, b);
}
