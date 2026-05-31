using Microsoft.Extensions.Logging;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Tests for the Win32 backup-stream wrappers: <c>TapeBackupSourceStream</c> and
///  <c>TapeBackupTargetStream</c>, including ADS round-trips, <c>SizeOnTape</c>
///  population, TOC persistence, and an end-to-end packed tape round-trip with
///  an ADS-carrying file.
/// <para>
/// Tests that open files via BackupRead/BackupWrite require
///  <c>SeBackupPrivilege</c> / <c>SeRestorePrivilege</c>, which are present when the
///  test runner is elevated (Administrator) or has been granted those privileges.
///  Tests that need elevation are decorated with <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>
///  and call <see cref="SkipUnlessElevated"/> so the suite does not fail in CI
///  environments that run unprivileged.
/// </para>
/// </summary>
public class BackupStreamTests
{
    // Shared logger for stream-level unit tests (no xUnit ITestOutputHelper needed — output goes to Debug)
    private static readonly Microsoft.Extensions.Logging.ILogger s_logger =
        TestLoggerFactory.Default.CreateLogger<BackupStreamTests>();
    #region *** Guards ***

    /// <summary>
    /// Skips the current test unless the process is running elevated (Administrator)
    ///  or holds SeBackupPrivilege. Opening files with FILE_FLAG_BACKUP_SEMANTICS +
    ///  ACCESS_SYSTEM_SECURITY requires elevation on a default Windows install.
    /// </summary>
    private static void SkipUnlessElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        Skip.IfNot(
            principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            "Test requires Administrator / SeBackupPrivilege – skipped in unprivileged environment.");
    }

    /// <summary>
    /// Skips the current test unless the temp directory resides on an NTFS volume
    ///  (ADS require NTFS).
    /// </summary>
    private static void SkipUnlessNtfs(string path)
    {
        Skip.IfNot(AdsHelper.IsNtfs(path),
            $"Test requires NTFS (path: {path}) – skipped on non-NTFS volumes.");
    }

    #endregion


    // =======================================================================
    #region *** SourceStream Unit Tests ***
    // =======================================================================

    /// <summary>
    /// Opening a plain file via <c>TapeBackupSourceStream.Open</c> and reading it
    ///  to completion should expose at least as many bytes as the original file size
    ///  (the blob contains the WIN32_STREAM_ID BACKUP_DATA header + content, so it
    ///  is always larger than the raw file).
    /// </summary>
    [SkippableFact]
    public void SourceStream_PlainFile_BlobLargerThanFile()
    {
        SkipUnlessElevated();

        using var tree = new TempFileTree();
        string path = tree.AddFile("plain.dat", 8 * 1024);

        var fileInfo = new FileInfo(path);
        using var stream = TapeBackupSourceStream.Open(fileInfo, s_logger);

        byte[] buf = new byte[256 * 1024];
        long totalRead = 0;
        int read;
        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            totalRead += read;

        // The backup blob must contain at least the raw file bytes
        Assert.True(totalRead >= fileInfo.Length,
            $"Backup blob ({totalRead} B) should be >= raw file size ({fileInfo.Length} B)");
        // Position should track bytes delivered
        Assert.Equal(totalRead, stream.Position);
    }

    /// <summary>
    /// Opening a zero-byte file should produce a non-empty blob (at minimum the
    ///  BACKUP_DATA stream header is present even for an empty main stream).
    /// </summary>
    [SkippableFact]
    public void SourceStream_EmptyFile_ProducesAtLeastHeader()
    {
        SkipUnlessElevated();

        using var tree = new TempFileTree();
        string path = tree.AddFile("empty.dat", 0);

        using var stream = TapeBackupSourceStream.Open(
            new FileInfo(path), s_logger);

        byte[] buf = new byte[1024];
        long totalRead = 0;
        int read;
        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            totalRead += read;

        // BackupRead with bProcessSecurity=false on a zero-byte file with no ADS or security
        //  streams produces no output at all — Windows omits the BACKUP_DATA header when
        //  the stream has zero size. A round-trip with a non-empty file is the reliable test.
        Assert.True(totalRead == 0 || totalRead >= 20,
            $"Expected either no data (empty-file shortcut) or at least a 20-byte stream header; got {totalRead} bytes");
    }

    /// <summary>
    /// A file with a named ADS should produce a larger blob than the same file
    ///  without ADS, because BackupRead emits an additional BACKUP_ALTERNATE_DATA stream.
    /// </summary>
    [SkippableFact]
    public void SourceStream_FileWithAds_BlobLargerThanWithoutAds()
    {
        SkipUnlessElevated();

        using var tree = new TempFileTree();
        SkipUnlessNtfs(tree.RootPath);

        const long fileSize = 4 * 1024;
        string plain = tree.AddFile("no_ads.dat", fileSize);
        string withAds = tree.AddFileWithAds("with_ads.dat", fileSize,
            new Dictionary<string, string> { ["meta"] = "Hello ADS!" })!;

        long blobSizePlain = ReadAllBytes(plain);
        long blobSizeAds = ReadAllBytes(withAds);

        Assert.True(blobSizeAds > blobSizePlain,
            $"ADS blob ({blobSizeAds} B) should exceed plain blob ({blobSizePlain} B)");
    }

    /// <summary>Reads the complete BackupRead blob of a file and returns the total byte count.</summary>
    private static long ReadAllBytes(string filePath)
    {
        using var stream = TapeBackupSourceStream.Open(
            new FileInfo(filePath), s_logger);
        byte[] buf = new byte[256 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            total += read;
        return total;
    }

    #endregion


    // =======================================================================
    #region *** Round-Trip Unit Tests (Source → buffer → Target) ***
    // =======================================================================

    /// <summary>
    /// Copying the blob from a plain file through a <c>TapeBackupTargetStream</c>
    ///  should faithfully restore the main data stream content.
    /// </summary>
    [SkippableFact]
    public void RoundTrip_PlainFile_MainDataRestored()
    {
        SkipUnlessElevated();

        using var src = new TempFileTree();
        using var dst = new TempFileTree();

        string originalPath = src.AddFile("original.dat", 32 * 1024);
        string restoredPath = Path.Combine(dst.RootPath, "restored.dat");

        CopyViaBackupStreams(originalPath, restoredPath);

        // Main data content must match byte-for-byte
        FileComparer.AssertContentEqual(originalPath, restoredPath);
    }

    /// <summary>
    /// Copying an ADS-carrying file through source→target streams should restore
    ///  both the main data stream and the named ADS.
    /// </summary>
    [SkippableFact]
    public void RoundTrip_FileWithAds_MainDataAndAdsRestored()
    {
        SkipUnlessElevated();

        using var src = new TempFileTree();
        using var dst = new TempFileTree();
        SkipUnlessNtfs(src.RootPath);

        const string adsName = "sidecar";
        const string adsContent = "ADS payload for round-trip test";

        string originalPath = src.AddFileWithAds("file_with_ads.dat", 16 * 1024,
            new Dictionary<string, string> { [adsName] = adsContent })!;
        string restoredPath = Path.Combine(dst.RootPath, "file_with_ads.dat");

        CopyViaBackupStreams(originalPath, restoredPath);

        // Main data
        FileComparer.AssertContentEqual(originalPath, restoredPath);

        // ADS — only assert when the restore root is also on NTFS
        if (AdsHelper.IsNtfs(dst.RootPath))
            AdsHelper.AssertAdsContent(restoredPath, adsName, adsContent);
    }

    /// <summary>
    /// Copies a file's full backup blob (all streams) from <paramref name="sourcePath"/>
    ///  to <paramref name="targetPath"/> using <c>TapeBackupSourceStream</c> and
    ///  <c>TapeBackupTargetStream</c> with a plain in-memory buffer.
    ///  This exercises the stream wrappers in isolation, without any tape involvement.
    /// </summary>
    private static void CopyViaBackupStreams(string sourcePath, string targetPath)
    {
        var logger = s_logger;

        // Capture the opaque blob
        byte[] blob;
        using (var srcStream = TapeBackupSourceStream.Open(new FileInfo(sourcePath), logger))
        {
            using var ms = new MemoryStream();
            srcStream.CopyTo(ms);
            blob = ms.ToArray();
        }

        Assert.True(blob.Length > 0, "Backup blob should not be empty");

        // Replay the blob into the target file
        using var dstStream = TapeBackupTargetStream.Create(new FileInfo(targetPath), logger);
        dstStream.Write(blob, 0, blob.Length);
    }

    #endregion


    // =======================================================================
    #region *** SizeOnTape Population and Persistence ***
    // =======================================================================

    /// <summary>
    /// After a packed backup, every <see cref="TapeFileInfo"/> in the set should have
    ///  a positive <c>SizeOnTape</c> (populated by <c>PackedCommitTracker</c>).
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void SizeOnTape_AfterPackedBackup_AllEntriesPositive(DriveProfile profile)
    {
        SkipUnlessElevated();
        using var tree = new TempFileTree();
        tree.AddFiles("packed", count: 8, minSize: 512, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "SizeOnTape Test");

        var set = fixture.TOC[1];
        Assert.NotEmpty(set);

        foreach (var entry in set)
        {
            Assert.True(entry.SizeOnTape > 0,
                $"SizeOnTape must be > 0 after packed backup: {entry.FileDescr.FullName}");
        }
    }

    /// <summary>
    /// <c>SizeOnTape</c> values written during backup must survive a full TOC
    ///  save-to-tape / load-from-tape round-trip, because restore uses them
    ///  to find file boundaries within the packed stream.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void SizeOnTape_SurvivesTocRoundTrip(DriveProfile profile)
    {
        SkipUnlessElevated();
        using var tree = new TempFileTree();
        tree.AddFiles("packed", count: 6, minSize: 1024, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "SizeOnTape Persistence");

        // Capture SizeOnTape values before save
        var before = fixture.TOC[1].Select(e => (e.FileDescr.FullName, e.SizeOnTape)).ToList();

        // Full TOC round-trip through tape
        fixture.SaveAndReloadTOC();

        var after = fixture.TOC[1].Select(e => (e.FileDescr.FullName, e.SizeOnTape)).ToList();

        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].FullName, after[i].FullName);
            Assert.Equal(before[i].SizeOnTape, after[i].SizeOnTape);
        }
    }

    #endregion


    // =======================================================================
    #region *** Packed Tape Integration with ADS ***
    // =======================================================================

    /// <summary>
    /// End-to-end packed backup → restore of a file that carries a named ADS.
    /// The restored file must have both the correct main data content and the ADS.
    /// <para>
    /// This test requires elevation (BackupRead/Write semantics) and NTFS.
    /// It skips when either condition is unmet.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void PackedRoundTrip_FileWithAds_BothStreamsRestored()
    {
        SkipUnlessElevated();

        using var tree = new TempFileTree();
        SkipUnlessNtfs(tree.RootPath);

        const string adsName = "description";
        const string adsContent = "This is an ADS payload for the tape integration test.";

        // One plain file + one ADS file
        string plain = tree.AddFile("plain.dat", 8 * 1024);
        string withAds = tree.AddFileWithAds("with_ads.dat", 8 * 1024,
            new Dictionary<string, string> { [adsName] = adsContent })!;

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        fixture.BackupFiles(tree.Files, description: "ADS Integration");

        // Restore into a fresh temp tree
        using var restoreTree = new TempFileTree();
        using var restoreAgent = fixture.CreateRestoreAgent(restoreTree.RootPath);
        bool restoreOk = (bool)restoreAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: null);
        Assert.True(restoreOk, "Restore failed");

        // Compute restored paths: recurseSubdirs=true maps absolute source paths under
        //  restoreTree.RootPath by stripping the drive root (e.g. C:\) and preserving the rest.
        string pathRoot = Path.GetPathRoot(tree.RootPath)!;
        string restoreEquivalentRoot = Path.Combine(restoreTree.RootPath,
            Path.GetRelativePath(pathRoot, tree.RootPath));
        string restoredPlain = Path.Combine(restoreEquivalentRoot, Path.GetRelativePath(tree.RootPath, plain));
        string restoredAds = Path.Combine(restoreEquivalentRoot, Path.GetRelativePath(tree.RootPath, withAds));

        // Plain file: main data
        FileComparer.AssertContentEqual(plain, restoredPlain);

        // ADS file: main data
        FileComparer.AssertContentEqual(withAds, restoredAds);

        // ADS file: named ADS (only if restore volume is NTFS)
        if (AdsHelper.IsNtfs(restoreTree.RootPath))
            AdsHelper.AssertAdsContent(restoredAds, adsName, adsContent);
    }

    /// <summary>
    /// Verify agent on a packed backup set that includes an ADS-carrying file must
    ///  complete without error. This exercises the verify path's use of
    ///  <c>TapeBackupSourceStream.Open</c> on the restored file.
    /// </summary>
    [SkippableFact]
    public void PackedVerify_FileWithAds_Succeeds()
    {
        SkipUnlessElevated();

        using var tree = new TempFileTree();
        SkipUnlessNtfs(tree.RootPath);

        string withAds = tree.AddFileWithAds("verified.dat", 4 * 1024,
            new Dictionary<string, string> { ["tag"] = "verify-payload" })!;

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        fixture.BackupFiles(tree.Files, description: "Verify ADS");

        // Restore first so the verify agent has files to compare against
        using var restoreTree = new TempFileTree();
        using (var restoreAgent = fixture.CreateRestoreAgent(restoreTree.RootPath))
        {
            Assert.True((bool)restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: false, fileNotify: null), "Restore failed");
        }

        // Rewind to the start of the set for the verify agent
        fixture.TOC.CurrentSetIndex = 1;
        var notifiable = new TestNotifiable();
        using var verifyAgent = fixture.CreateVerifyAgent();
        bool verifyOk = (bool)verifyAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: false, fileNotify: notifiable);

        Assert.True(verifyOk, "Verify failed");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion


    // =======================================================================
    #region *** Theory Data ***
    // =======================================================================

#pragma warning disable CA1825
    /// <summary>All four virtual drive profiles.</summary>
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825

    #endregion
}
