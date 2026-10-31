using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Media;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocReserve §9: the TOC partition budget. The drive learns the partition's capacity where it sees it; a volume
///  ends when the next files' TOC entries would not fit the partition twice — both copies always stand, and the end says
///  why; a series whose TOC fills the partition stops instead of asking for volumes forever.
/// </summary>
public class TocBudgetTests
{
    private const long KiB = 1024;

    #region *** Helpers ***

    /// <summary>Tiny files with long, unshared names: the TOC fills before the content does.</summary>
    private static TempFileTree LongNamedFiles(int count)
    {
        var tree = new TempFileTree();
        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[45];
            new Random(i).NextBytes(bytes);
            tree.AddFile($"{i:D4}_{Convert.ToHexString(bytes)}.bin", 1 + i % 64);
        }
        return tree;
    }

    private static void AssertBothTocCopiesInPartition(MultiVolumeVirtualTapeFixture fixture)
    {
        var media = fixture.Backend.InitiatorMedia!;
        var layout = media.Layout;
        Assert.True(layout.Count >= 4 && !layout[^4].IsMark && layout[^3].IsMark && !layout[^2].IsMark && layout[^1].IsMark,
            $"both TOC copies, each closed by its filemark\n{media.DescribeLayout()}");
    }

    private static MultiVolumeVirtualTapeFixture SmallTocPartition(long initiator)
        => new(DriveProfile.Partitions, contentCapacity: 4 * KiB * KiB, initiatorCapacity: initiator,
            headerMode: VolumeHeaderMode.All);

    #endregion

    #region *** The drive's cache (§9.2.1) ***

    [Fact]
    public void InitiatorCapacity_FilledWhereSeen_ClearedOnReload()
    {
        using var fixture = SmallTocPartition(96 * KiB);
        TapeDrive drive = fixture.Drive;
        Assert.Equal(-1L, drive.InitiatorCapacity);                       // loaded, the partition not seen yet

        Assert.True(drive.MoveToPartition(MediaPartition.Initiator));
        Assert.Equal(96 * KiB, drive.InitiatorCapacity);
        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.Equal(96 * KiB, drive.InitiatorCapacity);                  // kept while on the content partition
        Assert.Equal(4 * KiB * KiB, drive.ContentCapacity);

        Assert.True(drive.ReloadMedia());
        Assert.Equal(-1L, drive.InitiatorCapacity);                       // another cartridge may be in the drive

        Assert.True(drive.FastforwardToEnd(MediaPartition.Initiator));     // a partition switch by another verb
        Assert.Equal(96 * KiB, drive.InitiatorCapacity);
    }

    #endregion

    #region *** The budget ends a volume (§9.2, §9.6) ***

    /// <summary>
    /// A 96 KiB TOC partition (80 KiB budget: two copies of up to 2 blocks each): the volume ends by the budget, far
    ///  before its content does. Both copies stand, the end says why, and the files on it restore.
    /// </summary>
    [Fact]
    public void BudgetReached_VolumeEnds_BothCopiesStand_ReasonGiven()
    {
        using var tree = LongNamedFiles(600);
        using var fixture = SmallTocPartition(96 * KiB);
        fixture.TOC.AddNewSetTOC();
        fixture.TOC.CurrentSetTOC.Description = "Budget";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using (var agent = fixture.CreateBackupAgent())
        {
            TapeResult result = agent.BackupFileListToCurrentSet(newSet: true, tree.Files);
            Assert.False(result.Success);
            Assert.True(agent.CanResumeToNextVolume, "the volume must end by the budget");
            Assert.Equal(ContentStopReason.TocPartitionFull, agent.VolumeEndReason);
            Assert.True(agent.BackupTOC().Success);
            Assert.DoesNotContain(agent.TocAnomalies, a => a.Kind
                is TapeTocAnomalyKind.CopyWriteFailed or TapeTocAnomalyKind.NoCopyWritten);
        }
        AssertBothTocCopiesInPartition(fixture);

        fixture.LoadTOC();
        int onVolume = fixture.TOC.CurrentSetTOC.Count;
        Assert.InRange(onVolume, 1, tree.Files.Count - 1);
        long copy = new TapeTOC(fixture.TOC).EstimateSerializedSize();
        Assert.True(TapeNavigator.CopiesFootprint(copy) <= 80 * KiB, $"TOC of ~{copy} B exceeds the budget");

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_TocBudget_{Guid.NewGuid():N}");
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            Assert.Equal(onVolume, fixture.RestoreAllFilesFromCurrentSet(restoreDir).FilesSucceeded);
        }
        finally
        {
            try { Directory.Delete(restoreDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Every volume repeats the series TOC: once the partition is full, an equal next volume cannot take a single file.
    ///  The backup ends with the TOC-partition wording of the no-loop error (TR-7) — never a request for a third volume.
    /// </summary>
    [Fact]
    public void BudgetReached_EqualNextVolume_EndsWithTheTocPartitionError()
    {
        using var tree = LongNamedFiles(600);
        using var fixture = SmallTocPartition(96 * KiB);

        var ex = Assert.ThrowsAny<Exception>(() => fixture.BackupFiles(tree.Files, "Budget series"));
        Assert.Contains("TOC partition", ex.Message);
        Assert.True(fixture.TotalVolumes <= 2, $"{fixture.TotalVolumes} volumes requested");

        fixture.SwapToVolume(1);
        AssertBothTocCopiesInPartition(fixture);
    }

    /// <summary>A TOC well within the partition: no budget stop, the volume ends with the files.</summary>
    [Fact]
    public void WithinBudget_NoStop()
    {
        using var tree = LongNamedFiles(100);
        using var fixture = SmallTocPartition(256 * KiB);
        fixture.BackupFiles(tree.Files, "Fits");
        Assert.Equal(1, fixture.TotalVolumes);
        Assert.Equal(100, fixture.TOC.CurrentSetTOC.Count);
    }

    /// <summary>TOC in set: no budget — the reserve governs, and the limits say so.</summary>
    [Theory]
    [InlineData(DriveProfile.Setmarks)]
    [InlineData(DriveProfile.FilemarksOnly)]
    public void TocInSet_HasNoBudget(DriveProfile profile)
    {
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);
        using var probe = new TapeAgentBase(fixture.Drive, fixture.TOC);
        Assert.False(probe.Navigator.CaptureTocLimits().HasBudget);
    }

    #endregion
}
