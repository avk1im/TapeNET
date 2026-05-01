namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// In-memory test backend. Records every handed-off buffer's content (block-aligned
/// copy) and supports scripted EOM / hard-error injection. Built on top of
/// <see cref="WorkerThreadTapeWriteBackend"/> so test coverage exercises the real
/// concurrency machinery; only the sink callback differs from production.
/// </summary>
internal sealed class MemoryTapeWriteBackend : ITapeWriteBackend
{
    private readonly WorkerThreadTapeWriteBackend _inner;
    private readonly object _stateLock = new();
    private readonly List<byte[]> _written = [];
    private readonly uint _blockSize;

    private long _capacityBlocks;          // -1 means unlimited
    private long _eomAfterBlock;           // -1 means no EOM scripted
    private long _errorAfterBlock;         // -1 means no error scripted
    private string? _errorMessage;
    private long _blocksWrittenSoFar;
    private TimeSpan _perWriteDelay;

    public uint BlockSize => _blockSize;

    /// <summary>Snapshot of all bytes successfully written so far, concatenated in submission order.</summary>
    public IReadOnlyList<byte[]> WrittenBuffers
    {
        get { lock (_stateLock) return _written.ToArray(); }
    }

    /// <summary>Total full blocks reported as written (sum of <see cref="WriteResult.BlocksWritten"/>).</summary>
    public long TotalBlocksWritten
    {
        get { lock (_stateLock) return _blocksWrittenSoFar; }
    }

    public MemoryTapeWriteBackend(uint blockSize)
    {
        if (blockSize == 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize));

        _blockSize = blockSize;
        _capacityBlocks = -1;
        _eomAfterBlock = -1;
        _errorAfterBlock = -1;

        _inner = new WorkerThreadTapeWriteBackend(Sink, blockSize);
    }

    /// <summary>Inject EOM behavior: the (1-based) block index after which the drive returns EOM.</summary>
    public void ScriptEomAfterBlocks(long blockIndex)
    {
        if (blockIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(blockIndex));
        lock (_stateLock) _eomAfterBlock = blockIndex;
    }

    /// <summary>Inject hard error after the given (1-based) block index has been written.</summary>
    public void ScriptHardErrorAfterBlocks(long blockIndex, string message = "scripted hardware error")
    {
        if (blockIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(blockIndex));
        lock (_stateLock)
        {
            _errorAfterBlock = blockIndex;
            _errorMessage = message;
        }
    }

    /// <summary>Optional artificial latency per write call - lets tests observe overlap.</summary>
    public void SetPerWriteDelay(TimeSpan delay)
    {
        lock (_stateLock) _perWriteDelay = delay;
    }

    private WriteResult Sink(byte[] buffer, int validBytes)
    {
        TimeSpan delay;
        long eomAfter;
        long errorAfter;
        string? errorMessage;
        long alreadyWritten;

        lock (_stateLock)
        {
            delay = _perWriteDelay;
            eomAfter = _eomAfterBlock;
            errorAfter = _errorAfterBlock;
            errorMessage = _errorMessage;
            alreadyWritten = _blocksWrittenSoFar;
        }

        if (delay > TimeSpan.Zero)
            Thread.Sleep(delay);

        int requestedBlocks = validBytes / (int)_blockSize;
        int blocksAccepted = requestedBlocks;
        bool eom = false;
        Exception? error = null;

        if (eomAfter >= 0 && alreadyWritten + blocksAccepted > eomAfter)
        {
            blocksAccepted = (int)Math.Max(0, eomAfter - alreadyWritten);
            eom = true;
        }

        if (errorAfter >= 0 && alreadyWritten + blocksAccepted > errorAfter)
        {
            blocksAccepted = (int)Math.Max(0, errorAfter - alreadyWritten);
            error = new InvalidOperationException(errorMessage ?? "scripted error");
            // Fire only once - subsequent writes should observe a clean backend.
            lock (_stateLock) _errorAfterBlock = -1;
        }

        int acceptedBytes = blocksAccepted * (int)_blockSize;
        if (acceptedBytes > 0)
        {
            var copy = new byte[acceptedBytes];
            Buffer.BlockCopy(buffer, 0, copy, 0, acceptedBytes);
            lock (_stateLock)
            {
                _written.Add(copy);
                _blocksWrittenSoFar += blocksAccepted;
            }
        }

        return new WriteResult(blocksAccepted, eom, error);
    }

    public void StartWriting(byte[] buffer, int validBytes) => _inner.StartWriting(buffer, validBytes);
    public WriteBackendStatus PollStatus() => _inner.PollStatus();
    public (WriteResult Result, byte[]? Buffer) AwaitCompletion() => _inner.AwaitCompletion();

    public void Dispose() => _inner.Dispose();
}
