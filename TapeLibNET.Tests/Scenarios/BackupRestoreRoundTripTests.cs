using TapeLibNET.Compression;
using TapeLibNET.Toc;
using TapeLibNET.Agents;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Scenarios;


/// <summary>
/// End-to-end backup → restore round trips through the full agent pipeline on the virtual tape drive — the baseline
///  scenario every other <c>Scenarios</c> suite builds on.
/// <para>
/// Each test creates a <see cref="TempFileTree"/> with deterministic content, backs it up via
///  <see cref="VirtualTapeFixture.BackupFiles"/>, restores (or validates / verifies), and checks byte-for-byte
///  equivalence via <see cref="FileComparer"/>.
/// </para>
/// <para>
/// Coverage: all four drive profiles × hash algorithms, software compression, media / set header modes, edge-case
///  files, validate and verify agents, statistics and callbacks, TOC integrity after reload (incl. format 2.1 identity),
///  several sets on one tape, and nested directory restore.
/// </para>
/// </summary>
public class BackupRestoreRoundTripTests
{
    #region *** Test Data ***

    /// <summary>The four drive profiles every round trip must survive.</summary>
    private static readonly DriveProfile[] s_profiles =
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    /// <summary>All four drive profiles for parameterized theories.</summary>
    public static TheoryData<DriveProfile> AllProfiles
    {
        get
        {
            TheoryData<DriveProfile> data = [];
            foreach (var profile in s_profiles)
                data.Add(profile);
            return data;
        }
    }

    /// <summary>
    /// Cross-product of drive profile × hash algorithm: every integrity-check path through the pipeline, including
    ///  <see cref="TapeHashAlgorithm.None"/> (no hash recorded, no check on restore).
    /// </summary>
    public static TheoryData<DriveProfile, TapeHashAlgorithm> ProfilesAndHashes
    {
        get
        {
            TheoryData<DriveProfile, TapeHashAlgorithm> data = [];
            TapeHashAlgorithm[] hashes =
            [
                TapeHashAlgorithm.None,
                TapeHashAlgorithm.Crc64,
                TapeHashAlgorithm.XxHash3,
            ];
            foreach (var profile in s_profiles)
                foreach (var hash in hashes)
                    data.Add(profile, hash);
            return data;
        }
    }

    /// <summary>Cross-product of drive profile × software compression on/off.</summary>
    public static TheoryData<DriveProfile, TapeCompression> ProfilesAndCompression
    {
        get
        {
            TheoryData<DriveProfile, TapeCompression> data = [];
            foreach (var profile in s_profiles)
            {
                data.Add(profile, TapeCompression.None);
                data.Add(profile, TapeCompression.Software);
            }
            return data;
        }
    }

    /// <summary>
    /// Cross-product of drive profile × header mode: headerless, media header only, media + set headers. Set headers
    ///  without a media header are not a valid combination (SH-1).
    /// </summary>
    public static TheoryData<DriveProfile, bool, bool> ProfilesAndHeaderModes
    {
        get
        {
            TheoryData<DriveProfile, bool, bool> data = [];
            foreach (var profile in s_profiles)
            {
                data.Add(profile, false, false);
                data.Add(profile, true, false);
                data.Add(profile, true, true);
            }
            return data;
        }
    }

    #endregion

    #region *** Core Round Trips ***

    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void BackupAndRestore_SmallFileSet_RoundTrips(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("docs", count: 5, minSize: 100, maxSize: 8 * 1024);
        tree.AddFiles("data", count: 3, minSize: 1024, maxSize: 32 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Small set", hashAlgorithm: hash, notifiable: notifiable);
        notifiable.AssertAllSucceeded(tree.Files.Count);

        RestoreLastSetAndCompare(fixture, tree);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_LargerFileSet_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mixed", count: 20, minSize: 0, maxSize: 64 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Larger set", hashAlgorithm: TapeHashAlgorithm.XxHash3,
            notifiable: notifiable);
        notifiable.AssertAllSucceeded(tree.Files.Count);

        RestoreLastSetAndCompare(fixture, tree);
    }

