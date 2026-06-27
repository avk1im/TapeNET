using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia

namespace TapeLibNET.Tests.Services.Remote;

/// <summary>
/// Remote incremental-chain backup and restore tests: per-wave statistics and
///  version isolation across sets, over a gRPC backend.
/// <para>
/// Mirrors <see cref="ServiceIncrementalTests"/> but routes all drive I/O through
///  the in-process <see cref="LocalHostTapeServiceFixture"/> gRPC server.
///  Covers the same plan items (A-1 and A-2) as the local incremental suite.
/// </para>
/// </summary>
[Collection(LocalHostTapeServiceCollection.Name)]
public class RemoteServiceIncrementalTests(LocalHostTapeServiceFixture fixture)
    : RemoteServiceTestBase(fixture)
{
    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Result record returned by <see cref="SetupRemoteThreeWaveChainAsync"/>
    ///  so callers can access per-wave backup results and the wave-0 file snapshot.
    /// </summary>
    private sealed record ThreeWaveChain(
        BackupResult  Wave0Result,
        BackupResult  Wave1Result,
        BackupResult  Wave2Result,
        List<string>  Wave0Files);

    /// <summary>
    /// Sets up a three-wave incremental backup chain on <paramref name="media"/>
    ///  using <paramref name="src"/> as the source tree, over a remote gRPC backend.
    ///  Mirrors <c>SetupThreeWaveChainAsync</c> from the local suite.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///  <item><b>Wave 0</b> — full backup of 15 files.</item>
    ///  <item><b>Wave 1</b> — modify files[0..4] (version 2), then incremental backup:
    ///   5 written, 10 skipped.</item>
    ///  <item><b>Wave 2</b> — modify files[5..7] (version 2) + 2 new files, then
    ///   incremental backup: 5 written, 12 skipped.</item>
    /// </list>
    /// After this method returns <paramref name="src"/> reflects the final on-disk state
    ///  (17 files). The returned <c>wave2Files</c> list is a snapshot of
    ///  <see cref="TempFileTree.Files"/> after wave 2.
    /// </remarks>
    private async Task<(ThreeWaveChain chain, List<string> wave2Files)>
        SetupRemoteThreeWaveChainAsync(TempVirtualMedia media, TempFileTree src)
    {
        // ── Wave 0: full backup of 15 files ──────────────────────────────────
        src.AddFiles("chain", count: 15, minSize: 1_024, maxSize: 16 * 1_024);
        var wave0Files = new List<string>(src.Files); // snapshot before any modifications

        var (svc0, _) = await OpenAndFormatRemoteAsync(media);
        BackupResult w0Result;
        using (svc0)
        {
            w0Result = await svc0.ExecuteBackupAsync(
                MakeBackupRequest(svc0, src.RootPath, "Chain-Wave0"));
        }

        // ── Wave 1: modify first 5 files, incremental backup ─────────────────
        // ModifyFile guarantees LastWriteTime advances, so no Task.Delay needed.
        for (int i = 0; i < 5; i++)
            src.ModifyFile(src.Files[i], version: 2);

        var (svc1, _) = await ReopenRemoteAsync(media);
        BackupResult w1Result;
        using (svc1)
        {
            // append: true — writes a second set after wave 0 on the same tape
            w1Result = await svc1.ExecuteBackupAsync(
                MakeBackupRequest(svc1, src.RootPath, "Chain-Wave1", append: true, incremental: true));
        }

        // ── Wave 2: modify files[5..7], add 2 new, incremental backup ─────────
        for (int i = 5; i < 8; i++)
            src.ModifyFile(src.Files[i], version: 2);
        src.AddFile("chain/extra1.dat", 2_048);
        src.AddFile("chain/extra2.dat", 2_048);

        var wave2Files = new List<string>(src.Files); // snapshot of final state (17 files)

        var (svc2, _) = await ReopenRemoteAsync(media);
        BackupResult w2Result;
        using (svc2)
        {
            // append: true — writes the third set after waves 0 and 1
            w2Result = await svc2.ExecuteBackupAsync(
                MakeBackupRequest(svc2, src.RootPath, "Chain-Wave2", append: true, incremental: true));
        }

        return (new ThreeWaveChain(w0Result, w1Result, w2Result, wave0Files), wave2Files);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies per-wave statistics across a three-wave incremental chain over gRPC:
    ///  wave 0 full backup → wave 1 incremental (5 changed) → wave 2 incremental (3 changed + 2 new).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remote_IncrementalChain_BackupStatistics_CorrectSkipSucceedCounts(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();

        var (chain, _) = await SetupRemoteThreeWaveChainAsync(media, src);

        // ── Wave 0: full backup of 15 files ──────────────────────────────────
        var w0 = chain.Wave0Result;
        Assert.True(w0.Success,                "Wave 0 backup failed");
        Assert.Equal(0,  w0.FilesFailed);
        Assert.Equal(15, w0.FilesSucceeded);
        Assert.Equal(0,  w0.FilesSkipped);
        Assert.Equal(15, w0.FilesTotal);

        // ── Wave 1: incremental — 5 modified, 10 skipped ─────────────────────
        var w1 = chain.Wave1Result;
        Assert.True(w1.Success,                "Wave 1 backup failed");
        Assert.Equal(0,  w1.FilesFailed);
        Assert.Equal(5,  w1.FilesSucceeded);
        Assert.Equal(10, w1.FilesSkipped);
        Assert.Equal(15, w1.FilesTotal);

        // ── Wave 2: incremental — 3 modified + 2 new = 5 written, 12 skipped ─
        var w2 = chain.Wave2Result;
        Assert.True(w2.Success,                "Wave 2 backup failed");
        Assert.Equal(0,  w2.FilesFailed);
        Assert.Equal(5,  w2.FilesSucceeded);
        Assert.Equal(12, w2.FilesSkipped);
        Assert.Equal(17, w2.FilesTotal);

        // ── TOC flags and per-set file counts ─────────────────────────────────
        var (tocSvc, _) = await ReopenRemoteAsync(media);
        using (tocSvc)
        {
            var toc = tocSvc.TOC!;
            Assert.Equal(3, toc.Count);

            int s1 = toc.FirstSetOnVolume;
            int s2 = s1 + 1;
            int s3 = toc.LastSetOnVolume;

            Assert.False(toc[s1].Incremental, "Set 1 should be a full backup");
            Assert.True (toc[s2].Incremental, "Set 2 should be incremental");
            Assert.True (toc[s3].Incremental, "Set 3 should be incremental");

            Assert.Equal(15, toc[s1].Count);
            Assert.Equal(5,  toc[s2].Count);
            Assert.Equal(5,  toc[s3].Count);
        }
    }

    /// <summary>
    /// Verifies that an incremental-chain restore over gRPC reconstructs the correct
    ///  version of each file depending on which set (or chain) is targeted.
    /// <list type="bullet">
    ///  <item><b>Case A</b> — incremental restore from set 3 (latest): every file
    ///   reflects its most recent version across all three waves.</item>
    ///  <item><b>Case B</b> — non-incremental restore from set 2 only: only the
    ///   5 files backed up in wave 1 are restored.</item>
    ///  <item><b>Case C</b> — non-incremental restore from set 1 only: all 15
    ///   original files.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Remote_IncrementalChain_Restore_CorrectVersionsAcrossSets()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();

        var (chain, wave2Files) = await SetupRemoteThreeWaveChainAsync(media, src);

        // ── Case A: incremental restore from latest set → all 17 files, latest versions ──
        {
            var restoreRoot = Path.Combine(media.Root, "restore_A");
            Directory.CreateDirectory(restoreRoot);

            var (svc, host) = await ReopenRemoteAsync(media);
            using (svc)
            {
                var toc    = svc.TOC!;
                int latest = toc.SetIndexToStd(toc.CapSetIndex(0));
                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [latest] = null },
                    Incremental:           true,
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false,
                    EjectWhenDone:         false);

                var result = await svc.ExecuteRestoreAsync(req);
                Assert.True(result.Success, "Case A: incremental restore failed");
                Assert.Equal(0,  result.FilesFailed);
                // 17 total files (15 original + 2 added in wave 2)
                Assert.Equal(17, result.FilesSucceeded);
                Assert.False(host.HasErrors, "Case A: restore host received unexpected errors");
            }

            FileComparer.AssertFilesMatch(
                src.RootPath, wave2Files, FindRestoredRoot(restoreRoot, src.RootPath));
        }

        // ── Case B: non-incremental restore of set 2 only → exactly 5 files ─
        {
            var restoreRoot = Path.Combine(media.Root, "restore_B");
            Directory.CreateDirectory(restoreRoot);

            var (svc, host) = await ReopenRemoteAsync(media);
            using (svc)
            {
                var toc = svc.TOC!;
                int s2  = toc.FirstSetOnVolume + 1;
                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [s2] = null },
                    Incremental:           false,
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false,
                    EjectWhenDone:         false);

                var result = await svc.ExecuteRestoreAsync(req);
                Assert.True(result.Success, "Case B: set-2 restore failed");
                Assert.Equal(0, result.FilesFailed);
                Assert.Equal(5, result.FilesSucceeded);
                Assert.False(host.HasErrors, "Case B: restore host received unexpected errors");
            }
        }

        // ── Case C: non-incremental restore of set 1 only → all 15 original files ──
        {
            var restoreRoot = Path.Combine(media.Root, "restore_C");
            Directory.CreateDirectory(restoreRoot);

            var (svc, host) = await ReopenRemoteAsync(media);
            using (svc)
            {
                var toc = svc.TOC!;
                int s1  = toc.FirstSetOnVolume;
                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [s1] = null },
                    Incremental:           false,
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false,
                    EjectWhenDone:         false);

                var result = await svc.ExecuteRestoreAsync(req);
                Assert.Equal(0,  result.FilesFailed);
                Assert.Equal(15, result.FilesSucceeded);
                Assert.False(host.HasErrors, "Case C: restore host received unexpected errors");
            }
            // Byte-for-byte comparison omitted: wave-0 source files were overwritten by
            //  ModifyFile calls in subsequent waves.
        }
    }
}
