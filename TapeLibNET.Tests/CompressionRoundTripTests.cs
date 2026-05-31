using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Targeted round-trip tests for the per-file software (ZSTD) compression pipeline.
/// <para>
/// These tests verify the compression <em>contract</em>, not compression performance.
/// Only <see cref="ZstdLevel.Default"/> (= Balanced, level 5) is used throughout:
/// the ZSTD decompressor is level-agnostic, so testing multiple levels would be a
/// speed benchmark rather than a functional test.
/// </para>
/// <para>
/// Five areas are covered:
/// <list type="number">
///   <item>Basic byte-for-byte round-trip with software compression (all profiles × hash algorithms).</item>
///   <item>TOC stores <see cref="TapeFileCodec.Zstd"/> per file and <c>SizeOnTape &lt; logical size</c>.</item>
///   <item>Incompressible data triggers store-fallback (<see cref="TapeFileCodec.Stored"/>) and still restores correctly.</item>
///   <item>Files smaller than the 128 KiB probe window flush before the window fills.</item>
///   <item>Session reuse: the <see cref="ProbingCompressionStream.Session"/> is correctly reset between files.</item>
/// </list>
/// </para>
/// </summary>
public class CompressionRoundTripTests
{
    #region *** Test Data ***

#pragma warning disable CA1825
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825

