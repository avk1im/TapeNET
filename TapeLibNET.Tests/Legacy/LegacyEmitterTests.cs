using TapeLibNET.Compression;
using TapeLibNET.Agents;
using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Legacy;


/// <summary>
/// Legacy tapes made on the fly by the real agents with <see cref="LegacyRecordEmitter"/>: first proven faithful against
///  the Phase 0 images the old build wrote, then used for the two §11.5 rows no checked-in image covers —
///  <c>Format_LegacyVolume_SetHeaders</c> (headed legacy volumes) and <c>Format_MixedSeries_Restore</c>.
/// </summary>
public class LegacyEmitterTests
{
    #region *** Test data and helpers ***

    public static TheoryData<DriveProfile> AllProfiles =>
        [DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly];

    /// <summary>Two image profiles × the legacy media header's set-header flag.</summary>
    public static TheoryData<DriveProfile, bool> HeadedVolumes => new()
    {
        { DriveProfile.Setmarks, false }, { DriveProfile.Setmarks, true },
        { DriveProfile.Partitions, false }, { DriveProfile.Partitions, true },
    };

    private static string NewDir(string tag) => Path.Combine(Path.GetTempPath(), $"TapeNET_{tag}_{Guid.NewGuid():N}");

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private static TestNotifiable RestoreInto(VirtualTapeFixture fx, int setIndex, string dir)
    {
        var notify = new TestNotifiable();
        using var agent = fx.CreateRestoreAgent(dir);
        fx.TOC.CurrentSetIndex = setIndex;
        Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify),
            $"restore of set {setIndex} failed: {fx.Drive.LastError}");
        return notify;
    }

    private static string RestoredRoot(string restoreDir, string originalRoot)
        => Path.Combine(restoreDir, Path.GetRelativePath(Path.GetPathRoot(originalRoot)!, originalRoot));

    private static byte[] BomBlock(VirtualTapeFixture fx)
    {
        var snap = fx.Backend.CaptureMemorySnapshot();
        Assert.NotNull(snap);
        return snap!.ContentData[..TapeHeaderBlock.Size];
    }

    #endregion

    #region *** The emitter is faithful ***

    /// <summary>
    /// The same three files, the same profile: an emitter tape and the Phase 0 image read the same way — legacy TOC,
    ///  legacy set, the first file at the same address with the same 12-byte legacy header in front of it — and restore
    ///  the same bytes.
    /// </summary>
    /// <remarks>
    /// Later file addresses are not compared: a body is the <c>BackupRead</c> blob, whose stream headers depend on the
    ///  machine's security descriptors, so its length — and every address after the first — may differ by machine.
    /// </remarks>
    [Theory]
    [MemberData(nameof(LegacyVirtualImages.Profiles), MemberType = typeof(LegacyVirtualImages))]
    public void Emitter_MatchesPhase0Image(DriveProfile profile)
    {
        string src = LegacyVirtualImages.CreateSourceTree();
        string restore = NewDir("EmitR");
        try
        {
            using var made = new VirtualTapeFixture(profile, recordEmitter: LegacyRecordEmitter.Instance);
            made.BackupFiles(LegacyVirtualImages.SourceFiles(src), description: LegacyVirtualImages.SetDescription);
            made.LoadTOC();

            using var image = LegacyVirtualImages.Load(profile);

            Assert.True(made.TOC.LoadedFromLegacy);
            Assert.Equal(image.TOC.Count, made.TOC.Count);
            TapeSetTOC madeSet = made.TOC[1], imageSet = image.TOC[1];
            Assert.Equal(TapeDataFormat.Legacy, madeSet.DataFormat);
            Assert.Equal(Guid.Empty, madeSet.SetId);
            Assert.Equal(imageSet.Count, madeSet.Count);
            Assert.Equal(imageSet[0].Address, madeSet[0].Address);
            Assert.All(madeSet, f => Assert.Equal(TapeFileCodec.Stored, f.Codec));

            Assert.False(TapeFormat.IsV2(BomBlock(made)), "an emitter tape must not start with a 2.1 record");

            RestoreInto(made, 1, restore).AssertAllSucceeded(LegacyVirtualImages.FileNames.Length);
            LegacyVirtualImages.AssertRestored(restore);
        }
        finally
        {
            TryDelete(src);
            TryDelete(restore);
        }
    }

    /// <summary>A legacy body carries no codec prefix, so the emitter cannot write a compressed set.</summary>
    [Fact]
    public void Emitter_RefusesSoftwareCompression()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("zip", count: 2, minSize: 512, maxSize: 2 * 1024);

        using var fx = new VirtualTapeFixture(DriveProfile.Setmarks, recordEmitter: LegacyRecordEmitter.Instance);
        Assert.ThrowsAny<Exception>(() =>
            fx.BackupFiles(tree.Files, description: "Compressed", compression: TapeCompression.Software));
    }

    #endregion

    #region *** Format_LegacyVolume_SetHeaders — headed legacy volumes ***

    /// <summary>
    /// A legacy volume with a legacy media header declaring set headers (or not) takes a 2.1 set: the BOM block stays
    ///  untouched, the new set follows the volume's declaration, and both sets restore without a set-header anomaly —
    ///  the legacy one through its legacy set header, the new one through a 2.1 set header.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeadedVolumes))]
    public void HeadedLegacyVolume_Append21_FollowsTheDeclaration(DriveProfile profile, bool withSetHeaders)
    {
        using var legacyTree = new TempFileTree(seed: 11);
        legacyTree.AddFiles("old", count: 3, minSize: 512, maxSize: 4 * 1024);
        using var newTree = new TempFileTree(seed: 12);
        newTree.AddFiles("new", count: 3, minSize: 512, maxSize: 4 * 1024);
        string oldDir = NewDir("HdOld"), newDir = NewDir("HdNew");
        try
        {
            using var fx = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: withSetHeaders,
                recordEmitter: LegacyRecordEmitter.Instance);
            fx.BackupFiles(legacyTree.Files, description: "Legacy set");

            byte[] bom = BomBlock(fx);
            Assert.False(TapeFormat.IsV2(bom));
            var legacyHeader = Assert.IsType<TapeMediaHeader>(TapeHeaderBlock.Classify(bom, bom.Length));
            Assert.Equal(withSetHeaders, legacyHeader.HasSetHeaders);

            // The new build takes over
            fx.RecordEmitter = TapeRecordEmitter21.Instance;
            fx.LoadTOC();
            Assert.True(fx.TOC.LoadedFromLegacy);
            fx.BackupFiles(newTree.Files, description: "2.1 set");

            Assert.Equal(bom, BomBlock(fx));                          // a legacy header stays legacy (§3.3)

            fx.LoadTOC();
            Assert.False(fx.TOC.LoadedFromLegacy);
            Assert.Equal(TapeDataFormat.Legacy, fx.TOC[1].DataFormat);
            Assert.Equal(TapeDataFormat.V2, fx.TOC[2].DataFormat);

            var oldNotify = RestoreInto(fx, 1, oldDir);
            oldNotify.AssertAllSucceeded(legacyTree.Files.Count);
            oldNotify.AssertNoSetAnomalies(expectedSets: 1);
            FileComparer.AssertFilesMatch(legacyTree.RootPath, legacyTree.Files, RestoredRoot(oldDir, legacyTree.RootPath));

            var newNotify = RestoreInto(fx, 2, newDir);
            newNotify.AssertAllSucceeded(newTree.Files.Count);
            newNotify.AssertNoSetAnomalies(expectedSets: 1);
            FileComparer.AssertFilesMatch(newTree.RootPath, newTree.Files, RestoredRoot(newDir, newTree.RootPath));
        }
        finally
        {
            TryDelete(oldDir);
            TryDelete(newDir);
        }
    }

    #endregion

    #region *** Format_MixedSeries_Restore ***

    /// <summary>
    /// One set spanning a legacy volume 1 (written by the old build, header-less) and a 2.1 volume 2 (headed): the part on
    ///  volume 1 is a legacy set, its continuation on volume 2 a 2.1 set — and the whole set restores across both.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MixedSeries_LegacyVolume1_V21Volume2_RestoresAcrossBoth(DriveProfile profile)
    {
        using var tree = new TempFileTree(seed: 21);
        tree.AddFiles("span", count: 16, minSize: 16 * 1024, maxSize: 32 * 1024);
        string dir = NewDir("Mixed");
        try
        {
            using var fx = new MultiVolumeVirtualTapeFixture(profile, headerMode: VolumeHeaderMode.Mixed)
            {
                EmitterForVolume = v => v == 1 ? LegacyRecordEmitter.Instance : TapeRecordEmitter21.Instance,
            };
            fx.BackupFiles(tree.Files, "Spanning");
            Assert.True(fx.TotalVolumes >= 2, $"the set must span (got {fx.TotalVolumes} volume(s))");

            TapeTOC toc = fx.TOC;
            Assert.Equal(TapeDataFormat.Legacy, toc[1].DataFormat);
            for (int i = 2; i <= toc.Count; i++)
            {
                Assert.True(toc[i].ContinuedFromPrevVolume, $"set {i} must continue set {i - 1}");
                Assert.Equal(TapeDataFormat.V2, toc[i].DataFormat);
                Assert.NotEqual(Guid.Empty, toc[i].SetId);
            }

            for (int i = 1; i <= toc.Count; i++)
            {
                toc.CurrentSetIndex = i;
                fx.RestoreAllFilesFromCurrentSet(dir);
            }
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoredRoot(dir, tree.RootPath));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    #endregion
}
