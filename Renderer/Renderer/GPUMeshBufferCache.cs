using System.Diagnostics;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

#if DEBUG
using Microsoft.Extensions.Logging;
#endif

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Caches GPU mesh buffers and vertex array objects for efficient mesh rendering.
    /// </summary>
    public partial class GPUMeshBufferCache
    {
        private readonly RendererContext RendererContext;
        private readonly Dictionary<string, GPUMeshBuffers> gpuBuffers = [];
        private readonly Dictionary<VAOKey, int> vertexArrayObjects = [];

        /// <summary>
        /// Where each live mesh buffer handle came from, so a bare handle out of a <see cref="DrawCall"/>
        /// can be resolved back to a correctly sized <see cref="RHI.IBuffer"/>.
        /// </summary>
        /// <remarks>
        /// Locators rather than wrappers: building the wrapper is deferred to
        /// <see cref="GPUMeshBuffers.RhiVertexBuffer"/>, which memoizes, so a scene that never records
        /// through the RHI pays only a dictionary entry per buffer and allocates no wrappers at all.
        /// </remarks>
        private readonly Dictionary<int, BufferLocator> rhiBufferLocators = [];

        private readonly record struct BufferLocator(GPUMeshBuffers Owner, bool IsIndex, int Index);

        /// <summary>
        /// Sized wrappers for buffers this cache owns directly rather than through a
        /// <see cref="GPUMeshBuffers"/>, which today is only <see cref="VectorOneVertexBuffer"/>.
        /// </summary>
        private readonly Dictionary<int, RHI.IBuffer> standaloneRhiBuffers = [];

        /// <summary>Gets the number of distinct vertex array objects currently cached.</summary>
        public int VertexArrayObjectCount => vertexArrayObjects.Count;

        /// <summary>Identifies a vertex array object by the attribute layout of an input signature and the
        /// GPU buffers.</summary>
        private readonly struct VAOKey : IEquatable<VAOKey>
        {
            public required int InputSignature { get; init; }
            public required int IndexBuffer { get; init; }
            public required int[] VertexBuffers { get; init; }

            public bool Equals(VAOKey other)
                => InputSignature == other.InputSignature
                && IndexBuffer == other.IndexBuffer
                && VertexBuffers.AsSpan().SequenceEqual(other.VertexBuffers);

            public override bool Equals(object? obj) => obj is VAOKey other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(InputSignature);
                hash.Add(IndexBuffer);

                foreach (var handle in VertexBuffers)
                {
                    hash.Add(handle);
                }

                return hash.ToHashCode();
            }
        }

        /// <summary>Initializes a new GPU mesh buffer cache.</summary>
        /// <param name="rendererContext">The renderer context owning this cache.</param>
        public GPUMeshBufferCache(RendererContext rendererContext)
        {
            RendererContext = rendererContext;
        }

        /// <summary>Returns cached GPU buffers for the named mesh, uploading them if not yet present.</summary>
        /// <param name="meshName">Unique name identifying the mesh.</param>
        /// <param name="vbib">Vertex and index buffer data to upload on first use.</param>
        /// <returns>The GPU buffers for the mesh.</returns>
        public GPUMeshBuffers CreateVertexIndexBuffers(string meshName, VBIB vbib)
        {
            if (!gpuBuffers.TryGetValue(meshName, out var gpuVbib))
            {
                // The device comes from this cache's own context, never from the ambient fallback: a
                // process can hold several contexts on different devices, and mesh data must be allocated
                // on the one whose renderer is going to draw it.
                gpuVbib = new GPUMeshBuffers(vbib, RendererContext.Device);
                gpuBuffers.Add(meshName, gpuVbib);
                RegisterRhiBuffers(gpuVbib);

#if DEBUG
                for (var i = 0; i < gpuVbib.VertexBuffers.Length; i++)
                {
                    var bufferLabel = $"{meshName} VB {i}";
                    GL.ObjectLabel(ObjectLabelIdentifier.Buffer, gpuVbib.VertexBuffers[i], Math.Min(GLEnvironment.MaxLabelLength, bufferLabel.Length), bufferLabel);
                }

                for (var i = 0; i < gpuVbib.IndexBuffers.Length; i++)
                {
                    var bufferLabel = $"{meshName} IB {i}";
                    GL.ObjectLabel(ObjectLabelIdentifier.Buffer, gpuVbib.IndexBuffers[i], Math.Min(GLEnvironment.MaxLabelLength, bufferLabel.Length), bufferLabel);
                }
#endif
            }

            return gpuVbib;
        }

        /// <summary>Uploads the mesh buffers (if not yet present) and returns a VAO for the first
        /// vertex/index buffer pair, without exposing the GPU buffer handles to the caller.</summary>
        /// <param name="meshName">Unique name identifying the mesh.</param>
        /// <param name="vbib">Vertex and index buffer data; the first vertex buffer's layout describes the attributes.</param>
        /// <param name="inputSignature">Optional material input signature mapping buffer semantics to shader attribute names.</param>
        /// <returns>The OpenGL VAO handle.</returns>
        public int UploadBuffersAndCreateVertexArray(string meshName, VBIB vbib, Material.VsInputSignature inputSignature = default)
        {
            var gpuVbib = CreateVertexIndexBuffers(meshName, vbib);
            var vertexBuffer = vbib.VertexBuffers[0];

            return GetVertexArrayObject(
            [
                new VertexDrawBuffer
                {
                    Handle = gpuVbib.VertexBuffers[0],
                    ElementSizeInBytes = vertexBuffer.ElementSizeInBytes,
                    InputLayoutFields = vertexBuffer.InputLayoutFields,
                },
            ], inputSignature, vbib.IndexBuffers.Count > 0 ? gpuVbib.IndexBuffers[0] : 0, meshName);
        }

        /// <remarks>
        /// A zero handle is not registered. It is what every buffer of a mesh allocated on a non-OpenGL
        /// device reports, so registering them would collapse the whole mesh &#8212; in fact every mesh in
        /// the process &#8212; onto one entry that answers with whichever buffer was uploaded last. That is
        /// not a lookup miss, it is a confident wrong answer, and the draw would bind an index buffer where
        /// a vertex buffer belongs. Draw call bindings carry their own <see cref="RHI.IBuffer"/> for
        /// precisely this reason; see <see cref="VertexDrawBuffer.RhiBuffer"/>.
        /// </remarks>
        private void RegisterRhiBuffers(GPUMeshBuffers gpuVbib)
        {
            for (var i = 0; i < gpuVbib.VertexBuffers.Length; i++)
            {
                if (gpuVbib.VertexBuffers[i] != 0)
                {
                    rhiBufferLocators[gpuVbib.VertexBuffers[i]] = new BufferLocator(gpuVbib, false, i);
                }
            }

            for (var i = 0; i < gpuVbib.IndexBuffers.Length; i++)
            {
                if (gpuVbib.IndexBuffers[i] != 0)
                {
                    rhiBufferLocators[gpuVbib.IndexBuffers[i]] = new BufferLocator(gpuVbib, true, i);
                }
            }
        }

        private void UnregisterRhiBuffers(GPUMeshBuffers gpuVbib)
        {
            foreach (var handle in gpuVbib.VertexBuffers)
            {
                rhiBufferLocators.Remove(handle);
            }

            foreach (var handle in gpuVbib.IndexBuffers)
            {
                rhiBufferLocators.Remove(handle);
            }
        }

        /// <summary>
        /// Resolves a mesh buffer handle to a correctly sized <see cref="RHI.IBuffer"/>.
        /// </summary>
        /// <param name="handle">The OpenGL buffer handle, as carried by <see cref="VertexDrawBuffer.Handle"/>
        /// or <see cref="IndexDrawBuffer.Handle"/>.</param>
        /// <returns>A non-owning view of the same OpenGL object. This cache still owns it, and
        /// <see cref="DeleteVertexIndexBuffers"/> is still what frees it.</returns>
        /// <exception cref="ArgumentException">The handle was not uploaded through this cache, so its size
        /// is unknown.</exception>
        /// <remarks>
        /// <para>
        /// The size comes from the <see cref="VBIB"/> the buffer was uploaded from, which is the only thing
        /// that knows it: an OpenGL buffer handle carries no length, and a draw call carries only the
        /// handle. This is why the bridge lives here rather than on <see cref="DrawCall"/>.
        /// </para>
        /// <para>
        /// An unknown handle throws rather than returning a zero-sized or guessed buffer. A fabricated size
        /// would turn a genuine out-of-range draw into silently wrong geometry on OpenGL and a device loss
        /// on Vulkan, which is exactly the class of bug the RHI's sized buffers exist to catch.
        /// </para>
        /// </remarks>
        public RHI.IBuffer GetRhiBuffer(int handle)
        {
            if (!rhiBufferLocators.TryGetValue(handle, out var locator))
            {
                if (standaloneRhiBuffers.TryGetValue(handle, out var standalone))
                {
                    return standalone;
                }

                throw new ArgumentException(
                    $"Buffer handle {handle} was not uploaded through this {nameof(GPUMeshBufferCache)}, so its size is unknown. Only mesh buffers created by {nameof(CreateVertexIndexBuffers)} can be resolved; anything else has to carry its own size to {nameof(RHI.OpenGL.GLBuffer)}.{nameof(RHI.OpenGL.GLBuffer.Wrap)}.",
                    nameof(handle));
            }

            return locator.IsIndex
                ? locator.Owner.RhiIndexBuffer(locator.Index)
                : locator.Owner.RhiVertexBuffer(locator.Index);
        }

        /// <summary>Resolves a draw call's vertex buffer binding to a correctly sized <see cref="RHI.IBuffer"/>.</summary>
        /// <param name="buffer">The binding to resolve.</param>
        /// <returns>The buffer the binding names.</returns>
        /// <exception cref="ArgumentException">The binding carries no buffer and its handle was not
        /// uploaded through this cache.</exception>
        /// <remarks>A binding that carries its own <see cref="VertexDrawBuffer.RhiBuffer"/> answers from
        /// that and never consults <see cref="VertexDrawBuffer.Handle"/>. Only a raw OpenGL buffer the renderer still owns
        /// itself takes the handle path, and only an OpenGL device has such a buffer.</remarks>
        public RHI.IBuffer GetRhiBuffer(in VertexDrawBuffer buffer) => buffer.RhiBuffer ?? GetRhiBuffer(buffer.Handle);

        /// <summary>Resolves a draw call's index buffer binding to a correctly sized <see cref="RHI.IBuffer"/>.</summary>
        /// <param name="buffer">The binding to resolve.</param>
        /// <returns>The buffer the binding names.</returns>
        /// <exception cref="ArgumentException">The binding carries no buffer and its handle was not
        /// uploaded through this cache.</exception>
        /// <remarks>See <see cref="GetRhiBuffer(in VertexDrawBuffer)"/>.</remarks>
        public RHI.IBuffer GetRhiBuffer(in IndexDrawBuffer buffer) => buffer.RhiBuffer ?? GetRhiBuffer(buffer.Handle);

        /// <summary>
        /// Translates an OpenGL index element type to the RHI's, and converts a draw call's byte-offset
        /// <see cref="DrawCall.StartIndex"/> into the index count <see cref="RHI.ICommandList.DrawIndexed"/>
        /// takes.
        /// </summary>
        /// <param name="drawCall">The draw call to describe.</param>
        /// <returns>The index element width and the first index, as an element count.</returns>
        /// <exception cref="NotSupportedException">The draw call uses 8 bit indices, which the RHI has no
        /// member for. See the remarks.</exception>
        /// <remarks>
        /// <para>
        /// Both conversions are returned together on purpose. <see cref="DrawCall.StartIndex"/> is a
        /// <b>byte</b> offset, because that is the pointer <c>glDrawElements</c> takes, whereas
        /// <see cref="RHI.ICommandList.DrawIndexed"/> takes <c>firstIndex</c> as an element <b>count</b>.
        /// Passing one where the other belongs is silently wrong for 16 bit indices and wrong by a factor
        /// of four for 32 bit ones, and it is the natural mistake to make. Taking both from one call means
        /// a caller cannot convert the type and forget the offset.
        /// </para>
        /// <para>
        /// 8 bit indices are refused rather than widened. <see cref="DrawElementsType.UnsignedByte"/> is
        /// representable in <see cref="DrawCall.IndexType"/> and priced by
        /// <see cref="DrawCall.IndexSizeInBytes"/>, but no Source 2 mesh produces one: the only place index
        /// width is decided reads it from the <see cref="VBIB"/> and already rejects anything but 2 or 4
        /// bytes. Vulkan's <c>VK_INDEX_TYPE_UINT8</c> needs an extension that is not universally available,
        /// so widening here would mean silently reallocating and rewriting the buffer at draw time. If a
        /// mesh format ever does arrive with 8 bit indices, the conversion belongs at upload, where the
        /// buffer is built, not here.
        /// </para>
        /// </remarks>
        public static (RHI.IndexType IndexType, int FirstIndex) DescribeIndexedDraw(DrawCall drawCall)
        {
            ArgumentNullException.ThrowIfNull(drawCall);

            var indexType = drawCall.IndexType switch
            {
                DrawElementsType.UnsignedShort => RHI.IndexType.UInt16,
                DrawElementsType.UnsignedInt => RHI.IndexType.UInt32,
                DrawElementsType.UnsignedByte => throw new NotSupportedException(
                    $"8 bit indices have no {nameof(RHI.IndexType)} member. No Source 2 mesh produces them, and widening the buffer at draw time is not something this layer may do; convert at upload instead."),
                _ => throw new ArgumentOutOfRangeException(nameof(drawCall), drawCall.IndexType, "Unknown index element type."),
            };

            return (indexType, (int)(drawCall.StartIndex / drawCall.IndexSizeInBytes));
        }

        /// <summary>
        /// Disposes any cached gpu buffers and frees gpu vertex arrays.
        /// </summary>
        public void Clear()
        {
            foreach (var item in gpuBuffers)
            {
                item.Value.Delete();
            }

            gpuBuffers.Clear();
            rhiBufferLocators.Clear();

            foreach (var item in vertexArrayObjects)
            {
                VertexArray.Delete(item.Value);
            }

            vertexArrayObjects.Clear();
        }

        /// <summary>Deletes and removes the cached GPU buffers and vertex arrays for the specified mesh.</summary>
        /// <param name="meshName">Unique name identifying the mesh to delete.</param>
        public void DeleteVertexIndexBuffers(string meshName)
        {
            if (gpuBuffers.TryGetValue(meshName, out var gpuVbib))
            {
                gpuVbib.Delete();
                UnregisterRhiBuffers(gpuVbib);
                gpuBuffers.Remove(meshName);
                InvalidateVertexArrayObjectsForFreedBuffers([.. gpuVbib.VertexBuffers, .. gpuVbib.IndexBuffers]);
            }
        }

        /// <summary>Deletes and removes the cached VAOs built from the given GPU buffer handles, which the
        /// caller is about to delete. Because OpenGL never assigns a handle to two live objects at once, a
        /// handle passed here can only match VAOs built from that exact buffer, so this is a precise
        /// invalidation. Skipping it would leave a stale cache entry that silently matches whatever unrelated
        /// buffer GL later reuses that handle for.</summary>
        /// <param name="bufferHandles">Vertex and/or index buffer handles about to be freed.</param>
        public void InvalidateVertexArrayObjectsForFreedBuffers(params int[] bufferHandles)
            => DeleteVertexArrayObjects(key
                => Array.IndexOf(bufferHandles, key.IndexBuffer) >= 0
                || key.VertexBuffers.Any(handle => Array.IndexOf(bufferHandles, handle) >= 0));

        private void DeleteVertexArrayObjects(Func<VAOKey, bool> predicate)
        {
            List<VAOKey>? keysToRemove = null;

            foreach (var (key, vao) in vertexArrayObjects)
            {
                if (predicate(key))
                {
                    VertexArray.Delete(vao);
                    (keysToRemove ??= []).Add(key);
                }
            }

            keysToRemove?.ForEach(key => vertexArrayObjects.Remove(key));
        }

        /// <summary>Returns a cached VAO for the given buffers, creating it if necessary. Locations are
        /// canonical, so it is valid for every shader that draws these buffers. Keyed by what a VAO
        /// fundamentally is, so callers never need to invent a unique name to keep unrelated geometry from
        /// colliding, and identical layouts dedupe automatically.</summary>
        /// <param name="vertexBuffers">Vertex buffer bindings for the draw call.</param>
        /// <param name="inputSignature">Material input signature mapping buffer semantics to shader attribute names.</param>
        /// <param name="idxIndex">OpenGL handle of the index buffer, or 0 for non-indexed geometry.</param>
        /// <param name="debugLabel">Optional label applied to the VAO in debug builds when newly created.</param>
        /// <returns>The OpenGL VAO handle.</returns>
        public int GetVertexArrayObject(VertexDrawBuffer[] vertexBuffers, Material.VsInputSignature inputSignature, int idxIndex, string? debugLabel = null)
        {
            var vaoKey = new VAOKey
            {
                InputSignature = inputSignature.Hash,
                IndexBuffer = idxIndex,
                VertexBuffers = Array.ConvertAll(vertexBuffers, vb => vb.Handle),
            };

            if (vertexArrayObjects.TryGetValue(vaoKey, out var vaoHandle))
            {
                return vaoHandle;
            }

            var newVaoHandle = CreateVertexArrayObject(vertexBuffers, inputSignature, idxIndex, debugLabel);
            vertexArrayObjects.Add(vaoKey, newVaoHandle);
            return newVaoHandle;
        }

        /// <summary>Builds a new VAO for the given buffers without caching it. Each attribute takes the
        /// canonical location of its material input signature name, or of its own buffer semantic when the
        /// signature is absent or names something unknown.</summary>
        /// <param name="vertexBuffers">Vertex buffer bindings for the draw call.</param>
        /// <param name="inputSignature">Material input signature mapping buffer semantics to shader attribute names.</param>
        /// <param name="idxIndex">OpenGL handle of the index buffer.</param>
        /// <param name="debugLabel">Optional label applied to the VAO in debug builds.</param>
        /// <returns>The OpenGL VAO handle.</returns>
        private int CreateVertexArrayObject(VertexDrawBuffer[] vertexBuffers, Material.VsInputSignature inputSignature, int idxIndex, string? debugLabel = null)
        {
            Debug.Assert(vertexBuffers != null && vertexBuffers.Length > 0);

            GL.CreateVertexArrays(1, out int newVaoHandle);
            VertexArray.StartRecording(newVaoHandle);

            // Check for non-indexed geometry
            if (idxIndex != 0)
            {
                GL.VertexArrayElementBuffer(newVaoHandle, idxIndex);
            }

            // Workaround a bug in Intel drivers when mixing float and integer attributes
            // See https://gist.github.com/stefalie/e17a20a88a0fdbd97110611569a6605f for reference
            // We are using DSA apis, so we don't actually need to bind the VAO
            GL.BindVertexArray(newVaoHandle);

            var bindingIndex = 0;
            var boundLocations = 0;
            vertexBuffers = AddMissingAttributes(vertexBuffers);

            foreach (var curVertexBuffer in vertexBuffers)
            {
                GL.VertexArrayVertexBuffer(newVaoHandle, bindingIndex, curVertexBuffer.Handle, 0, (int)curVertexBuffer.ElementSizeInBytes);

                foreach (var attribute in curVertexBuffer.InputLayoutFields)
                {
                    var attributeLocation = VertexAttributeLocations.Resolve(inputSignature, attribute, out var insgElemName);

                    // Unknown, or a location an earlier buffer already took, which the table's aliases allow
                    if (attributeLocation == -1 || (boundLocations & (1 << attributeLocation)) != 0)
                    {
#if DEBUG
                        if (attributeLocation == -1 && !string.IsNullOrEmpty(insgElemName))
                        {
                            RendererContext.Logger.LogDebug("Attribute {SemanticName} ({SemanticIndex}) has no canonical location (insg: {InsgElemName})", attribute.SemanticName, attribute.SemanticIndex, insgElemName);
                        }
#endif
                        continue;
                    }

                    boundLocations |= 1 << attributeLocation;

                    GL.EnableVertexArrayAttrib(newVaoHandle, attributeLocation);
                    GL.VertexArrayAttribBinding(newVaoHandle, attributeLocation, bindingIndex);
                    VertexArray.SetAttribFormat(newVaoHandle, attributeLocation, attribute.Format, (int)attribute.Offset);
                }

                bindingIndex++;
            }

#if DEBUG
            if (debugLabel != null)
            {
                GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, newVaoHandle, Math.Min(GLEnvironment.MaxLabelLength, debugLabel.Length), debugLabel);
            }
#endif

            return newVaoHandle;
        }

        private VertexDrawBuffer[] AddMissingAttributes(VertexDrawBuffer[] vertexBuffers)
        {
            // Shaders read white where a mesh has no COLOR stream, matching the engine default
            if (!vertexBuffers.Any(vb => vb.InputLayoutFields.Any(f => f.SemanticName == "COLOR")))
            {
                var defaultColor = new VertexDrawBuffer
                {
                    Handle = VectorOneVertexBuffer,
                    ElementSizeInBytes = 0, // required for the singular attribute to apply to all vertices
                    InputLayoutFields =
                    [
                        new VBIB.RenderInputLayoutField
                        {
                            SemanticName = "COLOR",
                            Format = DXGI_FORMAT.R32G32B32A32_FLOAT,
                        },
                    ],
                };

                vertexBuffers = [.. vertexBuffers, defaultColor];
            }

            return vertexBuffers;
        }
    }
}
