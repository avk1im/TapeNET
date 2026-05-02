using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Tests the virtual tape drive stack itself — lifecycle, capabilities,
/// media operations, and basic data round-trips through the virtual media.
/// <para>
/// These tests exercise the <see cref="VirtualTapeDriveBackend"/> /
/// <see cref="VirtualTapeMedia"/> layers before any backup agent involvement,
/// validating that the emulation faithfully models the expected drive semantics.
/// </para>
/// </summary>
public class VirtualDriveBasicTests
{
    #region *** Drive Profiles ***

    /// <summary>
    /// Provides the three real-world drive profiles as <c>[Theory]</c> data.
    /// </summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    /// <summary>
    /// Profiles that can save/restore TOC on an empty tape (no prior content).
    /// SeqFilemarks excluded: its navigator (<c>TapeNavigatorTOCInSetWithFmksAndTOCMark</c>)
    ///  requires existing TOC markers on tape, which only exist after content has been written.
    ///  This matches real SDLT hardware behavior.
    /// </summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> ProfilesWithTOCOnEmptyTape =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    #endregion

    #region *** Lifecycle Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OpenAndClose_Succeeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        // Fixture constructor already asserts open/load/prepare — just verify state
        Assert.True(fixture.Drive.IsDriveOpen);
        Assert.True(fixture.Drive.IsMediaLoaded);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Drive_ReportsCorrectCapabilities(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        var expected = VirtualTapeFixture.ProfileToCapabilities(profile);

        Assert.Equal(expected.MinBlockSize, drive.MinimumBlockSize);
        Assert.Equal(expected.MaxBlockSize, drive.MaximumBlockSize);
        Assert.Equal(expected.DefaultBlockSize, drive.DefaultBlockSize);
        Assert.Equal(expected.SupportsSetmarks, drive.SupportsSetmarks);
        Assert.Equal(expected.SupportsSeqFilemarks, drive.SupportsSeqFilemarks);
        Assert.Equal(expected.SupportsInitiatorPartition, drive.SupportsInitiatorPartition);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Drive_HasCorrectDeviceName(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        Assert.StartsWith("VTAPE", fixture.Drive.DriveDeviceName);
    }

    #endregion

    #region *** Media State Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Media_ReportsCorrectCapacity(DriveProfile profile)
    {
        const long capacity = 100L * 1024 * 1024; // 100 MB
        using var fixture = new VirtualTapeFixture(profile, contentCapacity: capacity);

        // ContentCapacity should match what was requested
        Assert.Equal(capacity, fixture.Drive.ContentCapacity);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Media_StartsAtPositionZero(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        // Position should be at the beginning after load
        long block = fixture.Drive.BlockCounter;
        Assert.Equal(0, block);
    }

    [Fact]
    public void Partitions_Drive_HasInitiatorPartition()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        Assert.True(fixture.Drive.HasInitiatorPartition);
        Assert.Equal(2U, fixture.Drive.PartitionCount);
    }

    [Theory]
    [InlineData(DriveProfile.Setmarks)]
    [InlineData(DriveProfile.SeqFilemarks)]
    [InlineData(DriveProfile.FilemarksOnly)]
    public void NonPartitions_Drive_HasNoInitiatorPartition(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        Assert.False(fixture.Drive.HasInitiatorPartition);
        Assert.Equal(1U, fixture.Drive.PartitionCount);
    }

    #endregion

    #region *** Block Size Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_DefaultMatchesCapabilities(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        Assert.Equal(fixture.Capabilities.DefaultBlockSize, fixture.Drive.BlockSize);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_CanSetToMinimum(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        Assert.True(fixture.Drive.SetBlockSize(fixture.Capabilities.MinBlockSize));
        Assert.Equal(fixture.Capabilities.MinBlockSize, fixture.Drive.BlockSize);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_CanSetToMaximum(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        Assert.True(fixture.Drive.SetBlockSize(fixture.Capabilities.MaxBlockSize));
        Assert.Equal(fixture.Capabilities.MaxBlockSize, fixture.Drive.BlockSize);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_ZeroResetsToDefault(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        // First change to something else
        fixture.Drive.SetBlockSize(fixture.Capabilities.MinBlockSize);
        // Then reset with 0
        Assert.True(fixture.Drive.SetBlockSize(0));
        Assert.Equal(fixture.Capabilities.DefaultBlockSize, fixture.Drive.BlockSize);
    }

    #endregion

    #region *** Positioning Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Rewind_ResetsToBlockZero(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;

        // Write some data to advance position
        var buffer = new byte[drive.BlockSize];
        drive.WriteDirect(buffer, 0, buffer.Length, out _, out _);

        Assert.True(drive.BlockCounter > 0, "Position should have advanced after write");

        // Rewind
        Assert.True(drive.Rewind());
        Assert.Equal(0, drive.BlockCounter);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToBlock_SeeksCorrectly(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;

        // Write several blocks to create addressable space
        var buffer = new byte[drive.BlockSize];
        for (int i = 0; i < 10; i++)
            drive.WriteDirect(buffer, 0, buffer.Length, out _, out _);

        // Seek to a specific block
        Assert.True(drive.MoveToBlock(5));
        Assert.Equal(5, drive.BlockCounter);

        // Seek back to beginning
        Assert.True(drive.MoveToBlock(0));
        Assert.Equal(0, drive.BlockCounter);
    }

    #endregion

    #region *** Read/Write Round-Trip Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteAndRead_SingleBlock_RoundTrips(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        // Prepare a block with known content
        byte[] writeBuffer = new byte[blockSize];
        for (int i = 0; i < blockSize; i++)
            writeBuffer[i] = (byte)(i % 251); // prime modulus avoids aliasing with block size

        // Write
        int written = drive.WriteDirect(writeBuffer, 0, blockSize, out _, out _);
        Assert.Equal(blockSize, written);

        // Rewind and read back
        Assert.True(drive.Rewind());

        byte[] readBuffer = new byte[blockSize];
        int read = drive.ReadDirect(readBuffer, 0, blockSize, out _, out _);
        Assert.Equal(blockSize, read);

        // Verify content
        Assert.Equal(writeBuffer, readBuffer);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteAndRead_MultipleBlocks_RoundTrips(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;
        int blockCount = 20;
        int totalSize = blockSize * blockCount;

        // Write multiple blocks with position-dependent content
        byte[] writeBuffer = new byte[totalSize];
        for (int i = 0; i < totalSize; i++)
            writeBuffer[i] = (byte)((i / blockSize * 37 + i % blockSize) % 256);

        int written = drive.WriteDirect(writeBuffer, 0, totalSize, out _, out _);
        Assert.Equal(totalSize, written);

        // Rewind and read back
        Assert.True(drive.Rewind());

        byte[] readBuffer = new byte[totalSize];
        int read = drive.ReadDirect(readBuffer, 0, totalSize, out _, out _);
        Assert.Equal(totalSize, read);

        Assert.Equal(writeBuffer, readBuffer);
    }

    #endregion

    #region *** Filemark Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteFilemark_CanBeSpaced(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        // Write a block, then a filemark, then another block
        byte[] block1 = new byte[blockSize];
        Array.Fill(block1, (byte)0xAA);
        drive.WriteDirect(block1, 0, blockSize, out _, out _);

        Assert.True(fixture.Backend.WriteFilemarks(1));

        byte[] block2 = new byte[blockSize];
        Array.Fill(block2, (byte)0xBB);
        drive.WriteDirect(block2, 0, blockSize, out _, out _);

        // Rewind and read first block
        Assert.True(drive.Rewind());

        byte[] readBuf = new byte[blockSize];
        int read = drive.ReadDirect(readBuf, 0, blockSize, out bool tapemark, out _);
        Assert.Equal(blockSize, read);
        Assert.Equal(block1, readBuf);

        // Space past the filemark
        Assert.True(fixture.Backend.SpaceFilemarks(1));

        // Read second block
        read = drive.ReadDirect(readBuf, 0, blockSize, out tapemark, out _);
        Assert.Equal(blockSize, read);
        Assert.Equal(block2, readBuf);
    }

    [Fact]
    public void Setmarks_WriteAndSpace_Works()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        // Write block, setmark, block
        byte[] block1 = new byte[blockSize];
        Array.Fill(block1, (byte)0x11);
        drive.WriteDirect(block1, 0, blockSize, out _, out _);

        Assert.True(fixture.Backend.WriteSetmarks(1));

        byte[] block2 = new byte[blockSize];
        Array.Fill(block2, (byte)0x22);
        drive.WriteDirect(block2, 0, blockSize, out _, out _);

        // Rewind, read block 1, space past setmark, read block 2
        Assert.True(drive.Rewind());

        byte[] readBuf = new byte[blockSize];
        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(block1, readBuf);

        Assert.True(fixture.Backend.SpaceSetmarks(1));

        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(block2, readBuf);
    }

