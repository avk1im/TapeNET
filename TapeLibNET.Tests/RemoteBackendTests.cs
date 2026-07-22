using TapeLibNET.Remote;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Integration tests that exercise the full gRPC remote backend path:
/// <c>TapeDrive ? RemoteTapeDriveBackend ? gRPC ? TapeDriveGrpcService ? VirtualTapeDriveBackend</c>.
/// <para>
/// All tests share a single gRPC server via an <see cref="ITapeServiceFixture"/>
/// (collection fixture). Each test creates its own <see cref="RemoteVirtualTapeFixture"/>
/// for full isolation — the server replaces its backend on each <c>OpenVirtual</c> call.
/// </para>
/// These tests mirror a curated subset of <see cref="VirtualDriveBasicTests"/>,
/// <see cref="TapeBackupAgentTests"/>, and <see cref="TapeTOCRoundTripTests"/> to
/// verify that every operation round-trips correctly through the gRPC layer.
/// <para>
/// Concrete subclasses bind to a specific xUnit collection:
/// <see cref="LocalHostBackendTests"/> (in-process server) and
/// <see cref="RemoteHostBackendTests"/> (external server, skip-when-unconfigured).
/// </para>
/// </summary>
public abstract class RemoteBackendTestsBase(ITapeServiceFixture service)
{
    private readonly ITapeServiceFixture _service = service;

    /// <summary>
    /// Called at the start of each test. The <see cref="RemoteHostBackendTests"/> subclass
    /// overrides this to skip when no remote host is configured.
    /// </summary>
    protected virtual void EnsureServiceAvailable() { }

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

    #endregion

    // ========================================================================
    //  Drive Lifecycle & Capabilities
    // ========================================================================

    #region *** Lifecycle Tests ***

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void Drive_ReportsCorrectCapabilities(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var drive = fixture.Drive;
        var expected = VirtualTapeFixture.ProfileToCapabilities(profile);

        Assert.Equal(expected.MinBlockSize, drive.MinimumBlockSize);
        Assert.Equal(expected.MaxBlockSize, drive.MaximumBlockSize);
        Assert.Equal(expected.DefaultBlockSize, drive.DefaultBlockSize);
        Assert.Equal(expected.SupportsSetmarks, drive.SupportsSetmarks);
        Assert.Equal(expected.SupportsSeqFilemarks, drive.SupportsSeqFilemarks);
        Assert.Equal(expected.SupportsInitiatorPartition, drive.SupportsInitiatorPartition);
    }

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void Media_StartsAtPositionZero(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        Assert.Equal(0, fixture.Drive.BlockCounter);
    }

