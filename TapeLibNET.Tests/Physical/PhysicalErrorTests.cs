#if DEBUG
using TapeLibNET.Tests.Helpers;
using Xunit.Abstractions;

namespace TapeLibNET.Tests.Physical;

/// <summary>
/// Layer 4 — Error resilience tests on physical hardware.
/// <para>
/// Unlike virtual error tests (which simulate file-level failures in the agent),
/// these tests inject failures at the Win32 backend level via
/// <see cref="TapeDriveWin32Backend.SimulateIOFailures"/> and
/// <see cref="TapeDriveWin32Backend.SimulateTimeoutFailures"/>,
/// exercising the full error-handling stack from low-level I/O through to
/// agent retry/skip/abort logic.
/// </para>
/// </summary>
[Collection(PhysicalDriveCollectionDefinition.Name)]
[Trait("Category", "Physical")]
public class PhysicalErrorTests(PhysicalDriveFixtureWrapper wrapper, ITestOutputHelper output)
{
    private PhysicalTapeFixture Fixture => wrapper.Fixture!;

    /// <summary>Redirects trace output and returns the fixture (or skips).</summary>
    private PhysicalTapeFixture Init()
    {
        wrapper.SetOutput(output);
        Skip.If(wrapper.Fixture == null, "No physical tape drive available");
        Fixture.AssertHealthyOrSkip();
        return Fixture;
    }

    #region *** IO Error Simulation ***

