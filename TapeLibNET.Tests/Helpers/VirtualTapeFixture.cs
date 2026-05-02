using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TapeLibNET;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// The four real-world drive profiles we test against.
/// <list type="bullet">
///   <item><see cref="Setmarks"/> — basic drive with setmarks (like AIT or DAT).</item>
///   <item><see cref="Partitions"/> — setmarks + initiator partition (like AIT with TOC partition).</item>
///   <item><see cref="SeqFilemarks"/> — sequential filemarks (like SDLT).</item>
///   <item><see cref="FilemarksOnly"/> — filemarks only, no setmarks or sequential filemark counting (like LTO).</item>
/// </list>
/// </summary>
public enum DriveProfile
{
    /// <summary>Setmarks-capable drive (AIT/DAT-style). TOC stored in content partition.</summary>
    Setmarks,
    /// <summary>Setmarks + initiator partition (AIT-style). TOC stored in initiator partition.</summary>
    Partitions,
    /// <summary>Sequential-filemark drive (SDLT-style). TOC stored in content partition.</summary>
    SeqFilemarks,
    /// <summary>Filemarks-only drive (LTO-style). No setmarks, no sequential filemark counting.</summary>
    FilemarksOnly,
}

/// <summary>
/// Reusable test fixture that manages the full virtual tape drive lifecycle:
/// create → open → load → prepare → backup/restore agents → dispose.
/// <para>
/// Each test should create its own instance for full isolation — memory-backed
/// virtual drives are cheap to construct and tear down.
/// </para>
/// </summary>
public sealed class VirtualTapeFixture : IDisposable
{
    #region *** Constants ***

    /// <summary>Default content capacity: 200 MB — plenty for unit tests.</summary>
    public const long DefaultContentCapacity = 200L * 1024 * 1024;

