using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Phase 2 high-layer write packer. Accumulates source bytes from one or more files into
/// a fill buffer of <c>blockMultiplier × BlockSize</c>; hands full-block-aligned chunks
/// to an <see cref="ITapeWriteBackend"/>; and reports each file's final
/// <see cref="TapeAddress"/> via <see cref="FilesCommitted"/> only after that file's tail
/// block is durably on tape.
/// <para>
/// Single-threaded usage: the packer's public methods are not safe to call concurrently.
/// Concurrency with the underlying tape is hidden inside the backend; from the agent's
/// point of view <see cref="TapeWriteStream.Write"/> is synchronous.
/// </para>
/// <para>
/// At most one file is "open" at any time (between <see cref="BeginFile"/> and
/// <see cref="EndFile"/>). Closed files become "pending commit" until their tail block
/// is committed, at which point they are reported via <see cref="FilesCommitted"/>.
/// </para>
/// </summary>
internal sealed class TapeFileWritePacker : IDisposable
{
    // -----------------------------------------------------------------------
    //  Construction & external dependencies
    // -----------------------------------------------------------------------

    private readonly ITapeWriteBackend _backend;
    private readonly Action<long>? _rewindToBlock;
    private readonly SourceErrorMode _sourceErrorMode;
    private readonly ILogger _logger;
    private readonly int _blockSize;
    private readonly int _bufferCapacity;

    // -----------------------------------------------------------------------
    //  Buffer & tape position state
    // -----------------------------------------------------------------------

    // Current fill buffer (we own it until handing off to backend, at which point
    //  ownership transfers and we replace it with a fresh one).
    private byte[] _fillBuffer;

    // Bytes written into _fillBuffer that have NOT yet been handed to the backend.
    //  Includes leftover sub-block bytes carried over from the previous handoff.
    private int _fillPos;

    // Absolute byte offset on tape corresponding to _fillBuffer[0].
    //  Equals _committedTapeBlock*BlockSize + _inflightValidBytes.
    private long _baseAbsByteOfFill;

    // Number of full blocks the backend has reported as durably written.
    private long _committedTapeBlock;

    // Bytes currently being written by the backend (0 when backend idle).
    private int _inflightValidBytes;

    // -----------------------------------------------------------------------
    //  File registry
    // -----------------------------------------------------------------------

    private sealed class PendingEntry
    {
        public CommitToken Token;
        public long StartAbsByte;
        public long Length;     // updated by stream.Write while open; final at EndFile
        public bool IsOpen;
    }

    // Insertion-ordered list of pending entries. List is small (≤ ~blockMultiplier
    //  small files in flight typically), so a List<T> with linear scans is fine.
    private readonly List<PendingEntry> _pending = new();
    private PendingEntry? _openEntry;
    private ulong _nextSequence = 1;

    // The stream façade exposed to the agent for the currently open file.
    private TapeWriteStreamFacade? _openStream;

    // Lifecycle
    private bool _disposed;

    // -----------------------------------------------------------------------

    /// <param name="backend">Low-layer write backend (worker thread or test fake).</param>
    /// <param name="rewindToBlock">
    ///  Callback used by <see cref="DiscardOpenFile"/> in <see cref="SourceErrorMode.Rollback"/>
    ///  mode and by <see cref="RollbackPending"/>. Pass <c>b =&gt; mgr.Drive.MoveToBlock((int)b)</c>
    ///  in production. May be <c>null</c> in unit tests that never exercise rollback paths.
    /// </param>
    /// <param name="blockMultiplier">Buffer size = <c>blockMultiplier × BlockSize</c>. Must be ≥ 1.</param>
    /// <param name="sourceErrorMode">How <see cref="DiscardOpenFile"/> handles already-flushed bytes.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="initialAbsBlock">
    ///  Absolute drive block where the packer's first byte will land. Defaults to 0 for
    ///  unit tests / first-set scenarios. Production callers (e.g. <see cref="TapeStreamManager"/>)
    ///  pass <c>Drive.BlockCounter</c> at packer-construction time so that the
    ///  <see cref="TapeAddress"/> values reported via <see cref="FilesCommitted"/> are
    ///  absolute on-tape coordinates -- matching the legacy backup's TOC convention and
    ///  enabling correct packed restore across multi-set tapes.
    /// </param>
    public TapeFileWritePacker(
        ITapeWriteBackend backend,
        Action<long>? rewindToBlock = null,
        int blockMultiplier = 16,
        SourceErrorMode sourceErrorMode = SourceErrorMode.NoRollback,
        ILogger? logger = null,
        long initialAbsBlock = 0)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (blockMultiplier < 1)
            throw new ArgumentOutOfRangeException(nameof(blockMultiplier), "Must be ≥ 1.");
        if (initialAbsBlock < 0)
            throw new ArgumentOutOfRangeException(nameof(initialAbsBlock), "Must be ≥ 0.");

