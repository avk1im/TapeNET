using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Worker-thread pipelined read packer. Mirrors <see cref="TapeFileWritePacker"/> on the
/// read side: a dedicated background thread greedily prefetches blocks from the underlying
/// <see cref="ITapeReadBackend"/> into a ring buffer; the consumer reads through a
/// <see cref="TapeReadStreamFacade"/> that drains slots in producer order. Sequential
/// restore over many small files therefore overlaps tape I/O with consumer processing.
/// <para>
/// Coordination uses a single <see cref="Monitor"/> on <c>_lock</c>; the ring is a strict
/// FIFO indexed by <c>_producerIndex</c> / <c>_consumerIndex</c>. The worker performs
/// the actual <see cref="ITapeReadBackend.ReadOneBlock"/> call outside the lock so the
/// consumer can drain prefetched slots concurrently.
/// </para>
/// <para>
/// Cache policy: on <see cref="BeginRead"/>, if the requested <see cref="TapeAddress.Block"/>
/// lies inside the buffered window (between the consumer's next-to-read block and the
/// worker's next-to-prefetch block) the consumer simply advances locally - no tape I/O
/// ("seek-and-resume"). Otherwise the ring is flushed (after any in-flight read settles)
/// and the worker is signalled to seek to the new address ("seek-and-restart").
/// </para>
/// <para>
/// At most one file may be open at a time, matching <see cref="ITapeFileReader"/>.
/// </para>
/// </summary>
internal sealed class TapeFilePipelinedReader : ITapeFileReader, ITapeReadStreamHost
{
    // -----------------------------------------------------------------------
    //  Slot model
    // -----------------------------------------------------------------------
    private enum SlotState
    {
        Empty,          // free to be filled by worker
        Prefetching,    // worker has reserved this slot and is reading into it
        Ready,          // data ready; consumer may read
    }

    private sealed class Slot
    {
        public SlotState State;
        public long Block;           // absolute tape block held in this slot
        public int  ValidBytes;      // 0..BlockSize
        public bool EndOfStream;     // tapemark or EOF reported for this read
        public Exception? Error;     // hard read error captured for surfacing
    }

    // -----------------------------------------------------------------------
    //  Construction & state
    // -----------------------------------------------------------------------

    private readonly ITapeReadBackend _backend;
    private readonly ILogger _logger;
    private readonly int _blockSize;
    private readonly int _slotCount;

    private byte[] _ringBuffer;           // contiguous: slot i lives at [i*BlockSize .. +BlockSize)
    private readonly Slot[] _slots;

    private readonly Thread _worker;
    private readonly object _lock = new();

    private int _producerIndex;           // next slot the worker will fill
    private int _consumerIndex;           // next slot the consumer will read
    private long _prefetchBlock = -1;     // next absolute block the worker will fetch (-1 = idle)
    private long _prefetchEndBlockExcl = -1; // exclusive upper bound of blocks the worker may prefetch
                                          //  for the currently armed file (-1 = unbounded / not armed).
                                          //  Prevents the worker from reading past the open file's last
                                          //  block, which on partitioned media would consume the trailing
                                          //  content setmark and break the subsequent set transition.
    private bool _prefetchHalted;         // worker pauses until next BeginRead rearms
    private bool _seekPending;            // worker should seek before its next read
    private long _pendingSeekBlock;

    private bool _readOpen;
    private long _readCurrentAbsByte;     // next byte the consumer will deliver
    private long _readEndAbsByte;         // exclusive end of current file
    private TapeReadStreamFacade? _openStream;

    private bool _shutdown;
    private bool _disposed;

    // -----------------------------------------------------------------------
    //  ITapeFileReader
    // -----------------------------------------------------------------------

    public int BlockSize => _blockSize;
    public bool IsFileOpen => _readOpen;

