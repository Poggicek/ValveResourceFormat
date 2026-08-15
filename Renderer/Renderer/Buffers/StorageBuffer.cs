using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace ValveResourceFormat.Renderer.Buffers
{
    /// <summary>
    /// Shader storage buffer object for large read-write data arrays on the GPU.
    /// </summary>
    public class StorageBuffer : Buffer
    {
        private bool readback;

        /// <summary>Initializes a new storage buffer bound to the given reserved slot.</summary>
        public StorageBuffer(ReservedBufferSlots bindingPoint)
            : base(BufferTarget.ShaderStorageBuffer, (int)bindingPoint, bindingPoint.ToString())
        {
        }

        /// <inheritdoc/>
        /// <remarks>A readback buffer is host visible and persistently mapped; everything else this type
        /// allocates is device local. <see cref="RHI.OpenGL.GlBarrierTranslation"/> reads this to decide
        /// whether a shader write has to be made visible to the client mapping. It is decided before the
        /// storage is created, because <see cref="RHI.BufferDesc.Memory"/> is fixed at creation.</remarks>
        protected override RHI.BufferMemory RhiMemory => readback
            ? RHI.BufferMemory.HostReadback
            : RHI.BufferMemory.DeviceLocal;

        /// <summary>Gets the persistently mapped storage of a readback buffer, or an empty span.</summary>
        /// <remarks>Replaces the raw mapped pointer this type used to keep. The mapping now belongs to the
        /// <see cref="RHI.IBuffer"/>, which is what makes it work on both backends: OpenGL maps immutable
        /// storage persistently and Vulkan maps host visible memory, and neither needs a pointer held here.</remarks>
        private Span<byte> Mapped => readback && Size > 0 ? RhiBuffer.MappedData : default;

        /// <summary>Allocates a new storage buffer sized for the given number of elements.</summary>
        /// <remarks><see cref="BufferUsageHint.DynamicRead"/> asks for host visible readback memory, which
        /// is persistently mapped; anything else is device local.</remarks>
        /// <typeparam name="T">The element type used to compute the total byte size.</typeparam>
        /// <param name="bindingPoint">The reserved slot to bind the buffer to.</param>
        /// <param name="elements">Number of elements to allocate space for.</param>
        /// <param name="usage">The intended usage hint for the buffer.</param>
        /// <returns>The newly allocated <see cref="StorageBuffer"/>.</returns>
        public static StorageBuffer Allocate<T>(ReservedBufferSlots bindingPoint, int elements, BufferUsageHint usage)
        {
            var buffer = new StorageBuffer(bindingPoint)
            {
                readback = usage == BufferUsageHint.DynamicRead,
            };

            buffer.EnsureStorage(elements * Unsafe.SizeOf<T>());
            return buffer;
        }

        /// <summary>Uploads the contents of a list to this buffer, replacing any existing data.</summary>
        public void Create<T>(List<T> data) where T : struct
        {
            Create(ListAccessors<T>.GetBackingArray(data), data.Count * Unsafe.SizeOf<T>());
        }

        /// <summary>Uploads a typed array to this buffer with the given total byte size.</summary>
        /// <param name="data">The source array to upload.</param>
        /// <param name="totalSizeInBytes">Total number of bytes to upload from <paramref name="data"/>.</param>
        public void Create<T>(T[] data, int totalSizeInBytes) where T : struct
        {
            EnsureStorage(totalSizeInBytes);
            Upload(MemoryMarshal.AsBytes(data.AsSpan())[..totalSizeInBytes]);
        }

        /// <summary>Uploads a read-only span to this buffer using the specified usage hint.</summary>
        /// <param name="data">The source span to upload.</param>
        /// <param name="usageHint">The intended usage pattern for the buffer.</param>
        public void Create<T>(ReadOnlySpan<T> data, BufferUsageHint usageHint) where T : struct
        {
            EnsureStorage(data.Length * Unsafe.SizeOf<T>());
            Upload(MemoryMarshal.AsBytes(data));
        }

        /// <summary>Updates a region of this buffer with new data, allocating it first if empty.</summary>
        /// <param name="data">The source array containing new data.</param>
        /// <param name="offset">Byte offset into the buffer at which to begin writing.</param>
        /// <param name="size">Number of bytes to write.</param>
        public void Update<T>(T[] data, int offset, int size) where T : struct
        {
            if (Size == 0)
            {
                if (offset == 0)
                {
                    Create(data, size);
                    return;
                }

                throw new InvalidOperationException("Trying to update an uninitialized buffer.");
            }

            var mapped = Mapped;

            if (!mapped.IsEmpty)
            {
                Debug.Assert(offset + size <= Size);
                MemoryMarshal.AsBytes(data.AsSpan())[..size].CopyTo(mapped[offset..]);
                return;
            }

            Upload(MemoryMarshal.AsBytes(data.AsSpan())[..size], offset);
        }

        /// <summary>Zeroes the entire contents of this buffer.</summary>
        public void Clear() => Fill(0);

        /// <summary>Fills the entire buffer with a repeating 32 bit value.</summary>
        /// <param name="value">The value written to every 32 bit word.</param>
        /// <remarks>
        /// A host visible buffer is filled through its mapping. A device local one is filled by uploading
        /// the pattern, which replaces <c>glClearNamedBufferData</c>: the RHI's equivalent is
        /// <see cref="RHI.ICommandList.FillBuffer"/>, and that needs a command list this type does not
        /// hold. Uploading is correct on both backends and these buffers are small counter blocks, so the
        /// difference does not matter; a caller filling a large buffer every frame should record
        /// <see cref="RHI.ICommandList.FillBuffer"/> instead.
        /// </remarks>
        public void Fill(uint value)
        {
            if (Size <= 0)
            {
                return;
            }

            var mapped = Mapped;

            if (!mapped.IsEmpty)
            {
                MemoryMarshal.Cast<byte, uint>(mapped).Fill(value);
                return;
            }

            var words = Size / sizeof(uint);
            var pattern = ArrayPool<uint>.Shared.Rent(words);

            try
            {
                pattern.AsSpan(0, words).Fill(value);
                Upload(MemoryMarshal.AsBytes(pattern.AsSpan(0, words)));
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(pattern);
            }
        }

        /// <summary>Reads the buffer's contents back from the GPU into the given struct.</summary>
        /// <param name="output">The struct to populate with buffer data.</param>
        /// <exception cref="InvalidOperationException">The buffer is not host visible, so there is nothing
        /// to read without a command list.</exception>
        /// <remarks>Only a <see cref="RHI.BufferMemory.HostReadback"/> buffer can be read this way, which
        /// is what <see cref="Allocate"/> creates for <see cref="BufferUsageHint.DynamicRead"/>. Reading a
        /// device local buffer needs <see cref="RHI.ICommandList.CopyTextureToBuffer"/>-style staging and a
        /// submitted copy, which this type cannot do; <c>glGetNamedBufferSubData</c> used to hide that.</remarks>
        public unsafe void Read<T>(ref T output) where T : struct
        {
            Debug.Assert(Size <= Unsafe.SizeOf<T>());

            var mapped = Mapped;

            if (mapped.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Storage buffer '{Name}' is {RhiMemory} and cannot be read back directly. Allocate it with {nameof(BufferUsageHint.DynamicRead)} to get host visible memory.");
            }

            output = MemoryMarshal.Read<T>(mapped);
        }
    }
}
