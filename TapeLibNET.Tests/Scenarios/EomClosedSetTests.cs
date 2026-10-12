using Windows.Win32.Foundation;

using TapeLibNET.Drive;
using TapeLibNET.Media;
using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Toc;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Scenarios;


/// <summary>
/// F32: on TOC-in-partition media the last set of a full volume may end at end-of-media without its closing setmark. The
///  TOC records it, navigation counts one mark fewer, and the scan does not call a full volume a dead backup.
/// </summary>
public class EomClosedSetTests
{
    #region *** TOC — the trait ***

    private static TapeTOC TwoSets()
    {
        var toc = new TapeTOC("EOM");
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(new TapeFileInfo(toc.CurrentSetTOC.GenerateFileId(), new TapeAddress(3, 0),
            new TapeFileDescriptor(@"C:\a.bin") { Length = 10 }));
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(new TapeFileInfo(toc.CurrentSetTOC.GenerateFileId(), new TapeAddress(9, 0),
            new TapeFileDescriptor(@"C:\b.bin") { Length = 20 }));
        return toc;
    }

    private static byte[] Save(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms);
        return ms.ToArray();
    }

    /// <summary>Field 14 is written when set, read back, and elided when false.</summary>
    [Fact]
    public void Toc_ClosedByEom_RoundTripsThroughFormat21()
    {
        var toc = TwoSets();
        byte[] plain = Save(toc);
        toc[2].ClosedByEom = true;
        byte[] flagged = Save(toc);

        Assert.True(flagged.Length > plain.Length, "the field must be written when set");

        TapeTOC back = TapeTOC.LoadFrom(new MemoryStream(flagged));
        Assert.False(back[1].ClosedByEom);
        Assert.True(back[2].ClosedByEom);
        Assert.All(TapeTOC.LoadFrom(new MemoryStream(plain)), s => Assert.False(s.ClosedByEom));
    }

    /// <summary>Flipping the trait is a change to the set: its LastSaveTime is restamped on the next save.</summary>
    [Fact]
    public void Toc_ClosedByEom_IsAPersistedChange()
    {
        var toc = new TapeTOC("EOM");
        toc.AddNewSetTOC();
        Save(toc);
        Assert.False(toc.CurrentSetTOC.IsModified);

        toc.CurrentSetTOC.ClosedByEom = true;
        Assert.True(toc.CurrentSetTOC.IsModified);
    }

    /// <summary>Replacing a set keeps the trait until the replacement is written.</summary>
    [Fact]
    public void ReplaceCurrentSet_KeepsClosedByEom()
    {
        var toc = TwoSets();
        toc.CurrentSetTOC.ClosedByEom = true;
        TapeSetTOC before = toc.CurrentSetTOC;

        toc.ReplaceCurrentSetTOC();

        Assert.NotSame(before, toc.CurrentSetTOC);
        Assert.True(toc.CurrentSetTOC.ClosedByEom);
        Assert.Empty(toc.CurrentSetTOC);
    }

    #endregion

    #region *** Scan judgement and wording ***

    private static readonly TapeMediaLayout s_partitioned = new(
        nameof(TapeNavigatorTOCInPartition), UseSmks: true, TocInPartition: true, HasTocMark: false, MediaLoaded: true);

    private static readonly TapeMediaLayout s_inSet = new(
        nameof(TapeNavigatorTOCInSetWithSmks), UseSmks: true, TocInPartition: false, HasTocMark: false, MediaLoaded: true);

    private static readonly Guid s_mediaId = Guid.NewGuid();

    private static TapeMediaFragment UnclosedSetB() => new()
    {
        Ordinal = 2,
        StartBlock = 8,
        Kind = FragmentKind.SetHeader,
        ClosedBySeparator = false,
        Id = s_mediaId,
        Volume = 1,
        VolumeSetIndex = 1,
        Description = "B",
    };

    /// <summary>Volume 1 with two sets — the second unclosed — and a recovered TOC copy in the initiator partition.</summary>
    private static MediaScanMap FullVolumeMap(TapeMediaLayout layout, bool tocSaysClosedByEom)
    {
        var toc = TwoSets();
        toc.MediaId = s_mediaId;
        toc[2].ClosedByEom = tocSaysClosedByEom;

        TapeMediaFragment[] fragments =
        [
            new() { Ordinal = 0, StartBlock = 0, Kind = FragmentKind.MediaHeader, ClosedBySeparator = true,
                    Id = s_mediaId, Volume = 1, Description = "Full" },
            new() { Ordinal = 1, StartBlock = 2, Kind = FragmentKind.SetHeader, ClosedBySeparator = true,
                    Id = s_mediaId, Volume = 1, VolumeSetIndex = 0, Description = "A" },
            UnclosedSetB(),
            new() { Ordinal = 3, StartBlock = 0, Partition = MediaPartition.Initiator, Kind = FragmentKind.TOC,
                    ClosedBySeparator = true, Id = s_mediaId, HarvestedToc = toc },
        ];

        return new MediaScanMap
        {
            Layout = layout,
            Kind = ScannedMediaKind.Backup,
            Fragments = fragments,
            Partitions =
            [
                new ScannedPartition(MediaPartition.Content, 3, Truncated: false, (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED),
                new ScannedPartition(MediaPartition.Initiator, 1, Truncated: false, (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED),
            ],
            ScannedUtc = DateTime.UtcNow,
            TerminatorWin32 = (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED,
        };
    }

    /// <summary>A full partitioned volume whose TOC records the EOM-closed set is sound — no review advice.</summary>
    [Fact]
    public void Verbalize_UnclosedLastSet_RecordedAsClosedByEom_IsSound()
    {
        MediaScanMap map = FullVolumeMap(s_partitioned, tocSaysClosedByEom: true);
        Assert.True(map.LastSetUnclosed);        // the observation itself is unchanged (SM-3)

        var (level, headline, details) = TapeServiceBase.VerbalizeScan(map);
        Assert.Equal(ServiceReportLevel.Completed, level);
        Assert.Contains("looks sound", headline);
        Assert.Contains(details, d => d.Contains("runs to the end of the medium"));
        Assert.DoesNotContain(details, d => d.Contains("never completed"));
        Assert.DoesNotContain(ScanAdvice.ReviewUnclosedSet, TapeServiceBase.AdviseOnScan(map));
    }

    /// <summary>Without the TOC's word, an unclosed last set is still a backup that died mid-set.</summary>
    [Fact]
    public void Verbalize_UnclosedLastSet_NotRecorded_NeedsAttention()
    {
        MediaScanMap map = FullVolumeMap(s_partitioned, tocSaysClosedByEom: false);

        var (level, _, details) = TapeServiceBase.VerbalizeScan(map);
        Assert.Equal(ServiceReportLevel.Warning, level);
        Assert.Contains(details, d => d.Contains("never completed"));
        Assert.Contains(ScanAdvice.ReviewUnclosedSet, TapeServiceBase.AdviseOnScan(map));
    }

    /// <summary>The trait is never trusted on a TOC-in-set layout.</summary>
    [Fact]
    public void Verbalize_ClosedByEom_IgnoredOnTocInSetLayout()
    {
        var (level, _, details) = TapeServiceBase.VerbalizeScan(FullVolumeMap(s_inSet, tocSaysClosedByEom: true));
        Assert.Equal(ServiceReportLevel.Warning, level);
        Assert.Contains(details, d => d.Contains("never completed"));
    }

    /// <summary>The live line is neutral for an unclosed set on partitioned media only; elsewhere it still warns.</summary>
    [Fact]
    public void DescribeFragment_UnclosedSet_NeutralOnlyOnPartitionedMedia()
    {
        var (levelInPart, textInPart) = ServiceScanProgressHandler.DescribeFragment(UnclosedSetB(), tocInPartition: true)!.Value;
        Assert.Equal(ServiceReportLevel.Info, levelInPart);
        Assert.Contains("end of the medium", textInPart);

        var (levelInSet, textInSet) = ServiceScanProgressHandler.DescribeFragment(UnclosedSetB(), tocInPartition: false)!.Value;
        Assert.Equal(ServiceReportLevel.Warning, levelInSet);
        Assert.Contains("never completed", textInSet);
    }

    #endregion

    #region *** End to end — partitioned multi-volume ***

    /// <summary>
    /// The originating failure: a set spanning a full partitioned volume completes; the volume's last set is recorded as
    ///  closed by end-of-media; and every set — including the one before it, reached by counting back from the end —
    ///  restores byte for byte with no set anomaly (the −1 shift addressed it correctly).
    /// </summary>
    [Fact]
    public void MultiVolume_Partitions_FullVolume_NeedsNoSetmark_AndEverySetRestores()
    {
        using var setA = new TempFileTree(seed: 31);
        setA.AddFiles("a", count: 2, minSize: 2 * 1024, maxSize: 4 * 1024);
        using var setB = new TempFileTree(seed: 32);
        setB.AddFiles("b", count: 16, minSize: 16 * 1024, maxSize: 32 * 1024);
        string dirA = NewDir("EomA"), dirB = NewDir("EomB");
        try
        {
            using var fx = new MultiVolumeVirtualTapeFixture(DriveProfile.Partitions, 256L * 1024);
            fx.BackupFiles(setA.Files, "A");
            fx.BackupFiles(setB.Files, "B");
            Assert.True(fx.TotalVolumes >= 2, $"set B must span (got {fx.TotalVolumes} volume(s))");

            TapeTOC toc = fx.TOC;
            var volume1 = toc.Where(s => s.Volume == 1).ToList();
            Assert.True(volume1.Count >= 2, "volume 1 must hold set A and the first part of set B");
            Assert.True(volume1[^1].ClosedByEom,
                "precondition: the closing setmark of volume 1's last set did not fit — if this fails, the virtual " +
                "backend left room for it and the scenario is not reproduced");
            Assert.All(volume1.Take(volume1.Count - 1), s => Assert.False(s.ClosedByEom));

            toc.CurrentSetIndex = 1;
            fx.RestoreAllFilesFromCurrentSet(dirA);
            FileComparer.AssertFilesMatch(setA.RootPath, setA.Files, RestoredRoot(dirA, setA.RootPath));

            toc.CurrentSetIndex = 2;
            var stats = fx.RestoreAllFilesFromCurrentSet(dirB);
            FileComparer.AssertFilesMatch(setB.RootPath, setB.Files, RestoredRoot(dirB, setB.RootPath));

            Assert.False(stats.Sets.HasAnomalies);
        }
        finally
        {
            TryDelete(dirA);
            TryDelete(dirB);
        }
    }

    private static string NewDir(string tag) => Path.Combine(Path.GetTempPath(), $"TapeNET_{tag}_{Guid.NewGuid():N}");

    private static string RestoredRoot(string restoreDir, string originalRoot)
        => Path.Combine(restoreDir, Path.GetRelativePath(Path.GetPathRoot(originalRoot)!, originalRoot));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    #endregion
}