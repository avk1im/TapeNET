using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 7 integration coverage for the pipelined read path
/// (<see cref="TapeFilePipelinedReader"/> wired into
/// <see cref="TapeStreamManager"/>).
/// <para>
/// Sister suite to <see cref="TapeRestoreAgentPackedTests"/>, focused on
/// scenarios that exercise the prefetch ring's behavior end-to-end through
/// the real agent:
/// </para>
/// <list type="bullet">
///   <item>Explicit backward-seek selective restore — forces the reader to
///   abandon its in-flight prefetch window and re-arm to an earlier block.</item>
///   <item>Cross-set restore in one session — verifies that
///   <see cref="TapeStreamManager"/> disposes the read packer on a set boundary
///   so the next set starts with a clean prefetch ring.</item>
///   <item>Large-file restore — single file large enough to exhaust the ring
///   and let back-pressure dominate the read loop.</item>
/// </list>
/// </summary>
public class TapeRestoreAgentPipelinedTests
{
    #region *** Test Data ***

#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Filter that selects an explicit, ordered subset of file names. Order is
    /// preserved by <see cref="TapeSetTOC.SelectFiles(ITapeFileFilter)"/>'s
    /// iteration over the TOC, but the caller controls *which* names match.
    /// </summary>
    private sealed class NameSetFilter(IEnumerable<string> fullNames) : ITapeFileFilter
    {
        private readonly HashSet<string> _names = new(fullNames, StringComparer.OrdinalIgnoreCase);

        public bool Matches(in TapeFileDescriptor fileDescr) =>
            _names.Contains(fileDescr.FullName);
    }