        _backend = backend;
        _rewindToBlock = rewindToBlock;
        _sourceErrorMode = sourceErrorMode;
        _logger = logger ?? NullLogger.Instance;
        _blockSize = checked((int)backend.BlockSize);
        _bufferCapacity = checked(blockMultiplier * _blockSize);

        _fillBuffer = ArrayPool<byte>.Shared.Rent(_bufferCapacity);

        // Anchor packer position to the current drive head so addresses are absolute.
        _committedTapeBlock = initialAbsBlock;
        _baseAbsByteOfFill = initialAbsBlock * (long)_blockSize;
    }

    // -----------------------------------------------------------------------
    //  Public events & state
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fired (synchronously, on the calling thread) whenever one or more files cross the
    /// commit boundary as a side effect of <see cref="TapeWriteStream.Write"/>,
    /// <see cref="EndFile"/>, <see cref="Flush"/>, or <see cref="Dispose"/>.
    /// </summary>
    public event Action<IReadOnlyList<CommittedFile>>? FilesCommitted;

    /// <summary>True while a file is open (between <see cref="BeginFile"/> and <see cref="EndFile"/>).</summary>
    public bool IsFileOpen => _openEntry is not null;

    /// <summary>Block size in bytes (mirrors <see cref="ITapeWriteBackend.BlockSize"/>).</summary>
    public int BlockSize => _blockSize;

    // -----------------------------------------------------------------------
    //  Public API: BeginFile / EndFile
    // -----------------------------------------------------------------------

    /// <summary>
    /// Open a logical write slot for one file. Returns a stream the caller writes the
    /// source bytes into. The file's <see cref="TapeAddress"/> is not known yet; it is
    /// reported via <see cref="FilesCommitted"/> after the file's tail block is on tape.
    /// <para>May throw <see cref="TapePackerEndOfMediaException"/> since attempts to harvest!</para>
    /// </summary>
    public TapeWriteStreamFacade BeginFile()
    {
        ThrowIfDisposed();
        if (_openEntry is not null)
            throw new InvalidOperationException("A file is already open; call EndFile first.");

        // Proactive harvest: cheap polling check so EOM/exception surfaces here rather
        //  than one-buffer-late. See §4.13.7.
        TryProactiveHarvest();

        var entry = new PendingEntry
        {
            Token = new CommitToken(_nextSequence++),
            StartAbsByte = _baseAbsByteOfFill + _fillPos,
            Length = 0,
            IsOpen = true
        };
        _pending.Add(entry);
        _openEntry = entry;

        _openStream = new TapeWriteStreamFacade(this, entry.Token);
        return _openStream;
    }

    /// <summary>
    /// Close the open file. Returns its <see cref="CommitToken"/> for correlation with
    /// the eventual <see cref="FilesCommitted"/> notification.
    /// <para>Does NOT throw <see cref="TapePackerEndOfMediaException"/> since doesn't attempt to harvest.</para>
    /// </summary>
    public CommitToken EndFile()
    {
        ThrowIfDisposed();
        if (_openEntry is null)
            throw new InvalidOperationException("No file is open.");

        var token = _openEntry.Token;
        _openEntry.IsOpen = false;
        _openEntry = null;

        // Mark the stream closed so further writes throw.
        _openStream?.MarkClosed();
        _openStream = null;

        // The tail-block of this file may already be inside the fill buffer,
        //  but it is NOT durably on tape until the buffer is flushed and harvested.
        //  We do not promote here. Promotion happens on the next harvest.
        TryPromoteCommittables();

        return token;
    }

    // -----------------------------------------------------------------------
    //  Public API: discard / rollback / flush
    // -----------------------------------------------------------------------

    /// <summary>
    /// Discard the open file according to <see cref="SourceErrorMode"/>. Both modes
    /// truncate the still-buffered tail of the open file. Rollback mode additionally
    /// repositions the tape head past the last committed file when any of the open
    /// file's content has already been flushed.
    /// </summary>
    public void DiscardOpenFile()
    {
        ThrowIfDisposed();
        if (_openEntry is null)
            throw new InvalidOperationException("No file is open.");

        var entry = _openEntry;

        if (_sourceErrorMode == SourceErrorMode.NoRollback)
        {
            // Truncate fill back to the open file's start (or to 0 if the start has
            //  already left the buffer via a flush — those bytes stay as on-tape garbage).
            long startInFill = entry.StartAbsByte - _baseAbsByteOfFill;
            if (startInFill >= 0 && startInFill <= _fillPos)
                _fillPos = (int)startInFill;
            else if (startInFill < 0)
                _fillPos = 0;     // open file's start has already been flushed; truncate the buffer tail
            // else (startInFill > _fillPos): impossible — start can't be ahead of write head.
        }
        else // SourceErrorMode.Rollback
        {
            DiscardOpenFile_Rollback(entry);
        }

        // Remove the open entry from the pending registry without commit notification.
        _pending.Remove(entry);
        _openEntry = null;
        _openStream?.MarkClosed();
        _openStream = null;
    }

    private void DiscardOpenFile_Rollback(PendingEntry entry)
    {
        // The open file's start absbyte; everything ≥ this is to be reclaimed.
        long startAbs = entry.StartAbsByte;
        bool flushedAlready = startAbs < _baseAbsByteOfFill;

        if (!flushedAlready)
        {
            // Identical to NoRollback common case — nothing on tape yet.
            long startInFill = startAbs - _baseAbsByteOfFill;
            _fillPos = (int)startInFill;
            return;
        }

        if (_rewindToBlock is null)
            throw new InvalidOperationException(
                "DiscardOpenFile in Rollback mode requires a rewindToBlock callback (none was provided).");

        // Drain backend so committed counters are up to date.
        HarvestNow();

        // Sum of committed file lengths = startAbs of open file (files are appended).
        //  The "block immediately after the last committed file" = ceil(startAbs / blockSize).
        //  When startAbs is a block boundary, that block is fresh; otherwise the last
        //  committed file's tail shares it and we accept the open file's first partial
        //  block as on-tape garbage (see §4.13.5).
        long targetBlock = (startAbs + _blockSize - 1) / _blockSize;

        try
        {
            _rewindToBlock(targetBlock);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rewind to block {Block} failed during DiscardOpenFile", targetBlock);
            throw;
        }

        // Discard the entire fill buffer; reset state to the rewind position.
        _fillPos = 0;
        _baseAbsByteOfFill = targetBlock * (long)_blockSize;
        _committedTapeBlock = targetBlock;
        _inflightValidBytes = 0;
    }

    /// <summary>
    /// Discard ALL pending-commit files (open and closed); reposition the tape past the
    /// last fully-committed block. Intended for EOM recovery before switching media.
    /// Returns the rolled-back tokens in original order.
    /// </summary>
    public IReadOnlyList<CommitToken> RollbackPending()
    {
        ThrowIfDisposed();

        // Close the open file's stream first, so further writes throw.
        if (_openEntry is not null)
        {
            _openStream?.MarkClosed();
            _openStream = null;
            _openEntry = null;
        }

        if (_pending.Count == 0)
            return Array.Empty<CommitToken>();

        var rolled = new CommitToken[_pending.Count];
        for (int i = 0; i < _pending.Count; i++)
            rolled[i] = _pending[i].Token;
        _pending.Clear();

        // Drain the backend so the committed pointer is final.
        HarvestNow(suppressEomThrow: true);

        // Reset fill buffer to the committed boundary.
        _fillPos = 0;
        _baseAbsByteOfFill = _committedTapeBlock * (long)_blockSize;

        // Optional rewind: caller may want the tape head exactly at the committed
        //  boundary. We only call rewind if a callback was supplied.
        _rewindToBlock?.Invoke(_committedTapeBlock);

        return rolled;
    }

    /// <summary>
    /// Drain everything: zero-pads the trailing partial block, hands off the final
    /// buffer, awaits backend completion, and fires <see cref="FilesCommitted"/> for
    /// all newly committed tokens. Idempotent; safe to call after errors.
    /// </summary>
    public void Flush()
    {
        if (_disposed)
            return;

        // If there is buffered content, zero-pad to a block boundary and hand off.
        if (_fillPos > 0)
        {
            int padded = ((_fillPos + _blockSize - 1) / _blockSize) * _blockSize;
            if (padded > _fillPos)
                Array.Clear(_fillBuffer, _fillPos, padded - _fillPos);
            _fillPos = padded;

            DoFlushFillBuffer();
        }

        // Drain whatever may still be in flight.
        HarvestNow();
    }

    // -----------------------------------------------------------------------
    //  Internal API: stream-facing write hook
    // -----------------------------------------------------------------------

    /// <summary>Called by <see cref="TapeWriteStreamFacade"/> on each <c>Write</c> call.</summary>
    internal void WriteFromOpenFile(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (_openEntry is null)
            throw new InvalidOperationException("Write called with no file open.");

        while (count > 0)
        {
            int free = _bufferCapacity - _fillPos;
            if (free == 0)
            {
                DoFlushFillBuffer();
                continue;
            }

            int chunk = Math.Min(free, count);
            Buffer.BlockCopy(buffer, offset, _fillBuffer, _fillPos, chunk);
            _fillPos += chunk;
            offset += chunk;
            count -= chunk;
            _openEntry.Length += chunk;
        }
    }

    // -----------------------------------------------------------------------
    //  Buffer handoff & harvesting
    // -----------------------------------------------------------------------

    // Hands off the block-aligned prefix of _fillBuffer to the backend and rotates in
    //  a fresh fill buffer with the trailing sub-block bytes carried over.
    private void DoFlushFillBuffer()
    {
        int validBytes = (_fillPos / _blockSize) * _blockSize;
        if (validBytes == 0)
            return;

        // Harvest the previous write before posting a new one (StartWriting blocks
        //  internally too, but we want the result harvested into our state machine).
        HarvestNow();

        int leftover = _fillPos - validBytes;

        var newFill = ArrayPool<byte>.Shared.Rent(_bufferCapacity);
        if (leftover > 0)
            Buffer.BlockCopy(_fillBuffer, validBytes, newFill, 0, leftover);

        // Hand off; ownership of the old buffer transfers to the backend until harvest.
        _backend.StartWriting(_fillBuffer, validBytes);
        _inflightValidBytes = validBytes;

        _fillBuffer = newFill;
        _fillPos = leftover;
        _baseAbsByteOfFill += validBytes;
    }

    // Cheap, non-blocking "if idle, harvest result" used by BeginFile.
    private void TryProactiveHarvest()
    {
        if (_backend.PollStatus() == WriteBackendStatus.Idle)
            HarvestNow();
    }

    // Awaits any in-flight write, applies its result to packer state, releases the
    //  returned buffer to the pool, fires FilesCommitted for newly-promoted tokens,
    //  and surfaces hard errors / EOM as exceptions.
    private void HarvestNow(bool suppressEomThrow = false)
    {
        var (result, returnedBuffer) = _backend.AwaitCompletion();
        if (returnedBuffer is not null)
            ArrayPool<byte>.Shared.Return(returnedBuffer);

        if (result.BlocksWritten == 0 && result.Exception is null && !result.EomEncountered)
            return;     // nothing to do (idempotent harvest)

        long blocksWritten = result.BlocksWritten;
        _committedTapeBlock += blocksWritten;
        _inflightValidBytes = 0;

        TryPromoteCommittables();

        if (result.Exception is not null)
        {
            // Hard error: the backend did not fully drain its handed-off buffer. The
            //  unwritten suffix is gone; pending tokens beyond the committed boundary
            //  are unrecoverable. Roll them back and surface the exception.
            RollbackUncommittedPending();
            throw new IOException("Tape write failed.", result.Exception);
        }

        if (result.EomEncountered)
        {
            var rolled = CollectAndRollbackUncommittedPending();
            if (!suppressEomThrow)
                throw new TapePackerEndOfMediaException(rolled);
        }
    }

    // -----------------------------------------------------------------------
    //  Promotion & post-EOM rollback bookkeeping
    // -----------------------------------------------------------------------

    private void TryPromoteCommittables()
    {
        if (_pending.Count == 0)
            return;

        long committedAbsByte = _committedTapeBlock * (long)_blockSize;

        List<CommittedFile>? committed = null;
        int writeIdx = 0;
        for (int readIdx = 0; readIdx < _pending.Count; readIdx++)
        {
            var entry = _pending[readIdx];
            if (!entry.IsOpen && entry.StartAbsByte + entry.Length <= committedAbsByte)
            {
                committed ??= new List<CommittedFile>();
                committed.Add(new CommittedFile(
                    entry.Token,
                    new TapeAddress(
                        Block: entry.StartAbsByte / _blockSize,
                        Offset: (uint)(entry.StartAbsByte % _blockSize)),
                    Length: entry.Length));
            }
            else
            {
                if (writeIdx != readIdx)
                    _pending[writeIdx] = entry;
                writeIdx++;
            }
        }
        if (writeIdx != _pending.Count)
            _pending.RemoveRange(writeIdx, _pending.Count - writeIdx);

        if (committed is { Count: > 0 })
            FilesCommitted?.Invoke(committed);
    }

    // For EOM: roll back closed-but-unpromoted entries; collect their tokens.
    private CommitToken[] CollectAndRollbackUncommittedPending()
    {
        if (_pending.Count == 0)
            return Array.Empty<CommitToken>();

        var rolled = new List<CommitToken>(_pending.Count);
        for (int i = 0; i < _pending.Count; i++)
        {
            var e = _pending[i];
            if (e.IsOpen)
                continue;       // leave open entry alone; agent will Discard it
            rolled.Add(e.Token);
        }

        _pending.RemoveAll(e => !e.IsOpen);

        // Reset fill to the committed boundary; everything in fill is gone.
        _fillPos = 0;
        _baseAbsByteOfFill = _committedTapeBlock * (long)_blockSize;

        // If an open entry survives, its StartAbsByte is now stale (anything past the
        //  committed boundary is gone). Clamp it forward; further writes will fail at
        //  the next flush but the agent should react to the EOM exception by Discarding.
        if (_openEntry is not null && _openEntry.StartAbsByte < _baseAbsByteOfFill)
        {
            _openEntry.StartAbsByte = _baseAbsByteOfFill;
            _openEntry.Length = 0;
        }

        return rolled.ToArray();
    }

    // For hard errors: same as EOM rollback but no separate token list returned.
    private void RollbackUncommittedPending()
        => CollectAndRollbackUncommittedPending();

    // -----------------------------------------------------------------------
    //  Disposal
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;

        // Flush BEFORE flipping the disposed flag — Flush itself early-returns when disposed.
        //  EOM during the final flush is the only error condition worth propagating to the
        //  caller (so the agent can roll back uncommitted files and continue on a new volume).
        //  Capture it, complete disposal cleanup, then rethrow. Other exceptions are logged
        //  and swallowed -- disposal must not leak resources.
        TapePackerEndOfMediaException? pendingEom = null;
        try { Flush(); }
        catch (TapePackerEndOfMediaException eom) { pendingEom = eom; }
        catch (Exception ex) { _logger.LogDebug(ex, "Flush during TapeFileWritePacker.Dispose threw"); }

        _disposed = true;

        // Return the current fill buffer to the pool. Any in-flight buffer was already
        //  returned by the final HarvestNow inside Flush.
        if (_fillBuffer is not null)
        {
            try { ArrayPool<byte>.Shared.Return(_fillBuffer); } catch { /* ignore */ }
            _fillBuffer = null!;
        }

        _openStream?.MarkClosed();
        _openStream = null;

        if (pendingEom is not null)
            throw pendingEom;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TapeFileWritePacker));
    }
}
