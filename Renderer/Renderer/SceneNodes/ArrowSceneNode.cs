using System.Runtime.InteropServices;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;

namespace ValveResourceFormat.Renderer.SceneNodes;

/// <summary>
/// Scene node that renders a straight ribbon between two points, textured with the scrolling arrow flow map. The
/// ribbon billboards around its axis every frame so its flat face turns toward the camera, and fades out toward each
/// end. Useful for visualizing directed relationships such as entity IO connections, with the animation flowing from
/// start to end.
/// </summary>
public class ArrowSceneNode : SceneNode
{
    /// <summary>World-space width of the arrow ribbon.</summary>
    public const float DefaultWidth = 4.0f;

    /// <summary>World-space length each arrow tile occupies along the ribbon. Smaller means the arrow repeats more densely.</summary>
    public const float DefaultTileLength = 4.0f;

    /// <summary>Fraction of the ribbon at each end over which the arrow fades out toward the entities it connects.</summary>
    private const float FadeZone = 0.00f;

    /// <summary>Number of segments the ribbon is subdivided into (only needed so the end fade ramps smoothly).</summary>
    private const int Segments = 24;

    private const int SampleCount = Segments + 1;

    private const int VertexPositionOffset = 0;
    private const int VertexUVOffset = 12;
    private const int VertexColorOffset = 20;
    private const int VertexPhaseOffset = 24;
    private const int VertexSize = 28;

    private static readonly VBIB.RenderInputLayoutField[] InputLayout =
    [
        new() { SemanticName = "POSITION", Format = DXGI_FORMAT.R32G32B32_FLOAT, Offset = VertexPositionOffset },
        new() { SemanticName = "TEXCOORD", Format = DXGI_FORMAT.R32G32_FLOAT, Offset = VertexUVOffset },
        new() { SemanticName = "COLOR", Format = DXGI_FORMAT.R8G8B8A8_UNORM, Offset = VertexColorOffset },
        new() { SemanticName = "PHASE", Format = DXGI_FORMAT.R32_FLOAT, Offset = VertexPhaseOffset },
    ];

    [StructLayout(LayoutKind.Explicit, Size = VertexSize)]
    private struct VertexFormat
    {
        [FieldOffset(VertexPositionOffset)]
        public Vector3 Position;
        [FieldOffset(VertexUVOffset)]
        public Vector2 UVs;
        [FieldOffset(VertexColorOffset)]
        public Color32 Color;
        [FieldOffset(VertexPhaseOffset)]
        public float Phase;
    }

    private static int instanceCounter;

    private readonly RenderMaterial material;
    private readonly string meshName;
    private readonly int vaoHandle;
    private readonly int vboHandle;
    private readonly int indicesCount;

    // Centerline of the ribbon and per-sample data that stays constant; only the perpendicular "side" offset is
    // recomputed each frame so the ribbon faces the camera.
    private readonly Vector3[] centers = new Vector3[SampleCount];
    private readonly Vector3[] tangents = new Vector3[SampleCount];
    private readonly float[] us = new float[SampleCount];
    private readonly float halfWidth;
    private readonly float vMax;
    private readonly Color32 color;
    private readonly Color32[] sampleColors = new Color32[SampleCount];
    private readonly VertexFormat[] vertices = new VertexFormat[SampleCount * 2];

