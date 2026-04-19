using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Integration tests that exercise the full gRPC remote backend path:
/// <c>TapeDrive → RemoteTapeDriveBackend → gRPC → TapeDriveGrpcService → VirtualTapeDriveBackend</c>.
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
        drive.WriteDirect(buffer, 0, buffer.Length, out _, out _);
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
            drive.WriteDirect(buffer, 0, buffer.Length, out _, out _);

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

        int written = drive.WriteDirect(writeBuffer, 0, blockSize, out _, out _);
        Assert.Equal(blockSize, written);

        Assert.True(drive.Rewind());

        byte[] readBuffer = new byte[blockSize];
        int read = drive.ReadDirect(readBuffer, 0, blockSize, out _, out _);
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

        int written = drive.WriteDirect(writeBuffer, 0, totalSize, out _, out _);
        Assert.Equal(totalSize, written);

        Assert.True(drive.Rewind());

        byte[] readBuffer = new byte[totalSize];
        int read = drive.ReadDirect(readBuffer, 0, totalSize, out _, out _);
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
        drive.WriteDirect(block1, 0, blockSize, out _, out _);

        Assert.True(fixture.Backend.WriteFilemarks(1));

        byte[] block2 = new byte[blockSize];
        Array.Fill(block2, (byte)0xBB);
        drive.WriteDirect(block2, 0, blockSize, out _, out _);

        Assert.True(drive.Rewind());

        byte[] readBuf = new byte[blockSize];
        int read = drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(blockSize, read);
        Assert.Equal(block1, readBuf);

        Assert.True(fixture.Backend.SpaceFilemarks(1));

        read = drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
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
        drive.WriteDirect(block1, 0, blockSize, out _, out _);

        Assert.True(fixture.Backend.WriteSetmarks(1));

        byte[] block2 = new byte[blockSize];
        Array.Fill(block2, (byte)0x22);
        drive.WriteDirect(block2, 0, blockSize, out _, out _);

        Assert.True(drive.Rewind());

        byte[] readBuf = new byte[blockSize];
        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(block1, readBuf);

        Assert.True(fixture.Backend.SpaceSetmarks(1));

        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(block2, readBuf);
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
        Assert.True(stats.BytesProcessed > 0);

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
