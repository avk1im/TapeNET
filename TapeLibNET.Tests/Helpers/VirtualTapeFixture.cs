using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET.Drive;
using TapeLibNET.Virtual;
using TapeLibNET.Compression;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using TapeLibNET.Agents;

namespace TapeLibNET.Tests.Helpers;


/// <summary>
/// Reusable test fixture that creates a virtual tape media object only (no drive).
/// </summary>
public sealed class VirtualMediaOnlyFixture : IDisposable
{
    //         var media = new VirtualTapeMedia(new MemoryStream(16384 * 2), ownsStream: true, capacity: 16384 * 2,
    // minBlockSize: 1024, maxBlockSize: 16384, defaultBlockSize: 16384);
    public VirtualTapeMedia Media { get; }
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Creates a disposable virtual tape media object for unit tests.
    /// </summary>
    /// <param name="blockSize">The block size of the virtual tape media.</param>
    /// <param name="contentCapacity">The capacity of the virtual tape media.</param>
    /// <param name="name">The name of the virtual tape media.</param>
    /// <param name="loggerFactory">The logger factory to use for logging.</param>
    public VirtualMediaOnlyFixture(
        uint blockSize,
        long contentCapacity,
        string? name = null,
        ILoggerFactory? loggerFactory = null)
    {
        LoggerFactory = loggerFactory ?? TestLoggerFactory.Default;

        Media = new VirtualTapeMedia(
            new MemoryStream((int)contentCapacity),
            minBlockSize: blockSize,
            maxBlockSize: blockSize,
            defaultBlockSize: blockSize,
            capacity: contentCapacity,
            ownsStream: true,
            name: name ?? $"Test Virtual Media",
            loggerFactory: LoggerFactory);
    }

    /// <summary>
    /// Creates a disposable virtual tape media object for unit tests.
    /// </summary>
    /// <param name="blockSize">The block size of the virtual tape media.</param>
    /// <param name="capacity">The capacity of the virtual tape media.</param>
    /// <returns>A new instance of <see cref="VirtualTapeMedia"/>.</returns>
    public static VirtualTapeMedia CreateTestMedia(uint blockSize, uint capacity) =>
        new VirtualMediaOnlyFixture(blockSize, capacity).Media;

    #region *** Dispose ***

    public void Dispose()
    {
        Media.Dispose();
    }

