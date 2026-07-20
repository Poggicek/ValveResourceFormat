using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;

namespace ValveResourceFormat.Renderer.SceneNodes;

/// <summary>
/// Scene node that renders a straight ribbon between two points, textured with a scrolling arrow flow map. The ribbon
/// billboards around its axis every frame so its flat face turns toward the camera. Useful for visualizing directed
/// relationships such as entity IO connections, with the animation flowing from start to end.
/// </summary>
public class ArrowSceneNode : SceneNode
{
    /// <summary>World-space width of the arrow ribbon.</summary>
    public const float DefaultWidth = 3.0f;

    /// <summary>World-space length each arrow tile occupies along the ribbon. Smaller repeats the arrow more densely.</summary>
    public const float DefaultTileLength = 4.0f;

    // The shader multiplies incoming UVs by this, so pre-divide to get exactly one arrow column across the width.
    private const float UvScale = 2.0f;

    private const int VertexPositionOffset = 0;
    private const int VertexUVOffset = 12;
    private const int VertexColorOffset = 20;
    private const int VertexSize = 24;

    private static readonly VBIB.RenderInputLayoutField[] InputLayout =
    [
        new() { SemanticName = "POSITION", Format = DXGI_FORMAT.R32G32B32_FLOAT, Offset = VertexPositionOffset },
        new() { SemanticName = "TEXCOORD", Format = DXGI_FORMAT.R32G32_FLOAT, Offset = VertexUVOffset },
        new() { SemanticName = "COLOR", Format = DXGI_FORMAT.R8G8B8A8_UNORM, Offset = VertexColorOffset },
    ];

    private static readonly int[] Indices = [0, 1, 2, 0, 2, 3];

    [StructLayout(LayoutKind.Explicit, Size = VertexSize)]
    private struct VertexFormat
    {
        [FieldOffset(VertexPositionOffset)]
        public Vector3 Position;
        [FieldOffset(VertexUVOffset)]
        public Vector2 UVs;
        [FieldOffset(VertexColorOffset)]
        public Color32 Color;
    }

    private static int instanceCounter;

    private readonly RenderMaterial material;
    private readonly string meshName;
    private readonly int vaoHandle;
    private readonly int vboHandle;

    // Constant geometry; only the perpendicular "side" offset is recomputed each frame so the ribbon faces the camera.
    private readonly Vector3 start;
    private readonly Vector3 end;
    private readonly Vector3 direction;
    private readonly float halfWidth;
    private readonly float uMax;
    private readonly Color32 color;
    private readonly VertexFormat[] vertices = new VertexFormat[4];

    /// <summary>
    /// Initializes a new straight arrow ribbon from <paramref name="start"/> to <paramref name="end"/>.
    /// </summary>
    /// <param name="scene">The scene this node belongs to.</param>
    /// <param name="start">The tail of the arrow (animation flows away from here).</param>
    /// <param name="end">The tip of the arrow.</param>
    /// <param name="color">Solid color of the arrow.</param>
    /// <param name="arrowTexture">Scrolling arrow texture, see <see cref="LoadTexture"/>.</param>
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
        this.color = color;
        halfWidth = width * 0.5f;
        uMax = length / DefaultTileLength / UvScale;

        BoundingBox = new AABB(Vector3.Min(start, end) - new Vector3(halfWidth), Vector3.Max(start, end) + new Vector3(halfWidth));

        // Initial geometry with an arbitrary side; Render rebuilds it to face the camera before the first draw.
        UpdateVertices(start + Vector3.UnitZ);

        var vbib = new VBIB { Resource = null! };
        vbib.VertexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = (uint)vertices.Length,
            ElementSizeInBytes = VertexSize,
            InputLayoutFields = InputLayout,
            Data = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray(),
        });
        vbib.IndexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = (uint)Indices.Length,
            ElementSizeInBytes = sizeof(int),
            InputLayoutFields = [],
            Data = MemoryMarshal.AsBytes(Indices.AsSpan()).ToArray(),
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

    /// <summary>Loads the embedded scrolling arrow texture used for entity connection arrows.</summary>
    /// <param name="scene">Scene providing the material loader.</param>
    /// <returns>The loaded arrow texture.</returns>
    public static RenderTexture LoadTexture(Scene scene)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Renderer.Resources.arrow_io.vtex_c");
        using var resource = new Resource { FileName = "arrow_io.vtex_c" };

        Debug.Assert(stream != null);
        resource.Read(stream);

        return scene.RendererContext.MaterialLoader.LoadTexture(resource, srgbRead: true, isViewerRequest: true);
    }

    // Returns the half-width offset perpendicular to the arrow, oriented so the ribbon faces the camera.
    private Vector3 ComputeSide(Vector3 cameraPosition)
    {
        var side = Vector3.Cross(direction, cameraPosition - (start + end) * 0.5f);

        if (side.LengthSquared() < 1e-8f)
        {
            side = Vector3.Cross(direction, Vector3.UnitZ);

            if (side.LengthSquared() < 1e-8f)
            {
                side = Vector3.Cross(direction, Vector3.UnitX);
            }
        }

        return Vector3.Normalize(side) * halfWidth;
    }

    private void UpdateVertices(Vector3 cameraPosition)
    {
        var side = ComputeSide(cameraPosition);
        const float vMax = 1f / UvScale;

        vertices[0] = new VertexFormat { Position = start + side, UVs = new Vector2(0f, 0f), Color = color };
        vertices[1] = new VertexFormat { Position = start - side, UVs = new Vector2(0f, vMax), Color = color };
        vertices[2] = new VertexFormat { Position = end - side, UVs = new Vector2(uMax, vMax), Color = color };
        vertices[3] = new VertexFormat { Position = end + side, UVs = new Vector2(uMax, 0f), Color = color };
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

        GL.DrawElementsInstancedBaseInstance(PrimitiveType.Triangles, Indices.Length, DrawElementsType.UnsignedInt, 0, 1, Id);

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
