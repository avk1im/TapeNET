using TapeLibNET;
using TapeLibNET.Tests.Helpers;
using Xunit.Abstractions;

namespace TapeLibNET.Tests.Physical;

/// <summary>
/// Layer 3 — Physical Scenario Tests.
/// <para>
/// End-to-end scenarios that exercise the full agent pipeline
/// (<see cref="TapeFileBackupAgent"/> / <see cref="TapeFileRestoreAgentEx"/>)
/// through a physical <see cref="TapeDrive"/> with Win32 backend.
/// </para>
/// <para>
/// These tests catch integration issues between the backend and the agent
/// stack that Layer 2 conformance alone wouldn't surface: timing, buffering,
/// block alignment quirks on real hardware.
/// </para>
/// <para>
/// Non-TOC scenarios run in both filemarks modes via paired
/// <c>[SkippableFact]</c> methods delegating to shared <c>_Core</c> helpers.
/// (<c>[SkippableTheory]</c> is not available in Xunit.SkippableFact 1.x.)
/// </para>
/// </summary>
[Collection(PhysicalDriveCollectionDefinition.Name)]
[Trait("Category", "Physical")]
public class PhysicalScenarioTests(PhysicalDriveFixtureWrapper fixtureWrapper, ITestOutputHelper output)
{
    private readonly PhysicalDriveFixtureWrapper _fixtureWrapper = fixtureWrapper;
    private readonly ITestOutputHelper _output = output;

    #region *** Helpers ***

    /// <summary>
    /// Redirects trace output, checks fixture health, reformats the tape,
    /// and returns the ready fixture. Each scenario starts fresh.
    /// </summary>
    private PhysicalTapeFixture Init()
    {
        _fixtureWrapper.SetOutput(_output);
        var fixture = _fixtureWrapper.GetFixtureOrSkip();
        fixture.AssertHealthyOrSkip();

        Assert.True(fixture.RecoverAndReformat("Scenario Test Media"),
            "Failed to reformat tape for scenario test");

        return fixture;
    }

