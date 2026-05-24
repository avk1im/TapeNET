using System.Buffers;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// In-memory test backend for the read path. Mirrors <see cref="MemoryTapeWriteBackend"/>
///  in role: purely synchronous, no tape hardware, supports scripted tape conditions.
/// <para>
/// Seeded at construction with a flat array of block-sized byte arrays; the
///  <see cref="ReadOneBlock"/> method serves them sequentially, honouring any scripted
///  tapemarks, EOF position, and hard-error injection. <see cref="MoveToBlock"/> seeks
///  the internal read cursor and records the call for test assertion.
/// </para>
/// </summary>
internal sealed class MemoryTapeReadBackend : ITapeReadBackend
{
    private readonly byte[][] _blocks;          // immutable tape content, one entry per block
    private readonly HashSet<long> _tapemarkAt; // absolute block numbers BEFORE which a tapemark fires
    private long _eofAfterBlock;                // absolute block index; read past this returns EOF (-1 = none)
    private long _errorAtBlock;                 // fire hard error when reading this block (-1 = none)
    private string _errorMessage;

    private long _headBlock;    // current drive-head position (next block to read)
    private bool _disposed;

    /// <summary>All <see cref="MoveToBlock"/> calls in order, for assertion.</summary>
    public List<long> SeekHistory { get; } = [];

    /// <inheritdoc/>
    public uint BlockSize { get; }

    // -----------------------------------------------------------------------
    //  Construction helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Construct from a pre-split array of blocks. Each entry must be exactly
    ///  <paramref name="blockSize"/> bytes (the backend does not copy or re-slice them).
    /// </summary>
    public MemoryTapeReadBackend(uint blockSize, byte[][] blocks)
    {
        ArgumentOutOfRangeException.ThrowIfZero(blockSize);
        ArgumentNullException.ThrowIfNull(blocks);

        BlockSize  = blockSize;
        _blocks    = blocks;
        _tapemarkAt = [];
        _eofAfterBlock  = -1;
        _errorAtBlock   = -1;
        _errorMessage   = "scripted hardware error";
        _headBlock      = 0;
    }

    /// <summary>
    /// Convenience constructor: splits the concatenated buffers produced by a
    ///  <see cref="MemoryTapeWriteBackend"/> into individual blocks, so a write-side
    ///  test can feed its output directly into the read-side backend.
    /// </summary>
    public static MemoryTapeReadBackend FromWrittenBuffers(
        uint blockSize, IReadOnlyList<byte[]> writtenBuffers)
    {
        ArgumentOutOfRangeException.ThrowIfZero(blockSize);
        ArgumentNullException.ThrowIfNull(writtenBuffers);

        // Concatenate and re-slice into uniform blocks.
        int totalBytes = writtenBuffers.Sum(b => b.Length);
        int blockCount = totalBytes / (int)blockSize;

        var blocks = new byte[blockCount][];
        int srcBuf = 0, srcOff = 0;
        for (int i = 0; i < blockCount; i++)
        {
            var block = new byte[blockSize];
            int written = 0;
            while (written < (int)blockSize)
            {
                if (srcBuf >= writtenBuffers.Count) break;
                int chunk = Math.Min((int)blockSize - written, writtenBuffers[srcBuf].Length - srcOff);
                Buffer.BlockCopy(writtenBuffers[srcBuf], srcOff, block, written, chunk);
                written += chunk;
                srcOff  += chunk;
                if (srcOff >= writtenBuffers[srcBuf].Length) { srcBuf++; srcOff = 0; }
            }
            blocks[i] = block;
        }
        return new MemoryTapeReadBackend(blockSize, blocks);
    }

    // -----------------------------------------------------------------------
    //  Scripting
    // -----------------------------------------------------------------------

    /// <summary>
    /// Insert a tapemark immediately before <paramref name="blockNumber"/>: a read whose
    ///  drive head is positioned at that block returns
    ///  <see cref="ReadResult.TapemarkEncountered"/> = <c>true</c> with zero bytes.
    ///  Subsequent reads advance normally past the mark.
    /// </summary>
    public void ScriptTapemarkBefore(long blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        _tapemarkAt.Add(blockNumber);
    }

    /// <summary>
    /// After block index <paramref name="blockIndex"/> (0-based) has been fully read,
    ///  all further reads return <see cref="ReadResult.EofEncountered"/> = <c>true</c>.
    ///  Pass -1 to disable (default).
    /// </summary>
    public void ScriptEofAfterBlock(long blockIndex) => _eofAfterBlock = blockIndex;

    /// <summary>
    /// Inject a hard error when the backend tries to read <paramref name="blockNumber"/>.
    ///  Fires only once; subsequent reads at that block succeed normally.
    /// </summary>
    public void ScriptHardErrorAtBlock(long blockNumber,
        string message = "scripted hardware error")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        _errorAtBlock   = blockNumber;
        _errorMessage   = message;
    }

    // -----------------------------------------------------------------------
    //  ITapeReadBackend
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public bool MoveToBlock(long blockNumber)
    {
        ThrowIfDisposed();
        SeekHistory.Add(blockNumber);
        _headBlock = blockNumber;
        return true; // always succeeds in the in-memory fake
    }

    /// <inheritdoc/>
    public ReadResult ReadOneBlock(byte[] buffer, int offset)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + (int)BlockSize > buffer.Length)
            throw new ArgumentException(
                $"Buffer too small: offset {offset} + BlockSize {BlockSize} > buffer.Length {buffer.Length}.",
                nameof(buffer));

        long block = _headBlock;

        // --- tapemark ---
        if (_tapemarkAt.Contains(block))
            return new ReadResult(0, TapemarkEncountered: true, EofEncountered: false, Exception: null);

        // --- EOF ---
        if (_eofAfterBlock >= 0 && block > _eofAfterBlock)
            return new ReadResult(0, TapemarkEncountered: false, EofEncountered: true, Exception: null);

        // --- past end of seeded content ---
        if (block >= _blocks.Length)
            return new ReadResult(0, TapemarkEncountered: false, EofEncountered: true, Exception: null);

        // --- hard error (fires once) ---
        if (_errorAtBlock >= 0 && block == _errorAtBlock)
        {
            _errorAtBlock = -1; // reset so subsequent reads succeed
            return new ReadResult(0, TapemarkEncountered: false, EofEncountered: false,
                Exception: new InvalidOperationException(_errorMessage));
        }

        // --- normal read ---
        Buffer.BlockCopy(_blocks[block], 0, buffer, offset, (int)BlockSize);
        _headBlock++;
        return new ReadResult((int)BlockSize, TapemarkEncountered: false, EofEncountered: false,
            Exception: null);
    }

    // -----------------------------------------------------------------------
    //  IDisposable
    // -----------------------------------------------------------------------

    public void Dispose() => _disposed = true;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, nameof(MemoryTapeReadBackend));
}
