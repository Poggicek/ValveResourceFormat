using System.Runtime.InteropServices;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;

namespace ValveResourceFormat.Renderer.SceneNodes;

/// <summary>
/// Scene node that renders a flat, camera-agnostic ribbon between two points, textured with the scrolling arrow
/// flow map (the same texture and shader used by <see cref="CS2BombDamageSceneNode"/>). Useful for visualizing
/// directed relationships such as entity IO connections, with the animation flowing from start to end.
/// </summary>
public class ArrowSceneNode : SceneNode
{
    /// <summary>World-space width of the arrow ribbon. Also drives how densely the arrow texture tiles along it.</summary>
    public const float DefaultWidth = 16.0f;

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
    private readonly int indicesCount;

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
        var shader = Scene.RendererContext.ShaderLoader.LoadShader("vrf.cs2_baked_bomb_damage");
        meshName = $"arrow_{Interlocked.Increment(ref instanceCounter)}";

        material = new RenderMaterial(shader);
        material.Material.IntParams["F_TRANSLUCENT"] = 1;
        material.Material.IntParams["F_RENDER_BACKFACES"] = 1;
        material.Material.IntParams["F_DISABLE_Z_BUFFERING"] = 1;
        material.LoadRenderState();
        material.Textures["g_tColor"] = arrowTexture;

        // The ribbon tiles the arrow along its length (UVs run past 1.0), so the texture must repeat rather than clamp.
        arrowTexture.SetWrapMode(TextureWrapMode.Repeat);

        var vertexData = BuildRibbon(start, end, startColor, endColor, width, out var bounds);
        BoundingBox = bounds;

        Span<int> indices = [0, 1, 2, 0, 2, 3];
        indicesCount = indices.Length;

        var indexData = MemoryMarshal.AsBytes(indices).ToArray();

        var vbib = new VBIB { Resource = null! };
        vbib.VertexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = (uint)(vertexData.Length / VertexSize),
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

        VertexDrawBuffer[] vertexDrawBuffers =
        [
            new VertexDrawBuffer
            {
                Handle = gpuBuffers.VertexBuffers[0],
                ElementSizeInBytes = VertexSize,
                InputLayoutFields = InputLayout,
            },
        ];

        vaoHandle = meshBufferCache.GetVertexArrayObject(meshName, vertexDrawBuffers, material, gpuBuffers.IndexBuffers[0]);
    }

    private static byte[] BuildRibbon(Vector3 start, Vector3 end, Color32 startColor, Color32 endColor, float width, out AABB bounds)
    {
        var direction = end - start;
        var length = direction.Length();

        if (length < 1e-4f)
        {
            direction = Vector3.UnitZ;
            length = width;
            end = start + direction * length;
        }
        else
        {
            direction /= length;
        }

        // Pick a width axis perpendicular to the ribbon. Prefer horizontal (perpendicular to world up),
        // falling back to another axis when the connection is near-vertical.
        var side = Vector3.Cross(direction, Vector3.UnitZ);
        if (side.LengthSquared() < 1e-6f)
        {
            side = Vector3.Cross(direction, Vector3.UnitX);
        }
        side = Vector3.Normalize(side) * (width * 0.5f);

        // The shader multiplies incoming UVs by UV_SCALE, so pre-divide to land on the tiling we want:
        // exactly one arrow column across the width, and square tiles repeating along the length.
        const float UvScale = 2.0f;
        var vMax = 1f / UvScale;
        var uMax = length / width / UvScale;

        var vertices = new VertexFormat[4];
        vertices[0] = new VertexFormat { Position = start + side, UVs = new Vector2(0f, 0f), Color = startColor };
        vertices[1] = new VertexFormat { Position = start - side, UVs = new Vector2(0f, vMax), Color = startColor };
        vertices[2] = new VertexFormat { Position = end - side, UVs = new Vector2(uMax, vMax), Color = endColor };
        vertices[3] = new VertexFormat { Position = end + side, UVs = new Vector2(uMax, 0f), Color = endColor };

        var min = vertices[0].Position;
        var max = min;
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }
        bounds = new AABB(min, max);

        return MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();
    }

    /// <inheritdoc/>
    public override void Render(Scene.RenderContext context)
    {
        if (context.RenderPass != RenderPass.Translucent)
        {
            return;
        }

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
