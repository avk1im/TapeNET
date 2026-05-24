using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Worker-thread <see cref="ITapeReadBackend"/> implementation: routes every
///  <see cref="ReadOneBlock"/> and <see cref="MoveToBlock"/> call through a single
///  dedicated background thread, keeping all drive I/O on one OS thread while allowing
///  the consumer (high-layer packer/pipelined-reader) to overlap processing with the
///  next read.
/// <para>
/// Threading model (consumer-driven pull):
/// <list type="bullet">
///  <item>The consumer calls <see cref="ReadOneBlock"/> or <see cref="MoveToBlock"/>
///   and <em>blocks</em> until the worker delivers the result.</item>
///  <item>At most one request is in-flight at a time. There is no queue; the consumer
///   issues each request synchronously from its perspective.</item>
///  <item>The worker loop: wait on <c>_readRequested</c>, execute the request, write
///   the result, signal <c>_readComplete</c>, loop.</item>
///  <item><see cref="MoveToBlock"/> dispatches a seek request through the same channel,
///   so seeks are serialised with reads on the drive thread.</item>
/// </list>
/// </para>
/// <para>
/// <c>_drivePositionBlock</c> tracks the worker thread's last known drive-head position.
///  <see cref="MoveToBlock"/> skips the physical seek when the requested block equals
///  the already-known position, avoiding an unnecessary tape command on sequential
///  forward reads.
/// </para>
/// </summary>
internal sealed class WorkerThreadTapeReadBackend : ITapeReadBackend
{
    private readonly TapeReadSink _readSink;
    private readonly TapeReadSeek _seekSink;
    private readonly ILogger _logger;
    private readonly Thread _worker;

    // Coordination: consumer sets _readRequested before handing off; worker sets
    //  _readComplete when done. Both are reset by the party that picks up the gate.
    private readonly ManualResetEventSlim _readRequested = new(initialState: false);
    private readonly ManualResetEventSlim _readComplete  = new(initialState: true);

    // Request slot: written by the consumer (under _lock) before setting _readRequested;
    //  read by the worker after waking. Result slot: written by the worker before setting
    //  _readComplete; read by the consumer after waking.
    private readonly object _lock = new();
    private RequestKind _requestKind = RequestKind.None;
    private byte[]? _requestBuffer;
    private int     _requestOffset;
    private long    _requestBlock;      // used for Seek requests

    private ReadResult _readResult;
    private bool       _seekResult;

    // Drive-head position as last known by the worker (updated after every read / seek).
    //  Stored on the consumer side of the lock too: the consumer reads it between calls
    //  to decide whether a seek is truly needed. Protected by _lock.
    private long _drivePositionBlock = -1;

    private bool _shutdownRequested;
    private bool _disposed;

    // -----------------------------------------------------------------------
    //  Request discriminator
    // -----------------------------------------------------------------------
    private enum RequestKind { None, Read, Seek }

    // -----------------------------------------------------------------------
    //  Construction
    // -----------------------------------------------------------------------

    /// <inheritdoc cref="ITapeReadBackend.BlockSize"/>
    public uint BlockSize { get; }

    public WorkerThreadTapeReadBackend(
        TapeReadSink readSink, TapeReadSeek seekSink, uint blockSize, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(readSink);
        ArgumentNullException.ThrowIfNull(seekSink);
        if (blockSize == 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");

        _readSink  = readSink;
        _seekSink  = seekSink;
        BlockSize  = blockSize;
        _logger    = logger ?? NullLogger.Instance;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "TapeReadBackend"
        };
        _worker.Start();
    }

    // -----------------------------------------------------------------------
    //  ITapeReadBackend
    // -----------------------------------------------------------------------

    /// <summary>
    /// Seek the drive head to <paramref name="blockNumber"/>, serialised through the
    ///  worker thread so no seek races with an in-flight read.
    /// <para>
    /// The physical seek is skipped when the worker's last known drive position already
    ///  equals <paramref name="blockNumber"/>, avoiding a redundant tape command on
    ///  monotonic forward reads where the drive head is already there.
    /// </para>
    /// </summary>
    public bool MoveToBlock(long blockNumber)
    {
        ThrowIfDisposed();

        // Fast-path: if the worker already knows the head is at this block, skip the
        //  physical seek. This is safe because we are the only caller of MoveToBlock
        //  and ReadOneBlock; no external actor moves the head between our calls.
        lock (_lock)
        {
            if (_drivePositionBlock == blockNumber)
                return true;
        }

        // Dispatch a seek request to the worker thread and wait for it to complete.
        DispatchAndWait(RequestKind.Seek, buffer: null, offset: 0, block: blockNumber);

        lock (_lock) { return _seekResult; }
    }

