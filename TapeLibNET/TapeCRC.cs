using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Hashing;

namespace TapeLibNET
{

    /// <summary>
    /// Abstract pass-through stream that intercepts <see cref="Read"/> and <see cref="Write"/>
    ///  calls via <see cref="OnRead"/> / <see cref="OnWrite"/> hooks before delegating to the inner stream.
    /// <para>Seeking is disabled because repositioning would break sequential filtering invariants.</para>
    /// </summary>
    /// <param name="inner">Inner stream to wrap.</param>
    /// <param name="ownInner">If <see langword="true"/>, disposes <paramref name="inner"/> on <see cref="Dispose(bool)"/>.</param>
    public abstract class FilterStream(Stream inner, bool ownInner = false) : Stream
    {
        /// <summary>Called after data is read from the inner stream. <paramref name="count"/> reflects actual bytes read.</summary>
        protected abstract void OnRead(byte[] buffer, int offset, int count);
        /// <summary>Called before data is written to the inner stream.</summary>
        protected abstract void OnWrite(byte[] buffer, int offset, int count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                if (ownInner)
                    inner.Dispose();
            base.Dispose(disposing);
        }

        #region *** Abstract method implementations ***
        public override long Length => inner.Length;
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false; // cannot seek since this may disturb the filtering
        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException(nameof(Seek));
        public override void SetLength(long value) => inner.SetLength(value);
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException(nameof(Position)); // cannot seek since this may disturb the filtering
        }
        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int result = inner.Read(buffer, offset, count);
            OnRead(buffer, offset, result);
            return result;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            OnWrite(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        #endregion *** Abstract method implementations ***

    } // class FliterStream

    /// <summary>
    /// <see cref="FilterStream"/> that feeds every read/written byte span into a
    ///  <see cref="NonCryptographicHashAlgorithm"/> for incremental hash computation.
    /// </summary>
    /// <param name="inner">Inner stream to wrap.</param>
    /// <param name="hasher">Hash algorithm instance that accumulates data via <see cref="NonCryptographicHashAlgorithm.Append"/>.</param>
    /// <param name="ownInner">If <see langword="true"/>, disposes <paramref name="inner"/> on dispose.</param>
    public class HashingStream(Stream inner, NonCryptographicHashAlgorithm hasher,
        bool ownInner = false) : FilterStream(inner, ownInner)
    {

        #region *** Abstract method implementations ***
        protected override void OnRead(byte[] buffer, int offset, int count)
        {
            hasher.Append(new ReadOnlySpan<byte>(buffer, offset, count));
        }
        protected override void OnWrite(byte[] buffer, int offset, int count)
        {
            hasher.Append(new ReadOnlySpan<byte>(buffer, offset, count));
        }
        #endregion *** Abstract method implementations ***

    } // class HashingStream


    /// <summary>Extension methods for <see cref="Stream"/>.</summary>
    public static class StreamHelpers
    {
        /// <summary>
        /// Compares two streams byte-by-byte from their current positions using SIMD-vectorized
        ///  <see cref="MemoryExtensions.SequenceEqual{T}"/> and pooled buffers via <see cref="ByteBufferCache"/>.
        /// </summary>
        /// <returns><see langword="true"/> if both streams yield identical byte sequences until EOF.</returns>
        public static bool CompareTo(this Stream stream1, Stream stream2, int bufferSize = 81920) // same default buffer size as in Stream.CopyTo()
        {
            if (stream1 == stream2)
                return true;
            if (stream1 == null || stream2 == null)
                return false;

            // Do not compare Length as Length might be computed dynamically

            // Compare both streams from the current position onwards

            using var cache1 = new ByteBufferCache(bufferSize);
            using var cache2 = new ByteBufferCache(bufferSize);

            int read1, read2;
            while ((read1 = stream1.Read(cache1.Buffer, 0, cache1.Capacity)) > 0)
            {
                read2 = stream2.Read(cache2.Buffer, 0, read1);
                if (read1 != read2)
                    return false;

                // Compare only the valid bytes, not the full (potentially oversized) pool arrays
                //  SequenceEqual on byte spans is SIMD-vectorized in .NET 8 (not byte-by-byte)
                if (!cache1.Buffer.AsSpan(0, read1).SequenceEqual(cache2.Buffer.AsSpan(0, read2)))
                    return false;
            }

            return true;
        }

    }
}
