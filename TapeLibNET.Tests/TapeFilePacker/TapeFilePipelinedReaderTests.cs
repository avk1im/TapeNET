using TapeLibNET.TapeFilePacker;

namespace TapeLibNET.Tests.TapeFilePacker;

/// <summary>
/// Unit tests for <see cref="TapeFilePipelinedReader"/>. Exercises the worker-thread
/// prefetch ring against the in-memory <see cref="MemoryTapeReadBackend"/>:
/// sequential reads, intra-block offsets, packed file-on-file-in-same-block transitions,
/// seek-and-resume vs seek-and-restart, tapemark / EOF surfacing, hard-error propagation,
/// back-pressure, double-open rejection, and disposal under concurrency.
/// </summary>
public class TapeFilePipelinedReaderTests
{
    private const uint BlockSize = 256;

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    /// <summary>Build a block whose bytes encode (blockIndex, byteOffsetInBlock).</summary>
    private static byte[] MakePatternedBlock(int blockIndex)
    {
        var b = new byte[(int)BlockSize];
        for (int i = 0; i < (int)BlockSize; i++)
            b[i] = unchecked((byte)((blockIndex * 31) + i));
        return b;
    }

    private static byte[][] MakePatternedBlocks(int count)
    {
        var blocks = new byte[count][];
        for (int i = 0; i < count; i++)
            blocks[i] = MakePatternedBlock(i);
        return blocks;
    }

    /// <summary>Concatenate the seeded tape content from block <paramref name="start"/>
    /// covering <paramref name="length"/> bytes starting at <paramref name="offset"/>
    /// inside that block.</summary>
    private static byte[] ExpectedBytes(byte[][] blocks, long startBlock, int offsetInBlock, long length)
    {
        var expected = new byte[length];
        long absSrc = startBlock * BlockSize + offsetInBlock;
        for (long i = 0; i < length; i++)
        {
            long src = absSrc + i;
            expected[i] = blocks[src / BlockSize][src % BlockSize];
        }
        return expected;
    }

    private static byte[] ReadAll(Stream s, long length)
    {
        var buf = new byte[length];
        int total = 0;
        while (total < length)
        {
            int n = s.Read(buf, total, (int)length - total);
            if (n <= 0) break;
            total += n;
        }
        Array.Resize(ref buf, total);
        return buf;
    }

    // =======================================================================
    //  *** Construction ***
    // =======================================================================