    [SkippableFact]
    public void Partitions_Drive_HasInitiatorPartition()
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, DriveProfile.Partitions);
        Assert.True(fixture.Drive.HasInitiatorPartition);
        Assert.Equal(2U, fixture.Drive.PartitionCount);
    }

    #endregion

    // ========================================================================
    //  Block Size
    // ========================================================================

    #region *** Block Size Tests ***

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_DefaultMatchesCapabilities(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        Assert.Equal(fixture.Capabilities.DefaultBlockSize, fixture.Drive.BlockSize);
    }

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_CanSetToMinimum(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        Assert.True(fixture.Drive.SetBlockSize(fixture.Capabilities.MinBlockSize));
        Assert.Equal(fixture.Capabilities.MinBlockSize, fixture.Drive.BlockSize);
    }

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_ZeroResetsToDefault(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        fixture.Drive.SetBlockSize(fixture.Capabilities.MinBlockSize);
        Assert.True(fixture.Drive.SetBlockSize(0));
        Assert.Equal(fixture.Capabilities.DefaultBlockSize, fixture.Drive.BlockSize);
    }

    #endregion

    // ========================================================================
    //  Positioning
    // ========================================================================

    #region *** Positioning Tests ***

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void Rewind_ResetsToBlockZero(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var drive = fixture.Drive;

        // Write some data to advance position
        var buffer = new byte[drive.BlockSize];
        drive.WriteDirect(buffer, 0, buffer.Length);
        Assert.True(drive.BlockCounter > 0, "Position should have advanced after write");

        Assert.True(drive.Rewind());
        Assert.Equal(0, drive.BlockCounter);
    }

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToBlock_SeeksCorrectly(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var drive = fixture.Drive;

        // Write several blocks to create addressable space
        var buffer = new byte[drive.BlockSize];
        for (int i = 0; i < 10; i++)
            drive.WriteDirect(buffer, 0, buffer.Length);

        Assert.True(drive.MoveToBlock(5));
        Assert.Equal(5, drive.BlockCounter);

        Assert.True(drive.MoveToBlock(0));
        Assert.Equal(0, drive.BlockCounter);
    }

    #endregion

    // ========================================================================
    //  Read/Write Round-Trips
    // ========================================================================

    #region *** Read/Write Tests ***

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void WriteAndRead_SingleBlock_RoundTrips(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        byte[] writeBuffer = new byte[blockSize];
        for (int i = 0; i < blockSize; i++)
            writeBuffer[i] = (byte)(i % 251);

        int written = drive.WriteDirect(writeBuffer, 0, blockSize);
        Assert.Equal(blockSize, written);

        Assert.True(drive.Rewind());

        byte[] readBuffer = new byte[blockSize];
        int read = drive.ReadDirect(readBuffer, 0, blockSize);
        Assert.Equal(blockSize, read);
        Assert.Equal(writeBuffer, readBuffer);
    }

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void WriteAndRead_MultipleBlocks_RoundTrips(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;
        int blockCount = 20;
        int totalSize = blockSize * blockCount;

        byte[] writeBuffer = new byte[totalSize];
        for (int i = 0; i < totalSize; i++)
            writeBuffer[i] = (byte)((i / blockSize * 37 + i % blockSize) % 256);

        int written = drive.WriteDirect(writeBuffer, 0, totalSize);
        Assert.Equal(totalSize, written);

        Assert.True(drive.Rewind());

        byte[] readBuffer = new byte[totalSize];
        int read = drive.ReadDirect(readBuffer, 0, totalSize);
        Assert.Equal(totalSize, read);
        Assert.Equal(writeBuffer, readBuffer);
    }

    #endregion

    // ========================================================================
    //  Filemarks & Setmarks
    // ========================================================================

    #region *** Filemark Tests ***

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void WriteFilemark_CanBeSpaced(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        byte[] block1 = new byte[blockSize];
        Array.Fill(block1, (byte)0xAA);
        drive.WriteDirect(block1, 0, blockSize);

        Assert.True(fixture.Backend.WriteFilemarks(1));

        byte[] block2 = new byte[blockSize];
        Array.Fill(block2, (byte)0xBB);
        drive.WriteDirect(block2, 0, blockSize);

        Assert.True(drive.Rewind());

        byte[] readBuf = new byte[blockSize];
        int read = drive.ReadDirect(readBuf, 0, blockSize);
        Assert.Equal(blockSize, read);
        Assert.Equal(block1, readBuf);

        Assert.True(fixture.Backend.SpaceFilemarks(1));

        read = drive.ReadDirect(readBuf, 0, blockSize);
        Assert.Equal(blockSize, read);
        Assert.Equal(block2, readBuf);
    }

    [SkippableFact]
    public void Setmarks_WriteAndSpace_Works()
    {
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, DriveProfile.Setmarks);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        byte[] block1 = new byte[blockSize];
        Array.Fill(block1, (byte)0x11);
        drive.WriteDirect(block1, 0, blockSize);

        Assert.True(fixture.Backend.WriteSetmarks(1));

        byte[] block2 = new byte[blockSize];
        Array.Fill(block2, (byte)0x22);
        drive.WriteDirect(block2, 0, blockSize);

        Assert.True(drive.Rewind());

        byte[] readBuf = new byte[blockSize];
        drive.ReadDirect(readBuf, 0, blockSize);
        Assert.Equal(block1, readBuf);

        Assert.True(fixture.Backend.SpaceSetmarks(1));

        drive.ReadDirect(readBuf, 0, blockSize);
        Assert.Equal(block2, readBuf);
    }

    #endregion

    // ========================================================================
    //  Drive Discovery (ProbeDrives)
    // ========================================================================

    #region *** ProbeDrives Tests ***

    [SkippableFact]
    public void ProbeDrives_DefaultMaxDrive_ReturnsListWithoutSession()
    {
        // ProbeDrives must work without any prior Open* call (sessionless).
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var drives = backend.ProbeDrives();

        // Result is always a valid list — empty when no physical drives are present.
        Assert.NotNull(drives);
    }

    [SkippableFact]
    public void ProbeDrives_MaxDriveZero_ProbesOnlyDriveZero()
    {
        // When maxDrive = 0, only drive 0 is probed — result has 0 or 1 entries.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var drives = backend.ProbeDrives(maxDrive: 0);

        Assert.NotNull(drives);
        Assert.True(drives.Count <= 1, "ProbeDrives(0) should return at most one drive");
        Assert.True(drives.All(d => d == 0), "ProbeDrives(0) should only report drive number 0");
    }

    [SkippableFact]
    public void ProbeDrives_AllResultsWithinRange()
    {
        // Every returned drive number must be <= maxDrive.
        EnsureServiceAvailable();
        const uint maxDrive = 9;
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var drives = backend.ProbeDrives(maxDrive);

        Assert.All(drives, d => Assert.True(d <= maxDrive,
            $"Drive number {d} exceeds maxDrive ({maxDrive})"));
    }

    [SkippableFact]
    public void ProbeDrives_ReturnsNoDuplicates()
    {
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var drives = backend.ProbeDrives();

        Assert.Equal(drives.Count, drives.Distinct().Count());
    }

    [SkippableFact]
    public void ProbeDrives_CalledTwice_ReturnsSameResult()
    {
        // The call is idempotent and safe to repeat.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var first  = backend.ProbeDrives();
        var second = backend.ProbeDrives();

        Assert.Equal(first, second);
    }

    [SkippableFact]
    public void ProbeDrives_DoesNotAffectSubsequentOpen()
    {
        // Calling ProbeDrives before Open must not corrupt the session state.
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, DriveProfile.Setmarks);

        // Probe using the same channel (but a separate, sessionless backend instance)
        using var probeBackend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _ = probeBackend.ProbeDrives();

        // The fixture's drive (opened before the probe) should still be operational
        var drive = fixture.Drive;
        var buffer = new byte[drive.BlockSize];
        int written = drive.WriteDirect(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, written);
    }

    #endregion

    // ========================================================================
    //  CreateTempVirtual
    // ========================================================================

    #region *** CreateTempVirtual Tests ***

    [SkippableFact]
    public void CreateTempVirtual_Unnamed_OpensAndIsWritable()
    {
        // An unnamed drive is memory-backed and must open successfully and accept writes.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = backend.CreateTempVirtual(capacityBytes: 50L * 1024 * 1024);
        Assert.True(ok, "CreateTempVirtual (unnamed) failed");
        Assert.True(backend.IsOpen);

        var drive = new TapeDrive(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, backend);
        Assert.True(drive.ReopenDrive(0));
        Assert.True(drive.ReloadMedia());
        Assert.True(drive.PrepareMedia());

        var buffer = new byte[drive.BlockSize];
        int written = drive.WriteDirect(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, written);
    }

    [SkippableFact]
    public void CreateTempVirtual_Named_OpensAndIsWritable()
    {
        // A named drive is file-backed in the server's temp folder and must also accept writes.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = backend.CreateTempVirtual(
            capacityBytes: 50L * 1024 * 1024,
            name: "test_named_drive");
        Assert.True(ok, "CreateTempVirtual (named) failed");
        Assert.True(backend.IsOpen);

        var drive = new TapeDrive(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, backend);
        Assert.True(drive.ReopenDrive(0));
        Assert.True(drive.ReloadMedia());
        Assert.True(drive.PrepareMedia());

        var buffer = new byte[drive.BlockSize];
        int written = drive.WriteDirect(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, written);
    }

    [SkippableFact]
    public void CreateTempVirtual_DefaultCapacity_Opens()
    {
        // Passing capacityBytes = 0 lets the server use its 500 MB default.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = backend.CreateTempVirtual(); // all defaults
        Assert.True(ok, "CreateTempVirtual with defaults failed");
        Assert.True(backend.IsOpen);

        // Capacity is reported after media load, not just after open
        var drive = new TapeDrive(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, backend);
        Assert.True(drive.ReopenDrive(0));
        Assert.True(drive.ReloadMedia());
        Assert.True(backend.Capacity > 0, "Capacity should be non-zero after media load");
    }

    [SkippableFact]
    public void CreateTempVirtual_DoesNotInterferWithConcurrentSession()
    {
        // Creating a temp drive on a new backend must not disturb an already-open session.
        EnsureServiceAvailable();
        using var existingFixture = new RemoteVirtualTapeFixture(_service.Channel, DriveProfile.Setmarks);

        using var tempBackend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        bool ok = tempBackend.CreateTempVirtual(capacityBytes: 10L * 1024 * 1024);
        Assert.True(ok, "CreateTempVirtual failed");

        // The existing fixture's drive should still respond correctly
        var drive = existingFixture.Drive;
        var buffer = new byte[drive.BlockSize];
        int written = drive.WriteDirect(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, written);
    }

    #endregion

    // ========================================================================
    //  GetServerInfo
    // ========================================================================

    #region *** GetServerInfo Tests ***

    [SkippableFact]
    public void GetServerInfo_ReturnsNonNullWithPopulatedFields()
    {
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var info = backend.GetServerInfo();

        Assert.NotNull(info);
        Assert.False(string.IsNullOrWhiteSpace(info.ServerVersion),  "ServerVersion should be non-empty");
        Assert.False(string.IsNullOrWhiteSpace(info.HostName),       "HostName should be non-empty");
        Assert.True(info.ProtocolLevel >= 1,                         "ProtocolLevel should be >= 1");
    }

    [SkippableFact]
    public void GetServerInfo_CallableBeforeOpen_NoSessionRequired()
    {
        // Must succeed on a brand-new backend that has never called Open*.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        Assert.False(backend.IsOpen, "Precondition: backend must not be open yet");

        var info = backend.GetServerInfo();
        Assert.NotNull(info);
    }

    [SkippableFact]
    public void GetServerInfo_CalledTwice_ReturnsSameResult()
    {
        // The call is stateless and idempotent.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var first  = backend.GetServerInfo();
        var second = backend.GetServerInfo();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.ServerVersion,  second.ServerVersion);
        Assert.Equal(first.ProtocolLevel,  second.ProtocolLevel);
        Assert.Equal(first.HostName,       second.HostName);
    }

    [SkippableFact]
    public void GetServerInfo_DoesNotAffectSubsequentOpen()
    {
        // Calling GetServerInfo before Open must not corrupt session state.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        _ = backend.GetServerInfo();

        // Now open a real virtual drive on the same backend
        var request = new TapeLibNET.Remote.OpenVirtualRequest
        {
            DriveNumber = 0,
            MemoryConfig = new TapeLibNET.Remote.VirtualMemoryConfig
            {
                ContentCapacity = 50L * 1024 * 1024,
            },
        };
        Assert.True(backend.OpenVirtual(request), "OpenVirtual should succeed after GetServerInfo");
        Assert.True(backend.IsOpen);
    }

    #endregion

    // ========================================================================
    //  Async API
    // ========================================================================

    #region *** Async API Tests ***

    [SkippableFact]
    public async Task OpenAsync_Win32Drive_OpensAndClosesCleanly()
    {
        // Win32 drives may not be present — a "not found" error from the server is fine;
        // we only verify the async path completes without deadlock and without throwing
        // on connection-level problems.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = await backend.OpenAsync(0).ConfigureAwait(false);
        // On a machine with no physical tape drive the server returns false; that is
        // acceptable — the goal is no hang / no exception.
        Assert.False(backend.IsOpen != ok,
            "IsOpen must match the return value of OpenAsync");
        if (ok)
            await backend.CloseAsync().ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task OpenVirtualAsync_MemoryBacked_OpensSuccessfully()
    {
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var request = new TapeLibNET.Remote.OpenVirtualRequest
        {
            DriveNumber = 0,
            MemoryConfig = new TapeLibNET.Remote.VirtualMemoryConfig
            {
                ContentCapacity = 20L * 1024 * 1024,
            },
        };

        bool ok = await backend.OpenVirtualAsync(request).ConfigureAwait(false);
        Assert.True(ok, "OpenVirtualAsync should succeed");
        Assert.True(backend.IsOpen);

        await backend.CloseAsync().ConfigureAwait(false);
        // Note: IsOpen reflects cached server state; the Close response carries no State,
        // so the cache is not updated — consistent with the sync Close behaviour.
    }

    [SkippableFact]
    public async Task CreateTempVirtualAsync_MemoryBacked_OpensSuccessfully()
    {
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = await backend.CreateTempVirtualAsync(capacityBytes: 20L * 1024 * 1024)
            .ConfigureAwait(false);
        Assert.True(ok, "CreateTempVirtualAsync (memory-backed) failed");
        Assert.True(backend.IsOpen);

        await backend.CloseAsync().ConfigureAwait(false);
        // Note: IsOpen reflects cached server state; the Close response carries no State.
    }

    [SkippableFact]
    public async Task CreateTempVirtualAsync_Named_OpensAndCanWriteData()
    {
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = await backend.CreateTempVirtualAsync(
                capacityBytes: 30L * 1024 * 1024,
                name: "async_named_drive")
            .ConfigureAwait(false);
        Assert.True(ok, "CreateTempVirtualAsync (named) failed");
        Assert.True(backend.IsOpen);

        var drive = new TapeDrive(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, backend);
        Assert.True(drive.ReopenDrive(0));
        Assert.True(drive.ReloadMedia());
        Assert.True(drive.PrepareMedia());

        // Write a block to prove the session is fully functional.
        var data = new byte[drive.BlockSize];
        new Random(42).NextBytes(data);
        int written = drive.WriteDirect(data, 0, data.Length);
        Assert.Equal(data.Length, written);

        await backend.CloseAsync().ConfigureAwait(false);
        // Note: IsOpen reflects cached server state; the Close response carries no State.
    }

    [SkippableFact]
    public async Task CloseAsync_WithCancellationToken_CompletesOrCancels()
    {
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = await backend.CreateTempVirtualAsync(capacityBytes: 10L * 1024 * 1024)
            .ConfigureAwait(false);
        Assert.True(ok, "Setup: CreateTempVirtualAsync failed");

        // A non-cancelled token should allow clean close without throwing.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await backend.CloseAsync(cts.Token).ConfigureAwait(false);
        // Note: IsOpen reflects cached server state; the Close response carries no State.
    }

    #endregion

    // ========================================================================
    //  Backup & Restore Round-Trip
    // ========================================================================

    #region *** Backup/Restore Tests ***

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void SingleFile_BackupAndRestore_RoundTrips(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var tree = new TempFileTree();
        tree.AddFile("remote_test.dat", 4096);

        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var stats = fixture.BackupFiles(tree.Files, description: "Remote Single File");

        Assert.Equal(1, stats.FilesSucceeded);
        Assert.True(stats.FileBytesProcessed > 0);

        // Restore to a separate directory and verify
        using var restoreDir = new TempFileTree();
        string targetDir = restoreDir.RootPath;

        fixture.LoadTOC();

        using var agent = fixture.CreateRestoreAgent(targetDir);
        bool restoreOk = agent.RestoreAllFilesFromCurrentSet();
        Assert.True(restoreOk, "Restore failed");

        // Verify restored file exists and matches
        // Files are restored relative to the drive root, not the temp folder name
        string pathRoot = Path.GetPathRoot(tree.RootPath)!;
        string restoredFile = Path.Combine(targetDir,
            Path.GetRelativePath(pathRoot, tree.Files[0]));
        Assert.True(File.Exists(restoredFile), $"Restored file not found: {restoredFile}");
        Assert.Equal(
            File.ReadAllBytes(tree.Files[0]),
            File.ReadAllBytes(restoredFile));
    }

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void MultipleFiles_BackupAndValidate_Succeeds(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var tree = new TempFileTree();
        tree.AddFile("file1.txt", 1024);
        tree.AddFile("file2.bin", 8192);
        tree.AddFile("subdir/file3.dat", 16384);

        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        var stats = fixture.BackupFiles(tree.Files, description: "Remote Multi File");

        Assert.Equal(3, stats.FilesSucceeded);

        // Validate via CRC (no disk writes)
        fixture.LoadTOC();

        using var validator = fixture.CreateValidateAgent();
        bool validateOk = validator.RestoreAllFilesFromCurrentSet();
        Assert.True(validateOk, "Validation failed");
    }

    #endregion

    // ========================================================================
    //  TOC Round-Trip
    // ========================================================================

    // ========================================================================
    //  ListSessionVolumes
    // ========================================================================

    #region *** ListSessionVolumes Tests ***

    [SkippableFact]
    public void ListSessionVolumes_MemoryBacked_ReturnsEmpty()
    {
        // An in-memory virtual drive is never catalogued as a named volume.
        EnsureServiceAvailable();
        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, DriveProfile.Setmarks);

        var volumes = fixture.Backend.ListSessionVolumes();

        Assert.Empty(volumes);
    }

    [SkippableFact]
    public void ListSessionVolumes_AfterCreateTempVirtual_Named_ReturnsSingleEntry()
    {
        // A named (file-backed) temp volume must appear in the catalog immediately after creation.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = backend.CreateTempVirtual(capacityBytes: 20L * 1024 * 1024, name: "vol_single");
        Assert.True(ok, "CreateTempVirtual failed");

        var volumes = backend.ListSessionVolumes();

        Assert.Single(volumes);
        Assert.Equal("vol_single", volumes[0].Name);
        Assert.True(volumes[0].IsCurrent, "The only volume should be current");
        Assert.False(volumes[0].Media.InMemory);
    }

    [SkippableFact]
    public void ListSessionVolumes_AfterCreateTempVirtual_Unnamed_ReturnsEmpty()
    {
        // An unnamed (memory-backed) CreateTempVirtual must NOT appear in the catalog.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = backend.CreateTempVirtual(capacityBytes: 20L * 1024 * 1024);
        Assert.True(ok, "CreateTempVirtual (unnamed) failed");

        var volumes = backend.ListSessionVolumes();

        Assert.Empty(volumes);
    }

    [SkippableFact]
    public async Task ListSessionVolumes_AfterInsertMedia_ReturnsMultipleEntries()
    {
        // After swapping to a second named volume via InsertMediaAsync the catalog
        // must contain both entries, and only the second must be flagged IsCurrent.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        // Volume 1 - file-backed temp drive
        bool ok1 = backend.CreateTempVirtual(capacityBytes: 10L * 1024 * 1024, name: "vol01");
        Assert.True(ok1, "CreateTempVirtual vol01 failed");

        // Write a block so BytesWritten is non-zero on vol01
        var drive = new TapeDrive(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, backend);
        Assert.True(drive.ReopenDrive(0));
        Assert.True(drive.ReloadMedia());
        Assert.True(drive.PrepareMedia());
        var block = new byte[drive.BlockSize];
        drive.WriteDirect(block, 0, block.Length);

        // Volume 2 - swap via InsertMediaAsync
        bool ok2 = await backend.InsertMediaAsync(
            contentFilePath: "vol02",
            contentCapacity: 10L * 1024 * 1024,
            mediaMode: System.IO.FileMode.Create);
        Assert.True(ok2, "InsertMediaAsync vol02 failed");

        var volumes = backend.ListSessionVolumes();

        Assert.Equal(2, volumes.Count);

        var v1 = volumes.First(v => v.Name == "vol01");
        var v2 = volumes.First(v => v.Name == "vol02");

        Assert.False(v1.IsCurrent, "vol01 should no longer be current after swap");
        Assert.True(v2.IsCurrent,  "vol02 should be current after InsertMedia");
        Assert.True(v1.BytesWritten > 0, "vol01 should have non-zero BytesWritten after write");
    }

    [SkippableFact]
    public async Task ListSessionVolumes_Async_MatchesSyncResult()
    {
        // The async and sync overloads must return identical data.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        bool ok = backend.CreateTempVirtual(capacityBytes: 10L * 1024 * 1024, name: "vol_async");
        Assert.True(ok, "CreateTempVirtual failed");

        var sync  = backend.ListSessionVolumes();
        var async_ = await backend.ListSessionVolumesAsync();

        Assert.Equal(sync.Count, async_.Count);
        Assert.Equal(sync[0].Name,      async_[0].Name);
        Assert.Equal(sync[0].IsCurrent, async_[0].IsCurrent);
    }

    [SkippableFact]
    public void ListSessionVolumes_TwoIndependentSessions_AreIsolated()
    {
        // Two concurrently open sessions must each report only their own volumes.
        EnsureServiceAvailable();
        using var backendA = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        using var backendB = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        Assert.True(backendA.CreateTempVirtual(capacityBytes: 10L * 1024 * 1024, name: "session_a_vol"),
            "CreateTempVirtual A failed");
        Assert.True(backendB.CreateTempVirtual(capacityBytes: 10L * 1024 * 1024, name: "session_b_vol"),
            "CreateTempVirtual B failed");

        var volsA = backendA.ListSessionVolumes();
        var volsB = backendB.ListSessionVolumes();

        Assert.Single(volsA);
        Assert.Equal("session_a_vol", volsA[0].Name);
        Assert.DoesNotContain(volsA, v => v.Name == "session_b_vol");

        Assert.Single(volsB);
        Assert.Equal("session_b_vol", volsB[0].Name);
        Assert.DoesNotContain(volsB, v => v.Name == "session_a_vol");
    }

    [SkippableFact]
    public void ListSessionVolumes_VolumeFields_ArePopulatedCorrectly()
    {
        // Volume metadata (CreatedUtc, BlockSize, media descriptor) must be populated.
        EnsureServiceAvailable();
        using var backend = new RemoteTapeDriveBackend(_service.Channel,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var before = DateTime.UtcNow.AddSeconds(-2);
        bool ok = backend.CreateTempVirtual(capacityBytes: 15L * 1024 * 1024, name: "vol_meta");
        Assert.True(ok, "CreateTempVirtual failed");
        var after = DateTime.UtcNow.AddSeconds(2);

        var volumes = backend.ListSessionVolumes();
        Assert.Single(volumes);

        var vol = volumes[0];
        Assert.Equal("vol_meta", vol.Name);
        Assert.InRange(vol.CreatedUtc, before, after);
        Assert.True(vol.BlockSize > 0, "BlockSize should be non-zero");
        Assert.False(string.IsNullOrEmpty(vol.Media.ContentPath),
            "ContentPath should be populated for a file-backed volume");
        Assert.True(vol.Media.ContentCapacity > 0, "ContentCapacity should be non-zero");
    }

    #endregion

    #region *** TOC Tests ***

    [SkippableTheory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_SaveAndReload_PreservesMetadata(DriveProfile profile)
    {
        EnsureServiceAvailable();
        using var tree = new TempFileTree();
        tree.AddFile("toc_test.dat", 2048);

        using var fixture = new RemoteVirtualTapeFixture(_service.Channel, profile);
        fixture.BackupFiles(tree.Files, description: "TOC Round-Trip Test");

        // Reload TOC from tape
        var reloaded = fixture.SaveAndReloadTOC();

        Assert.Equal(1, reloaded.Count);
        Assert.Equal("TOC Round-Trip Test", reloaded.CurrentSetTOC.Description);
        Assert.Single(reloaded.CurrentSetTOC);
    }

    #endregion
}

/// <summary>
/// Runs all <see cref="RemoteBackendTestsBase"/> tests against an in-process gRPC server
/// hosted by <see cref="LocalHostTapeServiceFixture"/>.
/// </summary>
[Collection(LocalHostTapeServiceCollection.Name)]
public class LocalHostBackendTests(LocalHostTapeServiceFixture service)
    : RemoteBackendTestsBase(service);

/// <summary>
/// Runs all <see cref="RemoteBackendTestsBase"/> tests against an external gRPC server
/// configured via <c>remote-test-settings.json</c> or <c>TAPE_REMOTE_*</c> environment
/// variables. All tests skip automatically when no remote host is configured.
/// <para>
/// Resource-intensive: requires external gRPC server configuration.
/// Excluded from routine runs via <c>FullyQualifiedName!~RemoteHostBackendTests</c> in
/// <c>TapeNET.runsettings</c> — trait-based filtering does not work here because test
/// methods are inherited from <see cref="RemoteBackendTestsBase"/>, and xUnit applies
/// traits from the declaring (base) class, not the concrete subclass.
/// </para>
/// <para>
/// <b>To run these tests explicitly:</b><br/>
/// CLI: <c>dotnet test --filter "FullyQualifiedName~RemoteHostBackendTests"</c><br/>
/// Visual Studio Test Explorer: disable or override <c>TapeNET.runsettings</c>.
/// </para>
/// </summary>
[Collection(RemoteHostTapeServiceCollection.Name)]
public class RemoteHostBackendTests(RemoteHostTapeServiceFixture service)
    : RemoteBackendTestsBase(service)
{
    protected override void EnsureServiceAvailable()
    {
        Skip.If(!service.IsConfigured, service.SkipReason);
    }
}

/// <summary>
/// Runs all <see cref="RemoteBackendTestsBase"/> tests against an in-process gRPC server
/// hosted by <see cref="LocalHostTlsTapeServiceFixture"/> over TLS / HTTPS.
/// <para>
/// Tests skip automatically when the TLS certificate is not configured or the HTTPS
/// connection cannot be established. Configure TLS via <c>remote-test-settings.json</c>:
/// </para>
/// <code>
/// {
///   "TlsCertPath":  "certs/tapesvc.pfx",
///   "TlsCertPassword": "YourPassword",
///   "DangerousAcceptAnyServerCertificate": true
/// }
/// </code>
/// </summary>
[Collection(LocalHostTlsTapeServiceCollection.Name)]
public class LocalHostTlsBackendTests(LocalHostTlsTapeServiceFixture service)
    : RemoteBackendTestsBase(service)
{
    protected override void EnsureServiceAvailable()
    {
        Skip.If(!service.IsConfigured, service.SkipReason);
    }
}

/// <summary>
/// Runs all <see cref="RemoteBackendTestsBase"/> tests against an external gRPC server
/// configured via <c>remote-test-settings.json</c> or <c>TAPE_REMOTE_*</c> environment
/// variables, using TLS / HTTPS on <c>RemoteTlsPort</c> (default: 50552).
/// <para>
/// Tests skip automatically when no remote host is configured, TLS settings are absent,
/// or the HTTPS endpoint is unreachable.
/// </para>
/// <para>
/// <b>To run these tests explicitly:</b><br/>
/// CLI: <c>dotnet test --filter "FullyQualifiedName~RemoteHostTlsBackendTests"</c><br/>
/// Visual Studio Test Explorer: disable or override <c>TapeNET.runsettings</c>.
/// </para>
/// </summary>
[Collection(RemoteHostTlsTapeServiceCollection.Name)]
public class RemoteHostTlsBackendTests(RemoteHostTlsTapeServiceFixture service)
    : RemoteBackendTestsBase(service)
{
    protected override void EnsureServiceAvailable()
    {
        Skip.If(!service.IsConfigured, service.SkipReason);
    }
}
