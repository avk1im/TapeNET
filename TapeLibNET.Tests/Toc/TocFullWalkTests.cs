using Windows.Win32.Foundation;

using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Toc;


/// <summary>
/// Design-TocLocation Phase 5: S4 — the full-media walk, asked first, cancellable, never on a resume — on tapes damaged
///  beyond what the anchors reach.
/// </summary>
public class TocFullWalkTests
{
    #region *** Helpers ***

    private const int HeaderBlock = 16 * 1024;

    /// <summary>More debris than the S2 anchor chain looks back over (five filemarks): only S4 reaches the TOC.</summary>
    private const int DebrisBeyondAnchors = 7;

    private static TempFileTree Tree(string name)
    {
        var tree = new TempFileTree();
        tree.AddFiles(name, count: 3, minSize: 512, maxSize: 4 * 1024);
        return tree;
    }

    /// <summary>Junk files behind the TOC: what failed later writes leave on a filemark-only tape.</summary>
    private static void AppendDebris(VirtualTapeFixture fixture, int files)
    {
        TapeDrive drive = fixture.Drive;
        Assert.True(drive.FastforwardToEnd(MediaPartition.Content));
        Assert.True(drive.SetBlockSize(HeaderBlock));
        var junk = new byte[HeaderBlock];
        new Random(13).NextBytes(junk);
        for (int i = 0; i < files; i++)
        {
            Assert.Equal(junk.Length, drive.WriteDirect(junk, 0, junk.Length));
            Assert.True(drive.WriteFilemark());
        }
    }

