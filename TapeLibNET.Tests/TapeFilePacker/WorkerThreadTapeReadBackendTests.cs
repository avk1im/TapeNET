using TapeLibNET.TapeFilePacker;

namespace TapeLibNET.Tests.TapeFilePacker;

/// <summary>
/// Unit tests for <see cref="WorkerThreadTapeReadBackend"/>.
/// Exercises its threading handoff, seek-skip optimisation, scripted error paths,
/// and <see cref="IDisposable"/> drain semantics, mirroring the structure of
/// <see cref="TapeWriteBackendTests"/>.
/// </summary>
public class WorkerThreadTapeReadBackendTests
{
    private const uint BlockSize = 512;

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    /// <summary>Build a single block filled with <paramref name="fill"/>.</summary>
    private static byte[] MakeBlock(byte fill)
    {
        var b = new byte[(int)BlockSize];
        Array.Fill(b, fill);
        return b;
    }

    /// <summary>
    /// Build <paramref name="count"/> blocks each filled with a unique byte value
    ///  (1, 2, …).
    /// </summary>
    private static byte[][] MakeBlocks(int count)
    {
        var blocks = new byte[count][];
        for (int i = 0; i < count; i++)
            blocks[i] = MakeBlock(unchecked((byte)(i + 1)));
        return blocks;
    }

    /// <summary>
    /// Build a <see cref="WorkerThreadTapeReadBackend"/> backed by an in-memory
    ///  sequence of <paramref name="blocks"/>. The seek sink updates
    ///  <paramref name="headRef"/> so tests can observe seek calls.
    /// </summary>
    private static WorkerThreadTapeReadBackend MakeBackend(
        byte[][] blocks, out int[] headRef)
    {
        int head = 0;
        int[] captured = [head]; // single-element array acts as a ref box
        headRef = captured;

        ReadResult ReadSink(byte[] buffer, int offset)
        {
            int h = captured[0];
            if (h < 0 || h >= blocks.Length)
                return new ReadResult(0, false, true, null); // EOF

            Buffer.BlockCopy(blocks[h], 0, buffer, offset, (int)BlockSize);
            captured[0] = h + 1;
            return new ReadResult((int)BlockSize, false, false, null);
        }

        bool SeekSink(long blockNumber)
        {
            if (blockNumber < 0 || blockNumber > blocks.Length)
                return false;
            captured[0] = (int)blockNumber;
            return true;
        }

        return new WorkerThreadTapeReadBackend(ReadSink, SeekSink, BlockSize);
    }

    /// <summary>
    /// Helper: reads one block from <paramref name="backend"/> into a fresh, oversized
    ///  buffer (to exercise the offset parameter) and returns the content bytes.
    /// </summary>
    private static (ReadResult Result, byte[] Data) ReadOne(WorkerThreadTapeReadBackend backend)
    {
        var buf = new byte[BlockSize * 2];
        var result = backend.ReadOneBlock(buf, (int)BlockSize);
        var data = new byte[BlockSize];
        Buffer.BlockCopy(buf, (int)BlockSize, data, 0, (int)BlockSize);
        return (result, data);
    }

    // =======================================================================
    //  *** Construction ***
    // =======================================================================

