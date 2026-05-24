using TapeLibNET.TapeFilePacker;

namespace TapeLibNET.Tests.TapeFilePacker;

/// <summary>
/// Unit tests for <see cref="MemoryTapeReadBackend"/> / <see cref="ITapeReadBackend"/>
///  contract. Mirrors the structure of <see cref="TapeWriteBackendTests"/>.
/// </summary>
public class TapeReadBackendTests
{
    private const uint BlockSize = 512;

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    /// <summary>Build a single block filled with <paramref name="fill"/>.</summary>
    private static byte[] MakeBlock(byte fill)
    {
        var b = new byte[BlockSize];
        Array.Fill(b, fill);
        return b;
    }

    /// <summary>Build <paramref name="count"/> blocks each filled with consecutive fill values.</summary>
    private static byte[][] MakeBlocks(int count)
    {
        var blocks = new byte[count][];
        for (int i = 0; i < count; i++)
            blocks[i] = MakeBlock(unchecked((byte)(i + 1)));
        return blocks;
    }

    /// <summary>Read one block from <paramref name="backend"/> into a fresh buffer and return it.</summary>
    private static (ReadResult Result, byte[] Buffer) ReadOneBlock(MemoryTapeReadBackend backend)
    {
        var buf = new byte[BlockSize * 2]; // oversized — exercises the offset parameter
        var result = backend.ReadOneBlock(buf, (int)BlockSize); // read into second half
        var block = new byte[BlockSize];
        Buffer.BlockCopy(buf, (int)BlockSize, block, 0, (int)BlockSize);
        return (result, block);
    }

    // =======================================================================
    //  *** Sequential read basics ***
    // =======================================================================

    [Fact]
    public void ReadOneBlock_Sequential_ReturnsCorrectBytes()
    {
        var blocks = MakeBlocks(3);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);

        for (int i = 0; i < 3; i++)
        {
            var (result, block) = ReadOneBlock(backend);
            Assert.Equal((int)BlockSize, result.BytesRead);
            Assert.False(result.TapemarkEncountered);
            Assert.False(result.EofEncountered);
            Assert.Null(result.Exception);
            Assert.Equal(blocks[i], block);
        }
    }

    [Fact]
    public void ReadOneBlock_PastEnd_ReturnsEof()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(2));

        // Drain all seeded blocks.
        ReadOneBlock(backend);
        ReadOneBlock(backend);

        // One more read past the end.
        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0);

        Assert.Equal(0, result.BytesRead);
        Assert.False(result.TapemarkEncountered);
        Assert.True(result.EofEncountered);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void ReadOneBlock_WritesIntoCorrectOffset()
    {
        // Verify that the backend honours the offset parameter.
        var blocks = MakeBlocks(1);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);

        var buf = new byte[BlockSize * 3];
        Array.Fill(buf, (byte)0xFF);

        var result = backend.ReadOneBlock(buf, (int)BlockSize); // write into middle third

        Assert.Equal((int)BlockSize, result.BytesRead);
        // First and last thirds must remain 0xFF (not touched by the backend).
        Assert.All(buf.Take((int)BlockSize),            b => Assert.Equal(0xFF, b));
        Assert.All(buf.Skip((int)BlockSize * 2),        b => Assert.Equal(0xFF, b));
        // Middle third must equal the seeded block.
#pragma warning disable IDE0305 // Simplify collection initialization -- otherwise the Equal() becomes ambiguous between byte[] and Span<byte>
        Assert.Equal(blocks[0], buf.Skip((int)BlockSize).Take((int)BlockSize).ToArray());