    /// <summary>One TOC copy written raw: 16 KiB blocks and a closing filemark — exactly as a copy goes on tape.</summary>
    private static void WriteRawCopy(TapeDrive drive, TapeTOC toc, byte copyIndex)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, copyIndex, copyCount: 2);
        var bytes = new byte[(ms.Length + HeaderBlock - 1) / HeaderBlock * HeaderBlock];
        ms.ToArray().CopyTo(bytes, 0);
        Assert.Equal(bytes.Length, drive.WriteDirect(bytes, 0, bytes.Length));
        Assert.True(drive.WriteFilemark());
    }

    private static VirtualTapeFixture DebrisTape(TempFileTree tree, bool headed)
    {
        var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, withMediaHeader: headed);
        fixture.BackupFiles(tree.Files, description: "Full walk");
        AppendDebris(fixture, DebrisBeyondAnchors);
        return fixture;
    }

    #endregion

    #region *** Confirmed, declined, unasked ***

    /// <summary>
    /// Debris beyond the anchors: S0, S1 and S2 find nothing; S4 is asked once — for the loaded volume, 0 — walks the
    ///  whole medium with progress, and recovers copy 1 by <see cref="TapeTocAnomalyStage.FullWalk"/>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filemarks_DebrisBeyondAnchors_RecoveredByFullWalk(bool headed)
    {
        using var tree = Tree("full");
        using var fixture = DebrisTape(tree, headed);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        var notify = new TestNotifiable();
        Assert.True(reader.RestoreTOC(notify), reader.LastErrorMessage);
        Assert.Equal(1, reader.TOC.Count);
        Assert.Equal(tree.Files.Count, reader.TOC[1].Count);

        Assert.Equal([0], notify.TocFullWalkConfirms);
        Assert.NotEmpty(notify.TocSearchProgress);

        var recovered = Assert.Single(reader.TocAnomalies, a => a.IsRecovery);
        Assert.Equal(TapeTocAnomalyStage.FullWalk, recovered.Stage);
        Assert.Equal(1, recovered.CopyIndex);
        Assert.Equal(1, reader.Statistics.TocStats.AnomaliesRecoveredByWalk);
        Assert.Equal(TocAnomalyAdvice.TocFoundBySearch,
            TapeServiceBase.AdviseOnTocAnomalies(reader.TocAnomalies, reader.TOCPlacement).Advice);
    }

    /// <summary>Declined: no walk, a failure that says so, and the detection still names a stage that remains.</summary>
    [Fact]
    public void FullWalkDeclined_FailsNamingIt_DetectionCanStillRecover()
    {
        using var tree = Tree("declined");
        using var fixture = DebrisTape(tree, headed: true);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        var notify = new TestNotifiable { TocFullWalkAnswer = false };
        TapeResult result = reader.RestoreTOC(notify);

        Assert.False(result);
        Assert.Contains("declined", result.ErrorMessage);
        Assert.Single(notify.TocFullWalkConfirms);
        Assert.Empty(notify.TocSearchProgress);
        var detected = Assert.Single(reader.TocAnomalies);
        Assert.False(detected.IsRecovery);
        Assert.True(detected.CanAttemptRecovery, "S4 remains — the user may still ask for it");
    }

    /// <summary>No notifiable: nobody to ask — the read-only search runs, as the interface default would.</summary>
    [Fact]
    public void NoNotifiable_FullWalkRunsUnasked()
    {
        using var tree = Tree("unasked");
        using var fixture = DebrisTape(tree, headed: false);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        Assert.True(reader.RestoreTOC(), reader.LastErrorMessage);
        Assert.Equal(TapeTocAnomalyStage.FullWalk, Assert.Single(reader.TocAnomalies, a => a.IsRecovery).Stage);
    }

    #endregion

    #region *** Cancellation ***

    /// <summary>
    /// The user cancels while the whole medium is searched: the walk stops at a block boundary, the result is
    ///  <c>ERROR_CANCELLED</c>, and nothing is recorded as found wanting — an abort is no anomaly.
    /// </summary>
    [Fact]
    public void FullWalk_AbortedDuringTheWalk_Cancelled_NoAnomaly()
    {
        using var tree = Tree("abort");
        using var fixture = DebrisTape(tree, headed: true);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        var notify = new TestNotifiable { TocSearchProgressFunc = _ => reader.IsAbortRequested = true };
        TapeResult result = reader.RestoreTOC(notify);

        Assert.False(result);
        Assert.Equal((uint)WIN32_ERROR.ERROR_CANCELLED, result.ErrorCode);
        Assert.Empty(reader.TocAnomalies);
        Assert.Empty(notify.TocAnomaliesRecovered);
    }

    /// <summary>The same abort, thrown from the progress callback — the other channel a notifiable has.</summary>
    [Fact]
    public void FullWalk_AbortThrownFromProgress_Cancelled()
    {
        using var tree = Tree("throw");
        using var fixture = DebrisTape(tree, headed: false);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        var notify = new TestNotifiable
        {
            TocSearchProgressFunc = _ => throw new TapeAbortRequestedException("test abort"),
        };
        TapeResult result = reader.RestoreTOC(notify);

        Assert.False(result);
        Assert.Equal((uint)WIN32_ERROR.ERROR_CANCELLED, result.ErrorCode);
        Assert.True(reader.IsAbortRequested);
    }

    #endregion

    #region *** Partitioned media ***

    /// <summary>
    /// A blank TOC partition, the copies standing in the CONTENT partition (a mixed-up cartridge): S2 walks the whole TOC
    ///  partition and finds nothing; S4 walks the content partition and recovers copy 1 there.
    /// </summary>
    [Fact]
    public void Partitions_CopiesInContentPartition_RecoveredByFullWalk()
    {
        using var tree = Tree("mixed");
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true);
        fixture.BackupFiles(tree.Files, description: "Mixed up");
        fixture.Backend.InitiatorMedia!.TruncateAt(0);

        TapeDrive drive = fixture.Drive;
        Assert.True(drive.FastforwardToEnd(MediaPartition.Content));
        Assert.True(drive.SetBlockSize(HeaderBlock));
        WriteRawCopy(drive, fixture.TOC, copyIndex: 1);
        WriteRawCopy(drive, fixture.TOC, copyIndex: 2);
        Assert.True(drive.MoveToPartition(MediaPartition.Content, 0));

        using var reader = new TapeAgentBase(drive, new TapeTOC());
        var notify = new TestNotifiable();
        Assert.True(reader.RestoreTOC(notify), reader.LastErrorMessage);
        Assert.Equal(1, reader.TOC.Count);

        var recovered = Assert.Single(reader.TocAnomalies, a => a.IsRecovery);
        Assert.Equal(TapeTocAnomalyStage.FullWalk, recovered.Stage);
        Assert.Equal(MediaPartition.Content, recovered.Partition);
        Assert.Equal(1, recovered.CopyIndex);
    }

    #endregion

#if TEST_HOOKS
    #region *** Never on a resume ***

    /// <summary>
    /// With a series TOC at hand — a multi-volume resume — the search stops after S2: the series TOC addresses every
    ///  file, and reading a whole earlier volume to identify it would be pointless. Nothing is asked.
    /// </summary>
    [Fact]
    public void SeriesToc_NoFullWalk_NothingAsked()
    {
        using var tree = Tree("series");
        using var fixture = DebrisTape(tree, headed: true);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC()) { SimulateTOCFailureMask = -1 };
        var notify = new TestNotifiable();
        Assert.False(reader.RestoreTOCFromMedia(fixture.TOC.Volume, notify, seriesToc: fixture.TOC));
        Assert.Empty(notify.TocFullWalkConfirms);
        Assert.False(Assert.Single(reader.TocAnomalies).CanAttemptRecovery);
    }

    #endregion
#endif // TEST_HOOKS
}
