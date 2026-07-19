using System.Runtime.InteropServices;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;

namespace ValveResourceFormat.Renderer.SceneNodes;

/// <summary>
/// Scene node that renders a flat ribbon between two points, textured with the scrolling arrow flow map (the same
/// texture and shader used by <see cref="CS2BombDamageSceneNode"/>). The ribbon billboards around its own axis every
/// frame so its flat face always turns toward the camera. Useful for visualizing directed relationships such as
/// entity IO connections, with the animation flowing from start to end.
/// </summary>
public class ArrowSceneNode : SceneNode
{
    /// <summary>World-space width of the arrow ribbon.</summary>
    public const float DefaultWidth = 12.0f;

    /// <summary>World-space length each arrow tile occupies along the ribbon. Smaller means the arrow repeats more densely.</summary>
    public const float DefaultTileLength = 10.0f;

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

    // Geometry that stays constant; only the perpendicular "side" vector is recomputed each frame to face the camera.
    private readonly Vector3 start;
    private readonly Vector3 end;
    private readonly Vector3 direction;
    private readonly float halfWidth;
    private readonly float uMax;
    private readonly float vMax;
    private readonly Color32 startColor;
    private readonly Color32 endColor;
    private readonly VertexFormat[] vertices = new VertexFormat[4];

    /// <summary>
    /// Initializes a new arrow ribbon pointing from <paramref name="start"/> to <paramref name="end"/> using a single color.
    /// </summary>
    public ArrowSceneNode(Scene scene, Vector3 start, Vector3 end, Color32 color, RenderTexture arrowTexture, float width = DefaultWidth)
        : this(scene, start, end, color, color, arrowTexture, width)
    {
    }

    /// <summary>
    /// Initializes a new arrow ribbon pointing from <paramref name="start"/> to <paramref name="end"/> with a color
    /// gradient running from the tail to the tip.
    /// </summary>
    /// <param name="scene">The scene this node belongs to.</param>
    /// <param name="start">The tail of the arrow (animation flows away from here).</param>
    /// <param name="end">The tip of the arrow.</param>
    /// <param name="startColor">Color at the tail.</param>
    /// <param name="endColor">Color at the tip.</param>
    /// <param name="arrowTexture">Scrolling arrow texture, see <see cref="CS2BombDamageSceneNode.LoadArrowTexture"/>.</param>
    /// <param name="width">World-space width of the ribbon.</param>
    public ArrowSceneNode(Scene scene, Vector3 start, Vector3 end, Color32 startColor, Color32 endColor, RenderTexture arrowTexture, float width = DefaultWidth)
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

        var length = (end - start).Length();

        if (length < 1e-4f)
        {
            // Degenerate arrow: give it a tiny extent along Z so it stays visible.
            direction = Vector3.UnitZ;
            length = width;
            end = start + direction * length;
        }
        else
        {
            direction = (end - start) / length;
        }

        this.start = start;
        this.end = end;
        this.startColor = startColor;
        this.endColor = endColor;
        halfWidth = width * 0.5f;

        // The shader multiplies incoming UVs by UV_SCALE, so pre-divide to land on the tiling we want:
        // exactly one arrow column across the width, and one arrow tile per DefaultTileLength along the length.
        const float UvScale = 2.0f;
        vMax = 1f / UvScale;
        uMax = length / DefaultTileLength / UvScale;

        // Ribbon can rotate around its axis to any orientation, so its bounds are the segment padded by the half width.
        var min = Vector3.Min(start, end) - new Vector3(halfWidth);
        var max = Vector3.Max(start, end) + new Vector3(halfWidth);
        BoundingBox = new AABB(min, max);

        // Initial geometry with an arbitrary side; Render rebuilds it to face the camera before the first draw.
        UpdateVertices(ComputeSide(start + Vector3.UnitZ));
        var vertexData = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();

        Span<int> indices = [0, 1, 2, 0, 2, 3];
        indicesCount = indices.Length;

        var indexData = MemoryMarshal.AsBytes(indices).ToArray();

        var vbib = new VBIB { Resource = null! };
        vbib.VertexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = 4,
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

    // Returns the half-width offset vector perpendicular to the arrow axis, oriented so the ribbon faces the camera.
    private Vector3 ComputeSide(Vector3 cameraPosition)
    {
        var toCamera = cameraPosition - (start + end) * 0.5f;
        var side = Vector3.Cross(direction, toCamera);

        if (side.LengthSquared() < 1e-8f)
        {
            // Camera is (nearly) on the arrow axis; any perpendicular will do.
            side = Vector3.Cross(direction, Vector3.UnitZ);

            if (side.LengthSquared() < 1e-8f)
            {
                side = Vector3.Cross(direction, Vector3.UnitX);
            }
        }

        return Vector3.Normalize(side) * halfWidth;
    }

    private void UpdateVertices(Vector3 side)
    {
        vertices[0] = new VertexFormat { Position = start + side, UVs = new Vector2(0f, 0f), Color = startColor };
        vertices[1] = new VertexFormat { Position = start - side, UVs = new Vector2(0f, vMax), Color = startColor };
        vertices[2] = new VertexFormat { Position = end - side, UVs = new Vector2(uMax, vMax), Color = endColor };
        vertices[3] = new VertexFormat { Position = end + side, UVs = new Vector2(uMax, 0f), Color = endColor };
    }

    /// <inheritdoc/>
    public override void Render(Scene.RenderContext context)
    {
        if (context.RenderPass != RenderPass.Translucent)
        {
            return;
        }

        // Billboard the ribbon around its axis so its flat face turns toward the camera, then re-upload the vertices.
        UpdateVertices(ComputeSide(context.Camera.Location));
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
