using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TapeLibNET.Remote;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Test fixture that mirrors <see cref="VirtualTapeFixture"/> but creates the
/// virtual tape drive on the remote gRPC service rather than in-process.
/// <para>
/// Uses <see cref="RemoteTapeDriveBackend.OpenVirtual"/> to instruct the server
/// to create a memory-backed virtual drive, then wraps it in a local
/// <see cref="TapeDrive"/> for the same API surface as the local fixture.
/// </para>
/// </summary>
public sealed class RemoteVirtualTapeFixture : IDisposable
{
    #region *** Constants ***

    /// <summary>Default content capacity: 200 MB — same as <see cref="VirtualTapeFixture"/>.</summary>
    public const long DefaultContentCapacity = 200L * 1024 * 1024;

    /// <summary>Default initiator partition capacity: 4 MB.</summary>
    public const long DefaultInitiatorCapacity = 4L * 1024 * 1024;

    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; }
    public TapeTOC TOC { get; private set; }
    public ILoggerFactory LoggerFactory { get; }
    public RemoteTapeDriveBackend Backend { get; }

    /// <summary>
    /// The capabilities that were requested for the remote virtual drive.
    /// Mirrors <see cref="VirtualTapeFixture.Capabilities"/> for assertion parity.
    /// </summary>
    public VirtualTapeDriveCapabilities Capabilities { get; }

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Creates a fully ready remote fixture: virtual drive created on the server,
    /// media loaded and prepared, TOC initialized.
    /// </summary>
    /// <param name="channel">gRPC channel from <see cref="RemoteTapeServiceFixture"/>.</param>
    /// <param name="profile">Drive capability profile to emulate.</param>
    /// <param name="contentCapacity">Content partition capacity in bytes.</param>
    /// <param name="loggerFactory">Optional logger factory (defaults to <see cref="NullLoggerFactory"/>).</param>
    /// <param name="mediaDescription">Optional description for the initial TOC.</param>
    public RemoteVirtualTapeFixture(
        GrpcChannel channel,
        DriveProfile profile = DriveProfile.Setmarks,
        long contentCapacity = DefaultContentCapacity,
        ILoggerFactory? loggerFactory = null,
        string mediaDescription = "Test Media")
    {
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        Capabilities = VirtualTapeFixture.ProfileToCapabilities(profile);

        long initCap = Capabilities.SupportsInitiatorPartition
            ? DefaultInitiatorCapacity : 0;

        // Create the remote backend from the shared channel (not owned)
        Backend = new RemoteTapeDriveBackend(channel, LoggerFactory);

        // Build the OpenVirtual request with memory-backed config
        var request = new OpenVirtualRequest
        {
            DriveNumber = 0,
            MemoryConfig = new VirtualMemoryConfig
            {
                Capabilities = new VirtualCapabilities
                {
                    MinBlockSize = Capabilities.MinBlockSize,
                    MaxBlockSize = Capabilities.MaxBlockSize,
                    DefaultBlockSize = Capabilities.DefaultBlockSize,
                    SupportsSetmarks = Capabilities.SupportsSetmarks,
                    SupportsSeqFilemarks = Capabilities.SupportsSeqFilemarks,
                    SupportsInitiatorPartition = Capabilities.SupportsInitiatorPartition,
                    SupportsCompression = Capabilities.SupportsCompression,
                },
                ContentCapacity = contentCapacity,
                InitiatorCapacity = initCap,
            },
        };

        Assert.True(Backend.OpenVirtual(request), "Failed to open remote virtual drive");

        Drive = new TapeDrive(LoggerFactory, Backend);

        // Full lifecycle: open → load → prepare
        // ReopenDrive detects the backend is already open (via OpenVirtual) and only refreshes caps.
        Assert.True(Drive.ReopenDrive(0), "Failed to initialize remote virtual drive");
        Assert.True(Drive.ReloadMedia(), "Failed to load remote virtual media");
        Assert.True(Drive.PrepareMedia(), "Failed to prepare remote virtual media");

        // Create initial TOC
        TOC = new TapeTOC(mediaDescription);
    }

    #endregion

    #region *** Agent Factories ***

    /// <summary>Creates a backup agent bound to this fixture's drive and TOC.</summary>
    public TapeFileBackupAgent CreateBackupAgent()
        => new(Drive, TOC);

    /// <summary>Creates an extended restore agent.</summary>
    public TapeFileRestoreAgentEx CreateRestoreAgent(
        string targetDir,
        bool recurseSubdirs = true,
        TapeHowToHandleExisting handleExisting = TapeHowToHandleExisting.Overwrite)
        => new(Drive, targetDir, recurseSubdirs, handleExisting, TOC);

    /// <summary>Creates a CRC-only validation agent (no disk writes).</summary>
    public TapeFileValidateAgent CreateValidateAgent()
        => new(Drive, TOC);

    /// <summary>Creates a byte-for-byte verify agent.</summary>
    public TapeFileVerifyAgent CreateVerifyAgent()
        => new(Drive, TOC);

    #endregion

    #region *** TOC Helpers ***

    /// <summary>Writes the TOC to tape and asserts success.</summary>
    public void SaveTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        Assert.True(agent.BackupTOC(enforce: true), "Failed to save TOC to tape");
    }

    /// <summary>Reads the TOC from tape and replaces the fixture's TOC.</summary>
    public void LoadTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        Assert.True(agent.RestoreTOC(), "Failed to restore TOC from tape");
        TOC = agent.TOC;
    }

    /// <summary>Full TOC round-trip: save → restore → return.</summary>
    public TapeTOC SaveAndReloadTOC()
    {
        SaveTOC();
        LoadTOC();
        return TOC;
    }

    #endregion

    #region *** Backup Convenience ***

    /// <summary>
    /// Backs up a list of files as a new set, saves the TOC, and returns statistics.
    /// Mirrors <see cref="VirtualTapeFixture.BackupFiles"/>.
    /// </summary>
    public TapeFileStatistics BackupFiles(
        List<string> fileList,
        string description = "Test Set",
        bool incremental = false,
        TapeHashAlgorithm hashAlgorithm = TapeHashAlgorithm.Crc64,
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null)
    {
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

        Assert.True(success, "Backup failed");
        Assert.True(agent.BackupTOC(), "Failed to save TOC after backup");

        return agent.Statistics;
    }

    #endregion

    #region *** Dispose ***

    public void Dispose()
    {
        // Close the remote drive; channel is not owned, so not disposed here
        Backend.Close();
        Drive.Dispose();
    }

    #endregion
}