    [Fact]
    public void SeqFilemarks_WriteAndSpace_Works()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        // Write block, filemark, block
        byte[] block1 = new byte[blockSize];
        Array.Fill(block1, (byte)0x33);
        drive.WriteDirect(block1, 0, blockSize, out _, out _);

        Assert.True(fixture.Backend.WriteFilemarks(1));

        byte[] block2 = new byte[blockSize];
        Array.Fill(block2, (byte)0x44);
        drive.WriteDirect(block2, 0, blockSize, out _, out _);

        // Rewind and space through
        Assert.True(drive.Rewind());

        byte[] readBuf = new byte[blockSize];
        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(block1, readBuf);

        // Use sequential filemark spacing (SDLT-style)
        Assert.True(fixture.Backend.SpaceSequentialFilemarks(1));

        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(block2, readBuf);
    }

    [Fact]
    public void SeqFilemarks_DoNotSupportSetmarks()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);

        // SeqFilemarks profile should NOT support setmarks
        Assert.False(fixture.Drive.SupportsSetmarks);
        Assert.False(fixture.Backend.WriteSetmarks(1));
    }

    [Theory]
    [InlineData(DriveProfile.Setmarks)]
    [InlineData(DriveProfile.Partitions)]
    public void SetmarkDrives_DoNotSupportSeqFilemarks(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        Assert.False(fixture.Drive.SupportsSeqFilemarks);
        Assert.False(fixture.Backend.SpaceSequentialFilemarks(1));
    }

    #endregion

    #region *** Partition Tests ***

    [Fact]
    public void Partitions_CanSwitchBetweenPartitions()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        // Write to content partition
        byte[] contentData = new byte[blockSize];
        Array.Fill(contentData, (byte)0xCC);
        drive.WriteDirect(contentData, 0, blockSize, out _, out _);

        // Switch to initiator partition and write different data
        Assert.True(drive.MoveToPartition(MediaPartition.Initiator));
        byte[] initData = new byte[blockSize];
        Array.Fill(initData, (byte)0xDD);
        drive.WriteDirect(initData, 0, blockSize, out _, out _);

        // Switch back to content and verify
        Assert.True(drive.MoveToPartition(MediaPartition.Content, 0));
        byte[] readBuf = new byte[blockSize];
        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(contentData, readBuf);

        // Switch to initiator and verify
        Assert.True(drive.MoveToPartition(MediaPartition.Initiator, 0));
        drive.ReadDirect(readBuf, 0, blockSize, out _, out _);
        Assert.Equal(initData, readBuf);
    }

    #endregion

    #region *** TOC Round-Trip Tests ***

    [Theory]
    [MemberData(nameof(ProfilesWithTOCOnEmptyTape))]
    public void TOC_SaveAndReload_Succeeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        // Fixture creates an initial TOC — save it and reload
        var reloaded = fixture.SaveAndReloadTOC();

        Assert.NotNull(reloaded);
        Assert.Equal("Test Media", reloaded.Description);
    }

    [Theory]
    [MemberData(nameof(ProfilesWithTOCOnEmptyTape))]
    public void TOC_PreservesMultipleSets(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var toc = fixture.TOC;

        // Add set 1 with a dummy file entry so AddNewSetTOC won't reuse it
        //  (AddNewSetTOC reuses the last set if it's empty — by design)
        toc.AddNewSetTOC(0, incremental: false);
        toc.CurrentSetTOC.Description = "Set 1";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        toc.CurrentSetTOC.BlockSize = 16384;
        toc.CurrentSetTOC.Append(new TapeFileInfo(
            toc.GenerateUID(), address: TapeAddress.Zero,
            new TapeFileDescriptor("C:\\dummy1.txt") { Length = 100 }));

        // Now adding set 2 will actually create a new set (set 1 is non-empty)
        toc.AddNewSetTOC(0, incremental: false);
        toc.CurrentSetTOC.Description = "Set 2";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.XxHash3;
        toc.CurrentSetTOC.BlockSize = 32768;
        toc.CurrentSetTOC.Append(new TapeFileInfo(
            toc.GenerateUID(), address: TapeAddress.Zero,
            new TapeFileDescriptor("C:\\dummy2.txt") { Length = 200 }));

        // Save and reload
        var reloaded = fixture.SaveAndReloadTOC();

        Assert.Equal(2, reloaded.Count);

        Assert.Equal("Set 1", reloaded[1].Description);
        Assert.Equal(TapeHashAlgorithm.Crc64, reloaded[1].HashAlgorithm);
        Assert.Equal(16384u, reloaded[1].BlockSize);
        Assert.Single(reloaded[1]);
        Assert.Equal("C:\\dummy1.txt", reloaded[1][0].FileDescr.FullName);

        Assert.Equal("Set 2", reloaded[2].Description);
        Assert.Equal(TapeHashAlgorithm.XxHash3, reloaded[2].HashAlgorithm);
        Assert.Equal(32768u, reloaded[2].BlockSize);
        Assert.Single(reloaded[2]);
        Assert.Equal("C:\\dummy2.txt", reloaded[2][0].FileDescr.FullName);
    }

    #endregion

    #region *** Format Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FormatMedia_ResetsState(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        // Write some data
        byte[] data = new byte[blockSize * 5];
        Array.Fill(data, (byte)0xFF);
        drive.WriteDirect(data, 0, data.Length, out _, out _);

        // Format
        long initPartSize = drive.HasInitiatorPartition ? VirtualTapeFixture.DefaultInitiatorCapacity : -1;
        Assert.True(drive.FormatMedia(initPartSize));

        // After format, position should be at 0 and media usable
        Assert.True(drive.IsMediaLoaded);
        Assert.Equal(0, drive.BlockCounter);
    }

    #endregion

    #region *** Remaining Capacity Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Remaining_DecreasesAfterWrite(DriveProfile profile)
    {
        const long capacity = 50L * 1024 * 1024;
        using var fixture = new VirtualTapeFixture(profile, contentCapacity: capacity);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;

        long remainingBefore = drive.GetRemainingCapacity();

        // Write some data
        byte[] data = new byte[blockSize * 10];
        drive.WriteDirect(data, 0, data.Length, out _, out _);

        long remainingAfter = drive.GetRemainingCapacity();
        Assert.True(remainingAfter < remainingBefore,
            $"Remaining should decrease after write: {remainingBefore} → {remainingAfter}");
    }

    #endregion

    #region *** IO Throttle Disabled Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Fixture_HasThrottlingDisabled(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        Assert.Equal(0, fixture.Backend.IoRateBytesPerSecond);
        Assert.False(fixture.Backend.IsIoThrottled);
        Assert.Equal(0, fixture.Backend.LocateRateBytesPerSecond);
        Assert.Equal(0, fixture.Backend.SearchRateBytesPerSecond);
        Assert.False(fixture.Backend.IsMovementThrottled);
    }

    #endregion
}