    private static void BackupPackedAndSaveTOC(
        VirtualTapeFixture fixture,
        List<string> fileList,
        string description,
        TapeHashAlgorithm hash = TapeHashAlgorithm.Crc64)
    {
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = description;
        fixture.TOC.CurrentSetTOC.HashAlgorithm = hash;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var backupAgent = fixture.CreateBackupAgent();
        Assert.True(
            backupAgent.BackupFileListToCurrentSet(
                newSet: true, fileList, ignoreFailures: true, fileNotify: null),
            $"Packed backup failed: {description}");
        Assert.True(backupAgent.BackupTOC(), "TOC save failed after packed backup");
    }

    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_PipelinedRestore_{Guid.NewGuid():N}");

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
            // Best effort
        }
    }

    #endregion


    #region *** Backward-Seek Selective Restore ***

    /// <summary>
    /// Selects a sparse subset of files but presents them to the restore agent in
    /// *reverse* TOC order. Because <see cref="TapeFileRestoreBaseAgent.RestoreFilesFromCurrentSet"/>
    /// iterates the supplied list in order, each successive file lies at a block
    /// strictly *before* the one the prefetch ring is currently filling. This
    /// forces <c>TapeFilePipelinedReader.ArmReadSession</c> down the
    /// out-of-window / flush-and-restart branch on nearly every file.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_BackwardSeek_SelectiveRestore_AllFilesByteForByte(DriveProfile profile)
    {
        // Enough files spread across many blocks so that backward seeks always
        //  land outside the prefetch ring window.
        const int totalFiles = 40;
        using var tree = new TempFileTree();
        for (int i = 0; i < totalFiles; i++)
            tree.AddFile($"bwd_{i:D3}.dat", 32 * 1024 + i * 17);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Pipelined Backward Seek");

        // Pick every other file, then reverse so the agent walks the tape backward.
        var setToc = fixture.TOC[1];
        var picks = new List<TapeFileInfo>();
        for (int i = setToc.Count - 1; i >= 0; i -= 2)
            picks.Add(setToc[i]);

        Assert.True(picks.Count >= 8,
            "Need a meaningful number of backward seeks to stress the prefetch ring");

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = 1;
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            // The packed pendant of RestoreFilesFromCurrentSetDownAligned accepts
            //  a per-set list of pre-selected files and routes through the
            //  pipelined read packer. Supplying our reverse-ordered list forces
            //  the agent to re-seek backward on each successive file.
            var perSet = new List<TapeFileInfo>?[fixture.TOC.Count];
            perSet[0] = picks;
            var result = restoreAgent.RestoreFilesFromCurrentSetDown(
                perSet, ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Pipelined backward-seek restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(picks.Count);

            // Verify byte-for-byte content of every restored file.
            var expected = picks.Select(p => p.FileDescr.FullName).ToList();
            FileComparer.AssertFilesMatch(tree.RootPath, expected,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Restores the *first* file of the set last. After the restore agent
    /// streams through most of the set, it must seek back to block 0 — the
    /// most aggressive backward-seek the prefetch ring can be asked for.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_RestoreFirstFileLast_SeekToBlockZero_Succeeds(DriveProfile profile)
    {
        const int totalFiles = 20;
        using var tree = new TempFileTree();
        for (int i = 0; i < totalFiles; i++)
            tree.AddFile($"order_{i:D3}.dat", 24 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Pipelined Seek to 0");

        var setToc = fixture.TOC[1];
        // Sequential order, then the very first file appended at the end.
        var picks = new List<TapeFileInfo>();
        for (int i = 1; i < setToc.Count; i++)
            picks.Add(setToc[i]);
        picks.Add(setToc[0]);

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = 1;
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            var perSet = new List<TapeFileInfo>?[fixture.TOC.Count];
            perSet[0] = picks;
            var result = restoreAgent.RestoreFilesFromCurrentSetDown(
                perSet, ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Pipelined seek-to-zero restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(picks.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Cross-Set Restore in One Session ***

    /// <summary>
    /// Restores two consecutive sets back-to-back through the same restore
    /// agent (i.e. without disposing it between sets). Between sets the
    /// manager transitions back through <c>BeginReadContent</c>, which calls
    /// <c>DisposeReadPacker</c>; the next set must therefore allocate and
    /// arm a fresh <see cref="TapeFilePipelinedReader"/> with a clean ring.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_TwoSets_OneAgentSession_ResetsRingBetweenSets(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 1001);
        tree1.AddFiles("set1", count: 10, minSize: 1 * 1024, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 2002);
        tree2.AddFiles("set2", count: 10, minSize: 1 * 1024, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree1.Files, "Pipelined Set 1", TapeHashAlgorithm.Crc64);
        BackupPackedAndSaveTOC(fixture, tree2.Files, "Pipelined Set 2", TapeHashAlgorithm.XxHash3);
        Assert.Equal(2, fixture.TOC.Count);

        string restoreDir1 = MakeRestoreDir();
        string restoreDir2 = MakeRestoreDir();
        try
        {
            // Single restore agent / single open drive session restores set #1
            //  *then* set #2 — exercising the read-packer reset on set transition.
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir1);

            fixture.TOC.CurrentSetIndex = 1;
            var n1 = new TestNotifiable();
            var r1 = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: n1);
            Assert.True((bool)r1,
                $"Pipelined set #1 restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", n1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            n1.AssertAllSucceeded(tree1.Files.Count);
            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir1, tree1.RootPath));

            // Second restore through a fresh agent (the first one's target dir
            //  was bound at construction) but the *drive* and its stream
            //  manager are still the same open session — the read packer must
            //  re-initialize cleanly for set #2.
            using var restoreAgent2 = fixture.CreateRestoreAgent(restoreDir2);
            fixture.TOC.CurrentSetIndex = 2;
            var n2 = new TestNotifiable();
            var r2 = restoreAgent2.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: n2);
            Assert.True((bool)r2,
                $"Pipelined set #2 restore (same drive session) failed for {profile}: " +
                $"Failures=[{string.Join("; ", n2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            n2.AssertAllSucceeded(tree2.Files.Count);
            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
            TryDeleteDirectory(restoreDir2);
        }
    }

    /// <summary>
    /// Restores two sets in *reverse* order (newest set first, then oldest)
    /// through the same drive session. The transition from set #2 back to
    /// set #1 forces both a read-packer dispose *and* a fresh tape positioning
    /// to an earlier content set — covering the set-boundary teardown path
    /// of <see cref="TapeStreamManager.BeginReadContent"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_TwoSets_ReverseOrder_SameSession_Succeeds(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 3003);
        tree1.AddFiles("rev_set1", count: 8, minSize: 2 * 1024, maxSize: 6 * 1024);

        using var tree2 = new TempFileTree(seed: 4004);
        tree2.AddFiles("rev_set2", count: 8, minSize: 2 * 1024, maxSize: 6 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree1.Files, "Pipelined Rev Set 1");
        BackupPackedAndSaveTOC(fixture, tree2.Files, "Pipelined Rev Set 2");

        string restoreDir2 = MakeRestoreDir();
        string restoreDir1 = MakeRestoreDir();
        try
        {
            // Restore the newer set first (forward through tape), then the
            //  older set (requires positioning back to set #1's content).
            using (var ra2 = fixture.CreateRestoreAgent(restoreDir2))
            {
                fixture.TOC.CurrentSetIndex = 2;
                var n2 = new TestNotifiable();
                var r2 = ra2.RestoreAllFilesFromCurrentSet(
                    ignoreFailures: true, fileNotify: n2);
                Assert.True((bool)r2,
                    $"Pipelined reverse-order set #2 restore failed for {profile}");
                n2.AssertAllSucceeded(tree2.Files.Count);
                FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                    RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
            }

#pragma warning disable IDE0063 // Use simple 'using' statement -- for symmetry with the 1st restore using ra2
            using (var ra1 = fixture.CreateRestoreAgent(restoreDir1))
            {
                fixture.TOC.CurrentSetIndex = 1;
                var n1 = new TestNotifiable();
                var r1 = ra1.RestoreAllFilesFromCurrentSet(
                    ignoreFailures: true, fileNotify: n1);
                Assert.True((bool)r1,
                    $"Pipelined reverse-order set #1 restore failed for {profile}");
                n1.AssertAllSucceeded(tree1.Files.Count);
                FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                    RestoreEquivalentRoot(restoreDir1, tree1.RootPath));
            }
#pragma warning restore IDE0063 // Use simple 'using' statement
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
            TryDeleteDirectory(restoreDir2);
        }
    }

    /// <summary>
    /// Cross-set selective restore through <see cref="TapeFileRestoreBaseAgent.RestoreFilesFromSets"/>.
    /// Exercises the multi-set entry point that walks two sets newest→oldest
    /// in a single agent call, forcing a packer reset between sets.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_RestoreFromMultipleSets_PackerResetsBetweenSets(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 5005);
        tree1.AddFile("ms_a1.dat", 4 * 1024);
        tree1.AddFile("ms_b1.dat", 6 * 1024);
        tree1.AddFile("ms_c1.dat", 5 * 1024);

        using var tree2 = new TempFileTree(seed: 6006);
        tree2.AddFile("ms_a2.dat", 4 * 1024);
        tree2.AddFile("ms_b2.dat", 6 * 1024);
        tree2.AddFile("ms_c2.dat", 5 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree1.Files, "Pipelined MS Set 1");
        BackupPackedAndSaveTOC(fixture, tree2.Files, "Pipelined MS Set 2");

        string restoreDir = MakeRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            var notifiable = new TestNotifiable();

            var result = restoreAgent.RestoreFilesFromSets(
                setIndexes: [1, 2], incremental: false, fileFilter: null,
                ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Pipelined multi-set restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree1.Files.Count + tree2.Files.Count);

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir, tree1.RootPath));
            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Large-File Ring Back-Pressure ***

    /// <summary>
    /// Single file large enough that the prefetch ring (16 slots × block size)
    /// fills well before the consumer can drain it. Back-pressure on the worker
    /// thread dominates the read loop; correctness depends on the ring's
    /// producer/consumer hand-off and slot recycling.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_SingleLargeFile_RingBackPressure_RoundTrip(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        // Block size from the drive determines ring memory pressure
        //  (default ring is 16 slots wide).
        uint blockSize = fixture.Drive.DefaultBlockSize;
        long largeSize = blockSize * 64L + 12345L; // many ring-fills worth

        using var tree = new TempFileTree();
        tree.AddFile("huge_payload.bin", largeSize);

        BackupPackedAndSaveTOC(fixture, tree.Files, "Pipelined Large File");

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = 1;
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            var result = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Pipelined large-file restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(1);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Mix of one ring-saturating large file and many tiny files sharing
    /// neighbouring blocks. Validates that the ring transitions cleanly
    /// between sustained sequential streaming (large file) and rapid
    /// per-file BeginRead/EndRead cycles (tiny files) within a single set.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_MixedLargeAndTiny_RoundTrip(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.DefaultBlockSize;

        using var tree = new TempFileTree();
        for (int i = 0; i < 20; i++)
            tree.AddFile($"tiny_pre_{i:D2}.dat", 128 + i);
        tree.AddFile("ring_saturator.bin", blockSize * 48L + 99L);
        for (int i = 0; i < 20; i++)
            tree.AddFile($"tiny_post_{i:D2}.dat", 256 + i);

        BackupPackedAndSaveTOC(fixture, tree.Files, "Pipelined Mixed Large/Tiny");

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = 1;
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            var result = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Pipelined mixed restore failed for {profile}: " +
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

    /// <summary>
    /// Selective restore of *only* the large file from a set that also
    /// contains many small files placed both before and after it. Forces
    /// the reader to seek directly to a mid-set block, then sustain
    /// ring-saturating throughput from there.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Pipelined_SelectiveLargeFile_FromMixedSet_Succeeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.DefaultBlockSize;

        using var tree = new TempFileTree();
        for (int i = 0; i < 10; i++)
            tree.AddFile($"pad_pre_{i:D2}.dat", 512 + i);
        string largePath = tree.AddFile("only_this.bin", blockSize * 40L + 7L);
        for (int i = 0; i < 10; i++)
            tree.AddFile($"pad_post_{i:D2}.dat", 512 + i);

        BackupPackedAndSaveTOC(fixture, tree.Files, "Pipelined Selective Large");

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = 1;
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            var filter = new NameSetFilter([largePath]);
            var result = restoreAgent.RestoreFilesFromCurrentSet(
                filter, ignoreFailures: true, fileNotify: notifiable);

            Assert.True((bool)result,
                $"Pipelined selective large-file restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(1);

            FileComparer.AssertFilesMatch(tree.RootPath, [largePath],
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion
}