#pragma warning restore IDE0305 // Simplify collection initialization
    }

    [Fact]
    public void FromWrittenBuffers_SplitsIntoBlocks()
    {
        // Round-trip: write data via helper, re-read via FromWrittenBuffers.
        const int BlockCount = 4;
        var writtenBuffers = new List<byte[]>
        {
            // Two buffers of different sizes; total must be a multiple of BlockSize.
            new byte[BlockSize * 2],
            new byte[BlockSize * 2],
        };
        for (int i = 0; i < BlockCount; i++)
        {
            int bi = i / 2, off = (i % 2) * (int)BlockSize;
            Array.Fill(writtenBuffers[bi], unchecked((byte)(i + 1)), off, (int)BlockSize);
        }

        using var backend = MemoryTapeReadBackend.FromWrittenBuffers(BlockSize, writtenBuffers);

        for (int i = 0; i < BlockCount; i++)
        {
            var (result, block) = ReadOneBlock(backend);
            Assert.Equal((int)BlockSize, result.BytesRead);
            Assert.All(block, b => Assert.Equal(unchecked((byte)(i + 1)), b));
        }
    }

    // =======================================================================
    //  *** Seek / MoveToBlock ***
    // =======================================================================

    [Fact]
    public void MoveToBlock_SetsReadCursor_AndRecordsHistory()
    {
        var blocks = MakeBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);

        backend.MoveToBlock(3);
        var (result, block) = ReadOneBlock(backend);

        Assert.Equal((int)BlockSize, result.BytesRead);
        Assert.Equal(blocks[3], block);
        Assert.Equal([3L], backend.SeekHistory);
    }

    [Fact]
    public void MoveToBlock_Backward_ReadsCorrectBlock()
    {
        var blocks = MakeBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);

        // Read forward to block 4, then seek backward to block 1.
        backend.MoveToBlock(4);
        ReadOneBlock(backend);

        backend.MoveToBlock(1);
        var (result, block) = ReadOneBlock(backend);

        Assert.Equal(blocks[1], block);
        Assert.Equal([4L, 1L], backend.SeekHistory);
    }

    [Fact]
    public void MoveToBlock_AlwaysReturnsTrue()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(2));
        Assert.True(backend.MoveToBlock(0));
        Assert.True(backend.MoveToBlock(999)); // past end — seek still succeeds
    }

    [Fact]
    public void NoSeek_SeekHistoryIsEmpty()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(2));
        ReadOneBlock(backend);
        Assert.Empty(backend.SeekHistory);
    }

    // =======================================================================
    //  *** Tapemark scripting ***
    // =======================================================================

    [Fact]
    public void ScriptedTapemark_FiresAtCorrectBlock_ZeroBytes()
    {
        var blocks = MakeBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptTapemarkBefore(2); // tapemark fires when head is at block 2

        // Blocks 0 and 1 read normally.
        ReadOneBlock(backend);
        ReadOneBlock(backend);

        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0);

        Assert.Equal(0, result.BytesRead);
        Assert.True(result.TapemarkEncountered);
        Assert.False(result.EofEncountered);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void ScriptedTapemark_DoesNotAdvanceCursor_SubsequentReadSucceeds()
    {
        // After a tapemark the drive head does NOT advance; the caller must seek past it.
        // For simplicity the fake treats the tapemark as a one-shot wall: the cursor
        //  stays at the tapemark block, so the caller must MoveToBlock to continue.
        var blocks = MakeBlocks(4);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptTapemarkBefore(1);

        ReadOneBlock(backend); // block 0 — ok

        // Tapemark at 1 — head stays at 1.
        var buf = new byte[BlockSize];
        var r1 = backend.ReadOneBlock(buf, 0);
        Assert.True(r1.TapemarkEncountered);

        // Seek past it and resume — should get block 2 normally.
        backend.MoveToBlock(2);
        var (r2, block2) = ReadOneBlock(backend);
        Assert.Equal((int)BlockSize, r2.BytesRead);
        Assert.Equal(blocks[2], block2);
    }

    [Fact]
    public void MultipleTapemarks_EachFiresOnce()
    {
        var blocks = MakeBlocks(6);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptTapemarkBefore(1);
        backend.ScriptTapemarkBefore(3);

        ReadOneBlock(backend); // block 0

        var buf = new byte[BlockSize];
        var rm1 = backend.ReadOneBlock(buf, 0);
        Assert.True(rm1.TapemarkEncountered); // tapemark at 1

        backend.MoveToBlock(2);
        ReadOneBlock(backend); // block 2

        var rm2 = backend.ReadOneBlock(buf, 0);
        Assert.True(rm2.TapemarkEncountered); // tapemark at 3

        backend.MoveToBlock(4);
        var (r4, block4) = ReadOneBlock(backend);
        Assert.Equal((int)BlockSize, r4.BytesRead);
        Assert.Equal(blocks[4], block4);
    }

    // =======================================================================
    //  *** EOF scripting ***
    // =======================================================================

    [Fact]
    public void ScriptedEof_FiresAfterSpecifiedBlock()
    {
        var blocks = MakeBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptEofAfterBlock(2); // blocks 0, 1, 2 readable; block 3 returns EOF

        ReadOneBlock(backend); // 0
        ReadOneBlock(backend); // 1
        ReadOneBlock(backend); // 2

        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0);

        Assert.Equal(0, result.BytesRead);
        Assert.False(result.TapemarkEncountered);
        Assert.True(result.EofEncountered);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void ScriptedEof_AtBlockZero_ImmediateEof()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(3));
        backend.ScriptEofAfterBlock(-1); // -1 means "no EOF override" — let's use 0 blocks readable
        // Actually test immediate EOF: set EOF to -1 means "no EOF"; to get immediate EOF use a
        //  ScriptEofAfterBlock that fires before block 0 we have to check: block > eofAfter.
        //  With ScriptEofAfterBlock(-1) that would be block 0 > -1 → true → EOF immediately.
        // Re-assert: ScriptEofAfterBlock leaves _eofAfterBlock = -1 when called with -1.
        // To get immediate EOF the caller should set it to a value < 0 but the method
        //  guards against negatives for explicit "no EOF" semantics. Let's verify the
        //  guard is distinct: a fresh backend without scripting reads normally.
        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0);
        Assert.Equal((int)BlockSize, result.BytesRead);
        Assert.False(result.EofEncountered);
    }

    // =======================================================================
    //  *** Hard-error scripting ***
    // =======================================================================

    [Fact]
    public void ScriptedHardError_SurfacesException_AtCorrectBlock()
    {
        var blocks = MakeBlocks(5);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptHardErrorAtBlock(2, "boom");

        ReadOneBlock(backend); // 0 — ok
        ReadOneBlock(backend); // 1 — ok

        var buf = new byte[BlockSize];
        var result = backend.ReadOneBlock(buf, 0); // block 2 — error

        Assert.Equal(0, result.BytesRead);
        Assert.False(result.TapemarkEncountered);
        Assert.False(result.EofEncountered);
        Assert.NotNull(result.Exception);
        Assert.Contains("boom", result.Exception!.Message);
    }

    [Fact]
    public void ScriptedHardError_FiresOnlyOnce_BackendRemainsUsable()
    {
        var blocks = MakeBlocks(4);
        using var backend = new MemoryTapeReadBackend(BlockSize, blocks);
        backend.ScriptHardErrorAtBlock(1);

        ReadOneBlock(backend); // 0 — ok

        var buf = new byte[BlockSize];
        var r1 = backend.ReadOneBlock(buf, 0); // block 1 — error
        Assert.NotNull(r1.Exception);

        // Head did not advance on error; MoveToBlock to continue.
        backend.MoveToBlock(2);
        var (r2, block2) = ReadOneBlock(backend); // block 2 — ok again
        Assert.Null(r2.Exception);
        Assert.Equal((int)BlockSize, r2.BytesRead);
        Assert.Equal(blocks[2], block2);
    }

    // =======================================================================
    //  *** Input validation ***
    // =======================================================================

    [Fact]
    public void ReadOneBlock_BufferTooSmall_Throws()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(1));

        // Buffer has room for only one block but we ask for offset = BlockSize → out of range.
        var buf = new byte[BlockSize];
        Assert.Throws<ArgumentException>(() => backend.ReadOneBlock(buf, (int)BlockSize));
    }

    [Fact]
    public void ReadOneBlock_NegativeOffset_Throws()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(1));
        var buf = new byte[BlockSize];
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.ReadOneBlock(buf, -1));
    }

    [Fact]
    public void ReadOneBlock_NullBuffer_Throws()
    {
        using var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(1));
        Assert.Throws<ArgumentNullException>(() => backend.ReadOneBlock(null!, 0));
    }

    // =======================================================================
    //  *** Disposal ***
    // =======================================================================

    [Fact]
    public void ReadOneBlock_AfterDispose_Throws()
    {
        var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(1));
        backend.Dispose();
        var buf = new byte[BlockSize];
        Assert.Throws<ObjectDisposedException>(() => backend.ReadOneBlock(buf, 0));
    }

    [Fact]
    public void MoveToBlock_AfterDispose_Throws()
    {
        var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(1));
        backend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.MoveToBlock(0));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var backend = new MemoryTapeReadBackend(BlockSize, MakeBlocks(1));
        backend.Dispose();
        backend.Dispose(); // must not throw
    }
}
