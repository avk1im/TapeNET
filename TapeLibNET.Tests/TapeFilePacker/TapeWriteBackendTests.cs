using TapeLibNET.TapeFilePacker;

namespace TapeLibNET.Tests.TapeFilePacker;

/// <summary>
/// Unit tests for the low-layer write backend (<see cref="ITapeWriteBackend"/>).
/// Uses <see cref="MemoryTapeWriteBackend"/> which exercises the same
/// <see cref="WorkerThreadTapeWriteBackend"/> machinery as production but
/// records bytes in memory and supports scripted EOM / hard-error injection.
/// </summary>
public class TapeWriteBackendTests
{
    private const uint BlockSize = 512;

    private static byte[] MakeBuffer(int blocks, byte fill)
    {
        var b = new byte[blocks * (int)BlockSize];
        Array.Fill(b, fill);
        return b;
    }

    #region *** Basic round-trip ***

    [Fact]
    public void Backend_StartAwait_RoundtripsBytes()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        var buf = MakeBuffer(4, 0xAB);

        backend.StartWriting(buf, buf.Length);
        var (result, returned) = backend.AwaitCompletion();

        Assert.Equal(4, result.BlocksWritten);
        Assert.False(result.EomEncountered);
        Assert.Null(result.Exception);
        Assert.Same(buf, returned);
        Assert.Equal(4, backend.TotalBlocksWritten);

