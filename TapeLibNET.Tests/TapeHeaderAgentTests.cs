using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Tier-2 tests for on-tape media-header behavior through <see cref="TapeFileAgent"/> across all four
///  drive profiles: header write/read/probe, presence resolution, the clobber regression (header
///  survives content + TOC), and a full headed backup → restore round-trip.
/// </summary>
/// <remarks>
/// The stock <see cref="VirtualTapeFixture"/> never writes a header, so a fresh fixture models LEGACY
///  (header-less) media. These tests write a header explicitly via <see cref="WriteMediaHeader"/> —
///  mirroring what the service format path will do — then verify that the header is transparent to the
///  file-level backup/restore logic and is never clobbered.
/// </remarks>
public class TapeHeaderAgentTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

    /// <summary>
    /// Writes a media header to the fixture's freshly formatted tape (mirroring the format path),
    ///  minting the shared MediaId on the fixture's TOC, and returns that id.
    /// </summary>
    private static Guid WriteMediaHeader(VirtualTapeFixture fixture)
    {
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        Assert.True(agent.WriteHeader(), "WriteHeader failed");
        return fixture.TOC.MediaId;
    }

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_HeaderRestore_{Guid.NewGuid():N}");

    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relative = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relative);
    }

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
            // best effort
        }
    }

    #endregion

    #region *** Write / Read / Probe ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteThenRead_ReturnsMediaHeader_MatchingId(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var mediaId = WriteMediaHeader(fixture);

        // A FRESH agent (presence Unknown) must read + classify the header.
        using var reader = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var header = reader.ReadHeader();

        var media = Assert.IsType<TapeMediaHeader>(header);
        Assert.Equal(mediaId, media.MediaId);
        Assert.Equal(TapeHeaderPresence.Present, reader.Navigator.HeaderPresence);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProbeHeaderPresence_BlankMedia_IsAbsent(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        // No header written: reaching BOM and finding no data is the DEFINITIVE "no header" = Absent
        //  (a resolved state — NOT Unknown, which would wedge later navigation).
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        Assert.Equal(TapeHeaderPresence.Absent, agent.ProbeHeaderPresence());
        Assert.Null(agent.ReadHeader());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProbeHeaderPresence_AfterWrite_IsPresent(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        WriteMediaHeader(fixture);

        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        Assert.Equal(TapeHeaderPresence.Present, agent.ProbeHeaderPresence());
    }

    #endregion

    #region *** Integration — headed backup / restore ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeadedBackup_Restore_ByteForByteMatch(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("headed", count: 6, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        // Header first (format-path order), then a normal backup on top of it.
        WriteMediaHeader(fixture);
        fixture.BackupFiles(tree.Files, description: "Headed Set", hashAlgorithm: TapeHashAlgorithm.Crc64);

        string restoreDir = MakeRestoreDir();
        try
        {
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;

            var result = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Headed restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeadedBackup_HeaderSurvivesContentAndTOC(DriveProfile profile)
    {
        // The clobber regression: after a full backup (content + TOC), the media header at BOM must
        //  still read back intact — proving no assume-blank rewind or content write clobbered it.
        using var tree = new TempFileTree();
        tree.AddFiles("survive", count: 5, minSize: 256, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var mediaId = WriteMediaHeader(fixture);
        fixture.BackupFiles(tree.Files, description: "Survivor");

        using var reader = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var media = Assert.IsType<TapeMediaHeader>(reader.ReadHeader());
        Assert.Equal(mediaId, media.MediaId);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeadedBackup_TOCReload_ThenRestore(DriveProfile profile)
    {
        // Reload the TOC from tape (fresh agent) then restore — exercises TOC navigation (end-relative,
        //  header-agnostic) followed by content navigation (skips the header) on the same headed tape.
        using var tree = new TempFileTree();
        tree.AddFiles("reload", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var mediaId = WriteMediaHeader(fixture);
        fixture.BackupFiles(tree.Files, description: "Reload Set");

        fixture.LoadTOC();                                   // TOC round-trips through tape
        Assert.Equal(mediaId, fixture.TOC.MediaId);          // MediaId preserved on the reloaded TOC

        string restoreDir = MakeRestoreDir();
        try
        {
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;

            var result = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Headed restore-after-reload failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeadedMultiSet_RestoreEachIndependently(DriveProfile profile)
    {
        // Header + several sets; the header must not perturb per-set positioning.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);
        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        WriteMediaHeader(fixture);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        Assert.Equal(2, fixture.TOC.Count);

        foreach (var (setIndex, tree) in new[] { (1, tree1), (2, tree2) })
        {
            string restoreDir = MakeRestoreDir();
            try
            {
                var notifiable = new TestNotifiable();
                using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
                fixture.TOC.CurrentSetIndex = setIndex;

                var result = restoreAgent.RestoreAllFilesFromCurrentSet(
                    ignoreFailures: true, fileNotify: notifiable);

                Assert.True((bool)result,
                    $"Headed restore of set {setIndex} failed for {profile}: " +
                    $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
                notifiable.AssertAllSucceeded(tree.Files.Count);

                FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                    RestoreEquivalentRoot(restoreDir, tree.RootPath));
            }
            finally
            {
                TryDeleteDirectory(restoreDir);
            }
        }
    }

    #endregion

    #region *** Legacy (header-less) classification ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeaderlessBackup_ReadHeader_ReturnsNull_Absent(DriveProfile profile)
    {
        // No header written: content is written at BOM (block 0). Reading the "header" there classifies
        //  the content block as foreign → null → Absent. Confirms legacy media stays navigable.
        using var tree = new TempFileTree();
        tree.AddFiles("legacy", count: 4, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Legacy Set");

        using var reader = new TapeFileAgent(fixture.Drive, fixture.TOC);
        Assert.Null(reader.ReadHeader());
        Assert.Equal(TapeHeaderPresence.Absent, reader.Navigator.HeaderPresence);
    }

    #endregion
}