    /// <summary>
    /// Software compression on a mix of compressible (pattern) and incompressible (random) files: the probe must pick
    ///  ZSTD for the former and fall back to stored for the latter, and both must restore byte-for-byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfilesAndCompression))]
    public void BackupAndRestore_Compression_RoundTrips(DriveProfile profile, TapeCompression compression)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("compressible", count: 6, minSize: 4 * 1024, maxSize: 256 * 1024);
        tree.AddRandomFiles("incompressible", count: 4, minSize: 4 * 1024, maxSize: 64 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: $"Compression={compression}",
            hashAlgorithm: TapeHashAlgorithm.Crc64, notifiable: notifiable, compression: compression);
        notifiable.AssertAllSucceeded(tree.Files.Count);

        fixture.LoadTOC();
        var setToc = fixture.TOC[fixture.TOC.Count];
        Assert.Equal(compression, setToc.Compression);
        if (compression == TapeCompression.Software)
        {
            // Pattern files compress; random files must fall back to stored rather than grow
            Assert.Contains(setToc, tfi => tfi.Codec == TapeFileCodec.Zstd);
            Assert.Contains(setToc, tfi => tfi.Codec == TapeFileCodec.Stored);
        }
        else
            Assert.All(setToc, tfi => Assert.Equal(TapeFileCodec.Stored, tfi.Codec));

