using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TapeLibNET;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Test fixture for multi-volume tape scenarios.
/// Manages virtual tape volume lifecycle with snapshot-based volume swapping:
/// backup across volume boundaries, restore from multiple volumes, and volume re-insertion.
/// <para>
/// Each test creates its own instance for isolation. The fixture uses memory-backed
/// virtual tapes with a deliberately small capacity to trigger volume boundaries
/// with a small number of files.
/// </para>
/// </summary>
public sealed class MultiVolumeVirtualTapeFixture : IDisposable
{
    #region *** Constants ***

    /// <summary>
    /// Default content capacity per volume — small enough to force multi-volume
    /// with a modest number of files, large enough for the TOC and a few files.
    /// 256 KB with 16 KB block size ≈ 16 blocks of content.
    /// </summary>
    public const long DefaultContentCapacity = 256L * 1024;

    /// <summary>Default initiator partition capacity for Partitions profile.</summary>
    public const long DefaultInitiatorCapacity = 64L * 1024;

    /// <summary>
    /// Default TOC capacity override for tests — small enough to keep
    /// <c>CapacityForCurrentSet</c> positive on small volumes so the library's
    /// software capacity check (<see cref="TapeStreamManager.CheckContentCapacity"/>)
    /// triggers volume transitions before the tape physically fills.
    /// 32 KB ≈ 2 blocks at 16 KB block size, plenty for the test TOC.
    /// </summary>
    public const long DefaultTOCCapacity = 32L * 1024;

    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; }
    public TapeTOC TOC { get; private set; }
    public ILoggerFactory LoggerFactory { get; }
    public VirtualTapeDriveCapabilities Capabilities { get; }
    public VirtualTapeDriveBackend Backend { get; }

    /// <summary>Current volume number (1-based), mirrors <see cref="TapeTOC.Volume"/>.</summary>
    public int CurrentVolume => TOC.Volume;

    /// <summary>Number of volume snapshots saved (equals swaps performed).</summary>
    public int VolumeCount => _volumeSnapshots.Count;

    /// <summary>Total volumes used (snapshots + current loaded volume).</summary>
    public int TotalVolumes => _volumeSnapshots.Count + (Backend.ContentMedia != null ? 1 : 0);

    /// <summary>Content capacity per volume in bytes.</summary>
    public long ContentCapacity { get; }

    /// <summary>
    /// TOC capacity override applied to each agent's navigator.
    /// Defaults to <see cref="DefaultTOCCapacity"/> (32 KB) so the library's
    /// software capacity check works correctly on small test volumes.
    /// </summary>
    public long TOCCapacityOverride { get; set; } = DefaultTOCCapacity;

    #endregion

    #region *** Volume Snapshot Management ***

    /// <summary>
    /// Saved volume snapshots keyed by 1-based volume number.
    /// Used to re-insert previously used volumes for restore operations.
    /// </summary>
    private readonly Dictionary<int, VirtualTapeDriveBackend.MemoryMediaSnapshot> _volumeSnapshots = [];

    /// <summary>
    /// Captures the current volume's state and stores it in the snapshot dictionary.
    /// Must be called while media is loaded (before <see cref="SwapToNewVolume"/>
    /// or <see cref="SwapToVolume"/>).
    /// </summary>
    public void SaveCurrentVolumeSnapshot()
    {
        var snapshot = Backend.CaptureMemorySnapshot();
        Assert.NotNull(snapshot);
        _volumeSnapshots[CurrentVolume] = snapshot;
    }

    /// <summary>
    /// Saves the current volume snapshot, ejects it, and inserts a fresh
    /// blank volume with the next volume number. Prepares the drive for writing.
    /// </summary>
    /// <returns>The new volume number.</returns>
    public int SwapToNewVolume()
    {
        SaveCurrentVolumeSnapshot();

        int newVolume = _volumeSnapshots.Count + 1;

        long initCap = Capabilities.SupportsInitiatorPartition
            ? DefaultInitiatorCapacity : 0;

        Backend.InsertMemoryMedia(ContentCapacity, initCap);
        Assert.True(Drive.ReloadMedia(), $"Failed to load new volume #{newVolume}");
        Assert.True(Drive.PrepareMedia(), $"Failed to prepare new volume #{newVolume}");

        return newVolume;
    }

    /// <summary>
    /// Saves the current volume snapshot (if loaded), ejects it, and re-inserts
    /// a previously saved volume by number.
    /// </summary>
    /// <param name="volumeNumber">1-based volume number to re-insert.</param>
    public void SwapToVolume(int volumeNumber)
    {
        // Save current state if media is still loaded
        var currentSnapshot = Backend.CaptureMemorySnapshot();
        if (currentSnapshot != null)
            _volumeSnapshots[CurrentVolume] = currentSnapshot;

        Assert.True(_volumeSnapshots.ContainsKey(volumeNumber),
            $"No snapshot for volume #{volumeNumber}");

        Backend.InsertMemoryMedia(_volumeSnapshots[volumeNumber]);
        Assert.True(Drive.ReloadMedia(), $"Failed to reload volume #{volumeNumber}");
        Assert.True(Drive.PrepareMedia(), $"Failed to prepare volume #{volumeNumber}");
    }

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Creates a multi-volume fixture with a small content capacity to trigger
    /// volume boundaries. The first volume is already loaded and prepared.
    /// </summary>
    /// <param name="profile">Drive capability profile to emulate.</param>
    /// <param name="contentCapacity">Content partition capacity per volume.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="mediaDescription">Description for the initial TOC.</param>
    public MultiVolumeVirtualTapeFixture(
        DriveProfile profile = DriveProfile.Setmarks,
        long contentCapacity = DefaultContentCapacity,
        ILoggerFactory? loggerFactory = null,
        string mediaDescription = "Multi-Volume Test Media")
    {
        ContentCapacity = contentCapacity;
        LoggerFactory = loggerFactory ?? TestLoggerFactory.Default;
        Capabilities = VirtualTapeFixture.ProfileToCapabilities(profile);

        long initCap = Capabilities.SupportsInitiatorPartition
            ? DefaultInitiatorCapacity : 0;

        Backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            LoggerFactory, Capabilities, contentCapacity, initCap);

        // Disable IO throttling for test speed
        Backend.IoRate = VirtualTapeDriveIoRate.Unlimited;

        Drive = new TapeDrive(LoggerFactory, Backend);

        // Full lifecycle: open → load → prepare
        Assert.True(Drive.ReopenDrive(0), "Failed to open virtual drive");
        Assert.True(Drive.ReloadMedia(), "Failed to load virtual media");
        Assert.True(Drive.PrepareMedia(), "Failed to prepare virtual media");

        // Create initial TOC — volume 1
        TOC = new TapeTOC(mediaDescription);
    }

    #endregion

    #region *** Agent Factories ***

    /// <summary>Creates a backup agent bound to this fixture's drive and TOC.</summary>
    public TapeFileBackupAgent CreateBackupAgent()
    {
        var agent = new TapeFileBackupAgent(Drive, TOC);
        agent.Navigator.TOCCapacity = TOCCapacityOverride;
        return agent;
    }

    /// <summary>Creates a restore agent with target directory.</summary>
    public TapeFileRestoreAgentEx CreateRestoreAgent(
        string targetDir,
        bool recurseSubdirs = true,
        TapeHowToHandleExisting handleExisting = TapeHowToHandleExisting.Overwrite)
    {
        var agent = new TapeFileRestoreAgentEx(Drive, targetDir, recurseSubdirs, handleExisting, TOC);
        agent.Navigator.TOCCapacity = TOCCapacityOverride;
        return agent;
    }

    #endregion

    #region *** TOC Helpers ***

    /// <summary>
    /// Writes the TOC to tape (via agent) and asserts success.
    /// </summary>
    public void SaveTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        agent.Navigator.TOCCapacity = TOCCapacityOverride;
        if (!agent.BackupTOC())
            Assert.True(agent.BackupTOC(enforce: true), "Failed to save TOC to tape (even with enforce)");
    }

    /// <summary>
    /// Reads the TOC from tape (via agent) and replaces the fixture's TOC.
    /// </summary>
    public void LoadTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        agent.Navigator.TOCCapacity = TOCCapacityOverride;
        Assert.True(agent.RestoreTOC(), "Failed to restore TOC from tape");
        TOC = agent.TOC;
    }

    /// <summary>
    /// Performs a full TOC round-trip: save → restore → return the loaded TOC.
    /// </summary>
    public TapeTOC SaveAndReloadTOC()
    {
        SaveTOC();
        LoadTOC();
        return TOC;
    }

    #endregion

    #region *** Multi-Volume Backup ***

    /// <summary>
    /// Backs up files with automatic multi-volume handling.
    /// When the current volume fills up, the method saves the TOC, swaps to a new
    /// volume, and resumes the backup — mirroring the real-world console app flow.
    /// </summary>
    /// <param name="fileList">Full paths to back up.</param>
    /// <param name="description">Backup set description.</param>
    /// <param name="incremental">Whether the set is incremental.</param>
    /// <param name="hashAlgorithm">Hash algorithm for integrity checking.</param>
    /// <param name="blockSize">Block size (0 = drive default).</param>
    /// <param name="notifiable">Optional callback handler.</param>
    /// <returns>Statistics snapshot after the entire backup completes (across all volumes).</returns>
    public TapeFileStatistics BackupFiles(
        List<string> fileList,
        string description = "Test Set",
        bool incremental = false,
        TapeHashAlgorithm hashAlgorithm = TapeHashAlgorithm.Crc64,
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null)
    {
        // Configure the set
        TOC.AddNewSetTOC(0, incremental);
        TOC.CurrentSetTOC.Description = description;
        TOC.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
        TOC.CurrentSetTOC.BlockSize = blockSize == 0 ? Drive.DefaultBlockSize : blockSize;

        using var agent = CreateBackupAgent();

        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: true,
            fileNotify: notifiable);

        // Multi-volume loop: backup → TOC → swap → resume
        while (!success && agent.CanResumeToNextVolume)
        {
            // If the empty continuation set has no files, clean it up
            if (TOC.CurrentSetTOC.Count == 0)
                TOC.RemoveLastEmptySet();

            // Save TOC on the current volume before swapping.
            //  Try normal first, then enforce (mirrors console app flow).
            if (!agent.BackupTOC())
                Assert.True(agent.BackupTOC(enforce: true),
                    "Failed to save TOC before volume swap (even with enforce)");

            // Swap to a fresh volume
            SwapToNewVolume();

            // Resume backup on the new volume
            success = agent.ResumeBackupToNextVolume();
        }

        Assert.True(success, "Multi-volume backup failed");

        // Save TOC on the final volume
        if (!agent.BackupTOC())
            Assert.True(agent.BackupTOC(enforce: true),
                "Failed to save TOC after backup (even with enforce)");

        return agent.Statistics;
    }

    #endregion

    #region *** Multi-Volume Restore ***

    /// <summary>
    /// Restores all files from the current set (non-incremental) with automatic
    /// multi-volume handling. When the restore agent needs a different volume,
    /// the method swaps to it and resumes.
    /// </summary>
    /// <param name="restoreDir">Target directory for restored files.</param>
    /// <param name="notifiable">Optional callback handler.</param>
    /// <returns>Statistics snapshot after the entire restore completes.</returns>
    public TapeFileStatistics RestoreAllFilesFromCurrentSet(
        string restoreDir,
        ITapeFileNotifiable? notifiable = null)
    {
        using var agent = CreateRestoreAgent(restoreDir);

        bool success = agent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);

        // Multi-volume loop
        while (!success && agent.CanResumeFromAnotherVolume)
        {
            int volumeNeeded = agent.VolumeToResumeFrom;
            SwapToVolume(volumeNeeded);
            success = agent.ResumeRestoreFromAnotherVolume();
        }

        Assert.True(success, "Multi-volume restore failed");

        return agent.Statistics;
    }

    /// <summary>
    /// Restores files from the current set's incremental chain with automatic
    /// multi-volume handling.
    /// </summary>
    /// <param name="restoreDir">Target directory for restored files.</param>
    /// <param name="notifiable">Optional callback handler.</param>
    /// <returns>Statistics snapshot after the entire restore completes.</returns>
    public TapeFileStatistics RestoreFilesFromCurrentSetInc(
        string restoreDir,
        ITapeFileNotifiable? notifiable = null)
    {
        using var agent = CreateRestoreAgent(restoreDir);

        bool success = agent.RestoreFilesFromCurrentSetInc(
            null, ignoreFailures: true, fileNotify: notifiable);

        // Multi-volume loop
        while (!success && agent.CanResumeFromAnotherVolume)
        {
            int volumeNeeded = agent.VolumeToResumeFrom;
            SwapToVolume(volumeNeeded);
            success = agent.ResumeRestoreFromAnotherVolume();
        }

        Assert.True(success, "Multi-volume incremental restore failed");

        return agent.Statistics;
    }

    #endregion

    #region *** Dispose ***

    public void Dispose()
    {
        Drive.Dispose();
    }

    #endregion
}