    [Fact]
    public void Constructor_NullBackend_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TapeFilePipelinedReader(null!));
    }

    [Fact]
    public void Constructor_SlotCountTooSmall_Throws()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakePatternedBlocks(1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TapeFilePipelinedReader(backend, slotCount: 1));
    }

    [Fact]
    public void Constructor_ExposesBlockSizeAndState()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakePatternedBlocks(1));
        using var reader  = new TapeFilePipelinedReader(backend);
        Assert.Equal((int)BlockSize, reader.BlockSize);
        Assert.False(reader.IsFileOpen);
    }

    // =======================================================================
    //  *** Sequential single-file reads ***
    // =======================================================================

    [Fact]
    public void BeginRead_SequentialFullBlocks_ReturnsExpectedBytes()
    {
        var blocks = MakePatternedBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        long length = BlockSize * 3;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);
        Assert.True(reader.IsFileOpen);

        var data = ReadAll(stream, length);
        Assert.Equal(ExpectedBytes(blocks, 0, 0, length), data);
        Assert.Equal(length, stream.Position);
    }

    [Fact]
    public void BeginRead_NonZeroIntraBlockOffset_ReturnsBytesFromOffset()
    {
        var blocks = MakePatternedBlocks(3);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        const int Offset = 100;
        long length = BlockSize - Offset + 50; // spans block 0 (partial) and into block 1
        using var stream = reader.BeginRead(new TapeAddress(0, Offset), length);
        var data = ReadAll(stream, length);
        Assert.Equal(ExpectedBytes(blocks, 0, Offset, length), data);
    }

    [Fact]
    public void BeginRead_FileEndsMidBlock_ReturnsExactlyLengthBytes()
    {
        var blocks = MakePatternedBlocks(4);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        long length = (long)(BlockSize * 2) + 73;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);
        var data = ReadAll(stream, length + 100); // overshoot
        Assert.Equal(length, data.Length);
        Assert.Equal(ExpectedBytes(blocks, 0, 0, length), data);
    }

    [Fact]
    public void Read_PastEnd_ReturnsZero()
    {
        var blocks = MakePatternedBlocks(2);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        using var stream = reader.BeginRead(new TapeAddress(0, 0), 10);
        var buf = new byte[20];
        int read1 = stream.Read(buf, 0, 20);
        Assert.Equal(10, read1);
        int read2 = stream.Read(buf, 0, 20);
        Assert.Equal(0, read2);
    }

    [Fact]
    public void BeginRead_ZeroLength_ImmediatelyReturnsEmpty()
    {
        var blocks = MakePatternedBlocks(2);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        using var stream = reader.BeginRead(new TapeAddress(0, 0), 0);
        var buf = new byte[16];
        Assert.Equal(0, stream.Read(buf, 0, 16));
    }

    [Fact]
    public void BeginRead_SmallReadChunks_AggregatesCorrectly()
    {
        var blocks = MakePatternedBlocks(3);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        long length = BlockSize * 2 + 50;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);
        var buf = new byte[length];
        int total = 0;
        var rng = new Random(42);
        while (total < length)
        {
            int chunk = Math.Min((int)length - total, rng.Next(1, 37));
            int n = stream.Read(buf, total, chunk);
            Assert.True(n > 0);
            total += n;
        }
        Assert.Equal(ExpectedBytes(blocks, 0, 0, length), buf);
    }

    // =======================================================================
    //  *** Multi-file (packed) sequential reads ***
    // =======================================================================

    [Fact]
    public void Sequential_MultipleFiles_SameBlockTransition_NoSeek()
    {
        // Lay out three back-to-back files that pack into the same block.
        var blocks = MakePatternedBlocks(2);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        const int LenA = 80, LenB = 60, LenC = 100; // total < BlockSize
        // File A at (0,0), File B at (0,80), File C at (0,140).
        using (var sa = reader.BeginRead(new TapeAddress(0, 0), LenA))
            Assert.Equal(ExpectedBytes(blocks, 0, 0, LenA), ReadAll(sa, LenA));
        using (var sb = reader.BeginRead(new TapeAddress(0, LenA), LenB))
            Assert.Equal(ExpectedBytes(blocks, 0, LenA, LenB), ReadAll(sb, LenB));
        using (var sc = reader.BeginRead(new TapeAddress(0, LenA + LenB), LenC))
            Assert.Equal(ExpectedBytes(blocks, 0, LenA + LenB, LenC), ReadAll(sc, LenC));

        // Each sequential BeginRead stayed inside the buffered window; no MoveToBlock fired
        //  for any of them. (One seek-to-block-0 may occur on the very first arm; some
        //  backends elide it.)
        Assert.True(backend.SeekHistory.Count <= 1,
            $"Unexpected seeks: [{string.Join(",", backend.SeekHistory)}]");
    }

    [Fact]
    public void Sequential_MultipleFiles_CrossingBlockBoundaries_StillCachedWindow()
    {
        var blocks = MakePatternedBlocks(6);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        // File A spans blocks 0..1 (length 1.5 blocks); File B starts mid-block 1.
        long lenA = BlockSize + BlockSize / 2;
        long lenB = BlockSize * 2;
        using (var sa = reader.BeginRead(new TapeAddress(0, 0), lenA))
            Assert.Equal(ExpectedBytes(blocks, 0, 0, lenA), ReadAll(sa, lenA));
        using (var sb = reader.BeginRead(
                   new TapeAddress(1, (uint)(BlockSize / 2)), lenB))
            Assert.Equal(ExpectedBytes(blocks, 1, (int)(BlockSize / 2), lenB), ReadAll(sb, lenB));

        // Worker should have prefetched ahead; no backward seek should occur.
        Assert.True(backend.SeekHistory.Count <= 1);
    }

    // =======================================================================
    //  *** Backward / non-monotonic seek (seek-and-restart) ***
    // =======================================================================

    [Fact]
    public void BeginRead_BackwardSeek_FlushesAndReseeks()
    {
        var blocks = MakePatternedBlocks(8);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        // Read forward at block 5.
        using (var s1 = reader.BeginRead(new TapeAddress(5, 0), BlockSize))
            Assert.Equal(ExpectedBytes(blocks, 5, 0, BlockSize), ReadAll(s1, BlockSize));

        // Now seek backwards to block 1.
        using (var s2 = reader.BeginRead(new TapeAddress(1, 0), BlockSize))
            Assert.Equal(ExpectedBytes(blocks, 1, 0, BlockSize), ReadAll(s2, BlockSize));

        // Must contain both seeks: 5 (initial) and 1 (backward restart).
        Assert.Contains(5L, backend.SeekHistory);
        Assert.Contains(1L, backend.SeekHistory);
    }

    [Fact]
    public void BeginRead_ForwardJumpPastPrefetchWindow_Reseeks()
    {
        var blocks = MakePatternedBlocks(40);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend, slotCount: 4);

        using (var s1 = reader.BeginRead(new TapeAddress(0, 0), BlockSize))
            Assert.Equal(ExpectedBytes(blocks, 0, 0, BlockSize), ReadAll(s1, BlockSize));

        // Give the worker a moment to greedily prefetch up to 4 blocks ahead.
        Thread.Sleep(50);

        // Jump well past the prefetch window.
        using (var s2 = reader.BeginRead(new TapeAddress(30, 0), BlockSize))
            Assert.Equal(ExpectedBytes(blocks, 30, 0, BlockSize), ReadAll(s2, BlockSize));

        Assert.Contains(30L, backend.SeekHistory);
    }

    // =======================================================================
    //  *** Tapemark / EOF ***
    // =======================================================================

    [Fact]
    public void TapemarkMidPrefetch_SurfacedAsShortRead()
    {
        var blocks = MakePatternedBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptTapemarkBefore(3); // tapemark before block 3
        using var reader  = new TapeFilePipelinedReader(backend);

        // Ask for 5 blocks; only 3 are deliverable before the mark.
        long length = BlockSize * 5;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);
        var data = ReadAll(stream, length);
        Assert.Equal((long)BlockSize * 3, data.Length);
        Assert.Equal(ExpectedBytes(blocks, 0, 0, BlockSize * 3), data);
    }

    [Fact]
    public void EofMidPrefetch_SurfacedAsShortRead()
    {
        var blocks = MakePatternedBlocks(4);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptEofAfterBlock(1); // EOF after fully reading block 1
        using var reader  = new TapeFilePipelinedReader(backend);

        long length = BlockSize * 4;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);
        var data = ReadAll(stream, length);
        Assert.Equal((long)BlockSize * 2, data.Length);
    }

    // =======================================================================
    //  *** Hard error propagation ***
    // =======================================================================

    [Fact]
    public void HardErrorMidPrefetch_SurfacesAsIOException()
    {
        var blocks = MakePatternedBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptHardErrorAtBlock(2, "synthetic media defect");
        using var reader  = new TapeFilePipelinedReader(backend);

        long length = BlockSize * 4;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);
        var buf = new byte[length];

        var ex = Assert.Throws<IOException>(() =>
        {
            int total = 0;
            while (total < length)
            {
                int n = stream.Read(buf, total, (int)length - total);
                if (n == 0) break;
                total += n;
            }
        });
        Assert.Contains("block 2", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Contains("synthetic media defect", ex.InnerException!.Message);
    }

    // =======================================================================
    //  *** API invariants ***
    // =======================================================================

    [Fact]
    public void BeginRead_WhileFileOpen_Throws()
    {
        var blocks = MakePatternedBlocks(2);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        _ = reader.BeginRead(new TapeAddress(0, 0), 10);
        Assert.Throws<InvalidOperationException>(() =>
            reader.BeginRead(new TapeAddress(0, 20), 10));
    }

    [Fact]
    public void BeginRead_InvalidAddress_Throws()
    {
        var blocks = MakePatternedBlocks(1);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        Assert.Throws<ArgumentException>(() =>
            reader.BeginRead(TapeAddress.Invalid, 1));
    }

    [Fact]
    public void BeginRead_NegativeLength_Throws()
    {
        var blocks = MakePatternedBlocks(1);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            reader.BeginRead(new TapeAddress(0, 0), -1));
    }

    [Fact]
    public void EndRead_WithoutOpenFile_IsNoOp()
    {
        var blocks = MakePatternedBlocks(1);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        reader.EndRead();
        Assert.False(reader.IsFileOpen);
    }

    [Fact]
    public void StreamDispose_TriggersEndRead()
    {
        var blocks = MakePatternedBlocks(1);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        var stream = reader.BeginRead(new TapeAddress(0, 0), 10);
        Assert.True(reader.IsFileOpen);
        stream.Dispose();
        Assert.False(reader.IsFileOpen);
    }

    [Fact]
    public void ReadIntoOpenFile_AfterEndRead_Throws()
    {
        var blocks = MakePatternedBlocks(1);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        var stream = reader.BeginRead(new TapeAddress(0, 0), 10);
        reader.EndRead();
        var buf = new byte[10];
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buf, 0, 10));
    }

    // =======================================================================
    //  *** Back-pressure / slow consumer ***
    // =======================================================================

    [Fact]
    public void SlowConsumer_RingFullBackPressure_DoesNotLoseData()
    {
        // Make many blocks but a tiny ring, so the worker has to wait for the consumer.
        var blocks = MakePatternedBlocks(50);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend, slotCount: 2);

        long length = (long)BlockSize * 50;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);

        var buf = new byte[length];
        int total = 0;
        while (total < length)
        {
            int n = stream.Read(buf, total, (int)BlockSize);
            Assert.True(n > 0);
            total += n;
            Thread.Sleep(1); // simulate slow processing per block
        }
        Assert.Equal(ExpectedBytes(blocks, 0, 0, length), buf);
    }

    // =======================================================================
    //  *** Disposal under concurrency ***
    // =======================================================================

    [Fact]
    public void Dispose_WhileWorkerActive_ReturnsCleanly()
    {
        var blocks = MakePatternedBlocks(100);
        var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        var reader  = new TapeFilePipelinedReader(backend, slotCount: 4);

        var stream = reader.BeginRead(new TapeAddress(0, 0), (long)BlockSize * 100);
        var buf = new byte[BlockSize];
        // Read a single block to ensure the worker has been engaged.
        stream.Read(buf, 0, (int)BlockSize);

        // Dispose without draining the rest.
        reader.Dispose();
        backend.Dispose();

        // Reusing the reader after dispose must throw.
        Assert.Throws<ObjectDisposedException>(() =>
            reader.BeginRead(new TapeAddress(0, 0), 1));
    }

    [Fact]
    public void Dispose_TwiceIsSafe()
    {
        var blocks = MakePatternedBlocks(1);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        var reader = new TapeFilePipelinedReader(backend);
        reader.Dispose();
        reader.Dispose(); // no exception
    }

    // =======================================================================
    //  *** Re-arm after EndRead (cache continuity) ***
    // =======================================================================

    [Fact]
    public void Sequential_ResumingFromExactlyEndOfPreviousFile_NoExtraSeek()
    {
        var blocks = MakePatternedBlocks(10);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend);

        // File A: blocks 0..1, File B: blocks 2..3 (exactly aligned).
        long len = BlockSize * 2;
        using (var sa = reader.BeginRead(new TapeAddress(0, 0), len))
            ReadAll(sa, len);

        // Allow worker to prefetch into 2/3.
        Thread.Sleep(30);

        using (var sb = reader.BeginRead(new TapeAddress(2, 0), len))
            Assert.Equal(ExpectedBytes(blocks, 2, 0, len), ReadAll(sb, len));

        // No backward seek should have occurred.
        Assert.DoesNotContain(backend.SeekHistory, x => x < 2 && x != 0);
    }

    // =======================================================================
    //  *** Many-block stress ***
    // =======================================================================

    [Fact]
    public void LargeSequentialRead_ManyBlocks_AllBytesCorrect()
    {
        const int BlockCount = 200;
        var blocks = MakePatternedBlocks(BlockCount);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        using var reader  = new TapeFilePipelinedReader(backend, slotCount: 8);

        long length = (long)BlockSize * BlockCount;
        using var stream = reader.BeginRead(new TapeAddress(0, 0), length);
        var data = ReadAll(stream, length);
        Assert.Equal(length, data.Length);
        Assert.Equal(ExpectedBytes(blocks, 0, 0, length), data);
    }
}
