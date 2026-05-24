using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Phase 2 high-layer read packer. Mirrors <see cref="TapeFileWritePacker"/>: hides tape
/// block boundaries and intra-block file offsets behind a per-file <see cref="Stream"/>
/// façade returned by <see cref="BeginRead"/>.
/// <para>
/// Implements the small LRU ring cache described in Design §4.4: <c>K</c> block-sized
/// slots, lookup by block number, sequential forward extension reads, no prefetch in
/// Phase 2. At most one read slot may be open at a time.
/// </para>
/// </summary>
internal sealed class TapeFileReadPacker : ITapeFileReader, ITapeReadStreamHost
{
    private readonly ITapeReadBackend _backend;
    private readonly ILogger _logger;
    private readonly int _blockSize;
    private readonly int _slotCount;

    // Single contiguous buffer; each slot owns a [_blockSize]-sized region.
    private byte[] _ringBuffer;
    private readonly long[] _slotBlock;     // block number held by each slot, -1 if empty
    private readonly int[] _slotValid;      // valid bytes in each slot (≤ _blockSize)
    private readonly long[] _slotTick;      // LRU tick
    private long _tickCounter;

    // Open-read state
    private bool _readOpen;
    private long _readCurrentAbsByte;       // next byte to read, absolute on tape
    private long _readEndAbsByte;           // exclusive end of current file
    private TapeReadStreamFacade? _openStream;

    // Last block known to be at the drive head, for sequential reads without seek.
    //  -1 means unknown (any next read must seek first).
    private long _drivePositionBlock = -1;
    private bool _disposed;

    /// <summary>Block size in bytes (mirrors <see cref="ITapeReadBackend.BlockSize"/>).</summary>
    public int BlockSize => _blockSize;

    /// <summary>True while a file is open between <see cref="BeginRead"/> and <see cref="EndRead"/>.</summary>
    public bool IsFileOpen => _readOpen;

    public TapeFileReadPacker(ITapeReadBackend backend, int slotCount = 16, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (slotCount < 1)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Must be ≥ 1.");

        _backend = backend;
        _logger = logger ?? NullLogger.Instance;
        _blockSize = checked((int)backend.BlockSize);
        _slotCount = slotCount;

        _ringBuffer = ArrayPool<byte>.Shared.Rent(checked(slotCount * _blockSize));
        _slotBlock = new long[slotCount];
        _slotValid = new int[slotCount];
        _slotTick = new long[slotCount];
        for (int i = 0; i < slotCount; i++)
            _slotBlock[i] = -1;
    }

    // -----------------------------------------------------------------------
    //  Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Open a logical read slot for one file at <paramref name="addr"/> spanning
    /// <paramref name="length"/> bytes. Returns a <see cref="Stream"/> for sequential reads.
    /// </summary>
    public TapeReadStreamFacade BeginRead(TapeAddress addr, long length)
    {
        ThrowIfDisposed();
        if (_readOpen)
            throw new InvalidOperationException("A file is already open; call EndRead first.");
        if (!addr.IsValid)
            throw new ArgumentException("Invalid tape address.", nameof(addr));
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _readCurrentAbsByte = checked(addr.Block * (long)_blockSize + addr.Offset);
        _readEndAbsByte = checked(_readCurrentAbsByte + length);
        _readOpen = true;

        _openStream = new TapeReadStreamFacade(this, length);
        return _openStream;
    }

    /// <summary>Close the open read slot. Cached blocks are retained for the next caller.</summary>
    public void EndRead()
    {
        if (!_readOpen)
            return;

        _openStream?.MarkClosed();
        _openStream = null;
        _readOpen = false;
    }

    // -----------------------------------------------------------------------
    //  Internal: stream-facing read hook
    // -----------------------------------------------------------------------