    /// <summary>
    /// Read exactly one block into <paramref name="buffer"/> at <paramref name="offset"/>,
    ///  executed on the worker thread. Blocks the caller until the read completes.
    /// </summary>
    public ReadResult ReadOneBlock(byte[] buffer, int offset)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + (int)BlockSize > buffer.Length)
            throw new ArgumentException(
                $"Buffer too small: offset {offset} + BlockSize {BlockSize} > buffer.Length {buffer.Length}.",
                nameof(buffer));

        DispatchAndWait(RequestKind.Read, buffer, offset, block: 0);

        lock (_lock) { return _readResult; }
    }

    // -----------------------------------------------------------------------
    //  Internal: dispatch / worker loop
    // -----------------------------------------------------------------------

    // Submits a request to the worker and waits for completion.
    //  Caller must NOT hold _lock when calling this.
    private void DispatchAndWait(RequestKind kind, byte[]? buffer, int offset, long block)
    {
        // Wait for the worker to be idle (previous request fully consumed).
        _readComplete.Wait();

        lock (_lock)
        {
            ThrowIfDisposed();

            Debug.Assert(_requestKind == RequestKind.None,
                "Worker should be idle after _readComplete is set.");

            _requestKind   = kind;
            _requestBuffer = buffer;
            _requestOffset = offset;
            _requestBlock  = block;

            // Arm the completion gate before signalling work; worker will set it when done.
            _readComplete.Reset();
            _readRequested.Set();
        }

        // Block until the worker signals completion.
        _readComplete.Wait();
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                _readRequested.Wait();

                RequestKind kind;
                byte[]?     buffer;
                int         bufOffset;
                long        requestedBlock;

                lock (_lock)
                {
                    if (_shutdownRequested)
                        return;

                    kind           = _requestKind;
                    buffer         = _requestBuffer;
                    bufOffset      = _requestOffset;
                    requestedBlock = _requestBlock;

                    // Clear request slot so DispatchAndWait's assertion holds.
                    _requestKind   = RequestKind.None;
                    _requestBuffer = null;
                    _readRequested.Reset();
                }

                if (kind == RequestKind.Seek)
                {
                    ExecuteSeek(requestedBlock);
                }
                else
                {
                    Debug.Assert(kind == RequestKind.Read);
                    Debug.Assert(buffer is not null);
                    ExecuteRead(buffer!, bufOffset);
                }

                lock (_lock)
                {
                    _readComplete.Set();
                }
            }
        }
        catch (Exception ex)
        {
            // Unexpected crash: record an error result so the consumer doesn't deadlock.
            _logger.LogError(ex, "TapeReadBackend worker thread terminated unexpectedly");
            lock (_lock)
            {
                _readResult        = new ReadResult(0, false, false, ex);
                _shutdownRequested = true;
                _requestKind       = RequestKind.None;
                _readComplete.Set();
            }
        }
    }

    private void ExecuteSeek(long blockNumber)
    {
        bool ok;
        try
        {
            ok = _seekSink(blockNumber);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TapeReadBackend seek sink threw");
            ok = false;
        }

        lock (_lock)
        {
            _seekResult = ok;
            // Update tracked position: on success the head is at the target block;
            //  on failure we no longer know where the head is.
            _drivePositionBlock = ok ? blockNumber : -1;
        }
    }

    private void ExecuteRead(byte[] buffer, int offset)
    {
        ReadResult result;
        try
        {
            result = _readSink(buffer, offset);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TapeReadBackend read sink threw");
            result = new ReadResult(0, false, false, ex);
        }

        lock (_lock)
        {
            _readResult = result;

            // Advance the tracked position when the read fully consumed a block.
            //  On tapemark / EOF / error the position is unknown (set to -1) so the
            //  next caller will not be misled into skipping a needed seek.
            if (result.Succeeded && !result.TapemarkEncountered && !result.EofEncountered
                && result.BytesRead == (int)BlockSize)
            {
                _drivePositionBlock++;
            }
            else
            {
                _drivePositionBlock = -1;
            }
        }
    }

    // -----------------------------------------------------------------------
    //  IDisposable
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;

        // Drain any in-flight request so the worker isn't mid-read when we tear down.
        try { _readComplete.Wait(); } catch { /* ignore */ }

        lock (_lock)
        {
            _disposed          = true;
            _shutdownRequested = true;
            // Wake the worker so it can observe the shutdown flag.
            _requestKind = RequestKind.None;
            _readComplete.Reset();
            _readRequested.Set();
        }

        try { _worker.Join(); } catch { /* ignore */ }

        _readRequested.Dispose();
        _readComplete.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, nameof(WorkerThreadTapeReadBackend));
}
