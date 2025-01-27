using Microsoft.Extensions.Logging;
using System.Diagnostics;


namespace TapeLibNET
{

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


        public bool WentOK => m_mgr.WentOK;
        public bool WentBad => m_mgr.WentBad;
        public uint LastError => m_mgr.LastError;
        public string LastErrorMessage => m_mgr.LastErrorMessage;
        public bool EOFEncountered { get; protected set; } = false;
        public bool TapemarkEncountered { get; protected set; } = false;


        protected void CheckForRW(string methodName)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            m_mgr.Drive.CheckForRW(methodName);
        }
        protected void CheckForRW(string methodName, byte[] buffer, int offset, int count)
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


    public class TapeReadStream : TapeStream
    {
        public bool TextFileMode { get; private set; }
        public bool LengthLimitMode { get; private set; }
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
            CheckForRW(nameof(Read), buffer, offset, count);
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
                            throw new IOException($"ReadDirect error in {nameof(Read)}", (int)LastError);
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
            CheckForRW(nameof(Seek));

            // TODO: implement
            return Position;
        }

        // } end abstract method implementations

    } // class TapeReadStream


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
            CheckForRW(nameof(Write), buffer, offset, count);
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
                        m_logger.LogDebug("WriteDirect error 0x{Error:X8} in {Method}", LastError, nameof(Write));
                        throw new IOException($"WriteDirect error in {nameof(Write)}", (int)LastError);
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
                        throw new IOException($" in {nameof(Write)}", (int)LastError);
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
            CheckForRW(nameof(Flush));

            if (m_buffer.IsEmpty)
                return;

            m_buffer.SpillToOwner(m_buffer.ContentSize);

            if (WentOK && m_buffer.IsNonEmpty) // need to flush the rest, as a chunk of BlockSize
                m_buffer.SpillZeroPaddedToOwner((int)BlockSize);

            if (WentBad)
            {
                m_logger.LogError("Flush error 0x{Error:X8} in {Method}", LastError, nameof(Flush));
                throw new IOException($"Flush error in {nameof(Flush)}", (int)LastError);
            }

            // Notice: Here in Flush() we do not update Writte, as it counts only bytes that have been ingested by Write()
            //  and not the bytes actually witten out to the tape
        }

        // Override Seek method
        public override long Seek(long offset, SeekOrigin origin)
        {
            CheckForRW(nameof(Seek));

            // TODO: implement
            return Position;
        }

        // } end abstract method implementations

    } // class TapeWriteStream

}
