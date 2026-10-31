using TapeLibNET.Streams;
using TapeLibNET.Media;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Media;


#if TEST_HOOKS
/// <summary>
/// A write stream whose final flush fails — end-of-media on the second TOC copy, a write fault — must still sign out
///  from its manager. Before the fix it stayed the manager's <c>Stream</c>: the next TOC copy reused the zombie, and the
///  next state change flushed its stale bytes, across a volume swap onto the next cartridge.
/// </summary>
/// <remarks>
/// The Partitions profile writes the TOC to the initiator partition from a blank state with no search, so the fault is
///  aimed at exactly the TOC flush (<c>InitiatorWriteFaults</c>).
/// </remarks>
public class TapeStreamDisposalTests
{
    private static TapeWriteStream OpenTocStreamWithPendingBytes(TapeStreamManager mgr)
    {
        var stream = mgr.ProduceWriteTOCStream();
        Assert.NotNull(stream);
        stream!.Write(new byte[1000], 0, 1000);   // less than a block: stays buffered until disposal
        return stream;
    }

    [Fact]
    public void FailedFlushOnDispose_Throws_AndStillSignsOut()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var mgr = new TapeStreamManager(fixture.Drive);
        var stream = OpenTocStreamWithPendingBytes(mgr);

        fixture.Backend.InitiatorWriteFaults.FailOnce();
        Assert.ThrowsAny<Exception>(() => stream.Dispose());

        Assert.Equal(1, fixture.Backend.InitiatorWriteFaults.Occurrences);
        Assert.True(stream.IsDisposed);
        Assert.True(stream.WriteFailed, "a failed flush marks the file failed: no trailing filemark");
        Assert.False(mgr.IsStreamInUse, "the stream must sign out even though its flush failed");

        stream.Dispose();                          // a second dispose is a no-op — no retry of the lost bytes
        Assert.Equal(1, fixture.Backend.InitiatorWriteFaults.Occurrences);
    }

    /// <summary>The originating defect: the manager must not flush the failed stream's bytes on its next transition.</summary>
    [Fact]
    public void FailedFlushOnDispose_NextTransition_WritesNothingStale()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var mgr = new TapeStreamManager(fixture.Drive);
        var stream = OpenTocStreamWithPendingBytes(mgr);

        fixture.Backend.InitiatorWriteFaults.FailOnce();
        Assert.ThrowsAny<Exception>(() => stream.Dispose());

        // Any write the transition attempted would hit this armed fault
        fixture.Backend.InitiatorWriteFaults.FailOnce();
        fixture.Backend.ContentWriteFaults.FailOnce();
        Assert.True(mgr.EndReadWrite());
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
        Assert.True(fixture.Backend.InitiatorWriteFaults.Enabled, "nothing may have been written to the TOC partition");
        Assert.True(fixture.Backend.ContentWriteFaults.Enabled, "nothing may have been written to the content partition");

        fixture.Backend.InitiatorWriteFaults.Reset();
        fixture.Backend.ContentWriteFaults.Reset();
    }

    /// <summary>The next TOC copy gets a FRESH stream — never the failed one with its leftover bytes.</summary>
    [Fact]
    public void FailedFlushOnDispose_NextTocStream_IsFresh()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var mgr = new TapeStreamManager(fixture.Drive);
        var first = OpenTocStreamWithPendingBytes(mgr);

        fixture.Backend.InitiatorWriteFaults.FailOnce();
        Assert.ThrowsAny<Exception>(() => first.Dispose());

        var second = mgr.ProduceWriteTOCStream();
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(0, second!.Length);
        second.Dispose();
    }

    /// <summary>A failed write faults the stream: its disposal neither re-flushes nor throws a second time.</summary>
    [Fact]
    public void FailedWrite_FaultsTheStream_DisposeIsQuiet()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var mgr = new TapeStreamManager(fixture.Drive);
        var stream = mgr.ProduceWriteTOCStream();
        Assert.NotNull(stream);

        int big = (int)fixture.Drive.BlockSize * 4;  // ≥ the buffer: written directly
        fixture.Backend.InitiatorWriteFaults.FailOnce();
        Assert.ThrowsAny<Exception>(() => stream!.Write(new byte[big], 0, big));

        stream!.Dispose();                          // must not throw: nothing left to flush
        Assert.True(stream.WriteFailed);
        Assert.False(mgr.IsStreamInUse);
        Assert.Equal(1, fixture.Backend.InitiatorWriteFaults.Occurrences);
    }

    /// <summary>A media change drops a stream still in use WITHOUT any I/O, and leaves the manager ready.</summary>
    [Fact]
    public void RenewNavigator_AbandonsAStreamInUse_WithoutIo()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var mgr = new TapeStreamManager(fixture.Drive);
        var stream = OpenTocStreamWithPendingBytes(mgr);

        fixture.Backend.InitiatorWriteFaults.FailOnce();
        fixture.Backend.ContentWriteFaults.FailOnce();
        Assert.True(mgr.RenewNavigator());

        Assert.False(mgr.IsStreamInUse);
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
        Assert.True(stream.IsDisposed);
        Assert.True(fixture.Backend.InitiatorWriteFaults.Enabled, "an abandoned stream writes nothing");
        Assert.True(fixture.Backend.ContentWriteFaults.Enabled, "an abandoned stream writes nothing");

        stream.Dispose();                           // the owner's later dispose is a no-op
        fixture.Backend.InitiatorWriteFaults.Reset();
        fixture.Backend.ContentWriteFaults.Reset();
    }
}
#endif
