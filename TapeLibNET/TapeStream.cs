using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;


namespace TapeLibNET
{

    /// <summary>
    /// Abstract base for buffered tape I/O streams. Wraps a <see cref="TapeStreamBuffer"/>
    /// and tracks cumulative bytes via <see cref="Length"/>.
    /// <para>Instances are created exclusively by <see cref="TapeStreamManager"/>.</para>
    /// </summary>
    public abstract class TapeStream : Stream
    {
        protected readonly TapeStreamManager m_mgr;
        protected readonly TapeStreamBuffer m_buffer;
        private bool m_disposed = false;
        protected long m_accLength = 0;
        protected readonly ILogger<TapeStream> m_logger;

        protected uint BlockSize => m_mgr.Drive.BlockSize;

        public bool IsDisposed
        {
            get => m_disposed || m_buffer.IsDisposed;
            private set => m_disposed = value;
        }

        // a TapeStream object can only be prdouced by a TapeStreamManager
        internal TapeStream(TapeStreamManager mgr)
        {
            m_mgr = mgr;
            m_logger = m_mgr.Drive.LoggerFactory.CreateLogger<TapeStream>();
            int bufferSize = (int)BufferSizeToAllocate;
            ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, (int)BlockSize, nameof(BufferSizeToAllocate));
            m_buffer = new(this, bufferSize);

            m_logger.LogTrace("Created TapeStream with buffer size {Size} byte", bufferSize);
        }

        // Resets for a new use, i.e. to read or write a new file
        internal virtual void Reset()
        {
            Debug.Assert(!IsDisposed, "Attempting to reset disposed TapeStream.");

            m_buffer.Reset();
            m_accLength = 0;
            EOFEncountered = false;
            WriteFailed = false;
        }

