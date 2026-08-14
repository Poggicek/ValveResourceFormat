namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// Describes handbuilt geometry to the pipeline layer, turning the <see cref="VertexInputLayout"/> the
/// renderer already builds into the <see cref="VertexInputDesc"/> a pipeline is created from.
/// </summary>
/// <remarks>
/// The layout already carries the two things a pipeline needs, a <see cref="DXGI_FORMAT"/> per attribute
/// and the canonical location it was allocated, so nothing here decides anything &#8212; it translates.
/// </remarks>
public static class GLVertexInput
{
    /// <summary>Describes a vertex layout as pipeline vertex input state.</summary>
    /// <param name="layout">The layout to describe.</param>
    /// <param name="binding">The vertex buffer binding the attributes are fetched from.</param>
    /// <param name="perInstance">Whether the buffer advances per instance rather than per vertex.</param>
    /// <returns>The vertex input state.</returns>
    /// <exception cref="NotSupportedException">An attribute uses a format with no <see cref="RhiFormat"/>.</exception>
    public static VertexInputDesc ToVertexInputDesc(this VertexInputLayout layout, int binding = 0, bool perInstance = false)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var fields = layout.Fields();

        if (fields.Length == 0)
        {
            return VertexInputDesc.Empty;
        }

        var locations = layout.Locations;
        var attributes = new VertexAttributeDesc[fields.Length];

        for (var i = 0; i < fields.Length; i++)
        {
            attributes[i] = new VertexAttributeDesc(locations[i], ToRhiFormat(fields[i].Format), (int)fields[i].Offset, binding);
        }

        return new VertexInputDesc(attributes, [new VertexBindingDesc(binding, layout.Stride, perInstance)]);
    }

    /// <summary>
    /// Maps a buffer format to its <see cref="RhiFormat"/>.
    /// </summary>
    /// <param name="format">The format a vertex buffer stores the attribute in.</param>
    /// <returns>The equivalent <see cref="RhiFormat"/>.</returns>
    /// <exception cref="NotSupportedException"><paramref name="format"/> has no <see cref="RhiFormat"/>.</exception>
    /// <remarks>
    /// <para>
    /// Temporary. This table belongs in <c>RHI/FormatTables.cs</c> next to the rest of the format
    /// translation; it is duplicated here only because the pipeline layer needs it and that file does not
    /// exist yet. Delete this method in favour of the shared one, do not let the two drift.
    /// </para>
    /// <para>
    /// The cases it throws on are formats <see cref="VertexArray.SetAttribFormat"/> supports and
    /// <see cref="RhiFormat"/> has no member for. They are not exotic: the integer vector formats are what
    /// every skinned mesh stores its blend indices in, so this throws on real geometry until the contract
    /// gains them.
    /// </para>
    /// </remarks>
    public static RhiFormat ToRhiFormat(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT.R32_FLOAT => RhiFormat.R32_SFloat,
        DXGI_FORMAT.R32G32_FLOAT => RhiFormat.R32G32_SFloat,
        DXGI_FORMAT.R32G32B32_FLOAT => RhiFormat.R32G32B32_SFloat,
        DXGI_FORMAT.R32G32B32A32_FLOAT => RhiFormat.R32G32B32A32_SFloat,
        DXGI_FORMAT.R16G16_FLOAT => RhiFormat.R16G16_SFloat,
        DXGI_FORMAT.R16G16B16A16_FLOAT => RhiFormat.R16G16B16A16_SFloat,

        DXGI_FORMAT.R8G8B8A8_UNORM => RhiFormat.R8G8B8A8_UNorm,
        DXGI_FORMAT.R16G16_UNORM => RhiFormat.R16G16_UNorm,
        DXGI_FORMAT.R16G16B16A16_UNORM => RhiFormat.R16G16B16A16_UNorm,
        DXGI_FORMAT.R16G16_SNORM => RhiFormat.R16G16_SNorm,
        DXGI_FORMAT.R8G8B8A8_SNORM => RhiFormat.R8G8B8A8_SNorm,

        DXGI_FORMAT.R32_UINT => RhiFormat.R32_UInt,
        DXGI_FORMAT.R32G32_UINT => RhiFormat.R32G32_UInt,
        DXGI_FORMAT.R32G32B32A32_UINT => RhiFormat.R32G32B32A32_UInt,
        DXGI_FORMAT.R32_SINT => RhiFormat.R32_SInt,

        // Source 2 encodes BLENDINDICES in these, so every skinned mesh needs them
        DXGI_FORMAT.R8G8B8A8_UINT => RhiFormat.R8G8B8A8_UInt,
        DXGI_FORMAT.R16G16_SINT => RhiFormat.R16G16_SInt,
        DXGI_FORMAT.R16G16B16A16_UINT => RhiFormat.R16G16B16A16_UInt,
        DXGI_FORMAT.R16G16B16A16_SINT => RhiFormat.R16G16B16A16_SInt,
        DXGI_FORMAT.R32G32B32A32_SINT => RhiFormat.R32G32B32A32_SInt,

        // :VertexAttributeFormat - when adding one here, add it to VertexArray.SetAttribFormat too
        _ => throw new NotSupportedException(
            $"Vertex attribute format {format} has no {nameof(RhiFormat)} member."),
    };
}
