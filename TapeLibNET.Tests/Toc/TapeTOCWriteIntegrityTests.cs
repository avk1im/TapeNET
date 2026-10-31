using Windows.Win32.Foundation;

using TapeLibNET.Agents;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocLocation §8.2, Phase 0: a write the drive did not take is never reported as written (F1), the stream never
///  spins on a 0-byte write (F5), the early-warning reserve never clamps the TOC (F2), and a full headed volume keeps
///  the whole reserve for its TOC (F3) — both copies fit.
/// </summary>
public class TapeTOCWriteIntegrityTests
{
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    private const long SmallCapacity = 2L * 1024 * 1024;
    private const uint BlockSize = 16 * 1024;

    #region *** F1 / F5 — short writes fail ***

    /// <summary>
    /// F1: a content stream whose every write the reserve clamps to nothing must FAIL its flush — with end-of-media, the
    ///  write's own signal — instead of reporting success and letting a filemark seal an empty file.
    /// </summary>
    [Fact]
    public void ClampedFlush_FailsWithEndOfMedia_AndWritesNoFilemark()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, contentCapacity: SmallCapacity);
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        Assert.True(fixture.Drive.SetBlockSize(BlockSize));
        Assert.True(fixture.Drive.SetEarlyWarning(SmallCapacity * 2));   // nothing fits ahead of this reserve
        long before = fixture.Drive.CurrentBlock;

        var wstream = agent.Manager.ProduceWriteContentStream(length: -1, writtenSoFar: 0);
        Assert.NotNull(wstream);
        wstream!.Write(new byte[100], 0, 100);                            // buffered, not yet written

        var ex = Assert.Throws<TapeIOException>(wstream.Flush);
        Assert.Equal((uint)WIN32_ERROR.ERROR_END_OF_MEDIA, ex.Error);
        Assert.True(wstream.WriteFailed);

        wstream.Dispose();                                                // quiet: the stream is faulted
        Assert.Equal(before, fixture.Drive.CurrentBlock);                 // no data, and no filemark behind it
    }

    /// <summary>F5: a direct write the drive accepts 0 bytes of throws — under a time bound, so a regression fails, not hangs.</summary>
    [Fact]
    public async Task ZeroByteDirectWrite_Throws_InsteadOfSpinning()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, contentCapacity: SmallCapacity);
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        Assert.True(fixture.Drive.SetBlockSize(BlockSize));
        Assert.True(fixture.Drive.SetEarlyWarning(SmallCapacity * 2));

        var wstream = agent.Manager.ProduceWriteContentStream(length: -1, writtenSoFar: 0);
        Assert.NotNull(wstream);
        var big = new byte[4 * BlockSize];                                // ≥ buffer capacity ⇒ direct path

        Task write = Task.Run(() => wstream!.Write(big, 0, big.Length));
        Task done = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(write, done);
        var ex = await Assert.ThrowsAsync<TapeIOException>(() => write);
        Assert.Equal((uint)WIN32_ERROR.ERROR_END_OF_MEDIA, ex.Error);
        wstream!.Dispose();
    }

    #endregion

    #region *** F2 — the reserve never clamps the TOC ***

    /// <summary>
    /// F2: with a reserve larger than the cartridge, every reserve-honouring write clamps to 0 B. The TOC — and the TOC
    ///  mark's gap file — must bypass the clamp: the reserve is their room.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TocWrite_IsNotClampedByTheReserve(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("f2", count: 4, minSize: 1024, maxSize: 8 * 1024);
        using var fixture = new VirtualTapeFixture(profile, contentCapacity: SmallCapacity);

        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.Description = "Reserve";
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        using (var agent = fixture.CreateBackupAgent())
        {
            // The backup arms the reserve from Navigator.TOCCapacity (floor 32 MiB) — larger than this cartridge, so
            //  content would clamp to 0 B before the TOC is ever reached. Give the CONTENT phase a reserve that fits.
            agent.Navigator.TOCCapacity = 128 * 1024;
            Assert.True(agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: false),
                agent.LastErrorMessage);

            // Now the case under test: a reserve beyond the whole cartridge. Every reserve-honouring write would clamp
            //  to 0 B — the TOC and the TOC mark's gap file must not.
            Assert.True(fixture.Drive.SetEarlyWarning(SmallCapacity * 2));
            Assert.True(agent.BackupTOC(), $"the TOC must be written despite the reserve: {agent.LastErrorMessage}");
        }

        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);
    }

    /// <summary>The content path still honours the reserve: nothing is written past it.</summary>
    [Fact]
    public void ContentWrite_StillHonoursTheReserve()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, contentCapacity: SmallCapacity);
        var drive = fixture.Drive;
        Assert.True(drive.SetEarlyWarning(SmallCapacity * 2));

        var block = new byte[drive.BlockSize];
        int written = drive.WriteDirect(block, 0, block.Length, out _, out bool ew, out _);

        Assert.Equal(0, written);
        Assert.True(ew, "a clamped content write reports early warning so the set wraps up");
    }

    #endregion

    #region *** F3 + W1 — a full headed volume keeps its whole TOC reserve ***

    public static TheoryData<DriveProfile> MarkCountedInSetProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.FilemarksOnly,
    ];

    /// <summary>
    /// F3, the reproduction of the multi-volume failures: a headed volume filled to its early warning must still hold
    ///  BOTH TOC copies — the reserve was armed for exactly two blocks of TOC, and the media header used to eat one of
    ///  them. Copy 2 is proven present by reading it alone (copy 1 simulated unreadable).
    /// </summary>
    /// <remarks>
    /// Setmark and plain-filemark layouts only: the TOC-mark layout additionally needs its gap file, which a 32 KiB reserve
    ///  cannot hold together with two copies — that is F4's warning, not a defect.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MarkCountedInSetProfiles))]
    public void FullHeadedVolume_BothTocCopiesFit(DriveProfile profile)
    {
        const long reserve = 32L * 1024;
        using var tree = new TempFileTree();
        tree.AddFiles("f3", count: 24, minSize: 16 * 1024, maxSize: 32 * 1024);  // ≫ the cartridge
        using var fixture = new VirtualTapeFixture(profile, contentCapacity: 256L * 1024, withMediaHeader: true);

        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.Description = "Full";
        fixture.TOC.CurrentSetTOC.BlockSize = BlockSize;
        using (var agent = fixture.CreateBackupAgent())
        {
            agent.Navigator.TOCCapacity = reserve;
            agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: true);
            Assert.True(agent.CanResumeToNextVolume, "the volume must have filled up");

            fixture.TOC.RemoveLastEmptySet();
            Assert.True(agent.BackupTOC(), agent.LastErrorMessage);
            Assert.InRange(agent.Navigator.TOCFootprint(agent.LastTOCCopyBytes), 1, reserve);
        }

