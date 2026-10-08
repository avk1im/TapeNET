using TapeLibNET.Format;
using TapeLibNET.Toc;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Legacy;


/// <summary>
/// Phase 7 (Design-Format-v2 §6, §11.5): a GENUINE legacy cartridge — the Phase 0 images, with 12-byte file headers and
///  a legacy TOC — taking its first write from this build. The upgrade must be additive: the legacy set keeps restoring
///  exactly as before, the new set is 2.1, and the TOC on tape becomes 2.1.
/// </summary>
/// <remarks>
/// Restores compare byte for byte with the files the image was written from (<see cref="LegacyVirtualImages.ContentOf"/>)
///  — stronger than the Validate the design asked for, and possible because the source is deterministic.
/// </remarks>
public class LegacyTapeUpgradeTests
{
    #region *** Helpers ***

    private static string NewDir(string tag) => Path.Combine(Path.GetTempPath(), $"TapeNET_{tag}_{Guid.NewGuid():N}");

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>Restores set <paramref name="setIndex"/> into a fresh folder; returns the callbacks.</summary>
    private static (TestNotifiable Notify, string Dir) Restore(VirtualTapeFixture fx, int setIndex)
    {
        string dir = NewDir("LegUp");
        var notify = new TestNotifiable();
        using var agent = fx.CreateRestoreAgent(dir);
        fx.TOC.CurrentSetIndex = setIndex;
        Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify),
            $"restore of set {setIndex} failed: {fx.Drive.LastError}");
        return (notify, dir);
    }

    /// <summary>The first <paramref name="count"/> bytes of the content partition, straight from the virtual medium.</summary>
    private static byte[] ContentPrefix(VirtualTapeFixture fx, int count)
    {
        var snap = fx.Backend.CaptureMemorySnapshot();
        Assert.NotNull(snap);
        return snap!.ContentData[..Math.Min(count, snap.ContentData.Length)];
    }

    #endregion

    #region *** Format_LegacyTape_AppendUpgradesToc ***

    /// <summary>
    /// Appending to a legacy tape: the TOC becomes 2.1 on its first write, the legacy set restores unchanged through its
    ///  12-byte headers, and the new set restores through its 2.1 headers.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyVirtualImages.Profiles), MemberType = typeof(LegacyVirtualImages))]
    public void LegacyTape_AppendSet_UpgradesToc_AndBothSetsRestore(DriveProfile profile)
    {
        using var tree = new TempFileTree(seed: 77);
        tree.AddFiles("appended", count: 4, minSize: 512, maxSize: 8 * 1024);

        using var fx = LegacyVirtualImages.Load(profile);
        fx.BackupFiles(tree.Files, description: "Appended in 2.1");

        fx.LoadTOC();                                                     // what is on tape now
        Assert.False(fx.TOC.LoadedFromLegacy, "the TOC on tape must be 2.1 after the first write");
        Assert.Equal(2, fx.TOC.Count);

        var (oldNotify, oldDir) = Restore(fx, 1);
        try
        {
            oldNotify.AssertAllSucceeded(LegacyVirtualImages.FileNames.Length);
            LegacyVirtualImages.AssertRestored(oldDir);
        }
        finally { TryDelete(oldDir); }

        var (newNotify, newDir) = Restore(fx, 2);
        try
        {
            newNotify.AssertAllSucceeded(tree.Files.Count);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                Path.Combine(newDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath)));
        }
        finally { TryDelete(newDir); }
    }

    /// <summary>
    /// After the upgrade the two sets keep their own data formats: the legacy set stays <see cref="TapeDataFormat.Legacy"/>
    ///  with no identity, the new one is <see cref="TapeDataFormat.V2"/> with a <c>SetId</c>. The 2.1 TOC carries both.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyVirtualImages.Profiles), MemberType = typeof(LegacyVirtualImages))]
    public void LegacyTape_AppendSet_SetsKeepTheirOwnDataFormat(DriveProfile profile)
    {
        using var tree = new TempFileTree(seed: 78);
        tree.AddFiles("fmt", count: 2, minSize: 512, maxSize: 2 * 1024);

        using var fx = LegacyVirtualImages.Load(profile);
        fx.BackupFiles(tree.Files, description: "New");
        fx.LoadTOC();

        TapeSetTOC legacy = fx.TOC[1], fresh = fx.TOC[2];
        Assert.Equal(TapeDataFormat.Legacy, legacy.DataFormat);
        Assert.Equal(Guid.Empty, legacy.SetId);
        Assert.Equal(LegacyVirtualImages.SetDescription, legacy.Description);

        Assert.Equal(TapeDataFormat.V2, fresh.DataFormat);
        Assert.NotEqual(Guid.Empty, fresh.SetId);
        Assert.All(fresh, f => Assert.True(f.FileId > 0 && f.FileId < fresh.NextFileId));
    }

    /// <summary>
    /// A legacy TOC without a MediaId gets one minted on the upgrade — the series identity every later volume and every
    ///  2.1 header carries. Stays stable over a second write.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyVirtualImages.Profiles), MemberType = typeof(LegacyVirtualImages))]
    public void LegacyTape_AppendSet_MediaIdStable(DriveProfile profile)
    {
        using var tree = new TempFileTree(seed: 79);
        tree.AddFiles("id", count: 1, minSize: 512, maxSize: 1024);
        using var more = new TempFileTree(seed: 80);
        more.AddFiles("id2", count: 1, minSize: 512, maxSize: 1024);

        using var fx = LegacyVirtualImages.Load(profile);
        Guid before = fx.TOC.MediaId;

        fx.BackupFiles(tree.Files, description: "First 2.1 write");
        fx.LoadTOC();
        Guid minted = fx.TOC.MediaId;
        Assert.NotEqual(Guid.Empty, minted);
        if (before != Guid.Empty)
            Assert.Equal(before, minted);                                // an existing id is kept, never replaced

        fx.BackupFiles(more.Files, description: "Second 2.1 write");
        fx.LoadTOC();
        Assert.Equal(minted, fx.TOC.MediaId);
    }

    #endregion

    #region *** Format_LegacyTape_MediaHeaderUntouched ***

    /// <summary>
    /// The upgrade rewrites only what follows the legacy data: the BOM block — a legacy media header, or the first
    ///  legacy file record on a header-less tape — is byte-identical before and after (§3.3: a legacy header stays legacy).
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyVirtualImages.Profiles), MemberType = typeof(LegacyVirtualImages))]
    public void LegacyTape_AppendSet_BomBlockUntouched(DriveProfile profile)
    {
        using var tree = new TempFileTree(seed: 81);
        tree.AddFiles("bom", count: 3, minSize: 512, maxSize: 4 * 1024);

        using var fx = LegacyVirtualImages.Load(profile);
        int bom = (int)fx.Drive.BlockSize;
        byte[] before = ContentPrefix(fx, bom);
        Assert.False(TapeFormat.IsV2(before), "a legacy image must not start with a 2.1 record");

        fx.BackupFiles(tree.Files, description: "After");

        Assert.Equal(before, ContentPrefix(fx, bom));
    }

    #endregion

    #region *** Format_LegacyVolume_SetHeaders ***

    /// <summary>
    /// A volume is headed-with-sets, headed-without-sets, or legacy — never mixed within itself (SH-1). On a legacy
    ///  volume the new set therefore gets no set header, and restoring it raises no set-header anomaly.
    /// </summary>
    /// <remarks>
    /// Proven through the consequence that matters: a set header written where the volume declares none would be read
    ///  as content by the next restore, and a missing one where it declares some would raise an anomaly. Neither may
    ///  happen. The Phase 0 images carry no media header; an image WITH a legacy media header (both flag values) would
    ///  cover the other two branches — see the Phase 7 guide.
    /// </remarks>
    [Theory]
    [MemberData(nameof(LegacyVirtualImages.Profiles), MemberType = typeof(LegacyVirtualImages))]
    public void LegacyVolume_NewSet_FollowsTheVolumesHeaderDeclaration(DriveProfile profile)
    {
        using var tree = new TempFileTree(seed: 82);
        tree.AddFiles("sh", count: 3, minSize: 512, maxSize: 4 * 1024);

        using var fx = LegacyVirtualImages.Load(profile);
        fx.BackupFiles(tree.Files, description: "Set on a legacy volume");
        fx.LoadTOC();

        var (notify, dir) = Restore(fx, 2);
        try
        {
            notify.AssertAllSucceeded(tree.Files.Count);
            notify.AssertNoSetAnomalies(expectedSets: 1);
        }
        finally { TryDelete(dir); }
    }

    #endregion
}