    /// <summary>Creates a unique temporary restore directory.</summary>
    private static string CreateRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_PhysRestore_{Guid.NewGuid():N}");

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where the restore agent
    /// places files originally under <paramref name="originalRoot"/>.
    /// The agent strips the drive root from each file's full path, so the comparison
    /// root must account for that offset.
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
        catch { /* best-effort */ }
    }

    #endregion

    #region *** (1) TOC Persistence ***

    /// <summary>
    /// TOC round-trip: Backup files → Save TOC → Close drive → Reopen drive →
    /// Restore TOC → Verify set count and file metadata.
    /// <para>
    /// Uses <see cref="TapeDrive.CloseDrive"/>/<see cref="TapeDrive.ReopenDrive"/>
    /// instead of UnloadMedia/ReloadMedia to avoid physically ejecting the tape
    /// (AIT-2 and other SCSI drives treat UnloadMedia as a physical eject).
    /// </para>
    /// </summary>
    [SkippableFact]
    public void TOCPersistence_CloseReopen_RoundTrips()
    {
        var fixture = Init();
        var drive = fixture.Drive;

        _output.WriteLine($"Testing: {fixture.DriveDescription}");

        // Create a small file set
        using var tree = new TempFileTree();
        tree.AddFiles("toc_test", count: 5, minSize: 100, maxSize: 4 * 1024);

        // Backup files
        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "TOC Persistence Set",
            hashAlgorithm: TapeHashAlgorithm.Crc64, notifiable: notifiable);
        notifiable.AssertAllSucceeded(tree.Files.Count);

        int setCountBefore = fixture.TOC.Count;
        _output.WriteLine($"Backed up {tree.Files.Count} files, TOC has {setCountBefore} set(s)");

        // Close the drive handle (resets driver state without physical eject)
        drive.CloseDrive();
        _output.WriteLine("Drive handle closed");

        // Reopen drive and reload media
        Assert.True(drive.ReopenDrive(fixture.DriveNumber), "ReopenDrive failed");
        Assert.True(drive.ReloadMedia(), "ReloadMedia after reopen failed");
        Assert.True(drive.PrepareMedia(), "PrepareMedia after reopen failed");
        _output.WriteLine("Drive reopened, media reloaded");

        // Restore TOC from tape
        fixture.LoadTOC();
        _output.WriteLine($"TOC restored: {fixture.TOC.Count} set(s)");

        // Verify set count matches
        Assert.Equal(setCountBefore, fixture.TOC.Count);

        // Verify file metadata in the restored set
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count; // point to last set
        var setTOC = fixture.TOC.CurrentSetTOC;
        Assert.Equal(tree.Files.Count, setTOC.Count);
        Assert.Equal("TOC Persistence Set", setTOC.Description);
        _output.WriteLine($"Set metadata verified: {setTOC.Count} files, description matches");

        _output.WriteLine("=== TOC PERSISTENCE PASSED ===");
    }

    #endregion

    #region *** (2) Single-Set Round-Trip ***

    /// <summary>
    /// Backup a mixed set of files → TOC round-trip → Restore all → Byte-for-byte comparison.
    /// </summary>
    [SkippableFact]
    public void SingleSet_RoundTrips()
    {
        var fixture = Init();

        _output.WriteLine($"Testing: {fixture.DriveDescription}");

        // Rich, mixed file set: various sizes from 0-byte to 256 KB plus edge cases
        using var tree = new TempFileTree();
        tree.AddFiles("docs", count: 3, minSize: 0, maxSize: 1024);
        tree.AddFiles("data", count: 3, minSize: 1024, maxSize: 64 * 1024);
        tree.AddFiles("large", count: 2, minSize: 64 * 1024, maxSize: 256 * 1024);
        tree.AddEdgeCases(fixture.Drive.BlockSize);

        _output.WriteLine($"File set: {tree.Files.Count} files, {tree.TotalSize:N0} bytes total");

        // Backup
        var backupNotifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Single Set",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: backupNotifiable);
        backupNotifiable.AssertAllSucceeded(tree.Files.Count);
        _output.WriteLine("Backup completed");

        // TOC round-trip
        fixture.SaveAndReloadTOC();
        _output.WriteLine("TOC round-trip completed");

        // Restore
        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = fixture.TOC.Count; // last set
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSetAligned(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(tree.Files.Count);
            _output.WriteLine("Restore completed");

            // Byte-for-byte comparison
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
            _output.WriteLine($"=== SINGLE-SET PASSED ===");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (3) Multi-Set: First Set ***

    /// <summary>
    /// Backup set A → Backup set B → Navigate to set A (first/oldest) → Restore → Verify.
    /// </summary>
    [SkippableFact]
    public void MultiSet_FirstSet_RoundTrips()
    {
        var fixture = Init();

        _output.WriteLine($"Testing: {fixture.DriveDescription}");

        // Set A: small text files
        using var treeA = new TempFileTree(seed: 100);
        treeA.AddFiles("setA", count: 4, minSize: 50, maxSize: 2 * 1024);

        // Set B: larger binary files
        using var treeB = new TempFileTree(seed: 200);
        treeB.AddFiles("setB", count: 3, minSize: 1024, maxSize: 32 * 1024);

        // Backup set A then set B
        var notifyA = new TestNotifiable();
        fixture.BackupFiles(treeA.Files, description: "Set A", notifiable: notifyA);
        notifyA.AssertAllSucceeded(treeA.Files.Count);
        _output.WriteLine($"Set A backed up: {treeA.Files.Count} files");

        var notifyB = new TestNotifiable();
        fixture.BackupFiles(treeB.Files, description: "Set B", notifiable: notifyB);
        notifyB.AssertAllSucceeded(treeB.Files.Count);
        _output.WriteLine($"Set B backed up: {treeB.Files.Count} files, TOC has {fixture.TOC.Count} set(s)");

        fixture.SaveAndReloadTOC();

        // Navigate to set A (first set = standard index 1) and restore
        fixture.TOC.CurrentSetIndex = 1;
        _output.WriteLine($"Navigating to set A (index 1): \"{fixture.TOC.CurrentSetTOC.Description}\"");

        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            bool restored = restoreAgent.RestoreAllFilesFromCurrentSetAligned(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore of Set A failed");
            restoreNotifiable.AssertAllSucceeded(treeA.Files.Count);

            FileComparer.AssertFilesMatch(treeA.RootPath, treeA.Files,
                RestoreEquivalentRoot(restoreDir, treeA.RootPath));
            _output.WriteLine($"=== MULTI-SET FIRST PASSED ===");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (4) Multi-Set: Latest Set ***

    /// <summary>
    /// Backup set A → Backup set B → Navigate to set B (latest) → Restore → Verify.
    /// </summary>
    [SkippableFact]
    public void MultiSet_LatestSet_RoundTrips()
    {
        var fixture = Init();

        _output.WriteLine($"Testing: {fixture.DriveDescription}");

        using var treeA = new TempFileTree(seed: 300);
        treeA.AddFiles("setA", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var treeB = new TempFileTree(seed: 400);
        treeB.AddFiles("setB", count: 4, minSize: 200, maxSize: 16 * 1024);
        treeB.AddEdgeCases(fixture.Drive.BlockSize);

        fixture.BackupFiles(treeA.Files, description: "Set A - Latest");
        fixture.BackupFiles(treeB.Files, description: "Set B - Latest");
        _output.WriteLine($"Two sets backed up, TOC has {fixture.TOC.Count} set(s)");

        fixture.SaveAndReloadTOC();

        // Navigate to latest set (index 0 = latest)
        fixture.TOC.CurrentSetIndex = 0;
        _output.WriteLine($"Navigating to latest set: \"{fixture.TOC.CurrentSetTOC.Description}\"");

        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            bool restored = restoreAgent.RestoreAllFilesFromCurrentSetAligned(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore of latest set failed");
            restoreNotifiable.AssertAllSucceeded(treeB.Files.Count);

            FileComparer.AssertFilesMatch(treeB.RootPath, treeB.Files,
                RestoreEquivalentRoot(restoreDir, treeB.RootPath));
            _output.WriteLine($"=== MULTI-SET LATEST PASSED ===");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (5) Multi-Set: Middle Set ***

    /// <summary>
    /// Backup A → B → C → Navigate to middle set B (index 2) → Restore → Verify.
    /// </summary>
    [SkippableFact]
    public void MultiSet_MiddleSet_RoundTrips()
    {
        var fixture = Init();

        _output.WriteLine($"Testing: {fixture.DriveDescription}");

        using var treeA = new TempFileTree(seed: 500);
        treeA.AddFiles("setA", count: 3, minSize: 100, maxSize: 2 * 1024);

        using var treeB = new TempFileTree(seed: 600);
        treeB.AddFiles("setB", count: 5, minSize: 200, maxSize: 8 * 1024);
        treeB.AddEdgeCases(fixture.Drive.BlockSize);

        using var treeC = new TempFileTree(seed: 700);
        treeC.AddFiles("setC", count: 3, minSize: 500, maxSize: 16 * 1024);

        // Backup three sets
        fixture.BackupFiles(treeA.Files, description: "Set A - First");
        _output.WriteLine($"Set A backed up: {treeA.Files.Count} files");

        fixture.BackupFiles(treeB.Files, description: "Set B - Middle");
        _output.WriteLine($"Set B backed up: {treeB.Files.Count} files");

        fixture.BackupFiles(treeC.Files, description: "Set C - Last");
        _output.WriteLine($"Set C backed up: {treeC.Files.Count} files, TOC has {fixture.TOC.Count} set(s)");

        fixture.SaveAndReloadTOC();
        Assert.Equal(3, fixture.TOC.Count);

        // Navigate to middle set B (standard index 2)
        fixture.TOC.CurrentSetIndex = 2;
        _output.WriteLine($"Navigating to middle set (index 2): \"{fixture.TOC.CurrentSetTOC.Description}\"");

        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            bool restored = restoreAgent.RestoreAllFilesFromCurrentSetAligned(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore of middle set B failed");
            restoreNotifiable.AssertAllSucceeded(treeB.Files.Count);

            FileComparer.AssertFilesMatch(treeB.RootPath, treeB.Files,
                RestoreEquivalentRoot(restoreDir, treeB.RootPath));
            _output.WriteLine($"=== MULTI-SET MIDDLE PASSED ===");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (6) Multi-Set: Random Files from Random Sets ***

    /// <summary>
    /// Backup A → B → C → Select random files from sets A and C (skip B) →
    /// Restore via <see cref="TapeFileRestoreBaseAgent.RestoreFilesFromCurrentSetDownAligned"/>
    /// with a pre-assembled <c>filesSelected</c> array → Verify only the selected
    /// files are present on disk.
    /// </summary>
    [SkippableFact]
    public void MultiSet_RandomFiles_RoundTrips()
    {
        var fixture = Init();

        _output.WriteLine($"Testing: {fixture.DriveDescription}");

        // Three sets with distinct seeds for unique file content
        using var treeA = new TempFileTree(seed: 800);
        treeA.AddFiles("setA", count: 6, minSize: 100, maxSize: 4 * 1024);

        using var treeB = new TempFileTree(seed: 900);
        treeB.AddFiles("setB", count: 4, minSize: 200, maxSize: 8 * 1024);

        using var treeC = new TempFileTree(seed: 1000);
        treeC.AddFiles("setC", count: 6, minSize: 100, maxSize: 4 * 1024);

        // Backup all three sets
        fixture.BackupFiles(treeA.Files, description: "Set A - Random",
            hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(treeB.Files, description: "Set B - Skipped",
            hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(treeC.Files, description: "Set C - Random",
            hashAlgorithm: TapeHashAlgorithm.Crc64);
        _output.WriteLine($"Three sets backed up, TOC has {fixture.TOC.Count} set(s)");

        fixture.SaveAndReloadTOC();
        Assert.Equal(3, fixture.TOC.Count);

        // Select specific files from sets A and C, skip B entirely.
        //  Set A = standard index 1 (oldest), B = 2, C = 3 (newest)
        //  filesSelected array convention: [0]=newest set, [1]=middle, [2]=oldest
        //  null = all files from set, empty list = skip set
        var setA_TOC = fixture.TOC[1]; // standard index 1 = oldest
        var setC_TOC = fixture.TOC[3]; // standard index 3 = newest

        // Pick files at indices 0, 2, 4 from each set (every other file)
        var selectedFromA = new List<TapeFileInfo> { setA_TOC[0], setA_TOC[2], setA_TOC[4] };
        var selectedFromC = new List<TapeFileInfo> { setC_TOC[0], setC_TOC[2], setC_TOC[4] };
        int totalSelected = selectedFromA.Count + selectedFromC.Count;

        _output.WriteLine($"Selected {selectedFromA.Count} files from Set A, " +
            $"skipping Set B, {selectedFromC.Count} files from Set C");

        // Build the filesSelected array: [0]=newest(C), [1]=middle(B=skip), [2]=oldest(A)
        var filesSelected = new List<TapeFileInfo>?[]
        {
            selectedFromC,   // [0] = newest set (C) — selected files
            [],              // [1] = middle set (B) — skip entirely
            selectedFromA,   // [2] = oldest set (A) — selected files
        };

        // Position to the newest selected set and restore
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count; // newest set (C)

        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            bool restored = restoreAgent.RestoreFilesFromCurrentSetDownAligned(
                filesSelected, ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Selective restore failed");
            restoreNotifiable.AssertAllSucceeded(totalSelected);
            _output.WriteLine($"Restored {totalSelected} files successfully");

            // Verify selected files from Set A
            string restoreRootA = RestoreEquivalentRoot(restoreDir, treeA.RootPath);
            foreach (int idx in new[] { 0, 2, 4 })
            {
                string originalFile = treeA.Files[idx];
                string relativePath = Path.GetRelativePath(treeA.RootPath, originalFile);
                string restoredFile = Path.Combine(restoreRootA, relativePath);
                Assert.True(File.Exists(restoredFile),
                    $"Expected restored file not found: {restoredFile}");
                FileComparer.AssertFilesMatch(treeA.RootPath, [originalFile], restoreRootA);
            }
            _output.WriteLine("Set A selected files verified");

            // Verify selected files from Set C
            string restoreRootC = RestoreEquivalentRoot(restoreDir, treeC.RootPath);
            foreach (int idx in new[] { 0, 2, 4 })
            {
                string originalFile = treeC.Files[idx];
                string relativePath = Path.GetRelativePath(treeC.RootPath, originalFile);
                string restoredFile = Path.Combine(restoreRootC, relativePath);
                Assert.True(File.Exists(restoredFile),
                    $"Expected restored file not found: {restoredFile}");
                FileComparer.AssertFilesMatch(treeC.RootPath, [originalFile], restoreRootC);
            }
            _output.WriteLine("Set C selected files verified");

            _output.WriteLine($"=== MULTI-SET RANDOM FILES PASSED ===");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (7) Validate Agent ***

    /// <summary>
    /// Backup files → CRC validation pass (no restore to disk).
    /// Exercises <see cref="TapeFileValidateAgent"/> on real hardware.
    /// </summary>
    [SkippableFact]
    public void Validate_CRC()
    {
        var fixture = Init();

        _output.WriteLine($"Testing: {fixture.DriveDescription}");

        using var tree = new TempFileTree();
        tree.AddFiles("validate", count: 5, minSize: 100, maxSize: 16 * 1024);

        fixture.BackupFiles(tree.Files, description: "Validate Set",
            hashAlgorithm: TapeHashAlgorithm.Crc64);

        fixture.SaveAndReloadTOC();

        // Validate (CRC-only, no disk writes)
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        using var validateAgent = fixture.CreateValidateAgent();
        var validateNotifiable = new TestNotifiable();

        bool valid = validateAgent.RestoreAllFilesFromCurrentSetAligned(
            ignoreFailures: true, fileNotify: validateNotifiable);

        Assert.True(valid, "CRC validation failed");
        validateNotifiable.AssertAllSucceeded(tree.Files.Count);
        _output.WriteLine($"=== CRC VALIDATION PASSED ===");
    }

    #endregion
}