    public TapeFilePipelinedReader(
        ITapeReadBackend backend, int slotCount = 16, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (slotCount < 2)
            throw new ArgumentOutOfRangeException(nameof(slotCount),
                "Pipelined reader needs at least two slots (one in flight, one queued).");

        _backend   = backend;
        _logger    = logger ?? NullLogger.Instance;
        _blockSize = checked((int)backend.BlockSize);
        _slotCount = slotCount;

        _ringBuffer = ArrayPool<byte>.Shared.Rent(checked(slotCount * _blockSize));
        _slots = new Slot[slotCount];
        for (int i = 0; i < slotCount; i++)
            _slots[i] = new Slot { State = SlotState.Empty, Block = -1 };

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "TapeFilePipelinedReader"
        };
        _worker.Start();
    }

    // -----------------------------------------------------------------------
    //  Public API
    // -----------------------------------------------------------------------

    public TapeReadStreamFacade BeginRead(TapeAddress addr, long length)
    {
        ThrowIfDisposed();
        if (_readOpen)
            throw new InvalidOperationException("A file is already open; call EndRead first.");
        if (!addr.IsValid)
            throw new ArgumentException("Invalid tape address.", nameof(addr));
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        long startAbsByte = checked(addr.Block * (long)_blockSize + addr.Offset);
        long endAbsByte   = checked(startAbsByte + length);

        // Exclusive upper bound on blocks the worker may prefetch for this file: the
        //  block holding the last data byte, plus one. For zero-length reads, no block
        //  is needed at all (bound == start). This stops the worker from reading the
        //  block that follows the file payload, which on the Partitions layout is the
        //  trailing content setmark — and skipping that setmark would leave the drive
        //  past EOM and break the next EndReadContentSet / MoveToTargetContentSet.
        long endBlockExcl = length > 0
            ? ((endAbsByte - 1) / _blockSize) + 1
            : addr.Block;

        lock (_lock)
        {
            ArmReadSession_NoLock(addr.Block, endBlockExcl);
        }

        _readCurrentAbsByte = startAbsByte;
        _readEndAbsByte     = endAbsByte;
        _readOpen           = true;
        _openStream         = new TapeReadStreamFacade(this, length);
        return _openStream;
    }

    public void EndRead()
    {
        if (!_readOpen)
            return;

        _openStream?.MarkClosed();
        _openStream = null;
        _readOpen = false;

        // Park the worker so it doesn't keep prefetching blocks for a file that's
        //  no longer open. The next BeginRead will re-arm a fresh range.
        lock (_lock)
        {
            _prefetchEndBlockExcl = -1;
            _prefetchHalted = true;
            Monitor.PulseAll(_lock);
        }
    }

    // -----------------------------------------------------------------------
    //  ITapeReadStreamHost
    // -----------------------------------------------------------------------

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

            int slotIdx;
            Slot slot;
            int valid;
            Exception? error;
            lock (_lock)
            {
                if (!WaitForHeadReady_NoLock(out slotIdx))
                    return totalRead;

                slot = _slots[slotIdx];

                // If the head slot doesn't match the requested block, we've raced with a
                //  seek-and-restart in flight; surface what we have so far.
                if (slot.Block != block)
                    return totalRead;

                error = slot.Error;
                valid = slot.ValidBytes;
            }

            if (error is not null)
            {
                ReleaseHeadSlot(slotIdx);
                throw new IOException($"Tape read failed at block {block}.", error);
            }

            if (offsetInBlock >= valid)
            {
                // Partial / EOS block - nothing more available from here.
                return totalRead;
            }

            int available = valid - offsetInBlock;
            int chunk = Math.Min(available, toRead);

            Buffer.BlockCopy(_ringBuffer, slotIdx * _blockSize + offsetInBlock,
                buffer, offset, chunk);

            offset += chunk;
            totalRead += chunk;
            toRead -= chunk;
            _readCurrentAbsByte += chunk;

            if (offsetInBlock + chunk >= valid)
                ReleaseHeadSlot(slotIdx);
        }

        return totalRead;
    }

    // -----------------------------------------------------------------------
    //  Session arming (seek-and-resume vs seek-and-restart)
    // -----------------------------------------------------------------------

    private void ArmReadSession_NoLock(long startBlock, long endBlockExcl)
    {
        // The requested block is in-window iff it is currently buffered in a Ready slot
        //  at or after the consumer head, OR it is the next block the worker is about to
        //  fetch (and prefetch has not been halted). The _prefetchHalted flag only stops
        //  *new* prefetch work; cached blocks remain valid regardless.
        long headBlock = HeadSlotBlock_NoLock();
        bool inCache =
            headBlock >= 0 &&
            startBlock >= headBlock &&
            ((_prefetchBlock > 0 && startBlock < _prefetchBlock) || HasReadySlotFor_NoLock(startBlock));

        if (inCache)
        {
            AdvanceConsumerPast_NoLock(startBlock);
            // Re-arm the prefetch bound for the new file; the worker may have halted at
            //  the previous file's boundary, so reopen the gate up to the new end.
            _prefetchEndBlockExcl = endBlockExcl;
            if (_prefetchBlock < endBlockExcl)
                _prefetchHalted = false;
            Monitor.PulseAll(_lock);
            return;
        }

        // Out of window. Wait for any Prefetching slot to settle before flushing,
        //  so the worker's in-flight write doesn't bleed into a flushed slot.
        WaitForNoPrefetching_NoLock();
        FlushRing_NoLock();

        _pendingSeekBlock      = startBlock;
        _seekPending           = true;
        _prefetchHalted        = false;
        _prefetchBlock         = startBlock;
        _prefetchEndBlockExcl  = endBlockExcl;
        Monitor.PulseAll(_lock);
    }

    private bool HasReadySlotFor_NoLock(long block)
    {
        for (int i = 0; i < _slotCount; i++)
        {
            var s = _slots[i];
            if (s.State == SlotState.Ready && s.Block == block)
                return true;
        }
        return false;
    }

    private long HeadSlotBlock_NoLock()
    {
        var s = _slots[_consumerIndex];
        return s.State == SlotState.Empty ? -1 : s.Block;
    }

    private void AdvanceConsumerPast_NoLock(long targetBlock)
    {
        while (true)
        {
            var s = _slots[_consumerIndex];
            if (s.State != SlotState.Ready) break;
            if (s.Block >= targetBlock) break;

            ResetSlot(s);
            _consumerIndex = (_consumerIndex + 1) % _slotCount;
        }
        Monitor.PulseAll(_lock);
    }

    private void WaitForNoPrefetching_NoLock()
    {
        while (!_shutdown && AnyPrefetching_NoLock())
            Monitor.Wait(_lock);
    }

    private bool AnyPrefetching_NoLock()
    {
        for (int i = 0; i < _slotCount; i++)
            if (_slots[i].State == SlotState.Prefetching)
                return true;
        return false;
    }

    private void FlushRing_NoLock()
    {
        for (int i = 0; i < _slotCount; i++)
            ResetSlot(_slots[i]);
        _producerIndex = 0;
        _consumerIndex = 0;
    }

    private static void ResetSlot(Slot s)
    {
        s.State = SlotState.Empty;
        s.Block = -1;
        s.ValidBytes = 0;
        s.EndOfStream = false;
        s.Error = null;
    }

    // -----------------------------------------------------------------------
    //  Consumer / worker handoff
    // -----------------------------------------------------------------------

    private bool WaitForHeadReady_NoLock(out int slotIdx)
    {
        while (true)
        {
            if (_shutdown || _disposed)
            {
                slotIdx = -1;
                return false;
            }
            var s = _slots[_consumerIndex];
            if (s.State == SlotState.Ready)
            {
                slotIdx = _consumerIndex;
                return true;
            }
            Monitor.Wait(_lock);
        }
    }

    private void ReleaseHeadSlot(int expectedIdx)
    {
        lock (_lock)
        {
            if (_consumerIndex != expectedIdx)
                return;
            var s = _slots[_consumerIndex];
            if (s.State != SlotState.Ready)
                return;
            ResetSlot(s);
            _consumerIndex = (_consumerIndex + 1) % _slotCount;
            Monitor.PulseAll(_lock);
        }
    }

    // -----------------------------------------------------------------------
    //  Worker loop
    // -----------------------------------------------------------------------

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                int  slotIdx;
                long blockToFetch;
                bool doSeek;
                long seekBlock;

                lock (_lock)
                {
                    while (!_shutdown)
                    {
                        if (_seekPending) break;
                        if (_prefetchHalted || _prefetchBlock < 0)
                        {
                            Monitor.Wait(_lock);
                            continue;
                        }
                        // Respect the per-file prefetch bound: once we've queued every
                        //  block the open file may need, pause until the next BeginRead.
                        //  This prevents reading the block after the file payload, which
                        //  on partitioned media is the trailing content setmark.
                        if (_prefetchEndBlockExcl >= 0 && _prefetchBlock >= _prefetchEndBlockExcl)
                        {
                            Monitor.Wait(_lock);
                            continue;
                        }
                        if (_slots[_producerIndex].State == SlotState.Empty)
                            break;
                        Monitor.Wait(_lock);
                    }
                    if (_shutdown) return;

                    if (_seekPending)
                    {
                        doSeek      = true;
                        seekBlock   = _pendingSeekBlock;
                        _seekPending = false;
                        slotIdx     = -1;
                        blockToFetch = -1;
                    }
                    else
                    {
                        doSeek      = false;
                        seekBlock   = -1;
                        slotIdx     = _producerIndex;
                        blockToFetch = _prefetchBlock;
                        var s = _slots[slotIdx];
                        Debug.Assert(s.State == SlotState.Empty);
                        s.State = SlotState.Prefetching;
                        s.Block = blockToFetch;
                        _producerIndex = (slotIdx + 1) % _slotCount;
                    }
                }

                if (doSeek)
                {
                    bool ok;
                    try { ok = _backend.MoveToBlock(seekBlock); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Pipelined reader: backend seek to {Block} threw", seekBlock);
                        ok = false;
                    }
                    lock (_lock)
                    {
                        if (!ok)
                        {
                            PublishErrorSlot_NoLock(seekBlock,
                                new IOException($"Tape seek to block {seekBlock} failed."));
                            _prefetchHalted = true;
                        }
                        Monitor.PulseAll(_lock);
                    }
                    continue;
                }

                ReadResult result;
                try
                {
                    result = _backend.ReadOneBlock(_ringBuffer, slotIdx * _blockSize);
                }
                catch (Exception ex)
                {
                    result = new ReadResult(0, false, false, ex);
                }

                lock (_lock)
                {
                    var s = _slots[slotIdx];
                    if (s.State != SlotState.Prefetching || s.Block != blockToFetch)
                    {
                        // Ring was flushed under us; discard this read.
                        if (s.State == SlotState.Prefetching) ResetSlot(s);
                        Monitor.PulseAll(_lock);
                        continue;
                    }
                    s.ValidBytes  = Math.Min(Math.Max(0, result.BytesRead), _blockSize);
                    s.EndOfStream = result.TapemarkEncountered || result.EofEncountered;
                    s.Error       = result.Exception;
                    s.State       = SlotState.Ready;

                    if (result.Exception is not null
                        || result.TapemarkEncountered
                        || result.EofEncountered
                        || result.BytesRead < _blockSize)
                    {
                        _prefetchHalted = true;
                    }
                    else
                    {
                        _prefetchBlock = blockToFetch + 1;
                    }
                    Monitor.PulseAll(_lock);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TapeFilePipelinedReader worker terminated unexpectedly");
            lock (_lock)
            {
                _prefetchHalted = true;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void PublishErrorSlot_NoLock(long block, Exception error)
    {
        var s = _slots[_producerIndex];
        if (s.State != SlotState.Empty)
            return;
        s.State = SlotState.Ready;
        s.Block = block;
        s.ValidBytes = 0;
        s.Error = error;
        s.EndOfStream = false;
        _producerIndex = (_producerIndex + 1) % _slotCount;
    }

    // -----------------------------------------------------------------------
    //  Disposal
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;

        EndRead();

        lock (_lock)
        {
            _disposed = true;
            _shutdown = true;
            Monitor.PulseAll(_lock);
        }

        try { _worker.Join(); } catch { /* ignore */ }

        if (_ringBuffer is not null)
        {
            try { ArrayPool<byte>.Shared.Return(_ringBuffer); } catch { /* ignore */ }
            _ringBuffer = null!;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, nameof(TapeFilePipelinedReader));
}
