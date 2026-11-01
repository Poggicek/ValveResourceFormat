using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ValveResourceFormat.Renderer.Buffers
{
    /// <summary>
    /// The element ranges of a CPU copy that changed since it was last uploaded, so a frame uploads only
    /// those. Ranges close together upload as one, since a larger copy is cheaper than another call.
    /// </summary>
    internal sealed class DirtyRanges
    {
        private const int MergeGap = 64;

        private readonly List<(int Start, int End)> ranges = [];

        /// <summary>Marks the elements from <paramref name="start"/> up to <paramref name="end"/> changed.</summary>
        public void Add(int start, int end)
        {
            if (ranges.Count > 0)
            {
                // Writes mostly come in ascending order, which extends the last range
                ref var last = ref CollectionsMarshal.AsSpan(ranges)[^1];

                if (start >= last.Start && start <= last.End + MergeGap)
                {
                    last.End = Math.Max(last.End, end);
                    return;
                }
            }

            ranges.Add((start, end));
        }

        /// <summary>
        /// Writes <paramref name="value"/> at <paramref name="index"/>, and marks it changed only when it
        /// differs from what is there.
        /// </summary>
        public void Store<T>(Span<T> data, int index, in T value) where T : unmanaged
        {
            ref var slot = ref data[index];

            if (MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in slot)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))))
            {
                return;
            }

            slot = value;
            Add(index, index + 1);
        }

        /// <summary>Uploads the changed ranges of <paramref name="data"/> to <paramref name="buffer"/>, and forgets them.</summary>
        public void Upload<T>(StorageBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged
        {
            if (ranges.Count > 1)
            {
                Coalesce();
            }

            foreach (var (start, end) in ranges)
            {
                buffer.Update(data[start..end], start * Unsafe.SizeOf<T>());
            }

            ranges.Clear();
        }

        /// <summary>Forgets the changed ranges, once all of the data has been uploaded.</summary>
        public void Clear() => ranges.Clear();

        private void Coalesce()
        {
            ranges.Sort();

            var merged = CollectionsMarshal.AsSpan(ranges);
            var count = 1;

            for (var i = 1; i < merged.Length; i++)
            {
                ref var last = ref merged[count - 1];

                if (merged[i].Start <= last.End + MergeGap)
                {
                    last.End = Math.Max(last.End, merged[i].End);
                }
                else
                {
                    merged[count++] = merged[i];
                }
            }

            CollectionsMarshal.SetCount(ranges, count);
        }
    }
}
