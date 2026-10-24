using Windows.Win32.Foundation;
using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocLocation Phase 4: the TOC location ladder on DAMAGED tapes built deterministically — debris behind the TOC,
///  a destroyed TOC mark, junk in front of the TOC partition's copies — the expected position from a series TOC, foreign
///  copies, the append guard, and the block-length rule of the virtual drive.
/// </summary>
public class TocLocationLadderTests
{
    #region *** Helpers ***

    private const int HeaderBlock = 16 * 1024;

    private static TempFileTree Tree(string name, int seed = 1)
    {
        var tree = new TempFileTree(seed: seed);
        tree.AddFiles(name, count: 3, minSize: 512, maxSize: 4 * 1024);
        return tree;
    }

    private static string NewDir() => Path.Combine(Path.GetTempPath(), $"TapeNET_TL4_{Guid.NewGuid():N}");

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private static string RestoredRoot(string restoreDir, string originalRoot)
        => Path.Combine(restoreDir, Path.GetRelativePath(Path.GetPathRoot(originalRoot)!, originalRoot));

    /// <summary>Junk files behind the TOC: what a failed later write leaves on a filemark-only tape.</summary>
    private static void AppendDebris(VirtualTapeFixture fixture, int files)
    {
        TapeDrive drive = fixture.Drive;
        Assert.True(drive.FastforwardToEnd(MediaPartition.Content));
        Assert.True(drive.SetBlockSize(HeaderBlock));
        var junk = new byte[HeaderBlock];
        new Random(7).NextBytes(junk);
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

    private static TapeTocAnomaly Recovered(TapeAgentBase agent)
        => Assert.Single(agent.TocAnomalies, a => a.IsRecovery);

    #endregion

    #region *** S2: the anchor walk ***

    /// <summary>
    /// Debris behind the TOC on a filemark-only tape: counting back three filemarks lands on the junk, and both native
    ///  retries read junk too. The walk from five filemarks back meets copy 1 — recovered by <c>AnchorWalk</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filemarks_DebrisBehindToc_RecoveredByAnchorWalk(bool headed)
    {
        using var tree = Tree("debris");
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, withMediaHeader: headed);
        fixture.BackupFiles(tree.Files, description: "Debris");
        AppendDebris(fixture, files: 2);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        var notify = new TestNotifiable();
        Assert.True(reader.RestoreTOC(notify), reader.LastErrorMessage);

        Assert.Equal(1, reader.TOC.Count);
        Assert.Equal(tree.Files.Count, reader.TOC[1].Count);
        TapeTocAnomaly recovered = Recovered(reader);
        Assert.Equal(TapeTocAnomalyStage.AnchorWalk, recovered.Stage);
        Assert.Equal(TapeTocAnomalyKind.NoReadableCopy, recovered.Kind);
        Assert.Equal(1, recovered.CopyIndex);
        Assert.Equal(1, reader.Statistics.TocStats.AnomaliesRecoveredByWalk);
        Assert.Single(notify.TocAnomalies);
        Assert.Single(notify.TocAnomaliesRecovered);
    }

    /// <summary>
    /// A TOC mark missing one of its three filemarks: the TOC-mark search finds nothing, and "no mark" with content is
    ///  refused (R2). The walk from the filemark anchor crosses the damaged mark and reads copy 1.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TocMark_MarkDamaged_RecoveredByAnchorWalk(bool headed)
    {
        using var tree = Tree("mark");
        using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks, withMediaHeader: headed);
        fixture.BackupFiles(tree.Files, description: "Mark");

        var media = fixture.Backend.ContentMedia!;
        var layout = media.Layout;
        int run = -1;
        for (int i = 0; i + 2 < layout.Count && run < 0; i++)
            if (layout[i].IsMark && layout[i + 1].IsMark && layout[i + 2].IsMark)
                run = i;
        Assert.True(run >= 0, $"no TOC mark found:\n{media.DescribeLayout()}");
        media.RemoveMarkAt(layout[run + 1].BeginAtBlock);
        Assert.True(fixture.Drive.Rewind());

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        Assert.True(reader.RestoreTOC(), $"{reader.LastErrorMessage}\n{media.DescribeLayout()}");

