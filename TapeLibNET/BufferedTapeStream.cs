using System.Buffers;
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
        private readonly int m_bufferSize;  // logical buffer size (ArrayPool may rent larger arrays)
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
            m_bufferSize = checked((int)(blockSize * (uint)bufferMultiplier));
            m_bufferA = ArrayPool<byte>.Shared.Rent(m_bufferSize);
            m_bufferB = ArrayPool<byte>.Shared.Rent(m_bufferSize);
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
                int remaining = m_bufferSize - m_fillOffset;
                int toCopy = Math.Min(count, remaining);

                Buffer.BlockCopy(buffer, offset, m_fillBuffer, m_fillOffset, toCopy);
                m_fillOffset += toCopy;
                offset += toCopy;
                count -= toCopy;
                m_accLength += toCopy;

                if (m_fillOffset == m_bufferSize)
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
                        // Ensure background task has completed before returning its buffer to the pool
                        try { m_pendingWrite?.GetAwaiter().GetResult(); } catch { }
                        m_pendingWrite = null;
                    }
                }
                finally
                {
                    m_disposed = true;
                    ArrayPool<byte>.Shared.Return(m_bufferA);
                    ArrayPool<byte>.Shared.Return(m_bufferB);
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


    /// <summary>
    /// Double-buffering wrapper for <see cref="TapeReadStream"/> that overlaps tape reads with file writes
    /// (or file reads in verify mode).
    /// A background task pre-fetches tape data into one buffer while the caller consumes the other.
    /// When the current buffer is exhausted, the caller waits for the pre-fetch to complete, swaps buffers,
    /// and starts a new background pre-fetch.
    /// <para>
    /// The inner <see cref="TapeReadStream"/> is never accessed concurrently — only one background read
    /// is outstanding at a time, and the caller only touches the pre-fetched buffer in the foreground.
    /// </para>
    /// <para>
    /// <b>Buffer sizing:</b> for optimal throughput <c>blockSize * bufferMultiplier</c> should be ≥ the inner
    /// stream's internal buffer capacity so that <see cref="TapeReadStream.Read(byte[], int, int)"/>
    /// takes its direct-read path. The default multiplier of 8 satisfies this for all filemark modes.
    /// </para>
    /// </summary>
    public class BufferedTapeReadStream : Stream
    {
        #region *** Private Fields ***

        private readonly Stream m_inner;
        private readonly byte[] m_bufferA;
        private readonly byte[] m_bufferB;
        private readonly int m_bufferSize;  // logical buffer size (ArrayPool may rent larger arrays)
        private byte[] m_readBuffer;       // buffer currently being consumed by Read()
        private int m_readOffset;           // current read position in m_readBuffer
        private int m_readAvail;            // number of valid bytes in m_readBuffer
        private Task<int>? m_pendingRead;   // background pre-fetch task (reading into the other buffer)
        private bool m_eof;
        private bool m_faulted;
        private bool m_disposed;
        private long m_accLength;

        #endregion


        #region *** Constructor ***

        /// <summary>
        /// Creates a double-buffered read wrapper around <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">The underlying tape read stream. Not disposed by this wrapper.</param>
        /// <param name="blockSize">Tape block size in bytes.</param>
        /// <param name="bufferMultiplier">Number of blocks per buffer. Must be ≥ 2.
        /// For filemark mode (inner buffer = BlockSize × 4), use ≥ 4 to ensure the inner stream's
        /// direct-read optimization is triggered. Default 8 covers all modes.</param>
        public BufferedTapeReadStream(Stream inner, uint blockSize, int bufferMultiplier = 8)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize, nameof(blockSize));
            ArgumentOutOfRangeException.ThrowIfLessThan(bufferMultiplier, 2, nameof(bufferMultiplier));

            m_inner = inner;
            m_bufferSize = checked((int)(blockSize * (uint)bufferMultiplier));
            m_bufferA = ArrayPool<byte>.Shared.Rent(m_bufferSize);
            m_bufferB = ArrayPool<byte>.Shared.Rent(m_bufferSize);
            m_readBuffer = m_bufferA;
        }

        #endregion


        #region *** Double-buffered Read ***

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(m_disposed, this);
            if (m_faulted)
                throw new InvalidOperationException("Cannot read from a faulted BufferedTapeReadStream");
            ValidateBufferArguments(buffer, offset, count);
            if (count == 0) return 0;

            int totalRead = 0;

            while (count > 0)
            {
                // Serve from current read buffer if it has data
                if (m_readOffset < m_readAvail)
                {
                    int toCopy = Math.Min(count, m_readAvail - m_readOffset);
                    Buffer.BlockCopy(m_readBuffer, m_readOffset, buffer, offset, toCopy);
                    m_readOffset += toCopy;
                    offset += toCopy;
                    count -= toCopy;
                    totalRead += toCopy;
                    m_accLength += toCopy;
                    continue;
                }

                // Read buffer exhausted
                if (m_eof)
                    break;

                // Swap to the pre-fetched buffer (or fill synchronously on first call)
                if (!SwapBuffers())
                    break; // EOF reached

                // Pre-fetch into the now-free buffer
                StartBackgroundRead();
            }

            return totalRead;
        }

        /// <summary>
        /// Waits for the pending background read (if any) or performs a synchronous read,
        /// then makes the result the current read buffer.
        /// </summary>
        /// <returns>true if data is available, false if EOF.</returns>
        private bool SwapBuffers()
        {
            int bytesRead;

            if (m_pendingRead != null)
            {
                // Background pre-fetch completed (or completing) — harvest its result
                try
                {
                    bytesRead = m_pendingRead.GetAwaiter().GetResult();
                }
                catch
                {
                    m_faulted = true;
                    throw;
                }
                finally
                {
                    m_pendingRead = null;
                }

                // The pending read was into the "other" buffer — swap to it
                m_readBuffer = (m_readBuffer == m_bufferA) ? m_bufferB : m_bufferA;
            }
            else
            {
                // No pending read — fill current buffer synchronously (first call)
                bytesRead = m_inner.Read(m_readBuffer, 0, m_bufferSize);
            }

            m_readOffset = 0;
            m_readAvail = bytesRead;

            if (bytesRead == 0)
            {
                m_eof = true;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Starts a background pre-fetch read into the buffer that is not currently being consumed.
        /// </summary>
        private void StartBackgroundRead()
        {
            if (m_eof || m_pendingRead != null)
                return;

            var bgBuffer = (m_readBuffer == m_bufferA) ? m_bufferB : m_bufferA;
            m_pendingRead = Task.Run(() => m_inner.Read(bgBuffer, 0, m_bufferSize));
        }

        #endregion


        #region *** Flush and Dispose ***

        public override void Flush()
        {
            // No-op for a read stream
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !m_disposed)
            {
                try
                {
                    // Wait for any outstanding background read before returning its buffer to the pool
                    if (m_pendingRead != null)
                    {
                        try { m_pendingRead.GetAwaiter().GetResult(); } catch { }
                        m_pendingRead = null;
                    }
                }
                finally
                {
                    m_disposed = true;
                    ArrayPool<byte>.Shared.Return(m_bufferA);
                    ArrayPool<byte>.Shared.Return(m_bufferB);
                }
            }

            base.Dispose(disposing);
        }

        #endregion


        #region *** Stream abstract members ***

        public override bool CanRead => !m_disposed;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => m_accLength;
        public override long Position
        {
            get => m_accLength;
            set => throw new NotSupportedException(nameof(Position));
        }
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException(nameof(Write));
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException(nameof(Seek));
        public override void SetLength(long value)
            => throw new NotSupportedException(nameof(SetLength));

        #endregion

    } // class BufferedTapeReadStream

}
