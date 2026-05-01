using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Default <see cref="ITapeWriteBackend"/> implementation: serializes writes onto a
/// single dedicated worker thread, allowing the high layer to fill the next buffer
/// while the previous one is still being written to tape.
/// <para>
/// Single in-flight slot. <see cref="StartWriting"/> blocks until the previous
/// write completes; the result of that previous write is harvested into
/// <see cref="AwaitCompletion"/>'s return value (or surfaced lazily on the next
/// <see cref="StartWriting"/>). Cancellation is coarse - the worker observes it
/// only between writes; an in-flight <c>WriteDirect</c> call is the unit of
/// cancellability.
/// </para>
/// </summary>
internal sealed class WorkerThreadTapeWriteBackend : ITapeWriteBackend
{
    private readonly TapeWriteSink _sink;
    private readonly ILogger _logger;
    private readonly Thread _worker;

    // Coordination primitives: one "work available" gate signaled by StartWriting,
    //  one "work done" gate signaled by the worker.
    private readonly ManualResetEventSlim _workAvailable = new(initialState: false);
    private readonly ManualResetEventSlim _workComplete = new(initialState: true);

    // Protected by the lock; mutated only by the producer (under lock) before signaling
    //  _workAvailable, and by the worker (under lock) before signaling _workComplete.
    private readonly object _lock = new();
    private byte[]? _pendingBuffer;
    private int _pendingValidBytes;
    private byte[]? _completedBuffer;
    private WriteResult _completedResult;
    private bool _hasCompletedResult;
    private bool _shutdownRequested;
    private bool _disposed;

    public uint BlockSize { get; }

    public WorkerThreadTapeWriteBackend(TapeWriteSink sink, uint blockSize, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (blockSize == 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");

        _sink = sink;
        BlockSize = blockSize;
        _logger = logger ?? NullLogger.Instance;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "TapeWriteBackend"
        };
        _worker.Start();
    }

    public void StartWriting(byte[] buffer, int validBytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(validBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(validBytes, buffer.Length);

        ThrowIfDisposed();

        // Block until the previous write (if any) has finished.
        // Note: we deliberately drop the harvested result on the floor here when
        //  the producer doesn't call AwaitCompletion explicitly. The result will
        //  be re-collected by the next AwaitCompletion call, which is idempotent
        //  in the sense that it returns the most recent unconsumed completion.
        _workComplete.Wait();

        lock (_lock)
        {
            ThrowIfDisposed();

            Debug.Assert(_pendingBuffer is null, "Worker should be idle after _workComplete is set.");

            _pendingBuffer = buffer;
            _pendingValidBytes = validBytes;

            // Reset complete-gate; worker will set it after this work item finishes.
            _workComplete.Reset();
            _workAvailable.Set();
        }
    }

    public WriteBackendStatus PollStatus()
    {
        if (_disposed)
            return WriteBackendStatus.Idle;

        // Idle iff the work-complete gate is set AND there is no completion to harvest
        //  that the producer hasn't seen yet. We treat "completion present but unread"
        //  as Idle too - the worker is genuinely not working - so PollStatus's role is
        //  just "is the worker busy right now?".
        return _workComplete.IsSet ? WriteBackendStatus.Idle : WriteBackendStatus.Busy;
    }

    public (WriteResult Result, byte[]? Buffer) AwaitCompletion()
    {
        if (_disposed)
            return (WriteResult.Empty, null);

        _workComplete.Wait();

        lock (_lock)
        {
            if (!_hasCompletedResult)
                return (WriteResult.Empty, null);

            var result = _completedResult;
            var buffer = _completedBuffer;

            _hasCompletedResult = false;
            _completedBuffer = null;
            _completedResult = default;

            return (result, buffer);
        }
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                _workAvailable.Wait();

                byte[] buffer;
                int validBytes;

                lock (_lock)
                {
                    if (_shutdownRequested)
                        return;

                    Debug.Assert(_pendingBuffer is not null);
                    buffer = _pendingBuffer!;
                    validBytes = _pendingValidBytes;
                    _pendingBuffer = null;
                    _pendingValidBytes = 0;
                    _workAvailable.Reset();
                }

                WriteResult result;
                try
                {
                    // Round down to block boundary defensively; the sink also does this,
                    //  but enforcing it here keeps the contract local.
                    int aligned = (validBytes / (int)BlockSize) * (int)BlockSize;
                    result = _sink(buffer, aligned);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "TapeWriteBackend sink threw");
                    result = new WriteResult(BlocksWritten: 0, EomEncountered: false, Exception: ex);
                }

                lock (_lock)
                {
                    _completedBuffer = buffer;
                    _completedResult = result;
                    _hasCompletedResult = true;
                    _workComplete.Set();
                }
            }
        }
        catch (Exception ex)
        {
            // Worker crashed unexpectedly; record so producers don't deadlock.
            _logger.LogError(ex, "TapeWriteBackend worker thread terminated unexpectedly");
            lock (_lock)
            {
                _completedResult = new WriteResult(0, false, ex);
                _hasCompletedResult = true;
                _shutdownRequested = true;
                _workComplete.Set();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Drain any in-flight write so the buffer ownership invariant holds.
        try { _workComplete.Wait(); } catch { /* ignore */ }

        lock (_lock)
        {
            _disposed = true;
            _shutdownRequested = true;
            // Wake the worker so it can observe shutdown.
            _workAvailable.Set();
        }

        try { _worker.Join(); } catch { /* ignore */ }

        _workAvailable.Dispose();
        _workComplete.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkerThreadTapeWriteBackend));
    }
}
