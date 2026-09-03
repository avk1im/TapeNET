using TapeLibNET.TapeFilePacker;

namespace TapeLibNET.Tests.TapeFilePacker;

/// <summary>
/// Unit tests for the low-layer write backend (<see cref="ITapeWriteBackend"/>).
/// Uses <see cref="MemoryTapeWriteBackend"/> which exercises the same
/// <see cref="WorkerThreadTapeWriteBackend"/> machinery as production but
/// records bytes in memory and supports scripted EOM / hard-error injection.
/// <para>
/// Buffers are page-aligned <see cref="TapeWriteBuffer"/> instances handed out by
/// <see cref="PooledTestBuffers"/>, which owns the pool and returns every rental on dispose.
/// The fixture implements <see cref="IDisposable"/> so xUnit tears it down per test.
/// </para>
/// </summary>
public class TapeWriteBackendTests : IDisposable
{
    private const uint BlockSize = 512;

    private readonly PooledTestBuffers _bufs = new(BlockSize);

    // Convenience: byte length of N blocks, and a content snapshot of a buffer window.
    private int Bytes(int blocks) => _bufs.Bytes(blocks);
    private static byte[] Content(TapeWriteBuffer buf, int length) => PooledTestBuffers.Content(buf, length);

    public void Dispose()
    {
        _bufs.Dispose();
        GC.SuppressFinalize(this);
    }

    #region *** Basic round-trip ***

    [Fact]
    public void Backend_StartAwait_RoundtripsBytes()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        var buf = _bufs.Make(4, 0xAB);
        backend.StartWriting(buf, Bytes(4));
        var (result, returned) = backend.AwaitCompletion();

        Assert.Equal(4, result.BlocksWritten);
        Assert.False(result.EomEncountered);
        Assert.Null(result.Exception);
        Assert.Same(buf, returned);
        Assert.Equal(4, backend.TotalBlocksWritten);

        var written = backend.WrittenBuffers;
        Assert.Single(written);
        Assert.Equal(Content(buf, Bytes(4)), written[0]);
    }

    [Fact]
    public void Backend_AlignsValidBytesDownToBlockBoundary()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        var buf = _bufs.Make(3, 0x11);
        // Hand off 5 fewer bytes than block-aligned; should round down to 2 full blocks.
        backend.StartWriting(buf, Bytes(3) - 5);
        var (result, _) = backend.AwaitCompletion();

        Assert.Equal(2, result.BlocksWritten);
    }

    [Fact]
    public void Backend_MultipleSequentialWrites_PreserveOrder()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        for (byte i = 1; i <= 5; i++)
        {
            var buf = _bufs.Make(2, i);
            backend.StartWriting(buf, Bytes(2));
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
        var buf1 = _bufs.Make(2, 0x01);
        var buf2 = _bufs.Make(2, 0x02);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        backend.StartWriting(buf1, Bytes(2));
        // The second StartWriting must wait for the first to finish (~150ms).
        backend.StartWriting(buf2, Bytes(2));
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

        var buf = _bufs.Make(1, 0x77);
        backend.StartWriting(buf, Bytes(1));
        // Should be busy almost immediately.
        Assert.Equal(WriteBackendStatus.Busy, backend.PollStatus());

        backend.AwaitCompletion();
        Assert.Equal(WriteBackendStatus.Idle, backend.PollStatus());
    }

    [Fact]
    public void Backend_AwaitCompletion_IsIdempotent()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        var buf = _bufs.Make(1, 0x42);
        backend.StartWriting(buf, Bytes(1));

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
        var buf = _bufs.Make(5, 0xCC);
        backend.StartWriting(buf, Bytes(5));
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
        var buf = _bufs.Make(4, 0xDD);
        backend.StartWriting(buf, Bytes(4));
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
        var buf = _bufs.Make(5, 0xEE);
        backend.StartWriting(buf, Bytes(5));
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
        var bufA = _bufs.Make(2, 0xA1);
        backend.StartWriting(bufA, Bytes(2));
        var (rA, _) = backend.AwaitCompletion();
        Assert.NotNull(rA.Exception);

        // Subsequent write proceeds: scripted error fires only once because
        //  alreadyWritten > errorAfter after the first call.
        var bufB = _bufs.Make(2, 0xB2);
        backend.StartWriting(bufB, Bytes(2));
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
        var buf = _bufs.Make(1, 0x55);
        backend.StartWriting(buf, Bytes(1));
        // Should block until the in-flight write completes; total blocks must be recorded.
        backend.Dispose();

        Assert.Equal(1, backend.TotalBlocksWritten);
    }

    [Fact]
    public void Backend_StartWriting_AfterDispose_Throws()
    {
        var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.Dispose();
        var buf = _bufs.Make(1, 0);
        Assert.Throws<ObjectDisposedException>(() => backend.StartWriting(buf, Bytes(1)));
    }

    #endregion
}
