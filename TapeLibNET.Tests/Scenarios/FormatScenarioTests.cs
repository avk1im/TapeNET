using Windows.Win32.Foundation;

using TapeLibNET.Agents;
using TapeLibNET.Compression;
using TapeLibNET.Format;
using TapeLibNET.Toc;

using TapeLibNET.Tests.Helpers;
using TapeLibNET.Tests.Toc;

namespace TapeLibNET.Tests.Scenarios;


/// <summary>
/// Phase 7 (Design-Format-v2 §11.5): format 2.1 properties end to end, on all four drive profiles — self-describing
///  file headers, the codec byte, per-attempt FileIds, SetId across volumes, and the <c>.tapetoc</c> formats.
/// </summary>
/// <remarks>
/// Each identity test works by CONTRADICTION: the TOC is changed in memory after the backup, and restore must refuse
///  every file the tape now disagrees with. That proves the identity is on the tape — not merely in the TOC.
/// </remarks>
public class FormatScenarioTests
{
    #region *** Test Data ***

    /// <summary>All four drive profiles.</summary>
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Self-describing file headers (Format_FileHeader_SelfDescribing) ***

    /// <summary>
    /// Every 2.1 file header carries its set's <c>SetId</c>. A TOC that names another set must be refused file by
    ///  file — and nothing may reach the disk.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FileHeader_ForeignSetIdInToc_EveryFileRefused(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("sid", count: 4, minSize: 256, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "SetId check");

        Guid real = fixture.TOC[1].SetId;
        fixture.TOC[1].SetId = Guid.NewGuid();                       // the TOC now describes another set

        string restoreDir = CreateRestoreDir();
        try
        {
            var notifiable = new TestNotifiable();
            using (var agent = fixture.CreateRestoreAgent(restoreDir))
            {
                fixture.TOC.CurrentSetIndex = 1;
                agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable);
            }