        RestoreLastSetAndCompare(fixture, tree);
    }

    /// <summary>
    /// The same round trip on headerless media, with a media header, and with media + set headers — the header blocks
    ///  shift every content address, and set-header verification runs on restore.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfilesAndHeaderModes))]
    public void BackupAndRestore_HeaderModes_RoundTrips(DriveProfile profile, bool withMediaHeader, bool withSetHeaders)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("headed", count: 8, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile,
            withMediaHeader: withMediaHeader, withSetHeaders: withSetHeaders);
        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Header mode", hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);
        notifiable.AssertAllSucceeded(tree.Files.Count);

        var restoreNotifiable = RestoreLastSetAndCompare(fixture, tree);
        restoreNotifiable.AssertNoSetAnomalies(expectedSets: 1);
    }

    #endregion

    #region *** Edge-Case Files ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_EdgeCaseFiles_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);
        tree.AddEdgeCases(fixture.Drive.BlockSize);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Edge cases", hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);
        notifiable.AssertAllSucceeded(tree.Files.Count);

        RestoreLastSetAndCompare(fixture, tree);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_SingleZeroByteFile_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("zero.dat", 0);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Zero byte", hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        RestoreLastSetAndCompare(fixture, tree);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_ExactBlockSizeFile_RoundTrips(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var tree = new TempFileTree();
        tree.AddFile("exact_block.dat", fixture.Drive.BlockSize);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Block-aligned", hashAlgorithm: TapeHashAlgorithm.XxHash3,
            notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        RestoreLastSetAndCompare(fixture, tree);
    }

    #endregion

    #region *** Validate and Verify Agents ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Validate_AfterBackup_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 100, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.Crc64);

        // Validate: hash check only, no disk writes
        var notifiable = new TestNotifiable();
        using var validateAgent = fixture.CreateValidateAgent();
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        Assert.True(validateAgent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable),
            "Validation failed");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Verify_AfterBackup_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 100, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Verify: byte-for-byte comparison with the original disk files
        var notifiable = new TestNotifiable();
        using var verifyAgent = fixture.CreateVerifyAgent();
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        Assert.True(verifyAgent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable),
            "Verification failed");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    /// <summary>Hash logic is profile-independent: one representative profile, every algorithm.</summary>
    [Theory]
    [InlineData(TapeHashAlgorithm.None)]
    [InlineData(TapeHashAlgorithm.Crc32)]
    [InlineData(TapeHashAlgorithm.Crc64)]
    [InlineData(TapeHashAlgorithm.XxHash32)]
    [InlineData(TapeHashAlgorithm.XxHash3)]
    [InlineData(TapeHashAlgorithm.XxHash64)]
    [InlineData(TapeHashAlgorithm.XxHash128)]
    public void AllHashAlgorithms_BackupAndValidate_Succeeds(TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("hash_test", count: 3, minSize: 1024, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        fixture.BackupFiles(tree.Files, hashAlgorithm: hash);

        var notifiable = new TestNotifiable();
        using var validateAgent = fixture.CreateValidateAgent();
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        Assert.True(validateAgent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable),
            $"Validation failed for hash algorithm {hash}");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion

    #region *** Statistics and Callbacks ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_Statistics_AreConsistent(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("stats", count: 10, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        var stats = fixture.BackupFiles(tree.Files, description: "Stats test", hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertStatsInvariant();
        notifiable.AssertNoSetAnomalies(expectedSets: 1);

        Assert.Equal(tree.Files.Count, stats.FilesTotal);
        Assert.Equal(tree.Files.Count, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.Equal(0, stats.FilesSkipped);
        Assert.Equal(tree.TotalSize, stats.FileBytesProcessed);

        // One set → one start / end; every file passes pre- and post-processing exactly once
        Assert.Single(notifiable.BatchStarts);
        Assert.Single(notifiable.BatchEnds);
        Assert.Equal(tree.Files.Count, notifiable.PreProcessed.Count);
        Assert.Equal(tree.Files.Count, notifiable.PostProcessed.Count);
        Assert.Empty(notifiable.FilesFailed);
        Assert.Empty(notifiable.FilesSkipped);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_WithSkippedFiles_ReportsAndRestoresTheRest(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("skip", count: 6, minSize: 100, maxSize: 4 * 1024);

        // Skip the first two files in PreProcess
        var notifiable = new TestNotifiable();
        notifiable.FilesToSkip.Add(tree.Files[0]);
        notifiable.FilesToSkip.Add(tree.Files[1]);

        using var fixture = new VirtualTapeFixture(profile);
        var stats = fixture.BackupFiles(tree.Files, description: "Skip test", hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertStatsInvariant();
        Assert.Equal(tree.Files.Count, stats.FilesTotal);
        Assert.Equal(tree.Files.Count - 2, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.Equal(2, stats.FilesSkipped);
        Assert.Equal(2, notifiable.FilesSkipped.Count);

        // The TOC holds only what was written, and exactly that restores
        Assert.Equal(tree.Files.Count - 2, fixture.TOC[fixture.TOC.Count].Count);
        var written = tree.Files.Skip(2).ToList();
        RestoreLastSetAndCompare(fixture, tree, written);
    }

    #endregion

    #region *** TOC Integrity After Backup ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_ContainsCorrectFileEntries(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("toc_check", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "TOC integrity", hashAlgorithm: TapeHashAlgorithm.Crc64);

        fixture.LoadTOC();   // from tape
        Assert.Equal(1, fixture.TOC.Count);
        var setToc = fixture.TOC[1];   // 1-based
        Assert.Equal("TOC integrity", setToc.Description);
        Assert.Equal(TapeHashAlgorithm.Crc64, setToc.HashAlgorithm);
        Assert.Equal(tree.Files.Count, setToc.Count);

        var tocFileNames = new HashSet<string>(setToc.Select(tfi => tfi.FileDescr.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (string file in tree.Files)
            Assert.Contains(file, tocFileNames);

        // Sizes and hashes recorded per file
        foreach (var tfi in setToc)
        {
            Assert.Equal(new FileInfo(tfi.FileDescr.FullName).Length, tfi.FileDescr.Length);
            Assert.True(tfi.SizeOnTape > tfi.FileDescr.Length, "SizeOnTape must include the file header");
            Assert.NotNull(tfi.Hash);
        }
    }

    /// <summary>
    /// Format 2.1 identity survives the trip to tape and back: a V2 set with a real <c>SetId</c>, and unique, non-zero
    ///  <c>FileId</c>s below the set's <c>NextFileId</c> (Design-Format-v2 §5.2).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_CarriesSetAndFileIdentity(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("ids", count: 6, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Identity");
        Guid setIdInMemory = fixture.TOC[1].SetId;

        fixture.LoadTOC();
        var setToc = fixture.TOC[1];
        Assert.Equal(TapeDataFormat.V2, setToc.DataFormat);
        Assert.NotEqual(Guid.Empty, setToc.SetId);
        Assert.Equal(setIdInMemory, setToc.SetId);

        var fileIds = setToc.Select(tfi => tfi.FileId).ToList();
        Assert.All(fileIds, id => Assert.True(id > 0 && id < setToc.NextFileId, $"FileId {id} out of range"));
        Assert.Equal(fileIds.Count, fileIds.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_PreservesBlockSize(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint expectedBlockSize = fixture.Drive.DefaultBlockSize;

        using var tree = new TempFileTree();
        tree.AddFiles("bs_check", count: 3, minSize: 100, maxSize: 4 * 1024);
        fixture.BackupFiles(tree.Files, description: "Block size check");

        fixture.LoadTOC();
        Assert.Equal(expectedBlockSize, fixture.TOC[1].BlockSize);
    }

    /// <summary>A restore driven by a TOC freshly read from tape — not the in-memory one the backup produced.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_AfterTocReload_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("reload", count: 6, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Reload", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        fixture.LoadTOC();
        RestoreLastSetAndCompare(fixture, tree);
    }

    #endregion

    #region *** Multiple Sets on a Single Tape ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultipleSets_BackupAndRestore_Independently(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);
        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);
        using var tree3 = new TempFileTree(seed: 300);
        tree3.AddFiles("set3", count: 5, minSize: 0, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);
        fixture.BackupFiles(tree3.Files, description: "Set 3", hashAlgorithm: TapeHashAlgorithm.None);
        Assert.Equal(3, fixture.TOC.Count);

        // Out of order on purpose: the navigator must reach any set from wherever the head stands
        RestoreSetAndCompare(fixture, tree2, setIndex: 2);
        RestoreSetAndCompare(fixture, tree1, setIndex: 1);
        RestoreSetAndCompare(fixture, tree3, setIndex: 3);
    }

    #endregion

    #region *** Directory Structure ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_RecreatesSubdirectoryStructure(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("level1/file_a.txt", 500);
        tree.AddFile("level1/level2/file_b.txt", 1000);
        tree.AddFile("level1/level2/level3/file_c.txt", 1500);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.Crc64);

        RestoreLastSetAndCompare(fixture, tree);
    }

    #endregion

    #region *** Helpers ***

    /// <summary>Restores the LAST set into a fresh directory and compares it with the tree. Returns the restore callbacks.</summary>
    private static TestNotifiable RestoreLastSetAndCompare(
        VirtualTapeFixture fixture, TempFileTree tree, List<string>? expectedFiles = null)
        => RestoreSetAndCompare(fixture, tree, fixture.TOC.Count, expectedFiles);

    /// <summary>
    /// Restores set <paramref name="setIndex"/> into a fresh directory, asserts every expected file succeeded, and
    ///  compares the restored files byte for byte with the originals. Cleans the restore directory up.
    /// </summary>
    private static TestNotifiable RestoreSetAndCompare(
        VirtualTapeFixture fixture, TempFileTree tree, int setIndex, List<string>? expectedFiles = null)
    {
        var files = expectedFiles ?? tree.Files;
        string restoreDir = CreateRestoreDir();
        try
        {
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = setIndex;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable);

            Assert.True(restored,
                $"Restore of set {setIndex} failed: DriveErr={fixture.Drive.LastError}, " +
                $"Failures={string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result}"))}");
            notifiable.AssertAllSucceeded(files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
            return notifiable;
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>Creates a unique temporary restore directory path.</summary>
    private static string CreateRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where <see cref="TapeFileRestoreAgentEx"/> (with
    ///  RecurseSubdirectories=true) places files originally under <paramref name="originalRoot"/>. The agent strips only
    ///  the drive root (e.g. "C:\"), so the full hierarchy below it is preserved under the target.
    /// </summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

    /// <summary>Best-effort cleanup of a temporary restore directory; resets read-only attributes first.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort — temp directories may be locked
        }
    }

    #endregion
}