    /// <summary>
    /// Initializes a new straight arrow ribbon from <paramref name="start"/> to <paramref name="end"/>.
    /// </summary>
    /// <param name="scene">The scene this node belongs to.</param>
    /// <param name="start">The tail of the arrow (animation flows away from here).</param>
    /// <param name="end">The tip of the arrow.</param>
    /// <param name="color">Solid color of the arrow.</param>
    /// <param name="arrowTexture">Scrolling arrow texture, see <see cref="CS2BombDamageSceneNode.LoadArrowTexture"/>.</param>
    /// <param name="width">World-space width of the ribbon.</param>
    public ArrowSceneNode(Scene scene, Vector3 start, Vector3 end, Color32 color, RenderTexture arrowTexture, float width = DefaultWidth)
        : base(scene)
    {
        var shader = Scene.RendererContext.ShaderLoader.LoadShader("vrf.entity_connection_arrow");
        meshName = $"arrow_{Interlocked.Increment(ref instanceCounter)}";

        material = new RenderMaterial(shader);
        material.Material.IntParams["F_TRANSLUCENT"] = 1;
        material.Material.IntParams["F_RENDER_BACKFACES"] = 1;
        material.Material.IntParams["F_DISABLE_Z_BUFFERING"] = 1;
        material.LoadRenderState();
        material.Textures["g_tColor"] = arrowTexture;

        // The ribbon tiles the arrow along its length (UVs run past 1.0), so the texture must repeat rather than clamp.
        arrowTexture.SetWrapMode(TextureWrapMode.Repeat);

        this.color = color;
        halfWidth = width * 0.5f;

        // The shader multiplies incoming UVs by UV_SCALE; exactly one arrow column across the width.
        const float UvScale = 2.0f;
        vMax = 1f / UvScale;

        BuildLine(start, end, UvScale);

        // Initial geometry with an arbitrary side; Render rebuilds it to face the camera before the first draw.
        UpdateVertices(centers[0] + Vector3.UnitZ);
        var vertexData = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();

        var indices = BuildIndices();
        indicesCount = indices.Length;
        var indexData = MemoryMarshal.AsBytes(indices.AsSpan()).ToArray();

        var vbib = new VBIB { Resource = null! };
        vbib.VertexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = (uint)vertices.Length,
            ElementSizeInBytes = VertexSize,
            InputLayoutFields = InputLayout,
            Data = vertexData,
        });
        vbib.IndexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = (uint)indicesCount,
            ElementSizeInBytes = sizeof(int),
            InputLayoutFields = [],
            Data = indexData,
        });

        var meshBufferCache = Scene.RendererContext.MeshBufferCache;
        var gpuBuffers = meshBufferCache.CreateVertexIndexBuffers(meshName, vbib);
        vboHandle = gpuBuffers.VertexBuffers[0];

        VertexDrawBuffer[] vertexDrawBuffers =
        [
            new VertexDrawBuffer
            {
                Handle = vboHandle,
                ElementSizeInBytes = VertexSize,
                InputLayoutFields = InputLayout,
            },
        ];

        vaoHandle = meshBufferCache.GetVertexArrayObject(meshName, vertexDrawBuffers, material, gpuBuffers.IndexBuffers[0]);
    }

    // Samples the straight line between the two points, filling the centerline, tangents and UVs, and computing bounds.
    private void BuildLine(Vector3 start, Vector3 end, float uvScale)
    {
        var chord = end - start;
        var length = chord.Length();

        var direction = length < 1e-4f ? Vector3.UnitZ : chord / length;

        for (var i = 0; i < SampleCount; i++)
        {
            var t = (float)i / Segments;

            centers[i] = Vector3.Lerp(start, end, t);
            tangents[i] = direction;

            // UV runs along the length so arrow tiles stay evenly spaced.
            us[i] = t * length / DefaultTileLength / uvScale;

            // Fade the ribbon out toward both ends so the knot of arrows at the selected entity reads lightly.
            sampleColors[i] = color with { A = (byte)(color.A * FadeAlpha(t)) };
        }

        var min = Vector3.Min(start, end) - new Vector3(halfWidth);
        var max = Vector3.Max(start, end) + new Vector3(halfWidth);
        BoundingBox = new AABB(min, max);
    }

    // Smoothly ramps from 0 at either end of the ribbon to 1 in the middle.
    private static float FadeAlpha(float t)
    {
        var edge = Math.Clamp(MathF.Min(t, 1f - t) / FadeZone, 0f, 1f);
        return edge * edge * (3f - 2f * edge);
    }

    private static int[] BuildIndices()
    {
        var indices = new int[Segments * 6];

        for (var i = 0; i < Segments; i++)
        {
            var topA = i * 2;
            var botA = topA + 1;
            var topB = topA + 2;
            var botB = topA + 3;

            var baseIndex = i * 6;
            indices[baseIndex + 0] = topA;
            indices[baseIndex + 1] = botA;
            indices[baseIndex + 2] = botB;
            indices[baseIndex + 3] = topA;
            indices[baseIndex + 4] = botB;
            indices[baseIndex + 5] = topB;
        }

        return indices;
    }

    // Per-sample perpendicular offset, oriented so the ribbon faces the camera at that point.
    private Vector3 ComputeSide(Vector3 tangent, Vector3 center, Vector3 cameraPosition)
    {
        var side = Vector3.Cross(tangent, cameraPosition - center);

        if (side.LengthSquared() < 1e-8f)
        {
            side = Vector3.Cross(tangent, Vector3.UnitZ);

            if (side.LengthSquared() < 1e-8f)
            {
                side = Vector3.Cross(tangent, Vector3.UnitX);
            }
        }

        return Vector3.Normalize(side) * halfWidth;
    }

    private void UpdateVertices(Vector3 cameraPosition)
    {
        for (var i = 0; i < SampleCount; i++)
        {
            var side = ComputeSide(tangents[i], centers[i], cameraPosition);
            var u = us[i];
            var sampleColor = sampleColors[i];

            vertices[i * 2] = new VertexFormat { Position = centers[i] + side, UVs = new Vector2(u, 0f), Color = sampleColor };
            vertices[i * 2 + 1] = new VertexFormat { Position = centers[i] - side, UVs = new Vector2(u, vMax), Color = sampleColor };
        }
    }

    /// <inheritdoc/>
    public override void Render(Scene.RenderContext context)
    {
        if (context.RenderPass != RenderPass.Translucent)
        {
            return;
        }

        // Billboard the ribbon so its flat face turns toward the camera, then re-upload.
        UpdateVertices(context.Camera.Location);
        GL.NamedBufferSubData(vboHandle, IntPtr.Zero, VertexSize * vertices.Length, ref vertices[0]);

        var renderShader = context.ReplacementShader ?? material.Shader;
        renderShader.Use();
        GL.BindVertexArray(vaoHandle);
        material.Render(renderShader);
        renderShader.SetUniform3x4("transform", Matrix4x4.Identity);

        GL.DrawElementsInstancedBaseInstance(PrimitiveType.Triangles, indicesCount, DrawElementsType.UnsignedInt, 0, 1, Id);

        material.PostRender();
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    /// <inheritdoc/>
    public override void Delete()
    {
        base.Delete();
        Scene.RendererContext.MeshBufferCache.DeleteVertexIndexBuffers(meshName);
    }
}