        Assert.Equal(1, reader.TOC.Count);
        TapeTocAnomaly recovered = Recovered(reader);
        Assert.Equal(TapeTocAnomalyStage.AnchorWalk, recovered.Stage);
        Assert.Equal(TapeTocAnomalyKind.NotFoundNatively, recovered.Kind);
    }

    /// <summary>
    /// Partitioned media: junk in front of the TOC copies in the initiator partition defeats S0 and S1. S3 does not apply
    ///  to a TOC partition; the walk of the whole initiator partition finds copy 1 there.
    /// </summary>
    [Fact]
    public void Partitions_JunkBeforeToc_RecoveredInTheTocPartition()
    {
        using var tree = Tree("part");
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true);
        fixture.BackupFiles(tree.Files, description: "Partitioned");

        TapeDrive drive = fixture.Drive;
        Assert.True(drive.MoveToPartition(MediaPartition.Initiator, 0));
        Assert.True(drive.SetBlockSize(HeaderBlock));
        var junk = new byte[HeaderBlock];
        new Random(11).NextBytes(junk);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(junk.Length, drive.WriteDirect(junk, 0, junk.Length));
            Assert.True(drive.WriteFilemark());
        }
        WriteRawCopy(drive, fixture.TOC, copyIndex: 1);
        WriteRawCopy(drive, fixture.TOC, copyIndex: 2);
        Assert.True(drive.MoveToPartition(MediaPartition.Content, 0));

        using var reader = new TapeAgentBase(drive, new TapeTOC());
        Assert.True(reader.RestoreTOC(), reader.LastErrorMessage);

        Assert.Equal(1, reader.TOC.Count);
        TapeTocAnomaly recovered = Recovered(reader);
        Assert.Equal(TapeTocAnomalyStage.AnchorWalk, recovered.Stage);
        Assert.Equal(MediaPartition.Initiator, recovered.Partition);
        Assert.Equal(1, recovered.CopyIndex);
    }

    /// <summary>A blank TOC partition: nothing to find — failure, and the diagnosis says why.</summary>
    [Fact]
    public void Partitions_BlankTocPartition_FailsNamingIt()
    {
        using var tree = Tree("blank");
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true);
        fixture.BackupFiles(tree.Files, description: "Blank TOC partition");
        fixture.Backend.InitiatorMedia!.TruncateAt(0);
        Assert.True(fixture.Drive.MoveToPartition(MediaPartition.Content, 0));

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        var result = reader.RestoreTOC();

        Assert.False(result);
        Assert.Contains("TOC partition is blank", result.ErrorMessage);
        Assert.DoesNotContain(reader.TocAnomalies, a => a.IsRecovery);
    }

    #endregion

    #region *** S3: the expected position ***

    /// <summary>With a series TOC at hand, the search jumps to where its content ends: one locate, <c>ExpectedPosition</c>.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filemarks_SeriesToc_RecoveredAtExpectedPosition(bool headed)
    {
        using var tree = Tree("expected");
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, withMediaHeader: headed);
        fixture.BackupFiles(tree.Files, description: "Expected");
        AppendDebris(fixture, files: 2);

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        Assert.True(reader.RestoreTOCFromMedia(fixture.TOC.Volume, tocNotify: null, seriesToc: fixture.TOC),
            reader.LastErrorMessage);

        TapeTocAnomaly recovered = Recovered(reader);
        Assert.Equal(TapeTocAnomalyStage.ExpectedPosition, recovered.Stage);
        Assert.Equal(fixture.TOC[1].FirstBlockAfterSet, recovered.Block);
        Assert.Equal(0, reader.Statistics.TocStats.AnomaliesRecoveredByWalk);
    }

    /// <summary>Copies of another series are found — and skipped, each recorded as <c>ForeignCopySkipped</c>.</summary>
    [Fact]
    public void ForeignCopies_AreSkipped_AndTheLoadFails()
    {
        using var tree = Tree("foreign");
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly);
        fixture.BackupFiles(tree.Files, description: "Foreign");
        AppendDebris(fixture, files: 2);
        var otherSeries = new TapeTOC(fixture.TOC) { MediaId = Guid.NewGuid() };

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        Assert.False(reader.RestoreTOCFromMedia(fixture.TOC.Volume, tocNotify: null, seriesToc: otherSeries));

        Assert.Equal(TapeTocAnomalyKind.NoReadableCopy, reader.TocAnomalies[0].Kind);
        Assert.Contains(reader.TocAnomalies, a => a.Kind == TapeTocAnomalyKind.ForeignCopySkipped);
        Assert.DoesNotContain(reader.TocAnomalies, a => a.IsRecovery);
    }

    #endregion

    #region *** The append guard (TL-3) ***

    /// <summary>
    /// The exposure Phase 0 left open: <c>[MH][FM][set][FM][toc1][FM][toc2]</c> — copy 2's filemark missing. Counting back
    ///  three filemarks lands on the SET, and an append there would overwrite it. The append goes where the in-memory TOC
    ///  says content ends, confirmed on tape by the closing mark and the TOC copy standing there.
    /// </summary>
    [Fact]
    public void Append_MissingLastFilemark_WritesAtRecordedEndOfContent()
    {
        using var first = Tree("keep", seed: 1);
        using var second = Tree("append", seed: 2);
        string dir1 = NewDir(), dir2 = NewDir();
        try
        {
            using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, withMediaHeader: true,
                withSetHeaders: true);
            fixture.BackupFiles(first.Files, description: "Keep");

            var media = fixture.Backend.ContentMedia!;
            var lastMark = media.Layout[^1];
            Assert.True(lastMark.IsMark, media.DescribeLayout());
            media.TruncateAt(lastMark.BeginAtBlock);
            Assert.True(fixture.Drive.Rewind());

            fixture.BackupFiles(second.Files, description: "Append");     // the in-memory TOC knows where set 1 ends

            fixture.LoadTOC();
            Assert.Equal(2, fixture.TOC.Count);
            foreach ((int set, TempFileTree tree, string dir) in new[] { (1, first, dir1), (2, second, dir2) })
            {
                fixture.TOC.CurrentSetIndex = set;
                using var agent = fixture.CreateRestoreAgent(dir);
                Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false), $"set {set}");
                FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoredRoot(dir, tree.RootPath));
            }
        }
        finally
        {
            TryDelete(dir1);
            TryDelete(dir2);
        }
    }

    #endregion

    #region *** Block length (virtual drive) ***

    /// <summary>A block written larger than the read: data delivered, <c>ERROR_INVALID_BLOCK_LENGTH</c>, head past the block.</summary>
    [Fact]
    public void Virtual_ReadSmallerThanBlock_DeliversDataWithBlockLengthError()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly);
        TapeDrive drive = fixture.Drive;
        var block = new byte[64 * 1024];
        new Random(3).NextBytes(block);
        Assert.True(drive.SetBlockSize((uint)block.Length));
        Assert.Equal(block.Length, drive.WriteDirect(block, 0, block.Length));
        Assert.True(drive.WriteFilemark());

        Assert.True(drive.Rewind());
        Assert.True(drive.SetBlockSize(HeaderBlock));
        var buffer = new byte[HeaderBlock];
        int read = drive.ReadDirect(buffer, 0, buffer.Length, out bool tapemark, out _);

        Assert.Equal(WIN32_ERROR.ERROR_INVALID_BLOCK_LENGTH, drive.LastErrorWin32);
        Assert.False(tapemark);
        Assert.True(read > 0);
        Assert.Equal(block[..read], buffer[..read]);
        Assert.Equal(1, drive.CurrentBlock);
    }

    /// <summary>A block written smaller than the read: its data delivered, the same error.</summary>
    [Fact]
    public void Virtual_ReadLargerThanBlock_DeliversDataWithBlockLengthError()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly);
        TapeDrive drive = fixture.Drive;
        var block = new byte[4 * 1024];
        new Random(5).NextBytes(block);
        Assert.True(drive.SetBlockSize((uint)block.Length));
        Assert.Equal(block.Length, drive.WriteDirect(block, 0, block.Length));
        Assert.True(drive.WriteFilemark());

        Assert.True(drive.Rewind());
        Assert.True(drive.SetBlockSize(HeaderBlock));
        var buffer = new byte[HeaderBlock];
        int read = drive.ReadDirect(buffer, 0, buffer.Length, out _, out _);

        Assert.Equal(WIN32_ERROR.ERROR_INVALID_BLOCK_LENGTH, drive.LastErrorWin32);
        Assert.True(read >= block.Length);
        Assert.Equal(block, buffer[..block.Length]);
    }

    /// <summary>Same size, same as ever: no error.</summary>
    [Fact]
    public void Virtual_ReadMatchingBlock_NoError()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly);
        TapeDrive drive = fixture.Drive;
        var block = new byte[HeaderBlock];
        Assert.True(drive.SetBlockSize(HeaderBlock));
        Assert.Equal(block.Length, drive.WriteDirect(block, 0, block.Length));
        Assert.True(drive.Rewind());
        Assert.Equal(block.Length, drive.ReadDirect(new byte[HeaderBlock], 0, HeaderBlock, out _, out _));
        Assert.True(drive.WentOK);
    }

    /// <summary>
    /// Identification absorbs the mismatch: a 64 KiB content block read at 16 KiB is a finding, not a fault — no stale
    ///  error, the head past the block, the caller's block size back.
    /// </summary>
    [Fact]
    public void ReadAndIdentify_OtherBlockSize_IsAFindingNotAFault()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly);
        TapeDrive drive = fixture.Drive;
        var block = new byte[64 * 1024];
        new Random(9).NextBytes(block);
        Assert.True(drive.SetBlockSize((uint)block.Length));
        Assert.Equal(block.Length, drive.WriteDirect(block, 0, block.Length));
        Assert.True(drive.WriteFilemark());
        Assert.True(drive.Rewind());

        IdentifiedBlock id = TapeHeaderBlock.ReadAndIdentify(drive, out _, out int read, out bool tapemark, out bool eod);

        Assert.Equal(HeaderBlockIdentity.Foreign, id.Kind);
        Assert.True(read > 0);
        Assert.False(tapemark);
        Assert.False(eod);
        Assert.True(drive.WentOK, $"stale error {drive.LastErrorWin32}");
        Assert.Equal(1, drive.CurrentBlock);
        Assert.Equal((uint)block.Length, drive.BlockSize);
    }

    #endregion
}