        var written = backend.WrittenBuffers;
        Assert.Single(written);
        Assert.Equal(buf, written[0]);
    }

    [Fact]
    public void Backend_AlignsValidBytesDownToBlockBoundary()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        var buf = MakeBuffer(3, 0x11);

        // Hand off 5 fewer bytes than block-aligned; should round down to 2 full blocks.
        backend.StartWriting(buf, buf.Length - 5);
        var (result, _) = backend.AwaitCompletion();

        Assert.Equal(2, result.BlocksWritten);
    }

    [Fact]
    public void Backend_MultipleSequentialWrites_PreserveOrder()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);

        for (byte i = 1; i <= 5; i++)
        {
            var buf = MakeBuffer(2, i);
            backend.StartWriting(buf, buf.Length);
            backend.AwaitCompletion();
        }

        Assert.Equal(10, backend.TotalBlocksWritten);
        var written = backend.WrittenBuffers;
        Assert.Equal(5, written.Count);
        for (int i = 0; i < 5; i++)
            Assert.All(written[i], b => Assert.Equal((byte)(i + 1), b));
    }

    #endregion

    #region *** Concurrency / status ***

    [Fact]
    public void Backend_StartWriting_BlocksUntilPreviousCompletes()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.SetPerWriteDelay(TimeSpan.FromMilliseconds(150));

        var buf1 = MakeBuffer(2, 0x01);
        var buf2 = MakeBuffer(2, 0x02);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        backend.StartWriting(buf1, buf1.Length);
        // The second StartWriting must wait for the first to finish (~150ms).
        backend.StartWriting(buf2, buf2.Length);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 100,
            $"expected blocking ~150ms, observed {sw.ElapsedMilliseconds}ms");

        backend.AwaitCompletion();
    }

    [Fact]
    public void Backend_PollStatus_ReportsBusyThenIdle()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.SetPerWriteDelay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(WriteBackendStatus.Idle, backend.PollStatus());

        var buf = MakeBuffer(1, 0x77);
        backend.StartWriting(buf, buf.Length);

        // Should be busy almost immediately.
        Assert.Equal(WriteBackendStatus.Busy, backend.PollStatus());

        backend.AwaitCompletion();
        Assert.Equal(WriteBackendStatus.Idle, backend.PollStatus());
    }

    [Fact]
    public void Backend_AwaitCompletion_IsIdempotent()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        var buf = MakeBuffer(1, 0x42);

        backend.StartWriting(buf, buf.Length);
        var (r1, b1) = backend.AwaitCompletion();
        var (r2, b2) = backend.AwaitCompletion();
        var (r3, b3) = backend.AwaitCompletion();

        Assert.Equal(1, r1.BlocksWritten);
        Assert.Same(buf, b1);

        Assert.Equal(0, r2.BlocksWritten);
        Assert.Null(b2);
        Assert.Equal(0, r3.BlocksWritten);
        Assert.Null(b3);
    }

    [Fact]
    public void Backend_AwaitCompletion_WithNothingInFlight_ReturnsEmpty()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);

        var (r, b) = backend.AwaitCompletion();
        Assert.Equal(0, r.BlocksWritten);
        Assert.Null(b);
        Assert.Null(r.Exception);
        Assert.False(r.EomEncountered);
    }

    #endregion

    #region *** EOM scripting ***

    [Fact]
    public void Backend_ScriptedEom_ReportsPartialAcceptance()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.ScriptEomAfterBlocks(3);   // accept 3 full blocks, then EOM

        var buf = MakeBuffer(5, 0xCC);
        backend.StartWriting(buf, buf.Length);
        var (result, _) = backend.AwaitCompletion();

        Assert.Equal(3, result.BlocksWritten);
        Assert.True(result.EomEncountered);
        Assert.Null(result.Exception);
        Assert.Equal(3, backend.TotalBlocksWritten);
    }

    [Fact]
    public void Backend_ScriptedEom_AtBlockZero_ReportsZeroAccepted()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.ScriptEomAfterBlocks(0);

        var buf = MakeBuffer(4, 0xDD);
        backend.StartWriting(buf, buf.Length);
        var (result, returned) = backend.AwaitCompletion();

        Assert.Equal(0, result.BlocksWritten);
        Assert.True(result.EomEncountered);
        Assert.Same(buf, returned);
    }

    #endregion

    #region *** Hard error scripting ***

    [Fact]
    public void Backend_ScriptedHardError_SurfacesException()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.ScriptHardErrorAfterBlocks(2, "boom");

        var buf = MakeBuffer(5, 0xEE);
        backend.StartWriting(buf, buf.Length);
        var (result, returned) = backend.AwaitCompletion();

        Assert.Equal(2, result.BlocksWritten);
        Assert.False(result.EomEncountered);
        Assert.NotNull(result.Exception);
        Assert.Contains("boom", result.Exception!.Message);
        Assert.Same(buf, returned);
    }

    [Fact]
    public void Backend_HardError_DoesNotPoisonBackend()
    {
        // The backend reports the error and remains usable. Whether to keep going
        //  is the high-layer's policy decision.
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.ScriptHardErrorAfterBlocks(1);

        var bufA = MakeBuffer(2, 0xA1);
        backend.StartWriting(bufA, bufA.Length);
        var (rA, _) = backend.AwaitCompletion();
        Assert.NotNull(rA.Exception);

        // Subsequent write proceeds: scripted error fires only once because
        //  alreadyWritten > errorAfter after the first call.
        var bufB = MakeBuffer(2, 0xB2);
        backend.StartWriting(bufB, bufB.Length);
        var (rB, _) = backend.AwaitCompletion();
        Assert.Null(rB.Exception);
        Assert.Equal(2, rB.BlocksWritten);
    }

    #endregion

    #region *** Disposal ***

    [Fact]
    public void Backend_Dispose_DrainsInFlightWrite()
    {
        var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.SetPerWriteDelay(TimeSpan.FromMilliseconds(100));

        var buf = MakeBuffer(1, 0x55);
        backend.StartWriting(buf, buf.Length);

        // Should block until the in-flight write completes; total blocks must be recorded.
        backend.Dispose();
        Assert.Equal(1, backend.TotalBlocksWritten);
    }

    [Fact]
    public void Backend_StartWriting_AfterDispose_Throws()
    {
        var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.Dispose();

        var buf = MakeBuffer(1, 0);
        Assert.Throws<ObjectDisposedException>(() => backend.StartWriting(buf, buf.Length));
    }

    #endregion
}