    /// <summary>Default initiator partition capacity: 4 MB.</summary>
    public const long DefaultInitiatorCapacity = 4L * 1024 * 1024;

    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; }
    public TapeTOC TOC { get; private set; }
    public ILoggerFactory LoggerFactory { get; }
    public VirtualTapeDriveCapabilities Capabilities { get; }
    public VirtualTapeDriveBackend Backend { get; }

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Creates a fully ready fixture: memory-backed virtual drive opened, media loaded
    /// and prepared, TOC initialized.
    /// </summary>
    /// <param name="profile">Drive capability profile to emulate.</param>
    /// <param name="contentCapacity">Content partition capacity in bytes.</param>
    /// <param name="loggerFactory">Optional logger factory (defaults to <see cref="NullLoggerFactory"/>).</param>
    /// <param name="mediaDescription">Optional description for the initial TOC.</param>
    /// <param name="useMemoryMap">
    /// When <c>true</c>, uses <see cref="VirtualTapeDriveBackend.CreateMemoryMapBacked"/>
    /// (memory-mapped files) instead of <see cref="MemoryStream"/>-backed media.
    /// Required for content capacities exceeding 2 GB.
    /// </param>
    public VirtualTapeFixture(
        DriveProfile profile = DriveProfile.Setmarks,
        long contentCapacity = DefaultContentCapacity,
        ILoggerFactory? loggerFactory = null,
        string mediaDescription = "Test Media",
        bool useMemoryMap = false)
    {
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        Capabilities = ProfileToCapabilities(profile);

        long initCap = Capabilities.SupportsInitiatorPartition
            ? DefaultInitiatorCapacity : 0;

        Backend = useMemoryMap
            ? VirtualTapeDriveBackend.CreateMemoryMapBacked(
                LoggerFactory, Capabilities, contentCapacity, initCap)
            : VirtualTapeDriveBackend.CreateMemoryBacked(
                LoggerFactory, Capabilities, contentCapacity, initCap);

        // Ensure IO throttling is off — tests should run at memory speed
        Backend.IoRateBytesPerSecond = 0;
        Backend.LocateRateBytesPerSecond = 0;
        Backend.SearchRateBytesPerSecond = 0;

        Drive = new TapeDrive(LoggerFactory, Backend);

        // Full lifecycle: open → load → prepare
        Assert.True(Drive.ReopenDrive(0), "Failed to open virtual drive");
        Assert.True(Drive.ReloadMedia(), "Failed to load virtual media");
        Assert.True(Drive.PrepareMedia(), "Failed to prepare virtual media");

        // Create initial TOC
        TOC = new TapeTOC(mediaDescription);
    }

    /// <summary>
    /// Maps a <see cref="DriveProfile"/> to the corresponding
    /// <see cref="VirtualTapeDriveCapabilities"/> preset.
    /// </summary>
    public static VirtualTapeDriveCapabilities ProfileToCapabilities(DriveProfile profile) => profile switch
    {
        DriveProfile.Setmarks => VirtualTapeDriveCapabilities.WithSetmarks,
        DriveProfile.Partitions => VirtualTapeDriveCapabilities.WithPartitions,
        DriveProfile.SeqFilemarks => VirtualTapeDriveCapabilities.WithSeqFilemarks,
        DriveProfile.FilemarksOnly => VirtualTapeDriveCapabilities.WithFilemarksOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    #endregion

    #region *** Agent Factories ***

    /// <summary>Creates a backup agent bound to this fixture's drive and TOC.</summary>
    public TapeFileBackupAgent CreateBackupAgent()
    {
        return new TapeFileBackupAgent(Drive, TOC);
    }

    /// <summary>
    /// Creates an extended restore agent with target directory and existing-file handling.
    /// </summary>
    public TapeFileRestoreAgentEx CreateRestoreAgent(
        string targetDir,
        bool recurseSubdirs = true,
        TapeHowToHandleExisting handleExisting = TapeHowToHandleExisting.Overwrite)
    {
        return new TapeFileRestoreAgentEx(Drive, targetDir, recurseSubdirs, handleExisting, TOC);
    }

    /// <summary>Creates a CRC-only validation agent (no disk writes).</summary>
    public TapeFileValidateAgent CreateValidateAgent()
    {
        return new TapeFileValidateAgent(Drive, TOC);
    }

    /// <summary>Creates a byte-for-byte verify agent (compares tape against disk files).</summary>
    public TapeFileVerifyAgent CreateVerifyAgent()
    {
        return new TapeFileVerifyAgent(Drive, TOC);
    }

    #endregion

    #region *** TOC Helpers ***

    /// <summary>
    /// Writes the TOC to tape (via backup agent) and asserts success.
    /// </summary>
    public void SaveTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        Assert.True(agent.BackupTOC(enforce: true), "Failed to save TOC to tape");
    }

    /// <summary>
    /// Reads the TOC from tape (via agent) and replaces the fixture's TOC.
    /// Asserts success.
    /// </summary>
    public void LoadTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        Assert.True(agent.RestoreTOC(), "Failed to restore TOC from tape");
        // TOC is updated in-place by RestoreTOC via the agent's reference
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

    #region *** Backup Convenience ***

    /// <summary>
    /// Backs up a list of files as a new set with sensible defaults, saves the TOC,
    /// and returns the backup agent's final statistics snapshot.
    /// </summary>
    /// <param name="fileList">Full paths to back up.</param>
    /// <param name="description">Backup set description.</param>
    /// <param name="incremental">Whether the set is incremental.</param>
    /// <param name="hashAlgorithm">Hash algorithm for integrity checking.</param>
    /// <param name="blockSize">Block size (0 = drive default).</param>
    /// <param name="notifiable">Optional callback handler.</param>
    /// <returns>Statistics snapshot after backup completes.</returns>
    public TapeFileStatistics BackupFiles(
        List<string> fileList,
        string description = "Test Set",
        bool incremental = false,
        TapeHashAlgorithm hashAlgorithm = TapeHashAlgorithm.Crc64,
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null,
        bool useAligned = false)
    {
        // Configure the set
        TOC.AddNewSetTOC(0, incremental);
        TOC.CurrentSetTOC.Description = description;
        TOC.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
        TOC.CurrentSetTOC.BlockSize = blockSize == 0 ? Drive.DefaultBlockSize : blockSize;

        using var agent = CreateBackupAgent();

        /*
        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: true,
            fileNotify: notifiable);
        */
#pragma warning disable CS0618 // Type or member is obsolete // FIXME - transition period test
        bool success = useAligned
            ? agent.BackupFileListToCurrentSetAligned(
                newSet: true,
                fileList,
                ignoreFailures: true,
                fileNotify: notifiable)
            : agent.BackupFileListToCurrentSet(
                newSet: true,
                fileList,
                ignoreFailures: true,
                fileNotify: notifiable);
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.True(success, "Backup failed");

        // Save TOC after successful backup
        Assert.True(agent.BackupTOC(), "Failed to save TOC after backup");

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
