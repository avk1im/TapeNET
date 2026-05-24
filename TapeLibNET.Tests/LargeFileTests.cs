using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Tests for files exceeding 2 GB and 4 GB to verify 64-bit counter correctness
/// throughout the backup -> restore -> verify pipeline.
/// <para>
/// Resource-intensive: multi-GB virtual memory (memory-mapped) and disk I/O.
/// Tagged with <c>[Trait("Category", "LargeFile")]</c> for selective execution.
/// </para>
/// <para>
/// <b>Exclude from routine runs:</b><br/>
/// CLI: <c>dotnet test --filter "Category!=LargeFile"</c><br/>
/// Visual Studio Test Explorer: filter by Trait | Category | LargeFile.
/// </para>
/// </summary>
[Trait("Category", "LargeFile")]
public class LargeFileTests
{
    #region *** Constants ***

    /// <summary>2 GB + 1 MB — just past <c>int.MaxValue</c> (2,147,483,647).</summary>
    private const long Size2GBPlus = 2L * 1024 * 1024 * 1024 + 1024 * 1024;

    /// <summary>4 GB + 1 MB — just past <c>uint.MaxValue</c> (4,294,967,295).</summary>
    private const long Size4GBPlus = 4L * 1024 * 1024 * 1024 + 1024 * 1024;

    #endregion

    #region *** Single Large File Tests ***

    /// <summary>
    /// Backs up and restores a file just over 2 GB, verifying that <c>long</c>
    /// byte counters and TOC file sizes survive the <c>int.MaxValue</c> boundary.
    /// </summary>
    [Fact]
    public void FileOver2GB_BackupRestoreCompare_RoundTrips()
    {
        using var tree = new TempFileTree();
        tree.AddSparseFile("large_2gb.dat", Size2GBPlus);

        // 3 GB tape capacity — enough for the file plus TOC overhead
        using var fixture = new VirtualTapeFixture(
            DriveProfile.Setmarks,
            contentCapacity: 3L * 1024 * 1024 * 1024,
            useMemoryMap: true);

        var notifiable = new TestNotifiable();
        var stats = fixture.BackupFiles(
            tree.Files,
            description: ">2 GB file",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(1);

        // BytesProcessed tracks cumulative logical bytes — should exceed int.MaxValue
        Assert.True(stats.BytesProcessed > int.MaxValue,
            $"BytesProcessed ({stats.BytesProcessed}) should exceed int.MaxValue " +
            $"after backing up a {Size2GBPlus}-byte file");

        // TOC must store the correct long file size
        fixture.LoadTOC();
        var setToc = fixture.TOC[fixture.TOC.Count];
        Assert.Single(setToc);
        Assert.Equal(Size2GBPlus, setToc[0].FileDescr.Length);

        // Restore and byte-for-byte comparison
        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(1);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Backs up and restores a file just over 4 GB, verifying that counters
    /// and TOC sizes survive the <c>uint.MaxValue</c> boundary.
    /// </summary>
    [Fact]
    public void FileOver4GB_BackupRestoreCompare_RoundTrips()
    {
        using var tree = new TempFileTree();
        tree.AddSparseFile("large_4gb.dat", Size4GBPlus);

        // 5 GB tape capacity
        using var fixture = new VirtualTapeFixture(
            DriveProfile.Setmarks,
            contentCapacity: 5L * 1024 * 1024 * 1024,
            useMemoryMap: true);

        var notifiable = new TestNotifiable();
        var stats = fixture.BackupFiles(
            tree.Files,
            description: ">4 GB file",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(1);

        // BytesProcessed should exceed uint.MaxValue
        Assert.True(stats.BytesProcessed > uint.MaxValue,
            $"BytesProcessed ({stats.BytesProcessed}) should exceed uint.MaxValue " +
            $"after backing up a {Size4GBPlus}-byte file");

        // TOC must store the correct long file size
        fixture.LoadTOC();
        var setToc = fixture.TOC[fixture.TOC.Count];
        Assert.Single(setToc);
        Assert.Equal(Size4GBPlus, setToc[0].FileDescr.Length);

        // Restore and byte-for-byte comparison
        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(1);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Multiple Large Files ***

    /// <summary>
    /// Backs up two files that individually exceed 2 GB, verifying cumulative
    /// byte counters cross the <c>uint.MaxValue</c> boundary and the TOC records
    /// correct sizes for both entries.
    /// </summary>
    [Fact]
    public void MultipleLargeFiles_CumulativeCounters_RoundTrips()
    {
        using var tree = new TempFileTree();
        tree.AddSparseFile("batch/file_a.dat", Size2GBPlus);
        tree.AddSparseFile("batch/file_b.dat", Size2GBPlus);

        // Two files × 2.1 GB ˜ 4.2 GB total ? 5 GB tape capacity
        using var fixture = new VirtualTapeFixture(
            DriveProfile.Setmarks,
            contentCapacity: 5L * 1024 * 1024 * 1024,
            useMemoryMap: true);

        var notifiable = new TestNotifiable();
        var stats = fixture.BackupFiles(
            tree.Files,
            description: "Multiple >2 GB files",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(2);

        // Cumulative BytesProcessed should exceed uint.MaxValue (2 × 2.1 GB ˜ 4.2 GB)
        Assert.True(stats.BytesProcessed > uint.MaxValue,
            $"BytesProcessed ({stats.BytesProcessed}) should exceed uint.MaxValue " +
            $"after backing up {Size2GBPlus * 2} bytes across 2 files");

        // TOC must store correct sizes for both files
        fixture.LoadTOC();
        var setToc = fixture.TOC[fixture.TOC.Count];
        Assert.Equal(2, setToc.Count);
        Assert.Equal(Size2GBPlus, setToc[0].FileDescr.Length);
        Assert.Equal(Size2GBPlus, setToc[1].FileDescr.Length);

        // Restore and byte-for-byte comparison of both files
        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(2);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Validate Agent on Large Files ***

    /// <summary>
    /// CRC-only validation of a file exceeding 2 GB — no disk writes, just
    /// verifies the tape data integrity via hash check.
    /// </summary>
    [Fact]
    public void Validate_FileOver2GB_PassesCrc()
    {
        using var tree = new TempFileTree();
        tree.AddSparseFile("validate_2gb.dat", Size2GBPlus);

        using var fixture = new VirtualTapeFixture(
            DriveProfile.Setmarks,
            contentCapacity: 3L * 1024 * 1024 * 1024,
            useMemoryMap: true);

        fixture.BackupFiles(
            tree.Files,
            description: ">2 GB validate",
            hashAlgorithm: TapeHashAlgorithm.Crc64);

        // CRC-only validation — no disk writes
        var notifiable = new TestNotifiable();
        using var validateAgent = fixture.CreateValidateAgent();
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        bool validated = validateAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);

        Assert.True(validated, "Validation failed on >2 GB file");
        notifiable.AssertAllSucceeded(1);
    }

    #endregion

    #region *** Helpers ***

    /// <summary>Creates a unique temporary restore directory.</summary>
    private static string CreateRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where the restore agent
    /// places files originally under <paramref name="originalRoot"/>.
    /// </summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

    /// <summary>
    /// Best-effort cleanup of a temporary restore directory.
    /// Resets read-only attributes before deletion.
    /// </summary>
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
