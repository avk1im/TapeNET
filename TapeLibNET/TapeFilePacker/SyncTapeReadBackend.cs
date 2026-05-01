using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Default <see cref="ITapeReadBackend"/> implementation: synchronous, single-threaded,
/// no prefetch. Mirrors <see cref="WorkerThreadTapeWriteBackend"/> in role but is much
/// simpler -- Phase 2 read side has no async requirement (see Design §4.13.1, Step E).
/// </summary>
internal sealed class SyncTapeReadBackend : ITapeReadBackend
{
    private readonly TapeReadSink _readSink;
    private readonly TapeReadSeek _seekSink;
    private readonly ILogger _logger;
    private bool _disposed;

    public uint BlockSize { get; }

    public SyncTapeReadBackend(TapeReadSink readSink, TapeReadSeek seekSink, uint blockSize, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(readSink);
        ArgumentNullException.ThrowIfNull(seekSink);
        if (blockSize == 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");

        _readSink = readSink;
        _seekSink = seekSink;
        BlockSize = blockSize;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool MoveToBlock(long blockNumber)
    {
        ThrowIfDisposed();
        return _seekSink(blockNumber);
    }

    public ReadResult ReadBlocks(byte[] buffer, int bytesRequested)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesRequested);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytesRequested, buffer.Length);

        // Round down defensively; the sink also enforces block alignment.
        int aligned = (bytesRequested / (int)BlockSize) * (int)BlockSize;
        if (aligned == 0)
            return new ReadResult(0, false, false, null);

        try
        {
            return _readSink(buffer, aligned);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TapeReadBackend sink threw");
            return new ReadResult(0, false, false, ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SyncTapeReadBackend));
    }
}
