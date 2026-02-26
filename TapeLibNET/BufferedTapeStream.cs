using System.Diagnostics;


namespace TapeLibNET
{

    /// <summary>
    /// Double-buffering wrapper for <see cref="TapeWriteStream"/> that overlaps file reads with tape writes.
    /// Producer (caller thread) fills buffer A while consumer (background task) writes buffer B to tape via
    /// the inner stream. When buffer A is full, it is submitted to the background and the caller swaps to buffer B.
    /// <para>
    /// The inner <see cref="TapeWriteStream"/> must not be accessed concurrently — this class ensures
    /// only one background write is outstanding at a time.
    /// </para>
    /// <para>
    /// <b>Buffer sizing constraint:</b> <c>blockSize * bufferMultiplier</c> must be ≥ the inner stream's
    /// internal buffer capacity (<c>BlockSize * 2</c>) so that <see cref="TapeWriteStream.Write(byte[], int, int)"/>
    /// always takes its direct-write path and the inner buffer stays idle during streaming.
    /// </para>
    /// </summary>
    public class BufferedTapeWriteStream : Stream
    {
        #region *** Private Fields ***

        private readonly Stream m_inner;
        private readonly byte[] m_bufferA;
        private readonly byte[] m_bufferB;
        private byte[] m_fillBuffer;       // buffer currently being filled by Write()
        private int m_fillOffset;           // write position in m_fillBuffer
        private Task? m_pendingWrite;       // background write task (writing the other buffer to m_inner)
        private bool m_faulted;
        private bool m_disposed;
        private long m_accLength;

        #endregion


        #region *** Constructor ***

        /// <summary>
        /// Creates a double-buffered write wrapper around <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">The underlying tape write stream. Not disposed by this wrapper.</param>
        /// <param name="blockSize">Tape block size in bytes.</param>
        /// <param name="bufferMultiplier">Number of blocks per buffer. Must be ≥ 2 so that the inner stream's
        /// direct-write optimization is always triggered. Recommended 4–16.</param>
        public BufferedTapeWriteStream(Stream inner, uint blockSize, int bufferMultiplier = 8)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize, nameof(blockSize));
            ArgumentOutOfRangeException.ThrowIfLessThan(bufferMultiplier, 2, nameof(bufferMultiplier));

            m_inner = inner;
            int bufferSize = checked((int)(blockSize * (uint)bufferMultiplier));
            m_bufferA = ByteBufferCache.ProduceBuffer(bufferSize);
            m_bufferB = ByteBufferCache.ProduceBuffer(bufferSize);
            m_fillBuffer = m_bufferA;
        }

        #endregion


        #region *** Double-buffered Write ***

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(m_disposed, this);
            if (m_faulted)
                throw new InvalidOperationException("Cannot write to a faulted BufferedTapeWriteStream");
            ValidateBufferArguments(buffer, offset, count);

            while (count > 0)
            {
                int remaining = m_fillBuffer.Length - m_fillOffset;
                int toCopy = Math.Min(count, remaining);

                Buffer.BlockCopy(buffer, offset, m_fillBuffer, m_fillOffset, toCopy);
                m_fillOffset += toCopy;
                offset += toCopy;
                count -= toCopy;
                m_accLength += toCopy;

                if (m_fillOffset == m_fillBuffer.Length)
                    SubmitAndSwap();
            }
        }

        /// <summary>
        /// Submits the current fill buffer to a background task for writing to the inner stream,
        /// then swaps to the other buffer for continued filling.
        /// </summary>
        private void SubmitAndSwap()
        {
            WaitForPendingWrite();

            var bufToWrite = m_fillBuffer;
            int countToWrite = m_fillOffset;
            m_pendingWrite = Task.Run(() => m_inner.Write(bufToWrite, 0, countToWrite));

            // Swap to the other buffer
            m_fillBuffer = (m_fillBuffer == m_bufferA) ? m_bufferB : m_bufferA;
            m_fillOffset = 0;
        }

        /// <summary>
        /// Waits for the pending background write to complete.
        /// Rethrows any exception from the background task and sets <see cref="m_faulted"/>.
        /// </summary>
        private void WaitForPendingWrite()
        {
            if (m_pendingWrite != null)
            {
                try
                {
                    m_pendingWrite.GetAwaiter().GetResult();
                }
                catch
                {
                    m_faulted = true;
                    throw;
                }
                finally
                {
                    m_pendingWrite = null;
                }
            }
        }

        #endregion


        #region *** Flush and Dispose ***

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(m_disposed, this);

            WaitForPendingWrite();

            if (m_fillOffset > 0)
            {
                m_inner.Write(m_fillBuffer, 0, m_fillOffset);
                m_fillOffset = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !m_disposed)
            {
                try
                {
                    if (!m_faulted)
                        Flush();
                    else
                    {
                        // Ensure background task has completed before recycling its buffer
                        try { m_pendingWrite?.GetAwaiter().GetResult(); } catch { }
                        m_pendingWrite = null;
                    }
                }
                finally
                {
                    m_disposed = true;
                    ByteBufferCache.RecycleBuffer(m_bufferA);
                    ByteBufferCache.RecycleBuffer(m_bufferB);
                }
            }

            base.Dispose(disposing);
        }

        #endregion


        #region *** Stream abstract members ***

        public override bool CanRead => false;
        public override bool CanWrite => !m_disposed;
        public override bool CanSeek => false;
        public override long Length => m_accLength;
        public override long Position
        {
            get => m_accLength;
            set => throw new NotSupportedException(nameof(Position));
        }
        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException(nameof(Read));
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException(nameof(Seek));
        public override void SetLength(long value)
            => throw new NotSupportedException(nameof(SetLength));

        #endregion

    } // class BufferedTapeWriteStream

}