    /// <summary>Called by <see cref="TapeReadStreamFacade"/> on each <c>Read</c> call.</summary>
    public int ReadIntoOpenFile(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (!_readOpen)
            throw new InvalidOperationException("Read called with no file open.");

        long remainingFile = _readEndAbsByte - _readCurrentAbsByte;
        if (remainingFile <= 0 || count == 0)
            return 0;

        int toRead = (int)Math.Min(count, remainingFile);
        int totalRead = 0;

        while (toRead > 0)
        {
            long block = _readCurrentAbsByte / _blockSize;
            int offsetInBlock = (int)(_readCurrentAbsByte % _blockSize);

            int slot = EnsureBlockCached(block);
            if (slot < 0)
                break; // EOF / tapemark before requested length satisfied

            int valid = _slotValid[slot];
            if (offsetInBlock >= valid)
                break; // partial block / EOF reached inside this block

            int available = valid - offsetInBlock;
            int chunk = Math.Min(available, toRead);

            Buffer.BlockCopy(_ringBuffer, slot * _blockSize + offsetInBlock,
                buffer, offset, chunk);

            offset += chunk;
            totalRead += chunk;
            toRead -= chunk;
            _readCurrentAbsByte += chunk;
        }

        return totalRead;
    }

    // -----------------------------------------------------------------------
    //  Cache management
    // -----------------------------------------------------------------------

    // Returns the slot index containing `block`, populating the cache if needed.
    //  Returns -1 if the block could not be read (EOM/tapemark with zero bytes).
    private int EnsureBlockCached(long block)
    {
        // Lookup
        for (int i = 0; i < _slotCount; i++)
        {
            if (_slotBlock[i] == block)
            {
                _slotTick[i] = ++_tickCounter;
                return i;
            }
        }

        // Miss: pick LRU victim.
        int victim = PickLruVictim();

        // Determine whether a seek is required. If `block` immediately follows the
        //  drive head's last position, we can read forward without seeking.
        bool needSeek = _drivePositionBlock != block;
        if (needSeek)
        {
            if (!_backend.MoveToBlock(block))
            {
                _logger.LogWarning("TapeFileReadPacker: MoveToBlock({Block}) failed", block);
                _drivePositionBlock = -1;
                return -1;
            }
            _drivePositionBlock = block;
        }

        // Read directly into the ring slot at its offset in the contiguous buffer,
        //  avoiding a temporary ArrayPool allocation that the old ReadBlocks path required.
        int slotOffset = victim * _blockSize;
        ReadResult result = _backend.ReadOneBlock(_ringBuffer, slotOffset);
        if (result.Exception is not null)
        {
            _logger.LogWarning(result.Exception, "TapeFileReadPacker: ReadOneBlock for block {Block} failed", block);
            _drivePositionBlock = -1;
            throw new IOException($"Tape read failed at block {block}.", result.Exception);
        }

        // Advance our notion of drive position to the next block, regardless of
        //  partial-read amount, since the drive itself advances per-block.
        _drivePositionBlock = block + 1;

        if (result.BytesRead <= 0)
        {
            // Tapemark/EOM with zero bytes: do not populate the slot.
            _slotBlock[victim] = -1;
            _slotValid[victim] = 0;
            return -1;
        }

        _slotBlock[victim] = block;
        _slotValid[victim] = Math.Min(result.BytesRead, _blockSize);
        _slotTick[victim] = ++_tickCounter;
        return victim;
    }

    private int PickLruVictim()
    {
        int victim = 0;
        long oldestTick = long.MaxValue;
        for (int i = 0; i < _slotCount; i++)
        {
            if (_slotBlock[i] == -1)
                return i;
            if (_slotTick[i] < oldestTick)
            {
                oldestTick = _slotTick[i];
                victim = i;
            }
        }
        return victim;
    }

    // -----------------------------------------------------------------------
    //  Disposal
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;

        EndRead();

        if (_ringBuffer is not null)
        {
            try { ArrayPool<byte>.Shared.Return(_ringBuffer); } catch { /* ignore */ }
            _ringBuffer = null!;
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(TapeFileReadPacker));
    }
}