            Assert.Equal(tree.Files.Count, notifiable.FilesFailed.Count);
            Assert.All(notifiable.FilesFailed,
                f => Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, f.Result.ErrorCode));
            Assert.False(Directory.Exists(restoreDir)
                && Directory.EnumerateFiles(restoreDir, "*", SearchOption.AllDirectories).Any(),
                "a refused file must never reach the disk");

            // The identity, and only the identity, made the difference
            fixture.TOC[1].SetId = real;
            RestoreAndCompare(fixture, tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// The body's first byte names its codec; restore requires it to agree with the TOC. A disagreement refuses that
    ///  one file and leaves its neighbours alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CodecByte_DisagreesWithToc_OnlyThatFileRefused(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("codec", count: 3, minSize: 256, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Codec check");     // no compression: every body is Stored

        TapeFileInfo tampered = fixture.TOC[1][0];
        Assert.Equal(TapeFileCodec.Stored, tampered.Codec);
        tampered.Codec = TapeFileCodec.Zstd;

        string restoreDir = CreateRestoreDir();
        try
        {
            var notifiable = new TestNotifiable();
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;
            agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable);

            var failed = Assert.Single(notifiable.FilesFailed);
            Assert.Equal(tampered.FileDescr.FullName, failed.FileInfo.FileDescr.FullName);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, failed.Result.ErrorCode);
            Assert.Equal(tree.Files.Count - 1, notifiable.PostProcessed.Count);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** FileId per attempt (Format_FileId_RetryBurnsNumber) ***

    /// <summary>
    /// A failed attempt burns its <c>FileId</c>: the retry gets a fresh one, so the orphaned attempt that may remain
    ///  on tape can never be mistaken for the committed file (Design-Format-v2 §5.2).
    /// </summary>
    /// <remarks>
    /// The failure is real, not simulated: the file is held open exclusively, so <c>BackupRead</c> cannot open it.
    ///  The failure callback releases the lock and asks for a retry, which then succeeds.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FileId_RetryBurnsTheFailedAttemptsNumber(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("retry", count: 5, minSize: 512, maxSize: 4 * 1024);
        string lockedFile = tree.Files[2];

        var hold = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        int failures = 0;
        var notifiable = new TestNotifiable
        {
            FailedActionFunc = (_, _) =>
            {
                failures++;
                hold.Dispose();                       // the retry finds the file free
                return failures == 1 ? FileFailedAction.Retry : FileFailedAction.Skip;
            },
        };

        using var fixture = new VirtualTapeFixture(profile);
        try
        {
            fixture.BackupFiles(tree.Files, description: "Retry", notifiable: notifiable);
        }
        finally
        {
            hold.Dispose();
        }

        Assert.Equal(1, failures);
        TapeSetTOC set = fixture.TOC[1];
        Assert.Equal(tree.Files.Count, set.Count);

        var ids = set.Select(f => f.FileId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(set.NextFileId >= (ulong)set.Count + 2,
            $"the failed attempt must burn a FileId (NextFileId {set.NextFileId}, files {set.Count})");

        // The orphaned attempt does not disturb the committed files
        var validate = new TestNotifiable();
        using var agent = fixture.CreateValidateAgent();
        fixture.TOC.CurrentSetIndex = 1;
        Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: validate), "validation failed");
        validate.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion

    #region *** SetId across volumes (Format_SetId_ContinuationShared_OverwriteFresh, first half) ***

    /// <summary>
    /// A set that spans volumes stays ONE logical set: every continuation carries the original <c>SetId</c>, and
    ///  FileIds keep counting across the volume boundary. The next backup gets a fresh <c>SetId</c>.
    /// </summary>
    /// <remarks>The overwrite half lives in <c>ServiceMixedMediaTests.Overwrite_ReplacedSet_GetsFreshSetId</c>.</remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SetId_SharedByContinuations_FreshForTheNextSet(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("span", count: 16, minSize: 16 * 1024, maxSize: 32 * 1024);
        using var next = new TempFileTree(seed: 7);
        next.AddFiles("next", count: 2, minSize: 512, maxSize: 2 * 1024);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, 256L * 1024);
        fixture.BackupFiles(tree.Files, "Spanning");
        Assert.True(fixture.TotalVolumes >= 2, $"the set must span (got {fixture.TotalVolumes} volume(s))");

        TapeTOC toc = fixture.TOC;
        int spanningSets = toc.Count;
        Guid id = toc[1].SetId;
        Assert.NotEqual(Guid.Empty, id);
        for (int i = 2; i <= spanningSets; i++)
        {
            Assert.True(toc[i].ContinuedFromPrevVolume, $"set {i} must continue set {i - 1}");
            Assert.Equal(id, toc[i].SetId);
        }

        var fileIds = Enumerable.Range(1, spanningSets).SelectMany(i => toc[i]).Select(f => f.FileId).ToList();
        Assert.Equal(tree.Files.Count, fileIds.Count);
        Assert.Equal(fileIds.Count, fileIds.Distinct().Count());

        fixture.BackupFiles(next.Files, "Next");
        Assert.NotEqual(id, fixture.TOC[fixture.TOC.Count].SetId);
    }

    #endregion

    #region *** .tapetoc formats (Format_TapetocImportBoth_ExportOnly21) ***

    /// <summary>
    /// Import reads both formats; export always writes 2.1 — and exporting upgrades nothing on tape, so a legacy TOC
    ///  stays flagged legacy until a copy reaches the tape (Design-Format-v2 §6.4).
    /// </summary>
    [Fact]
    public void Tapetoc_ImportsBothFormats_ExportsOnly21()
    {
        string dir = CreateRestoreDir();
        Directory.CreateDirectory(dir);
        try
        {
            TapeTOC source = TocMigrationFixtures.Build();
            string legacyPath = Path.Combine(dir, "legacy" + TapeAgentBase.TOCFileExtension);
            string exportPath = Path.Combine(dir, "export" + TapeAgentBase.TOCFileExtension);
            File.WriteAllBytes(legacyPath, LegacyTocWriter.TocBytes(source));

            using var fixture = new VirtualTapeFixture();
            using var agent = new TapeAgentBase(fixture.Drive, new TapeTOC());

            // Import, legacy
            Assert.True(agent.LoadTOCFromFile(legacyPath), "legacy .tapetoc did not import");
            Assert.True(agent.TOC.LoadedFromLegacy);
            Assert.Equal(source.Count, agent.TOC.Count);

            // Export: 2.1, and the TOC stays legacy — no tape was written
            Assert.True(agent.SaveTOCToFile(exportPath), "export failed");
            Assert.True(TapeFormat.IsV2(File.ReadAllBytes(exportPath)), "an export must be format 2.1");
            Assert.True(agent.TOC.LoadedFromLegacy, "an export must not clear the legacy flag");

            // Import, 2.1
            using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
            Assert.True(reader.LoadTOCFromFile(exportPath), "2.1 .tapetoc did not import");
            Assert.False(reader.TOC.LoadedFromLegacy);
            Assert.Equal(source.Count, reader.TOC.Count);
            for (int s = 1; s <= source.Count; s++)
            {
                Assert.Equal(source[s].Count, reader.TOC[s].Count);
                Assert.Equal(TapeDataFormat.Legacy, reader.TOC[s].DataFormat);   // legacy sets stay legacy in 2.1
            }
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    #endregion

    #region *** Helpers ***

    /// <summary>Restores set 1 into <paramref name="restoreDir"/> and compares it with the tree, byte for byte.</summary>
    private static void RestoreAndCompare(VirtualTapeFixture fixture, TempFileTree tree, string restoreDir)
    {
        TryDeleteDirectory(restoreDir);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateRestoreAgent(restoreDir);
        fixture.TOC.CurrentSetIndex = 1;
        Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable), "restore failed");
        notifiable.AssertAllSucceeded(tree.Files.Count);
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
    }

    private static string CreateRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_Format_{Guid.NewGuid():N}");

    /// <summary>Where the restore agent places files originally under <paramref name="originalRoot"/>.</summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        return Path.Combine(restoreDir, Path.GetRelativePath(pathRoot, originalRoot));
    }

    /// <summary>Best-effort cleanup; resets read-only attributes first.</summary>
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