        protected virtual uint BufferSizeToAllocate
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BlockSize, nameof(BlockSize));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(BlockSize, (uint)int.MaxValue, nameof(BlockSize));
                return BlockSize;
            }
        }


        /// <summary>Last drive operation succeeded.</summary>
        public bool WentOK => m_mgr.WentOK;
        /// <summary>Last drive operation failed.</summary>
        public bool WentBad => m_mgr.WentBad;
        /// <summary>Win32 error code of the last failed operation.</summary>
        public uint LastError => m_mgr.LastError;
        /// <summary>Human-readable message for <see cref="LastError"/>.</summary>
        public string LastErrorMessage => m_mgr.LastErrorMessage;
        /// <summary>Set when the tape signals end-of-data during a read.</summary>
        public bool EOFEncountered { get; protected set; } = false;
        /// <summary>Set when a filemark or setmark is hit during a read or write.</summary>
        public bool TapemarkEncountered { get; protected set; } = false;
        /// <summary>
        /// Set by the caller when a write operation fails. When <see langword="true"/>,
        ///  <see cref="TapeStreamManager"/> skips writing the trailing filemark on stream disposal
        ///  so that the tape can be repositioned without an orphan filemark in between.
        /// </summary>
        public bool WriteFailed { get; set; } = false;


        protected void CheckForRW([CallerMemberName] string methodName = "")
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            m_mgr.Drive.CheckForRW(methodName);
        }
        protected void CheckForRW(byte[] buffer, int offset, int count, [CallerMemberName] string methodName = "")
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ValidateBufferArguments(buffer, offset, count);
            m_mgr.Drive.CheckForRW(methodName);
        }


        // overridable IDisposable implementation via virtual Dispose(bool)
        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                m_logger.LogTrace("Disposing TapeStream with parameter {Param}", disposing);

                if (disposing)
                {
                    // dispose managed resources
                    m_mgr.OnDisposeStream(this);
                    m_buffer.Dispose();
                }
                // no unmanaged resources to dispose

                IsDisposed = true;
            }

            base.Dispose(disposing);
        }


        // { begin abstract method implementations
        public override long Length => m_accLength;
        public override long Position
        {
            get => m_accLength;
            set => throw new NotSupportedException(nameof(Position));
        }
        // } end abstract method implementations


    } // class TapeStream


    /// <summary>
    /// Read-only tape stream. Buffers data from the drive via <see cref="TapeStreamBuffer"/>
    /// and supports two optional modes:
    /// <list type="bullet">
    ///   <item><see cref="LengthLimitMode"/> — caps reads at an exact byte count (used to
    ///     read a file whose length is known from TOC).</item>
    ///   <item><see cref="TextFileMode"/> — stops at the first null byte (legacy text files).</item>
    /// </list>
    /// </summary>
    public class TapeReadStream : TapeStream
    {
        /// <summary>When <c>true</c>, reading stops at the first null byte.</summary>
        public bool TextFileMode { get; private set; }
        /// <summary>When <c>true</c>, <see cref="Read"/> enforces <see cref="LengthLimit"/>.</summary>
        public bool LengthLimitMode { get; private set; }
        /// <summary>
        /// Absolute byte limit. Setting a non-negative value activates <see cref="LengthLimitMode"/>;
        /// setting −1 deactivates it.
        /// </summary>
        public long LengthLimit
        {
            get => m_lengthLimit;
            set
            {
                if (value >= 0)
                {
                    ArgumentOutOfRangeException.ThrowIfLessThan(value, m_accLength, nameof(LengthLimit) + '.' + nameof(value));
                    m_lengthLimit = value;
                    LengthLimitMode = true;
                }
                else
                {
                    LengthLimitMode = false;
                    m_lengthLimit = -1;
                }

                m_logger.LogTrace("LengthLimitMode set to {LengthLimitMode}; LengthLimit to {LengthLimit}", LengthLimitMode, m_lengthLimit);
            }
        }
        private long m_lengthLimit = 0;
        /// <summary>Bytes remaining before <see cref="LengthLimit"/> is reached, or −1 if unlimited.</summary>
        public long RemainingLength
        {
            get => LengthLimitMode ? LengthLimit - m_accLength : -1;
            set
            {
                if (value >= 0)
                {
                    LengthLimit = m_accLength + value;
                }
                else
                {
                    LengthLimit = -1;
                }
            }
        }

        protected override uint BufferSizeToAllocate => m_mgr.Navigator.FmksMode ? base.BufferSizeToAllocate * 4 : BlockSize;
            // when reading without filemarks, must read just one block at a time


        // a TapeStream object can only be produced by a TapeStreamManager
        internal TapeReadStream(TapeStreamManager mgr, bool textFileMode = false, long lengthLimit = -1)
            : base(mgr)
        {
            TextFileMode = textFileMode;
            LengthLimit = lengthLimit;
        }

        internal void Reset(bool textFileMode, long lengthLimit = -1)
        {
            base.Reset(); // call first to reset accumulated length

            TextFileMode = textFileMode;
            LengthLimit = lengthLimit;
        }
        internal override void Reset()
        {
            Reset(false, -1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // This doesn't indicate a error, hence not needed
                /*
                if (m_buffer.IsNonEmpty)
                {
                    Debug.WriteLine("Warning: disposing TapeReadStream while m_buffer isn't empty.");
                }
                */
            }

            base.Dispose(disposing);
        }


        internal int ReadDirect(byte[] buffer, int offset, int count)
        {
            var result = m_mgr.Drive.ReadDirect(buffer, offset, count, out bool tapemark, out bool eof);
            TapemarkEncountered = tapemark;
            EOFEncountered = eof;
            return result;
        }


        // { begin abstract method implementations
        // Implement the abstract members from Stream
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false; // TODO: consider implementing some sort of Seek, e.g. to skip forward
        public override long Length => LengthLimitMode? LengthLimit : base.Length;
        public override void SetLength(long newLength)
        {
            if (LengthLimitMode && newLength == LengthLimit)
                return; // nothing to do

            ArgumentOutOfRangeException.ThrowIfLessThan(newLength, m_accLength, nameof(newLength));
            LengthLimit = newLength;
        }

        // Override Read and ReadAsync methods
        public override int Read(byte[] buffer, int offset, int count)
        {
            CheckForRW(buffer, offset, count);
            Debug.Assert(m_buffer != null);

            // do NOT yet check for EOFEncountered -- in case m_buffer still has some content!
            //if (EOFEncountered)
            //    return 0;

            if (LengthLimitMode)
            {
                if (RemainingLength <= 0)
                    return 0;
                count = Math.Min(count, (int)Math.Min(RemainingLength, int.MaxValue));
                Debug.Assert(count <= RemainingLength);
            }

            int read = 0;
            int offsetInit = offset;

            while (count > 0)
            {
                if (m_buffer.IsEmpty)
                {
                    // only now check for EOF -- after we've for sure ingested the remainder of m_buffer
                    if (EOFEncountered)
                        break; // cannot read from tape anymore, hence cannot refill m_buffer

                    if (count >= m_buffer.Capacity)
                    {
                        // Optimization: if count >= m_buffer.Capacity read directly from tape to 'buffer' without using m_buffer
                        int readDirectly = ReadDirect(buffer, offset, count);
                        if (WentBad)
                        {
                            m_logger.LogDebug("ReadDirect error 0x{Error:X8} in {Method}", LastError, nameof(Read));
                            throw new TapeIOException(m_mgr, this, "read failed");
                        }
                        if (readDirectly == 0)
                        {
                            EOFEncountered = true;
                            break; // no need to proceed to m_buffer.SpillTo() code below since m_buffer is still empty
                        }

                        offset += readDirectly;
                        count -= readDirectly;
                        read += readDirectly;

                        continue; // no need to proceed to m_buffer.SpillTo() code below since m_buffer is still empty
                    }
                    else // normal reading path via m_buffer
                    {
                        int buffered = m_buffer.FillFromOwner(m_buffer.Capacity);
                        if (buffered == 0)
                        {
                            EOFEncountered = true;
                            break; // no need to proceed to m_buffer.SpillTo() code below since m_buffer is still empty
                        }
                    }
                }

                int portion = m_buffer.SpillTo(buffer, offset, count);
                offset += portion;
                count -= portion;
                read += portion;

            } // while (count > 0)

            if (TextFileMode)
            {
                int firstZero = Array.FindIndex(buffer, offsetInit, read, by => by == 0x0);
                if (firstZero >= offsetInit)
                {
                    read = firstZero - offsetInit;
                    EOFEncountered = true;
                    // discard the rest of m_buffer so that we don't erroneously ingest it on the next call to Read()
                    m_buffer.Reset();
                }
            }

            m_accLength += read;

            return read;
        }

        /*
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // TODO: Implement your custom asynchronous Read logic
            // ...
        }
        */

        // Override Write and WriteAsync methods
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException(nameof(Write));
        }

        /*
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // TODO: Implement your custom asynchronous Write logic
            // ...
        }
        */

        public override void Flush()
        {
            throw new NotImplementedException(nameof(Write));
        }

        // Override Seek method
        public override long Seek(long offset, SeekOrigin origin)
        {
            CheckForRW();

            // TODO: implement
            return Position;
        }

        // } end abstract method implementations

    } // class TapeReadStream


    /// <summary>
    /// Write-only tape stream. Accumulates data in a <see cref="TapeStreamBuffer"/> of
    /// <c>2 × BlockSize</c> and writes full blocks to the drive. <see cref="Flush"/>
    /// zero-pads any partial trailing block to the next block boundary.
    /// </summary>
    public class TapeWriteStream : TapeStream
    {
        protected override uint BufferSizeToAllocate => BlockSize * 2;

        // a TapeStream object can only be produced by a TapeStreamManager
        internal TapeWriteStream(TapeStreamManager mgr)
            : base(mgr)
        {
            Reset();
        }

        internal override void Reset()
        {
            base.Reset();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Flush();

            base.Dispose(disposing);
        }

        internal int WriteDirect(byte[] buffer, int offset, int count)
        {
            var result = m_mgr.Drive.WriteDirect(buffer, offset, count, out bool tapemark, out bool eof);
            TapemarkEncountered = tapemark;
            EOFEncountered = eof;
            return result;
        }


        // { begin abstract method implementations
        // Implement the abstract members from Stream
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false; // TODO: consider implementing some sort of Seek, e.g. skipping n blocks forward
        public override void SetLength(long value)
        {
            throw new NotSupportedException(nameof(SetLength));
        }

        // Override Read and ReadAsync methods
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException(nameof(Read));
        }

        /*
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // TODO: Implement your custom asynchronous Read logic
            // ...
        }
        */

        // Override Write and WriteAsync methods
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckForRW(buffer, offset, count);
            Debug.Assert(m_buffer != null);

            while (count > 0)
            {
                if (m_buffer.IsFull)
                    Flush();

                if (m_buffer.IsEmpty && count >= m_buffer.Capacity)
                {
                    // Optimization: if count >= m_buffer.Capacity, write directly to tape from 'buffer' without using m_buffer
                    int written = WriteDirect(buffer, offset, count);
                    if (WentBad)
                    {
                        m_logger.LogDebug("Tape stream WriteDirect error 0x{Error:X8} in {Method}", LastError, nameof(Write));
                        throw new TapeIOException(m_mgr, this, "write failed");
                    }

                    Debug.Assert(written <= count);
                    offset += written;
                    count -= written;
                    m_accLength += written;
                }
                else
                {
                    // If m_buffer has content or it's larger than count, we have to first fill up the buffer
                    int buffered = m_buffer.FillFrom(buffer, offset, count);
                    Debug.Assert(buffered <= count);
                    offset += buffered;
                    count -= buffered;
                    m_accLength += buffered;

                    if (WentBad)
                        throw new TapeIOException(m_mgr, this, "write failed");
                }
            }
        }

        /*
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // TODO: Implement your custom asynchronous Write logic
            // ...
        }
        */

        public override void Flush()
        {
            CheckForRW();

            if (m_buffer.IsEmpty)
                return;

            m_buffer.SpillToOwner(m_buffer.ContentSize);

            if (WentOK && m_buffer.IsNonEmpty) // need to flush the rest, as a chunk of BlockSize
                m_buffer.SpillZeroPaddedToOwner((int)BlockSize);

            if (WentBad)
            {
                m_logger.LogError("Tape stream flush error 0x{Error:X8} in {Method}", LastError, nameof(Flush));
                throw new TapeIOException(m_mgr, this, "flush failed");
            }

            // Notice: Here in Flush() we do not update Writte, as it counts only bytes that have been ingested by Write()
            //  and not the bytes actually witten out to the tape
        }

        // Override Seek method
        public override long Seek(long offset, SeekOrigin origin)
        {
            CheckForRW();

            // TODO: implement
            return Position;
        }

        // } end abstract method implementations

    } // class TapeWriteStream

}