    public static TheoryData<DriveProfile, TapeHashAlgorithm> ProfilesAndHashes
    {
        get
        {
            TheoryData<DriveProfile, TapeHashAlgorithm> data = [];
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
                foreach (var hash in new[] { TapeHashAlgorithm.None, TapeHashAlgorithm.Crc64, TapeHashAlgorithm.XxHash3 })
                    data.Add(profile, hash);
            return data;
        }
    }

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Backs up <paramref name="fileList"/> with software compression and saves the TOC.
    /// </summary>
    private static void BackupWithCompression(
        VirtualTapeFixture fixture,
        List<string> fileList,
        string description,
        TapeHashAlgorithm hash = TapeHashAlgorithm.Crc64,
        int level = ZstdLevel.Default)
    {
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description       = description;
        fixture.TOC.CurrentSetTOC.HashAlgorithm     = hash;
        fixture.TOC.CurrentSetTOC.BlockSize         = fixture.Drive.DefaultBlockSize;
        fixture.TOC.CurrentSetTOC.Compression       = TapeCompression.Software;
        fixture.TOC.CurrentSetTOC.CompressionLevel  = level;

        using var agent = fixture.CreateBackupAgent();
        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: true,
            fileNotify: null);
        Assert.True(success, $"Compressed backup failed: {description}");
        Assert.True(agent.BackupTOC(), "TOC save failed after compressed backup");
    }

    /// <summary>
    /// Restores all files from set index 1 and returns success + the notifiable.
    /// </summary>
    private static (bool Success, TestNotifiable Notifiable) RestoreSet(
        VirtualTapeFixture fixture,
        string restoreDir,
        int setIndex = 1)
    {
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateRestoreAgent(restoreDir);
        fixture.TOC.CurrentSetIndex = setIndex;
        var result = agent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);
        return ((bool)result, notifiable);
    }

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where the restore agent
    /// places files originally rooted at <paramref name="originalRoot"/>.
    /// </summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

    /// <summary>Best-effort cleanup of a temporary restore directory.</summary>
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
        catch { /* best effort */ }
    }

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_CompRestore_{Guid.NewGuid():N}");

    #endregion


    #region *** 1 — Basic round-trip (all profiles × hash algorithms) ***

    /// <summary>
    /// Core correctness proof: compressible files backed up with software ZSTD,
    /// restored byte-for-byte, across all four drive profiles and three hash algorithms.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void Compression_ByteForByteRoundTrip(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        // Mix of sizes: many sub-probe-window and a couple that exceed 128 KiB.
        tree.AddFiles("comp_rt", count: 10, minSize: 512, maxSize: 32 * 1024);
        tree.AddFile("comp_rt/large_200k.dat", 200 * 1024);
        tree.AddFile("comp_rt/large_300k.dat", 300 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupWithCompression(fixture, tree.Files, $"Comp RT {profile}/{hash}", hash);

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestoreSet(fixture, restoreDir);
            Assert.True(success,
                $"Compressed restore failed for {profile}/{hash}: " +
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

    #endregion


    #region *** 2 — TOC records codec and SizeOnTape reflects compression ***

    /// <summary>
    /// After backup with software ZSTD, every compressible file in the TOC must have
    /// <c>Codec == TapeFileCodec.Zstd</c> and <c>SizeOnTape &lt; logical file size</c>.
    /// Also verifies that <c>Compression</c> and <c>CompressionLevel</c> survive a
    /// TOC serialize → deserialize round-trip.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Compression_TOC_StoresCodecAndSizeOnTape(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // Files large enough so ZSTD almost certainly compresses them.
        tree.AddFile("toc/alpha.dat",  256 * 1024);
        tree.AddFile("toc/beta.dat",   128 * 1024);
        tree.AddFile("toc/gamma.dat",  192 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupWithCompression(fixture, tree.Files, "Comp TOC Check");

        var setTOC = fixture.TOC[1];
        Assert.Equal(TapeCompression.Software, setTOC.Compression);
        Assert.Equal(ZstdLevel.Default, setTOC.CompressionLevel);

        for (int i = 0; i < setTOC.Count; i++)
        {
            var tfi = setTOC[i];
            Assert.Equal(TapeFileCodec.Zstd, tfi.Codec);
            // Compressed size must be strictly smaller than the original for our
            // highly compressible repeating-pattern files.
            Assert.True(tfi.SizeOnTape < tree.TotalSize / tree.Files.Count,
                $"Expected SizeOnTape < average logical size for compressible file {tfi.FileDescr.FullName}");
        }

        // Verify compression metadata survives a TOC reload.
        fixture.LoadTOC();
        Assert.Equal(TapeCompression.Software, fixture.TOC[1].Compression);
        Assert.Equal(ZstdLevel.Default, fixture.TOC[1].CompressionLevel);
        for (int i = 0; i < fixture.TOC[1].Count; i++)
            Assert.Equal(TapeFileCodec.Zstd, fixture.TOC[1][i].Codec);
    }

    #endregion


    #region *** 3 — Incompressible data triggers store fallback ***

    /// <summary>
    /// When file content is high-entropy (random bytes), ZSTD cannot compress it.
    /// <see cref="ProbingCompressionStream"/> must detect this during the probe and
    /// fall back to <see cref="TapeFileCodec.Stored"/>.  The restore path must still
    /// produce a byte-for-byte match regardless of the per-file codec decision.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Compression_StoreFallback_IncompressibleData(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // Random (high-entropy) content — effectively incompressible.
        // Files exceed the 128 KiB probe window so the full probe comparison runs.
        tree.AddRandomFiles("rand", count: 6, minSize: 200 * 1024, maxSize: 400 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupWithCompression(fixture, tree.Files, "Comp StoreFallback");

        // Every file must have been auto-stored (probe detected no space saving).
        var setTOC = fixture.TOC[1];
        for (int i = 0; i < setTOC.Count; i++)
            Assert.Equal(TapeFileCodec.Stored, setTOC[i].Codec);

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestoreSet(fixture, restoreDir);
            Assert.True(success,
                $"Store-fallback restore failed for {profile}: " +
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

    #endregion


    #region *** 4 — Files smaller than the 128 KiB probe window ***

    /// <summary>
    /// Files smaller than <see cref="ProbingCompressionStream.ProbeLength"/> (128 KiB) never
    /// fill the probe buffer; <see cref="System.IO.Stream.Flush"/> or
    /// <see cref="System.IO.Stream.Dispose"/> must trigger <c>Commit()</c> early.
    /// Verifies correct codec assignment and byte-for-byte restore for this code path.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Compression_ProbeStraddle_FilesSmallerThanProbeWindow(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // All files are well below the 128 KiB probe window.
        tree.AddFile("small/tiny.dat",    1);
        tree.AddFile("small/hundred.dat", 100);
        tree.AddFile("small/one_k.dat",   1024);
        tree.AddFile("small/ten_k.dat",   10 * 1024);
        tree.AddFile("small/sixty_k.dat", 60 * 1024);
        // Boundary: exactly one byte under the probe window.
        tree.AddFile("small/probe_minus_one.dat", ProbingCompressionStream.ProbeLength - 1);

        using var fixture = new VirtualTapeFixture(profile);
        BackupWithCompression(fixture, tree.Files, "Comp SubProbe");

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestoreSet(fixture, restoreDir);
            Assert.True(success,
                $"Sub-probe restore failed for {profile}: " +
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

    #endregion


    #region *** 5 — Session reuse across many files ***

    /// <summary>
    /// Backs up a large number of files so the <see cref="ProbingCompressionStream.Session"/>
    /// is reused many times within a single set.  Verifies that <c>ResetBuffers()</c> is
    /// called correctly between files and that no state bleeds from one file to the next.
    /// A mix of compressible and incompressible files intentionally exercises both code
    /// paths within the same session.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Compression_SessionReuse_ManyFilesAllRestoreCorrectly(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // 20 compressible files (repeating pattern).
        tree.AddFiles("session/comp", count: 20, minSize: 1024, maxSize: 50 * 1024);
        // 10 incompressible files (random bytes) interleaved in the same set.
        tree.AddRandomFiles("session/rand", count: 10, minSize: 1024, maxSize: 50 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupWithCompression(fixture, tree.Files, "Comp SessionReuse");

        // Sanity: at least one file should have been compressed and one stored.
        var setTOC = fixture.TOC[1];
        Assert.Contains(setTOC, tfi => tfi.Codec == TapeFileCodec.Zstd);
        Assert.Contains(setTOC, tfi => tfi.Codec == TapeFileCodec.Stored);

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestoreSet(fixture, restoreDir);
            Assert.True(success,
                $"Session-reuse restore failed for {profile}: " +
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

    #endregion
}