#if TEST_HOOKS
        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC()) { SimulateTOCFailureMask = 1 };
        Assert.True(reader.RestoreTOC(), $"copy 2 must be on tape: {reader.LastErrorMessage}");
        Assert.True(reader.TOC.Count >= 1);
#endif
    }

    #endregion

    #region *** Single TOC copy on a full volume ***

    /// <summary>
    /// A small TOC reserve on purpose (<see cref="MultiVolumeVirtualTapeFixture.SingleCopyTOCCapacity"/>): the full volume 1
    ///  keeps one TOC copy (TOC-mark layout) or loses copy 2's closing filemark (the others). Either way copy 2 is not
    ///  written in full — reported as <see cref="TapeTocAnomalyKind.CopyWriteFailed"/> with end-of-media, on every layout.
    ///  The tape shows each shape; the TOC still loads, and the whole series restores.
    /// </summary>
    /// <remarks>
    /// Only volume 1 fills up: the fixture inserts every further volume with room for a production TOC reserve.
    /// </remarks>
    [Theory]
    [InlineData(DriveProfile.Setmarks)]
    [InlineData(DriveProfile.SeqFilemarks)]
    [InlineData(DriveProfile.FilemarksOnly)]
    public void SingleCopyReserve_FullVolume_TocStillLoads_SeriesRestores(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("single", count: 16, minSize: 16 * 1024, maxSize: 32 * 1024);
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, headerMode: VolumeHeaderMode.All)
        {
            TOCCapacityOverride = MultiVolumeVirtualTapeFixture.SingleCopyTOCCapacity,
        };
        var notify = new TestNotifiable();
        var stats = fixture.BackupFiles(tree.Files, "Single copy", notifiable: notify);
        Assert.True(fixture.TotalVolumes >= 2, "the series must span");

        // Reported, on every layout: copy 2 of volume 1 is incomplete, for lack of room
        var anomaly = Assert.Single(notify.TocAnomalies,
            i => i.Anomaly is { Kind: TapeTocAnomalyKind.CopyWriteFailed, Volume: 1 }).Anomaly;
        Assert.Equal((byte)2, anomaly.CopyIndex);
        Assert.Equal((uint)WIN32_ERROR.ERROR_END_OF_MEDIA, anomaly.Diagnosis.ErrorCode);
        Assert.True(stats.TocStats.CopyWriteFailures >= 1);

        // The shape of volume 1's TOC area — per layout
        fixture.SwapToVolume(1);
        var media = fixture.Backend.ContentMedia!;
        var layout = media.Layout;
        string shape = media.DescribeLayout();
        if (profile == DriveProfile.SeqFilemarks)
            // [gap][FM][FM][FM][toc1][FM] — copy 2 did not fit at all
            Assert.True(layout[^1].IsMark && !layout[^2].IsMark && layout[^3].IsMark && layout[^4].IsMark, shape);
        else
            // [toc1][FM][toc2] — copy 2 complete, its closing filemark missing
            Assert.True(!layout[^1].IsMark && layout[^2].IsMark && !layout[^3].IsMark, shape);

        // The TOC of the full volume still loads — from copy 1, or (open tail) by identification
        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Volume);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_SingleCopy_{Guid.NewGuid():N}");
        try
        {
            fixture.SwapToVolume(fixture.TotalVolumes);
            fixture.LoadTOC();
            fixture.TOC.MakeLastSetCurrent();
            Assert.Equal(16, fixture.RestoreAllFilesFromCurrentSet(restoreDir).FilesSucceeded);
        }
        finally
        {
            try { Directory.Delete(restoreDir, recursive: true); } catch { /* best effort */ }
        }
    }

    #endregion

}