    #endregion

}

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

    /// <summary>Default content capacity: 128 MB — plenty for unit tests.</summary>
    public const long DefaultContentCapacity = 128L * 1024 * 1024;

    /// <summary>Default initiator partition capacity: 4 MB.</summary>
    public const long DefaultInitiatorCapacity = 4L * 1024 * 1024;

    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; }
    public TapeTOC TOC { get; private set; }
    public ILoggerFactory LoggerFactory { get; }
    public VirtualTapeDriveCapabilities Capabilities { get; }
    public VirtualTapeDriveBackend Backend { get; }

    /// <summary>
    /// The record emitter used by the fixture's writing agents. Make sure to apply to every writing
    ///  agent created by the fixture! Defaults to <see cref="TapeRecordEmitter21.Instance"/>.
    /// </summary>
    internal ITapeRecordEmitter RecordEmitter { get; set; }

    #endregion

    #region *** Media Header and Set Headers ***

    /// <summary>
    /// Whether the fixture's backup agents write a <see cref="TapeMediaHeader"/> to the tape at creation.
    /// </summary>
    public bool WithMediaHeader { get; init; }
    /// <summary>First block of the content area — past the media header block and its filemark.</summary>
    public long FirstContentBlock => WithMediaHeader ? HeaderBlocks : 0L;
    private const long HeaderBlocks = 2L;   // 1 block + 1 filemark COUNTED AS BLOCK by VirtualTapeMedia

    /// <summary>Whether the fixture's backup agents stamp a <see cref="TapeSetHeader"/> per set.</summary>
    public bool WithSetHeaders { get; init; }
    /// <summary>
    /// Block holding the first FILE of the first set — one further along when a set header is present.
    /// </summary>
    public long FirstFileBlock => FirstContentBlock + (WithSetHeaders ? 1 : 0);

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
    /// When <see langword="true"/>, uses <see cref="VirtualTapeDriveBackend.CreateMemoryMapBacked"/>
    /// (memory-mapped files) instead of <see cref="MemoryStream"/>-backed media.
    /// Required for content capacities exceeding 2 GB.
    /// </param>
    /// <param name="withMediaHeader">
    /// When <see langword="true"/>, writes a <see cref="TapeMediaHeader"/> to the tape.
    /// Set to <see langword="false"/> by default (no header).
    /// </param>
    /// <param name="withSetHeaders">
    /// When <see langword="true"/>, each set receives a <see cref="TapeSetHeader"/>. Requires
    ///  <paramref name="withMediaHeader"/> — a volume that carries no media header can declare no set
    ///  headers either (SH-1), so the combination is rejected rather than silently downgraded.
    /// </param>
    public VirtualTapeFixture(
        DriveProfile profile = DriveProfile.Setmarks,
        long contentCapacity = DefaultContentCapacity,
        ILoggerFactory? loggerFactory = null,
        string mediaDescription = "Test Media",
        bool useMemoryMap = false,
        bool withMediaHeader = false,
        bool withSetHeaders = false,
        ITapeRecordEmitter? recordEmitter = null)
    {
        if (withSetHeaders && !withMediaHeader) // cannot have set headers without a media header (SH-1)
            throw new ArgumentException("Set headers require a media header (SH-1)", nameof(withSetHeaders));
        WithMediaHeader = withMediaHeader;
        WithSetHeaders = withSetHeaders;   // BEFORE the header write below — CreateHeader reads it

        LoggerFactory = loggerFactory ?? TestLoggerFactory.Default;
        Capabilities = ProfileToCapabilities(profile);

        long initCap = Capabilities.SupportsInitiatorPartition
            ? DefaultInitiatorCapacity : 0;

        // Set before anything is written — the agent writing media header below
        //  must already use RecordEmitter
        RecordEmitter = recordEmitter ?? TapeRecordEmitter21.Instance;

        Backend = useMemoryMap
            ? VirtualTapeDriveBackend.CreateMemoryMapBacked(
                LoggerFactory, Capabilities, contentCapacity, initCap)
            : VirtualTapeDriveBackend.CreateMemoryBacked(
                LoggerFactory, Capabilities, contentCapacity, initCap);

        // Ensure IO throttling is off — tests should run at memory speed
        Backend.IoRate = VirtualTapeDriveIoRate.Unlimited;

        Drive = new TapeDrive(LoggerFactory, Backend);

        // Full lifecycle: open → load → prepare
        Assert.True(Drive.ReopenDrive(0), "Failed to open virtual drive");
        Assert.True(Drive.ReloadMedia(), "Failed to load virtual media");
        Assert.True(Drive.PrepareMedia(), "Failed to prepare virtual media");

        // Create initial TOC
        TOC = new TapeTOC(mediaDescription);
        
        // Write header if requested (Notice some tests want to start with a blank tape)
        if (withMediaHeader)
        {
            using var agent = new TapeAgentBase(Drive, TOC)
            {
                WritesSetHeaders = withSetHeaders,
                RecordEmitter = RecordEmitter,
            };
            Assert.True(agent.WriteMediaHeader(), "Fixture: WriteMediaHeader failed");
        }
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
        DriveProfile.FilemarksOnly => VirtualTapeDriveCapabilities.WithFilemarksOnly, // WithFilemarksOnlyLargeBlocks,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    #endregion

    #region *** Agent Factories ***

    /// <summary>Creates a backup agent bound to this fixture's drive and TOC.</summary>
    public TapeFileBackupAgent CreateBackupAgent()
    {
        return new TapeFileBackupAgent(Drive, TOC)
        {
            WritesMediaHeader = WithMediaHeader,
            WritesSetHeaders = WithSetHeaders,
            RecordEmitter = RecordEmitter,
        };
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
        using var agent = new TapeAgentBase(Drive, TOC)
            { RecordEmitter = RecordEmitter };
        Assert.True(agent.BackupTOC(), "Failed to save TOC to tape");
    }

    /// <summary>
    /// Reads the TOC from tape (via agent) and replaces the fixture's TOC.
    /// Asserts success.
    /// </summary>
    public void LoadTOC()
    {
        using var agent = new TapeAgentBase(Drive, TOC);
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
    /// <param name="compression">Software compression mode (default <see cref="TapeCompression.None"/>).</param>
    /// <param name="compressionLevel">ZSTD level; only used when <paramref name="compression"/> is <see cref="TapeCompression.Software"/>.</param>
    /// <param name="setId">
    /// Pins the new set's <see cref="TapeSetTOC.SetId"/> — for tests that compare the bytes of two independent backups.
    ///  <see langword="null"/> keeps the freshly minted id.
    /// </param>
    /// <returns>Statistics snapshot after backup completes.</returns>
    public TapeFileStatistics BackupFiles(
        List<string> fileList,
        string description = "Test Set",
        bool incremental = false,
        TapeHashAlgorithm hashAlgorithm = TapeHashAlgorithm.Crc64,
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null,
        TapeCompression compression = TapeCompression.None,
        int compressionLevel = ZstdLevel.Default,
        Guid? setId = null)
    {
        // Configure the set
        TOC.AddNewSetTOC(0, incremental);
        TOC.CurrentSetTOC.Description = description;
        TOC.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
        TOC.CurrentSetTOC.BlockSize = blockSize == 0 ? Drive.DefaultBlockSize : blockSize;
        TOC.CurrentSetTOC.Compression = compression;
        TOC.CurrentSetTOC.CompressionLevel = compressionLevel;

        if (setId is { } id)
            TOC.CurrentSetTOC.SetId = id;

        using var agent = CreateBackupAgent();

        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: true,
            fileNotify: notifiable);

        Assert.True(success, "Backup failed");

        // Save TOC after successful backup
        Assert.True(agent.BackupTOC(), "Failed to save TOC after backup");

        return agent.Statistics;
    }

    #endregion

    #region ***Debug Helpers ***

    /// <summary>
    /// Overwrites the setmark that closes the LAST set, so a backward setmark count runs one set too far.
    /// </summary>
    /// <remarks>
    /// Writing a block AT the setmark's position destroys it (a tape write truncates everything beyond),
    ///  which is e.g. exactly what a partial write during a power loss does. The TOC is deliberately left
    ///  describing the pre-damage tape, mirroring an operation that never reached its TOC write.
    /// </remarks>
    public void EraseLastSetmark()
    {
        using var agent = new TapeAgentBase(Drive, TOC);
        agent.EnsureMediaHeaderResolved();

        // End of content, then back over the setmark that closes the last set.
        Assert.True(agent.Navigator.MoveToEndOfContent(), "failed to reach end-of-content");
        Assert.True(agent.Navigator.MoveToNextContentSetmark(-1), "failed to step back over the last setmark");

        // Write here: the setmark is gone, and with it the anchor a backward count depends on.
        Assert.True(Drive.WriteGapFile(), "failed to overwrite the trailing setmark");

        agent.Navigator.ResetContentSet();   // nobody knows where anything is now — which is the point
    }

    #endregion

    #region *** Dispose ***

    public void Dispose()
    {
        Drive.Dispose();
    }

    #endregion
}
