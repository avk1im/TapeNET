using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using TapeLibNET.Media;
using Windows.Win32.Foundation;

namespace TapeLibNET.Streams;


/// <summary>
/// Abstract base for buffered tape I/O streams. Wraps a <see cref="TapeStreamBuffer"/>
/// and tracks cumulative bytes via <see cref="Length"/>.
/// <para>Instances are created exclusively by <see cref="TapeStreamManager"/>.</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Disposal always signs out.</b> Completing the stream on disposal (the write stream's final flush) may fail —
///  end-of-media, a write fault. Whatever happens there, the stream still signs out from its manager
///  (<see cref="TapeStreamManager.OnDisposeStream"/>) and returns its buffer; only THEN is the failure rethrown. A
///  stream that failed to sign out stayed the manager's <see cref="TapeStreamManager.Stream"/>: the next
///  <c>ProduceWriteTOCStream</c> handed the zombie out again, and the next state change flushed its stale bytes —
///  even onto the next volume.
/// </para>
/// <para>
/// <b>Abandoning</b> (<see cref="Abandon"/>) is the manager's way out when it must drop a stream WITHOUT any I/O — a
///  media change. The owner's later <c>Dispose</c> is then a no-op.
/// </para>
/// </remarks>
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
    /// Set by the caller — or by the stream itself — when a write operation fails. When <see langword="true"/>,
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

    /// <summary>
    /// Completes the stream before it signs out — the write stream flushes its buffer here. May throw; the sign-out
    ///  happens regardless (see <see cref="Dispose(bool)"/>).
    /// </summary>
    protected virtual void CompleteOnDispose() { }

    /// <summary>
    /// Drops the stream WITHOUT any I/O and without signing out: no flush, no filemark. For the manager only, when it
    ///  must let go of a stream it can no longer complete — a media change. The owner's later <c>Dispose</c> is a no-op.
    /// </summary>
    internal void Abandon()
    {
        if (IsDisposed)
            return;
        m_logger.LogWarning("Abandoning {Type} with {Bytes} unwritten byte(s)", GetType().Name, m_buffer.ContentSize);
        m_buffer.Dispose();
        IsDisposed = true;
    }

    // overridable IDisposable implementation via virtual Dispose(bool)
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            base.Dispose(disposing);
            return;
        }

        m_logger.LogTrace("Disposing TapeStream with parameter {Param}", disposing);
        ExceptionDispatchInfo? failure = null;
        if (disposing)
        {
            // 1. Complete — may fail (EOM, write fault)
            try
            {
                CompleteOnDispose();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            // 2. Sign out from the manager — ALWAYS, even after a failed completion
            try
            {
                m_mgr.OnDisposeStream(this);
            }
            catch (Exception ex)
            {
                m_logger.LogWarning(ex, "Signing out from the stream manager failed");
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                m_buffer.Dispose();
            }
        }
        // no unmanaged resources to dispose
        IsDisposed = true;
        base.Dispose(disposing);

        // 3. Only now report the failure: the stream is gone, the manager is clean
        failure?.Throw();
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
    /// <summary>When <see langword="true"/>, reading stops at the first null byte.</summary>
    public bool TextFileMode { get; private set; }

    /// <summary>When <see langword="true"/>, <see cref="Read"/> enforces <see cref="LengthLimit"/>.</summary>
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

    protected override uint BufferSizeToAllocate => BlockSize;

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

    // Notice: disposing a read stream with unread buffered content is no error — nothing to complete.

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

    // Override Write and WriteAsync methods
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException(nameof(Write));
    }

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
/// <remarks>
/// <b>A failed write faults the stream.</b> The unwritten bytes are discarded and <see cref="TapeStream.WriteFailed"/>
///  is set, so disposal neither retries them — the same bytes would only fail again, or worse, land somewhere else
///  later — nor writes a trailing filemark behind a broken file.
/// </remarks>
public class TapeWriteStream : TapeStream
{
    // A write or flush failed: the buffered bytes are gone, and disposal must not try again.
    private bool m_faulted;

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
        m_faulted = false;
    }

    /// <inheritdoc/>
    /// <remarks>The final flush — skipped when the stream is faulted already.</remarks>
    protected override void CompleteOnDispose()
    {
        if (!m_faulted)
            Flush();    // faults the stream itself on failure
    }

    /// <summary>Discards the unwritten bytes and marks the stream failed — never written later.</summary>
    private void Fault()
    {
        m_faulted = true;
        WriteFailed = true;
        m_buffer.Reset();
    }

    /// <summary>
    /// The error for bytes the drive did not take: its own error if it reported one; else end-of-media if the write
    ///  itself signalled it (early warning / EOM); else a write fault — never a silent success (Design-TocLocation TL-7).
    /// </summary>
    private TapeIOException ShortWriteError(int notAccepted, string what)
    {
        if (WentBad)
            return new TapeIOException(m_mgr, this, what);
        uint code = EOFEncountered
            ? (uint)WIN32_ERROR.ERROR_END_OF_MEDIA
            : (uint)WIN32_ERROR.ERROR_WRITE_FAULT;
        return new TapeIOException(code,
            $"{what}: the drive accepted {notAccepted:N0} byte(s) fewer than offered without reporting an error");
    }

    internal int WriteDirect(byte[] buffer, int offset, int count)
    {
        // When writing a content set with the TOC co-located (TOC-in-set), a logical early warning means
        //  "stop content, leave room for the TOC" -- surfaced here as end-of-media so the caller wraps up
        //  exactly as it does for a hard EOM. While writing the TOC itself we ignore EW (only a real EOM
        //  counts) and the drive must not clamp the write to the reserve: the reserve IS the TOC's room
        //  (Design-TocLocation F2). An Initiator partition needs no reserve.
        bool contentWrite = m_mgr.ShouldStopContentOnEarlyWarning;
        var result = m_mgr.Drive.WriteDirect(buffer, offset, count, honorEwReserve: contentWrite,
            out bool tapemark, out bool ew, out bool eom);
        TapemarkEncountered = tapemark;
        bool ewStop = ew && contentWrite;
        EOFEncountered = eom || ewStop;
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

    /// <inheritdoc/>
    /// <remarks>Large writes can be sent directly to the device when the internal buffer is empty. Any failed
    ///  or short write faults the stream and prevents further writes.</remarks>
    /// <param name="buffer">The source byte array containing data to write.</param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin copying data.
    /// </param>
    /// <param name="count">The number of bytes to write from <paramref name="buffer"/>.</param>
    /// <exception cref="TapeIOException">
    /// The stream is faulted, a write operation fails, or the underlying device accepts fewer bytes than required.
    /// </exception>
    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckForRW(buffer, offset, count);
        Debug.Assert(m_buffer != null);

        if (m_faulted)
            throw new TapeIOException(m_mgr, this, "write after a failed write");

        while (count > 0)
        {
            if (m_buffer.IsFull)
                Flush();    // faults the stream itself on failure — including a short spill (F1)

            if (m_buffer.IsEmpty && count >= m_buffer.Capacity)
            {
                // Optimization: if count >= m_buffer.Capacity, write directly to tape from 'buffer' without using m_buffer
                int written = WriteDirect(buffer, offset, count);
                Debug.Assert(written <= count);

                // A failed write — or one the drive accepted nothing of: without this check the loop would spin
                //  forever on a 0-byte write that reported no error (F5).
                if (WentBad || written == 0)
                {
                    m_logger.LogDebug("Media stream WriteDirect error 0x{Error:X8} ({Written} of {Count} B) in {Method}",
                        LastError, written, count, nameof(Write));
                    var ex = ShortWriteError(count - written, "write failed");
                    Fault();
                    throw ex;
                }

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
                {
                    var ex = new TapeIOException(m_mgr, this, "write failed");
                    Fault();
                    throw ex;
                }
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="TapeIOException">
    /// The stream is faulted, a write operation fails, or the underlying device accepts fewer bytes than remained.
    /// </exception>
    public override void Flush()
    {
        CheckForRW();

        if (m_buffer.IsEmpty)
            return;

        m_buffer.SpillToOwner(m_buffer.ContentSize);
        if (WentOK && m_buffer.IsNonEmpty) // need to flush the rest, as a chunk of BlockSize
            m_buffer.SpillZeroPaddedToOwner((int)BlockSize);

        // Anything left in the buffer was NOT written — whether or not the drive said why. Reporting success here
        //  sealed an empty TOC copy with a filemark (F1); faulting the stream suppresses that filemark.
        if (WentBad || m_buffer.IsNonEmpty)
        {
            m_logger.LogError("Media stream flush error 0x{Error:X8}, {Left} B unwritten in {Method}",
                LastError, m_buffer.ContentSize, nameof(Flush));
            var ex = ShortWriteError(m_buffer.ContentSize, "flush incomplete");    // capture the error first
            Fault();
            throw ex;
        }

        // Notice: Here in Flush() we do not update the accumulated length, as it counts only bytes ingested by Write()
        //  and not the bytes actually written out to the tape
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