    /// <summary>
    /// Backup with simulated I/O write failures in the backend.
    /// Every 2nd low-level Write() call returns ERROR_IO_DEVICE.
    /// The agent should skip failed files (via <see cref="FileFailedAction.Skip"/>)
    /// and complete the batch.
    /// </summary>
    [SkippableFact]
    public void Backup_SimulatedIOWriteFailure_SkipsFailedFiles()
    {
        _ = Init();

        const int fileCount = 6;

        // Reformat to start clean
        Assert.True(Fixture.RecoverAndReformat(), "Reformat failed");

        using var tree = new TempFileTree();
        tree.AddFiles("ioerr", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        Fixture.TOC.AddNewSetTOC(0, incremental: false);
        Fixture.TOC.CurrentSetTOC.Description = "IO Write Error Test";
        Fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        Fixture.TOC.CurrentSetTOC.BlockSize = Fixture.Drive.DefaultBlockSize;

        using var agent = Fixture.CreateBackupAgent();

        // Enable I/O failure simulation on the Win32 backend
        var win32Backend = (TapeDriveWin32Backend)Fixture.Drive.Backend;
        win32Backend.SimulateIOFailures.EveryNth = 4; // every 4th Write() call fails
        win32Backend.SimulateIOFailures.Enabled = true;

        try
        {
            bool success = agent.BackupFileListToCurrentSetAligned(
                newSet: true,
                tree.Files,
                ignoreFailures: true,
                fileNotify: notifiable);

            // With ignoreFailures=true, operation should complete
            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            output.WriteLine($"Files total={stats.FilesTotal}, succeeded={stats.FilesSucceeded}, " +
                $"failed={stats.FilesFailed}, skipped={stats.FilesSkipped}");

            // Some files should have failed due to I/O errors
            Assert.True(stats.FilesFailed > 0, "Expected some files to fail from simulated I/O errors");
            Assert.True(stats.FilesSucceeded > 0, "Expected some files to succeed despite I/O errors");
        }
        finally
        {
            win32Backend.SimulateIOFailures.Enabled = false;
            Fixture.RecoverDrive();
        }
    }

    /// <summary>
    /// Restore with simulated I/O read failures in the backend.
    /// First backs up files normally, then enables SimulateIOFailures for reading.
    /// The agent should handle failures per <see cref="FileFailedAction.Skip"/>.
    /// </summary>
    [SkippableFact]
    public void Restore_SimulatedIOReadFailure_SkipsFailedFiles()
    {
        _ = Init();

        const int fileCount = 6;

        // Reformat and backup clean
        Assert.True(Fixture.RecoverAndReformat(), "Reformat failed");

        using var tree = new TempFileTree();
        tree.AddFiles("iorestore", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        Fixture.BackupFiles(tree.Files, description: "IO Read Error Source");

        // Now restore with simulated read failures
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_PhysIORead_{Guid.NewGuid():N}");

        try
        {
            var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

            var win32Backend = (TapeDriveWin32Backend)Fixture.Drive.Backend;
            win32Backend.SimulateIOFailures.EveryNth = 3; // every 3rd Read() call fails
            win32Backend.SimulateIOFailures.Enabled = true;

            try
            {
                using var agent = Fixture.CreateRestoreAgent(restoreDir);
                bool success = agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, notifiable);

                notifiable.AssertStatsInvariant();
                var stats = notifiable.BatchEnds[^1].Stats;

                output.WriteLine($"Restore: total={stats.FilesTotal}, succeeded={stats.FilesSucceeded}, " +
                    $"failed={stats.FilesFailed}");

                // Some files should have failed from read errors
                Assert.True(stats.FilesFailed > 0, "Expected some files to fail from simulated I/O read errors");
            }
            finally
            {
                win32Backend.SimulateIOFailures.Enabled = false;
                Fixture.RecoverDrive();
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Timeout Simulation ***

    /// <summary>
    /// Simulates a timeout during tape positioning (via <see cref="TapeDriveWin32Backend.SimulateTimeoutFailures"/>).
    /// The PollForCompletion method returns WIN32_ERROR_WAIT_TIMEOUT immediately instead of
    /// performing the operation, testing how the stack handles timeout errors.
    /// </summary>
    [SkippableFact]
    public void Positioning_SimulatedTimeout_ReportsTimeoutError()
    {
        _ = Init();

        Assert.True(Fixture.RecoverAndReformat(), "Reformat failed");

        var win32Backend = (TapeDriveWin32Backend)Fixture.Drive.Backend;

        // Enable timeout simulation — every poll will "time out"
        win32Backend.SimulateTimeoutFailures.EveryNth = 1;
        win32Backend.SimulateTimeoutFailures.Enabled = true;

        try
        {
            // Attempt a positioning operation — should fail with timeout
            bool result = Fixture.Drive.Rewind();

            output.WriteLine($"Rewind result={result}, LastError=0x{Fixture.Drive.LastError:X8}");

            // The operation should fail since PollForCompletion immediately returns timeout
            Assert.False(result, "Rewind should fail when timeout is simulated");
        }
        finally
        {
            win32Backend.SimulateTimeoutFailures.Enabled = false;
            Fixture.RecoverDrive();
        }
    }

    /// <summary>
    /// Simulates a timeout during backup (write positioning fails with timeout).
    /// The backup should fail gracefully, and recovery should bring the drive back.
    /// </summary>
    [SkippableFact]
    public void Backup_SimulatedTimeout_FailsGracefully()
    {
        _ = Init();

        Assert.True(Fixture.RecoverAndReformat(), "Reformat failed");

        using var tree = new TempFileTree();
        tree.AddFiles("timeout", count: 4, minSize: 100, maxSize: 2 * 1024);

        Fixture.TOC.AddNewSetTOC(0, incremental: false);
        Fixture.TOC.CurrentSetTOC.Description = "Timeout Test";
        Fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        Fixture.TOC.CurrentSetTOC.BlockSize = Fixture.Drive.DefaultBlockSize;

        var win32Backend = (TapeDriveWin32Backend)Fixture.Drive.Backend;

        // Let the first positioning work, but fail the 2nd poll (during content writing)
        win32Backend.SimulateTimeoutFailures.EveryNth = 2;
        win32Backend.SimulateTimeoutFailures.Enabled = true;

        try
        {
            using var agent = Fixture.CreateBackupAgent();
            var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

            bool success = agent.BackupFileListToCurrentSetAligned(
                newSet: true,
                tree.Files,
                ignoreFailures: true,
                fileNotify: notifiable);

            output.WriteLine($"Backup result={success}");

            // The backup may fail entirely or partially — the key assertion is that
            //  we don't hang and we can recover
        }
        finally
        {
            win32Backend.SimulateTimeoutFailures.Enabled = false;
        }

        // The critical test: can we recover after timeout?
        Assert.True(Fixture.RecoverDrive(), "Drive should be recoverable after simulated timeout");
        Assert.True(Fixture.IsHealthy, "Fixture should be healthy after recovery");
    }

    #endregion

    #region *** Recovery After Errors ***

    /// <summary>
    /// After inducing I/O errors, verifies the full recovery → reformat → backup cycle works.
    /// This ensures errors don't leave the drive in an unrecoverable state.
    /// </summary>
    [SkippableFact]
    public void Recovery_AfterIOErrors_FullCycleWorks()
    {
        _ = Init();

        const int fileCount = 4;

        // First: induce I/O errors during backup
        Assert.True(Fixture.RecoverAndReformat(), "Initial reformat failed");

        using var tree = new TempFileTree();
        tree.AddFiles("recover", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var win32Backend = (TapeDriveWin32Backend)Fixture.Drive.Backend;
        win32Backend.SimulateIOFailures.EveryNth = 2;
        win32Backend.SimulateIOFailures.Enabled = true;

        Fixture.TOC.AddNewSetTOC(0, incremental: false);
        Fixture.TOC.CurrentSetTOC.Description = "Error Inducing Set";
        Fixture.TOC.CurrentSetTOC.BlockSize = Fixture.Drive.DefaultBlockSize;

        using (var agent = Fixture.CreateBackupAgent())
        {
            var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };
            // Backup with errors — we don't care about the result
            agent.BackupFileListToCurrentSetAligned(true, tree.Files, ignoreFailures: true, fileNotify: notifiable);
        }

        win32Backend.SimulateIOFailures.Enabled = false;

        // Now: recover, reformat, and do a clean backup
        Assert.True(Fixture.RecoverAndReformat(), "Recovery after errors should succeed");
        Assert.True(Fixture.IsHealthy, "Fixture should be healthy after recovery");

        // Clean backup should succeed fully
        var stats = Fixture.BackupFiles(tree.Files, description: "Clean After Recovery");

        Assert.Equal(fileCount, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
    }

    #endregion

    #region *** Helpers ***

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    #endregion
}
#endif