    [Fact]
    public void Constructor_NullReadSink_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WorkerThreadTapeReadBackend(null!, _ => true, BlockSize));
    }

    [Fact]
    public void Constructor_NullSeekSink_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WorkerThreadTapeReadBackend((_, _) =>
                new ReadResult(0, false, false, null), null!, BlockSize));
    }

    [Fact]
    public void Constructor_ZeroBlockSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkerThreadTapeReadBackend(
                (_, _) => new ReadResult(0, false, false, null),
                _ => true,
                blockSize: 0));
    }

    [Fact]
    public void Constructor_ValidArgs_BlockSizeExposed()
    {
        static ReadResult Rs(byte[] _, int __) => new(0, false, false, null);
        static bool Ss(long _) => true;
        using var backend = new WorkerThreadTapeReadBackend(Rs, Ss, BlockSize);
        Assert.Equal(BlockSize, backend.BlockSize);
    }

    // =======================================================================
    //  *** Basic read ***
    // =======================================================================

    [Fact]
    public void ReadOneBlock_SingleBlock_DeliversCorrectBytes()
    {
        var blocks = MakeBlocks(1);
        using var backend = MakeBackend(blocks, out _);

        var (result, data) = ReadOne(backend);

        Assert.Equal((int)BlockSize, result.BytesRead);
        Assert.False(result.TapemarkEncountered);
        Assert.False(result.EofEncountered);
        Assert.Null(result.Exception);
        Assert.Equal(blocks[0], data);
    }

    [Fact]
    public void ReadOneBlock_Sequential_DeliversBlocksInOrder()
    {
        var blocks = MakeBlocks(5);
        using var backend = MakeBackend(blocks, out _);

        for (int i = 0; i < 5; i++)
        {
            var (result, data) = ReadOne(backend);
            Assert.Equal((int)BlockSize, result.BytesRead);
            Assert.Equal(blocks[i], data);
        }
    }

    [Fact]
    public void ReadOneBlock_WritesAtCorrectOffset()
    {
        var blocks = MakeBlocks(1);
        using var backend = MakeBackend(blocks, out _);

        // Provide a buffer that is 3 blocks long; request write at the second block slot.
        var buf = new byte[BlockSize * 3];
        Array.Fill(buf, (byte)0xFF); // sentinel

        var result = backend.ReadOneBlock(buf, (int)BlockSize);

        Assert.Equal((int)BlockSize, result.BytesRead);
        // Bytes before the offset must be untouched.
        Assert.All(buf.Take((int)BlockSize), b => Assert.Equal(0xFF, b));
        // Bytes in the slot must equal the block.
#pragma warning disable IDE0305 // Simplify collection initialization -- otherwise Equal() becomes ambiguous between byte[] and Span<byte>
        Assert.Equal(blocks[0], buf.Skip((int)BlockSize).Take((int)BlockSize).ToArray());
#pragma warning restore IDE0305 // Simplify collection initialization
                               // Bytes after the slot must be untouched.
        Assert.All(buf.Skip((int)BlockSize * 2), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ReadOneBlock_BufferTooSmall_Throws()
    {
        using var backend = MakeBackend(MakeBlocks(1), out _);
        var tinyBuf = new byte[BlockSize - 1];
        Assert.Throws<ArgumentException>(() => backend.ReadOneBlock(tinyBuf, 0));
    }

    [Fact]
    public void ReadOneBlock_NullBuffer_Throws()
    {
        using var backend = MakeBackend(MakeBlocks(1), out _);
        Assert.Throws<ArgumentNullException>(() => backend.ReadOneBlock(null!, 0));
    }

    [Fact]
    public void ReadOneBlock_NegativeOffset_Throws()
    {
        using var backend = MakeBackend(MakeBlocks(1), out _);
        var buf = new byte[BlockSize * 2];
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.ReadOneBlock(buf, -1));
    }

    // =======================================================================
    //  *** EOF / tapemark reporting ***
    // =======================================================================

    [Fact]
    public void ReadOneBlock_PastEnd_ReportsEof()
    {
        var blocks = MakeBlocks(2);
        using var backend = MakeBackend(blocks, out _);

        ReadOne(backend);
        ReadOne(backend);
        var (result, _) = ReadOne(backend); // past end

        Assert.Equal(0, result.BytesRead);
        Assert.True(result.EofEncountered);
        Assert.False(result.TapemarkEncountered);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void ReadOneBlock_SinkReportsTapemark_Forwarded()
    {
        static ReadResult TapemarkSink(byte[] _, int __) => new(0, TapemarkEncountered: true, EofEncountered: false, null);
        static bool SeekSink(long _) => true;
        using var backend = new WorkerThreadTapeReadBackend(TapemarkSink, SeekSink, BlockSize);

        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0);

        Assert.True(result.TapemarkEncountered);
        Assert.False(result.EofEncountered);
        Assert.Null(result.Exception);
    }

    // =======================================================================
    //  *** Hard error ***
    // =======================================================================

    [Fact]
    public void ReadOneBlock_SinkThrows_ExceptionSurfacedInResult()
    {
        var boom = new IOException("drive fault");
        ReadResult FaultSink(byte[] _, int __) => throw boom;
        static bool SeekSink(long _) => true;
        using var backend = new WorkerThreadTapeReadBackend(FaultSink, SeekSink, BlockSize);

        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0);

        Assert.NotNull(result.Exception);
        Assert.IsType<IOException>(result.Exception);
        Assert.Same(boom, result.Exception);
    }

    [Fact]
    public void ReadOneBlock_SinkReturnsHardError_ExceptionForwarded()
    {
        var ex = new InvalidOperationException("tape jam");
        ReadResult ErrorSink(byte[] _, int __) => new(0, false, false, ex);
        static bool SeekSink(long _) => true;
        using var backend = new WorkerThreadTapeReadBackend(ErrorSink, SeekSink, BlockSize);

        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0);

        Assert.False(result.Succeeded);
        Assert.Same(ex, result.Exception);
    }

    // =======================================================================
    //  *** MoveToBlock / seek ***
    // =======================================================================

    [Fact]
    public void MoveToBlock_SeeksToCorrectBlock()
    {
        var blocks = MakeBlocks(5);
        using var backend = MakeBackend(blocks, out int[] head);

        bool ok = backend.MoveToBlock(3);

        Assert.True(ok);
        Assert.Equal(3, head[0]);
    }

    [Fact]
    public void MoveToBlock_ThenRead_DeliversCorrectBlock()
    {
        var blocks = MakeBlocks(5);
        using var backend = MakeBackend(blocks, out _);

        backend.MoveToBlock(4);
        var (result, data) = ReadOne(backend);

        Assert.Equal((int)BlockSize, result.BytesRead);
        Assert.Equal(blocks[4], data);
    }

    [Fact]
    public void MoveToBlock_OutOfRange_ReturnsFalse()
    {
        var blocks = MakeBlocks(3);
        using var backend = MakeBackend(blocks, out _);

        bool ok = backend.MoveToBlock(99);
        Assert.False(ok);
    }

    [Fact]
    public void MoveToBlock_BackwardSeekAfterForwardRead_Succeeds()
    {
        var blocks = MakeBlocks(5);
        using var backend = MakeBackend(blocks, out _);

        ReadOne(backend); // head = 1
        ReadOne(backend); // head = 2
        backend.MoveToBlock(0);
        var (result, data) = ReadOne(backend); // should re-read block 0

        Assert.Equal(blocks[0], data);
        Assert.Equal((int)BlockSize, result.BytesRead);
    }

    // =======================================================================
    //  *** Seek-skip optimisation (_drivePositionBlock) ***
    // =======================================================================

    [Fact]
    public void MoveToBlock_SamePositionAsAfterRead_SkipsPhysicalSeek()
    {
        // After a successful read the backend bumps _drivePositionBlock.
        // If MoveToBlock is called for the same block the worker is now at,
        // the physical seek sink must NOT be invoked.
        int seekCount = 0;
        int head = 0;

        ReadResult ReadSink(byte[] buffer, int offset)
        {
            Buffer.BlockCopy(MakeBlock(0xCC), 0, buffer, offset, (int)BlockSize);
            head++;
            return new((int)BlockSize, false, false, null);
        }

        bool SeekSink(long blockNumber)
        {
            seekCount++;
            head = (int)blockNumber;
            return true;
        }

        using var backend = new WorkerThreadTapeReadBackend(ReadSink, SeekSink, BlockSize);

        // Seek to block 0 so the backend knows the head position (seekCount becomes 1),
        //  then read — after a successful read the backend increments _drivePositionBlock
        //  to 1. The seek here is expected; we reset the counter afterwards.
        backend.MoveToBlock(0);
        seekCount = 0; // reset; we only care about the skip optimisation below
        ReadOne(backend); // head moves to block 1

        // Ask to move to block 1 — the backend should skip the physical seek.
        bool ok = backend.MoveToBlock(1);

        Assert.True(ok);
        Assert.Equal(0, seekCount); // no physical seek was issued
    }

    [Fact]
    public void MoveToBlock_DifferentPosition_IssuesPhysicalSeek()
    {
        int seekCount = 0;
        int head = 0;

        ReadResult ReadSink(byte[] buffer, int offset)
        {
            Buffer.BlockCopy(MakeBlock(0xAA), 0, buffer, offset, (int)BlockSize);
            head++;
            return new((int)BlockSize, false, false, null);
        }

        bool SeekSink(long blockNumber)
        {
            seekCount++;
            head = (int)blockNumber;
            return true;
        }

        using var backend = new WorkerThreadTapeReadBackend(ReadSink, SeekSink, BlockSize);

        ReadOne(backend); // head moves to 1
        backend.MoveToBlock(3); // different position — should issue a seek

        Assert.Equal(1, seekCount);
        Assert.Equal(3, head);
    }

    [Fact]
    public void MoveToBlock_AfterTapemark_DoesNotSkipSeek()
    {
        // A tapemark result should invalidate the tracked position.
        // Even if the consumer requests the "same" block number, the seek
        // must be issued because the drive position is now unknown.
        int seekCount = 0;

        ReadResult TapemarkSink(byte[] _, int __) =>
            new(0, TapemarkEncountered: true, EofEncountered: false, null);

        bool SeekSink(long blockNumber)
        {
            seekCount++;
            return true;
        }

        using var backend = new WorkerThreadTapeReadBackend(TapemarkSink, SeekSink, BlockSize);

        var buf = new byte[BlockSize];
        backend.ReadOneBlock(buf, 0); // returns tapemark → position unknown

        // Any MoveToBlock call now must go through the physical seek.
        backend.MoveToBlock(0);
        Assert.Equal(1, seekCount);
    }

    // =======================================================================
    //  *** Multiple sequential reads without seeks ***
    // =======================================================================

    [Fact]
    public void MultipleReads_NoSeeks_AllSucceed()
    {
        const int Count = 20;
        var blocks = MakeBlocks(Count);
        using var backend = MakeBackend(blocks, out _);

        for (int i = 0; i < Count; i++)
        {
            var (result, data) = ReadOne(backend);
            Assert.Equal((int)BlockSize, result.BytesRead);
            Assert.True(blocks[i].SequenceEqual(data), $"Data mismatch at block {i}");
        }
    }

    // =======================================================================
    //  *** Dispose ***
    // =======================================================================

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var backend = MakeBackend(MakeBlocks(1), out _);
        backend.Dispose();
        var ex = Record.Exception(() => backend.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void ReadOneBlock_AfterDispose_Throws()
    {
        var backend = MakeBackend(MakeBlocks(1), out _);
        backend.Dispose();

        var buf = new byte[BlockSize];
        Assert.Throws<ObjectDisposedException>(() => backend.ReadOneBlock(buf, 0));
    }

    [Fact]
    public void MoveToBlock_AfterDispose_Throws()
    {
        var backend = MakeBackend(MakeBlocks(1), out _);
        backend.Dispose();

        Assert.Throws<ObjectDisposedException>(() => backend.MoveToBlock(0));
    }
}
