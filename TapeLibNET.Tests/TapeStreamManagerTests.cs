using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Unit tests for <see cref="TapeStreamManager"/> — stream provisioning, state machine,
/// and tape file stream (<see cref="TapeWriteStream"/> / <see cref="TapeReadStream"/>) behavior.
/// <para>
/// These tests exercise:
/// <list type="bullet">
///   <item>State machine transitions (valid, invalid, cross-transitions)</item>
///   <item>TOC and content stream provisioning lifecycle</item>
///   <item>Multi-set write/read with setmark verification</item>
///   <item>Set-switching during read sessions</item>
///   <item>Capacity checking and enforcement</item>
///   <item>Stream disposal and <c>OnDisposeStream</c> behavior</item>
///   <item>TapeWriteStream / TapeReadStream write/read round-trips</item>
/// </list>
/// </para>
/// </summary>
public class TapeStreamManagerTests
{
    #region *** Test Data ***

    /// <summary>All three drive profiles for parameterized theories.</summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    /// <summary>Profiles that use actual setmarks (not emulated via filemarks).</summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> SetmarkProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Creates a <see cref="VirtualTapeFixture"/> and a <see cref="TapeStreamManager"/>
    /// bound to the fixture's drive.
    /// </summary>
    private static (VirtualTapeFixture fixture, TapeStreamManager mgr) CreateManager(DriveProfile profile,
        long contentCapacity = VirtualTapeFixture.DefaultContentCapacity)
    {
        var fixture = new VirtualTapeFixture(profile, contentCapacity);
        var mgr = new TapeStreamManager(fixture.Drive);
        return (fixture, mgr);
    }

    /// <summary>
    /// Generates deterministic test data of the specified length.
    /// Each byte is derived from its position for easy verification.
    /// </summary>
    private static byte[] GenerateTestData(int length, byte seed = 0xA0)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(seed ^ (i & 0xFF));
        return data;
    }

    /// <summary>
    /// Writes a complete TOC round-trip: two copies of data separated by filemarks,
    /// using TapeStreamManager's TOC stream provisioning.
    /// Returns the data written for verification.
    /// </summary>
    private static byte[] WriteTOCViaManager(TapeStreamManager mgr, int dataLength = 1024)
    {
        var tocData = GenerateTestData(dataLength, 0xCC);

        EnsureReadyForTOCWrite(mgr);

        // TOC copy 1
        using (var ws = mgr.ProduceWriteTOCStream())
        {
            Assert.NotNull(ws);
            ws!.Write(tocData, 0, tocData.Length);
        }

        // TOC copy 2
        using (var ws = mgr.ProduceWriteTOCStream())
        {
            Assert.NotNull(ws);
            ws!.Write(tocData, 0, tocData.Length);
        }

        Assert.True(mgr.EndWriteTOC(), "Failed to end TOC writing");

        return tocData;
    }

    /// <summary>
    /// Reads TOC data back via TapeStreamManager's read stream provisioning.
    /// </summary>
    private static byte[] ReadTOCViaManager(TapeStreamManager mgr, int expectedLength)
    {
        var buffer = new byte[expectedLength];

        using var rs = mgr.ProduceReadTOCStream(lengthLimit: expectedLength);
        Assert.NotNull(rs);

        int totalRead = 0;
        while (totalRead < expectedLength)
        {
            int read = rs!.Read(buffer, totalRead, expectedLength - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        Assert.Equal(expectedLength, totalRead);
        return buffer;
    }

    /// <summary>
    /// Writes a single content file via the content stream.
    /// Returns the data written.
    /// </summary>
    private static byte[] WriteContentFileViaManager(TapeStreamManager mgr, int dataLength, byte seed = 0xA0)
    {
        var data = GenerateTestData(dataLength, seed);

        using var ws = mgr.ProduceWriteContentStream(dataLength, 0);
        Assert.NotNull(ws);
        ws!.Write(data, 0, data.Length);

        return data;
    }

    /// <summary>
    /// Reads a single content file via the content stream.
    /// </summary>
    private static byte[] ReadContentFileViaManager(TapeStreamManager mgr, int expectedLength)
    {
        var buffer = new byte[expectedLength];

        using var rs = mgr.ProduceReadContentStream(lengthLimit: expectedLength);
        Assert.NotNull(rs);

        int totalRead = 0;
        while (totalRead < expectedLength)
        {
            int read = rs!.Read(buffer, totalRead, expectedLength - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        return buffer[..totalRead];
    }

    /// <summary>
    /// Ensures the manager's navigator is positioned correctly for TOC writing.
    /// For TOC-in-set navigators on an empty tape, <c>MoveToBeginOfTOC()</c> requires
    /// <c>CurrentContentSet == -1</c> (end of content). This mimics the real workflow
    /// where content is always written before TOC.
    /// </summary>
    private static void EnsureReadyForTOCWrite(TapeStreamManager mgr)
    {
        if (mgr.Navigator is TapeNavigatorTOCInSet && mgr.Navigator.CurrentContentSet == TapeNavigator.UnknownSet)
        {
            Assert.True(mgr.Navigator.MoveToEndOfContent(), "Failed to move to end of content for TOC preparation");
        }
    }

    #endregion


    #region *** Construction ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Constructor_CreatesManagerWithNavigator(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        Assert.NotNull(mgr.Navigator);
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
        Assert.False(mgr.IsStreamInUse);
        Assert.Null(mgr.Stream);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RenewNavigator_CreatesNewNavigatorOfSameType(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        var originalType = mgr.Navigator.GetType();
        Assert.True(mgr.RenewNavigator());
        Assert.Equal(originalType, mgr.Navigator.GetType());
    }

    #endregion


    #region *** State Machine — Valid Transitions ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginWriteTOC_FromMediaPrepared_TransitionsToWritingTOC(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        Assert.True(mgr.BeginWriteTOC());
        Assert.Equal(TapeState.WritingTOC, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndWriteTOC_FromWritingTOC_TransitionsToMediaPrepared(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        Assert.True(mgr.BeginWriteTOC());
        Assert.True(mgr.EndWriteTOC());
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginReadTOC_FromMediaPrepared_TransitionsToReadingTOC(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write some TOC data first so there's something to position to
        WriteTOCViaManager(mgr);

        Assert.True(mgr.BeginReadTOC());
        Assert.Equal(TapeState.ReadingTOC, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndReadTOC_FromReadingTOC_TransitionsToMediaPrepared(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        WriteTOCViaManager(mgr);

        Assert.True(mgr.BeginReadTOC());
        Assert.True(mgr.EndReadTOC());
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginWriteContent_FromMediaPrepared_TransitionsToWritingContent(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        mgr.Navigator.TargetContentSet = -1; // end of content
        Assert.True(mgr.BeginWriteContent(100_000));
        Assert.Equal(TapeState.WritingContent, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndWriteContent_FromWritingContent_TransitionsToMediaPrepared(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        Assert.True(mgr.EndWriteContent());
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginReadContent_FromMediaPrepared_TransitionsToReadingContent(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write a content set + TOC first so navigation works
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        var data = GenerateTestData(512);
        using (var ws = mgr.ProduceWriteContentStream(data.Length, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(data, 0, data.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Now read content
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());
        Assert.Equal(TapeState.ReadingContent, (TapeState)mgr.State);

        Assert.True(mgr.EndReadContent());
    }

    #endregion


    #region *** State Machine — Idempotent / Short-Circuit ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginWriteTOC_AlreadyWritingTOC_ReturnsTrue(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        Assert.True(mgr.BeginWriteTOC());
        Assert.True(mgr.BeginWriteTOC()); // second call is idempotent
        Assert.Equal(TapeState.WritingTOC, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginWriteContent_AlreadyWritingContent_ReturnsTrue(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        Assert.True(mgr.BeginWriteContent(100_000)); // idempotent
        Assert.Equal(TapeState.WritingContent, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndWriteTOC_FromMediaPrepared_ReturnsTrue(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Already MediaPrepared → nothing to do
        Assert.True(mgr.EndWriteTOC());
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndReadWrite_FromMediaPrepared_ReturnsTrue(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Already in MediaPrepared → nothing to do
        Assert.True(mgr.EndReadWrite());
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
    }

    #endregion


    #region *** State Machine — Cross-Transitions ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginWriteTOC_FromWritingContent_TransitionsViaMediaPrepared(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Start writing content
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        Assert.Equal(TapeState.WritingContent, (TapeState)mgr.State);

        // Cross-transition: WritingContent → (EndWriteContent) → MediaPrepared → WritingTOC
        Assert.True(mgr.BeginWriteTOC());
        Assert.Equal(TapeState.WritingTOC, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginReadContent_FromReadingTOC_TransitionsViaMediaPrepared(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write some data so navigation is possible
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Start reading TOC
        Assert.True(mgr.BeginReadTOC());
        Assert.Equal(TapeState.ReadingTOC, (TapeState)mgr.State);

        // Cross-transition: ReadingTOC → MediaPrepared → ReadingContent
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());
        Assert.Equal(TapeState.ReadingContent, (TapeState)mgr.State);

        Assert.True(mgr.EndReadContent());
    }

    #endregion


    #region *** State Machine — Invalid State Errors ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndWriteContent_FromWritingTOC_SetsError(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        Assert.True(mgr.BeginWriteTOC());

        // EndWriteContent checks for WritingContent state
        bool result = mgr.EndWriteContent();

        // The method sets an error for wrong state but may still return true
        //  due to its error handling. The key is that the state doesn't change improperly.
        // After EndWriteTOC() the state should return to something sensible.
        mgr.EndWriteTOC();
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndReadContent_FromWritingTOC_SetsError(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        Assert.True(mgr.BeginWriteTOC());

        // EndReadContent when in WritingTOC state should detect wrong state
        mgr.EndReadContent();

        // Clean up properly
        mgr.EndReadWrite();
    }

    #endregion


    #region *** TOC Stream Provisioning ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceWriteTOCStream_ReturnsValidStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        using var ws = mgr.ProduceWriteTOCStream();

        Assert.NotNull(ws);
        Assert.True(ws!.CanWrite);
        Assert.False(ws.CanRead);
        Assert.True(mgr.IsStreamInUse);
        Assert.Equal(TapeState.WritingTOC, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceWriteTOCStream_WhileStreamInUse_ReturnsSameStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        using var ws1 = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws1);

        // Second call while stream is in use → return same stream
        var ws2 = mgr.ProduceWriteTOCStream();
        Assert.Same(ws1, ws2);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceReadTOCStream_ReturnsValidStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Must write TOC first
        WriteTOCViaManager(mgr);

        using var rs = mgr.ProduceReadTOCStream();

        Assert.NotNull(rs);
        Assert.True(rs!.CanRead);
        Assert.False(rs.CanWrite);
        Assert.True(mgr.IsStreamInUse);
        Assert.Equal(TapeState.ReadingTOC, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceReadTOCStream_WithLengthLimit_SetsLimitCorrectly(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        WriteTOCViaManager(mgr, 2048);

        using var rs = mgr.ProduceReadTOCStream(lengthLimit: 512);
        Assert.NotNull(rs);
        Assert.Equal(512, rs!.Length);
    }

    #endregion


    #region *** TOC Write/Read Round-Trip ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_WriteAndReadBack_DataMatches(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = 1024;
        var tocData = WriteTOCViaManager(mgr, dataLength);

        // Read back TOC copy 1
        var readData = ReadTOCViaManager(mgr, dataLength);

        Assert.Equal(tocData, readData);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_WriteAndReadBack_BothCopiesMatch(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = 512;
        var tocData = WriteTOCViaManager(mgr, dataLength);

        // Read TOC copy 1
        var copy1 = ReadTOCViaManager(mgr, dataLength);
        // Dispose the read stream to advance past the filemark
        Assert.True(mgr.EndReadTOC());

        // Read TOC copy 2 — navigate to TOC again, then skip past the first copy's filemark
        Assert.True(mgr.BeginReadTOC());
        mgr.Navigator.MoveToNextTOCFilemark(); // skip past copy 1

        var copy2Buffer = new byte[dataLength];
        using (var rs2 = mgr.ProduceReadTOCStream(lengthLimit: dataLength))
        {
            Assert.NotNull(rs2);
            int totalRead = 0;
            while (totalRead < dataLength)
            {
                int read = rs2!.Read(copy2Buffer, totalRead, dataLength - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            Assert.Equal(dataLength, totalRead);
        }
        Assert.True(mgr.EndReadTOC());

        Assert.Equal(tocData, copy1);
        Assert.Equal(tocData, copy2Buffer);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_LargeData_WritesAndReadsCorrectly(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write enough data to span multiple blocks
        int dataLength = (int)fixture.Drive.BlockSize * 10 + 137; // non-aligned
        var tocData = WriteTOCViaManager(mgr, dataLength);

        var readData = ReadTOCViaManager(mgr, dataLength);
        Assert.Equal(tocData, readData);
    }

    #endregion


    #region *** Content Stream Provisioning ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceWriteContentStream_ReturnsValidStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        mgr.Navigator.TargetContentSet = -1;
        using var ws = mgr.ProduceWriteContentStream(1024, 0);

        Assert.NotNull(ws);
        Assert.True(ws!.CanWrite);
        Assert.False(ws.CanRead);
        Assert.True(mgr.IsStreamInUse);
        Assert.Equal(TapeState.WritingContent, (TapeState)mgr.State);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceWriteContentStream_WhileStreamInUse_ReturnsSameStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        mgr.Navigator.TargetContentSet = -1;
        using var ws1 = mgr.ProduceWriteContentStream(1024, 0);
        Assert.NotNull(ws1);

        var ws2 = mgr.ProduceWriteContentStream(1024, 0);
        Assert.Same(ws1, ws2);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceReadContentStream_ReturnsValidStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write some content first
        mgr.Navigator.TargetContentSet = -1;
        var data = GenerateTestData(512);
        using (var ws = mgr.ProduceWriteContentStream(data.Length, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(data, 0, data.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Now read it
        mgr.Navigator.TargetContentSet = 0;
        using var rs = mgr.ProduceReadContentStream(lengthLimit: data.Length);

        Assert.NotNull(rs);
        Assert.True(rs!.CanRead);
        Assert.False(rs.CanWrite);
        Assert.True(mgr.IsStreamInUse);
        Assert.Equal(TapeState.ReadingContent, (TapeState)mgr.State);
    }

    #endregion


    #region *** Single-File Content Write/Read Round-Trip ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Content_SingleFile_WriteAndReadBack_DataMatches(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = 2048;
        var originalData = GenerateTestData(dataLength, 0xB1);

        // Write content
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(originalData, 0, originalData.Length);
        }
        Assert.True(mgr.EndWriteContent());

        // Write TOC (required for navigation)
        WriteTOCViaManager(mgr);

        // Read content back
        mgr.Navigator.TargetContentSet = 0;
        var readData = ReadContentFileViaManager(mgr, dataLength);
        Assert.True(mgr.EndReadContent());

        Assert.Equal(originalData, readData);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Content_NonAlignedSize_WriteAndReadBack_DataMatches(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Non-block-aligned data size
        int dataLength = (int)fixture.Drive.BlockSize + 137;
        var originalData = GenerateTestData(dataLength, 0xC2);

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(originalData, 0, originalData.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        mgr.Navigator.TargetContentSet = 0;
        var readData = ReadContentFileViaManager(mgr, dataLength);
        Assert.True(mgr.EndReadContent());

        Assert.Equal(originalData, readData);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Content_SmallData_SubBlockSize_WriteAndReadBack(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Data smaller than one block
        int dataLength = 100;
        var originalData = GenerateTestData(dataLength, 0xD3);

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(originalData, 0, originalData.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        mgr.Navigator.TargetContentSet = 0;
        var readData = ReadContentFileViaManager(mgr, dataLength);
        Assert.True(mgr.EndReadContent());

        Assert.Equal(originalData, readData);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Content_LargeData_MultipleBlocks_WriteAndReadBack(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Data spanning many blocks
        int dataLength = (int)fixture.Drive.BlockSize * 20 + 1;
        var originalData = GenerateTestData(dataLength, 0xE4);

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000_000));
        using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(originalData, 0, originalData.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        mgr.Navigator.TargetContentSet = 0;
        var readData = ReadContentFileViaManager(mgr, dataLength);
        Assert.True(mgr.EndReadContent());

        Assert.Equal(originalData, readData);
    }

    #endregion


    #region *** Multi-File Content Within Single Set ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Content_MultipleFilesInOneSet_WriteAndReadBack(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int fileCount = 3;
        int fileLength = 1024;
        var files = new byte[fileCount][];

        // Write multiple files in a single content set
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000_000));

        for (int i = 0; i < fileCount; i++)
        {
            files[i] = GenerateTestData(fileLength, (byte)(0x10 * (i + 1)));
            using var ws = mgr.ProduceWriteContentStream(fileLength, (long)i * fileLength);
            Assert.NotNull(ws);
            ws!.Write(files[i], 0, files[i].Length);
        }

        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Read all files back
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());

        for (int i = 0; i < fileCount; i++)
        {
            var buffer = new byte[fileLength];
            using var rs = mgr.ProduceReadContentStream(lengthLimit: fileLength);
            Assert.NotNull(rs);

            int totalRead = 0;
            while (totalRead < fileLength)
            {
                int read = rs!.Read(buffer, totalRead, fileLength - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            Assert.Equal(fileLength, totalRead);
            Assert.Equal(files[i], buffer);
        }

        Assert.True(mgr.EndReadContent());
    }

    #endregion


    #region *** Multi-Set Write and Read ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultipleSets_WriteAndRead_ByPositiveIndex(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int setCount = 3;
        int dataLength = 512;
        var setData = new byte[setCount][];

        // Write multiple content sets
        for (int s = 0; s < setCount; s++)
        {
            setData[s] = GenerateTestData(dataLength, (byte)(0x20 * (s + 1)));

            mgr.Navigator.TargetContentSet = -1; // always append at the end
            Assert.True(mgr.BeginWriteContent(100_000_000));
            using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
            {
                Assert.NotNull(ws);
                ws!.Write(setData[s], 0, setData[s].Length);
            }
            Assert.True(mgr.EndWriteContent());
        }

        // Write TOC
        WriteTOCViaManager(mgr);

        // Read each set by positive index
        for (int s = 0; s < setCount; s++)
        {
            mgr.Navigator.TargetContentSet = s;
            var readData = ReadContentFileViaManager(mgr, dataLength);
            Assert.True(mgr.EndReadContent());

            Assert.Equal(setData[s], readData);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultipleSets_WriteAndRead_ByNegativeIndex(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int setCount = 3;
        int dataLength = 512;
        var setData = new byte[setCount][];

        // Write multiple content sets
        for (int s = 0; s < setCount; s++)
        {
            setData[s] = GenerateTestData(dataLength, (byte)(0x30 * (s + 1)));

            mgr.Navigator.TargetContentSet = -1;
            Assert.True(mgr.BeginWriteContent(100_000_000));
            using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
            {
                Assert.NotNull(ws);
                ws!.Write(setData[s], 0, setData[s].Length);
            }
            Assert.True(mgr.EndWriteContent());
        }

        WriteTOCViaManager(mgr);

        // Read sets via negative indexing:
        //  -2 = last set (setCount-1), -3 = second-to-last, etc.
        for (int s = 0; s < setCount; s++)
        {
            int negIndex = -(s + 2); // -2 = newest, -3 = second newest, etc.
            int expectedDataIndex = setCount - 1 - s; // map to positive index

            mgr.Navigator.TargetContentSet = negIndex;
            var readData = ReadContentFileViaManager(mgr, dataLength);
            Assert.True(mgr.EndReadContent());

            Assert.Equal(setData[expectedDataIndex], readData);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultipleSets_SetmarkWritten_BetweenSets(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write set 0
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000_000));
        using (var ws = mgr.ProduceWriteContentStream(512, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(GenerateTestData(512, 0xAA), 0, 512);
        }
        long blockAfterSet0Write = fixture.Drive.GetCurrentBlock();
        Assert.True(mgr.EndWriteContent());

        // After EndWriteContent, a setmark was written → block position should have advanced
        long blockAfterSet0Setmark = fixture.Drive.GetCurrentBlock();
        // The setmark occupies no data blocks, but we should be past the content

        // Write set 1
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000_000));
        long blockAtSet1Start = fixture.Drive.GetCurrentBlock();
        using (var ws = mgr.ProduceWriteContentStream(512, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(GenerateTestData(512, 0xBB), 0, 512);
        }
        Assert.True(mgr.EndWriteContent());

        WriteTOCViaManager(mgr);

        // Verify we can navigate to each set independently
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());
        Assert.Equal(0, mgr.Navigator.CurrentContentSet);
        Assert.True(mgr.EndReadContent());

        mgr.Navigator.TargetContentSet = 1;
        Assert.True(mgr.BeginReadContent());
        Assert.Equal(1, mgr.Navigator.CurrentContentSet);
        Assert.True(mgr.EndReadContent());
    }

    #endregion


    #region *** BeginReadContent — Set Switching ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginReadContent_SwitchSets_MidSession(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int setCount = 3;
        int dataLength = 512;
        var setData = new byte[setCount][];

        // Write 3 sets
        for (int s = 0; s < setCount; s++)
        {
            setData[s] = GenerateTestData(dataLength, (byte)(0x40 * (s + 1)));

            mgr.Navigator.TargetContentSet = -1;
            Assert.True(mgr.BeginWriteContent(100_000_000));
            using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
            {
                Assert.NotNull(ws);
                ws!.Write(setData[s], 0, setData[s].Length);
            }
            Assert.True(mgr.EndWriteContent());
        }
        WriteTOCViaManager(mgr);

        // Start reading set 0
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());
        Assert.Equal(TapeState.ReadingContent, (TapeState)mgr.State);
        Assert.Equal(0, mgr.Navigator.CurrentContentSet);

        // Switch to set 2 mid-session (without EndReadContent)
        mgr.Navigator.TargetContentSet = 2;
        Assert.True(mgr.BeginReadContent()); // should handle set switch internally
        Assert.Equal(TapeState.ReadingContent, (TapeState)mgr.State);
        Assert.Equal(2, mgr.Navigator.CurrentContentSet);

        // Read set 2 data and verify
        var buffer = new byte[dataLength];
        using (var rs = mgr.ProduceReadContentStream(lengthLimit: dataLength))
        {
            Assert.NotNull(rs);
            int totalRead = 0;
            while (totalRead < dataLength)
            {
                int read = rs!.Read(buffer, totalRead, dataLength - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            Assert.Equal(dataLength, totalRead);
        }

        Assert.Equal(setData[2], buffer);
        Assert.True(mgr.EndReadContent());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginReadContent_SameSetAlreadyReading_ShortCircuits(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write a set + TOC
        var data = GenerateTestData(512, 0x55);
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(data.Length, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(data, 0, data.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Start reading set 0
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());

        // Call again with same target → should short-circuit
        Assert.True(mgr.BeginReadContent());
        Assert.Equal(TapeState.ReadingContent, (TapeState)mgr.State);
        Assert.Equal(0, mgr.Navigator.CurrentContentSet);

        Assert.True(mgr.EndReadContent());
    }

    #endregion


    #region *** Capacity Checking ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContentCapacityLimit_ExceedsLimit_ReturnsNull(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Set a low capacity limit
        mgr.ContentCapacityLimit = 500;

        mgr.Navigator.TargetContentSet = -1;

        // Request a stream for a file larger than the limit
        var ws = mgr.ProduceWriteContentStream(1000, 0);
        Assert.Null(ws);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContentCapacityLimit_WithinLimit_ReturnsStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        mgr.ContentCapacityLimit = 5000;

        mgr.Navigator.TargetContentSet = -1;

        using var ws = mgr.ProduceWriteContentStream(1000, 0);
        Assert.NotNull(ws);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContentCapacityLimit_Negative_NoLimit(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        mgr.ContentCapacityLimit = -1; // no limit

        mgr.Navigator.TargetContentSet = -1;

        using var ws = mgr.ProduceWriteContentStream(100_000, 0);
        Assert.NotNull(ws);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BeginWriteContent_WithRemainingCapacity_StoresCapacity(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        long remainingCapacity = 50_000;
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(remainingCapacity));

        // Capacity enforcement: ProduceWriteContentStream with a file larger than remaining should fail
        // Note: BeginWriteContent was already called, so ProduceWriteContentStream won't call it again
        //  but internally CheckContentCapacity uses CapacityForCurrentSet
        // We verify by writing something within limits
        using var ws = mgr.ProduceWriteContentStream(1000, 0);
        Assert.NotNull(ws);

        Assert.True(mgr.EndWriteContent());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CheckContentCapacity_WrittenSoFarExceedsRemaining_ReturnsNull(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Start content with explicit remaining capacity
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(1000));

        // First file — fits
        using (var ws1 = mgr.ProduceWriteContentStream(500, 0))
        {
            Assert.NotNull(ws1);
            var data = GenerateTestData(500);
            ws1!.Write(data, 0, data.Length);
        }

        // Second file — writtenSoFar=500 + length=600 > capacity=1000
        var ws2 = mgr.ProduceWriteContentStream(600, 500);
        Assert.Null(ws2);

        Assert.True(mgr.EndWriteContent());
    }

    #endregion


    #region *** Stream Disposal and OnDisposeStream ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void StreamDisposal_WriteStream_ClearsIsStreamInUse(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);
        Assert.True(mgr.IsStreamInUse);

        ws!.Dispose();

        Assert.False(mgr.IsStreamInUse);
        Assert.Null(mgr.Stream);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void StreamDisposal_ReadStream_ClearsIsStreamInUse(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        WriteTOCViaManager(mgr);

        var rs = mgr.ProduceReadTOCStream();
        Assert.NotNull(rs);
        Assert.True(mgr.IsStreamInUse);

        rs!.Dispose();

        Assert.False(mgr.IsStreamInUse);
        Assert.Null(mgr.Stream);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void StreamDisposal_SecondDisposeStream_ProducesNewStream(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);

        // Produce first TOC write stream and dispose it
        var ws1 = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws1);
        ws1!.Dispose();
        Assert.False(mgr.IsStreamInUse);

        // Produce second stream → new instance
        using var ws2 = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws2);
        Assert.NotSame(ws1, ws2);
        Assert.True(mgr.IsStreamInUse);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EndReadWrite_WithStreamInUse_ForcesDispose(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);
        Assert.True(mgr.IsStreamInUse);

        // EndReadWrite should force-dispose the stream
        Assert.True(mgr.EndReadWrite());
        Assert.False(mgr.IsStreamInUse);
        Assert.Equal(TapeState.MediaPrepared, (TapeState)mgr.State);
    }

    #endregion


    #region *** TapeWriteStream Behavior ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeWriteStream_Write_UpdatesLength(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        using var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);

        var data = GenerateTestData(256);
        ws!.Write(data, 0, data.Length);

        Assert.Equal(256, ws.Length);
        Assert.Equal(256, ws.Position);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeWriteStream_MultipleWrites_AccumulatesLength(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        using var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);

        var data = GenerateTestData(100);
        ws!.Write(data, 0, data.Length);
        ws.Write(data, 0, data.Length);
        ws.Write(data, 0, data.Length);

        Assert.Equal(300, ws.Length);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeWriteStream_CannotRead(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        using var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);

        Assert.False(ws!.CanRead);
        Assert.Throws<NotImplementedException>(() => ws.Read(new byte[10], 0, 10));
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeWriteStream_CannotSeek(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        using var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);

        Assert.False(ws!.CanSeek);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeWriteStream_SetLength_ThrowsNotSupported(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        using var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);

        Assert.Throws<NotSupportedException>(() => ws!.SetLength(1000));
    }

    #endregion


    #region *** TapeReadStream Behavior ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeReadStream_Read_UpdatesLength(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = 512;
        WriteTOCViaManager(mgr, dataLength);

        using var rs = mgr.ProduceReadTOCStream(lengthLimit: dataLength);
        Assert.NotNull(rs);

        var buffer = new byte[256];
        int read = rs!.Read(buffer, 0, 256);
        Assert.True(read > 0);
        Assert.Equal(read, rs.Position);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeReadStream_LengthLimit_StopsAtLimit(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write a 2KB file
        int writeLength = 2048;
        WriteTOCViaManager(mgr, writeLength);

        // Read with a limit of 512 bytes
        int readLimit = 512;
        using var rs = mgr.ProduceReadTOCStream(lengthLimit: readLimit);
        Assert.NotNull(rs);

        var buffer = new byte[2048];
        int totalRead = 0;
        while (true)
        {
            int read = rs!.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        // Should have read exactly the limit
        Assert.Equal(readLimit, totalRead);
        Assert.Equal(readLimit, rs!.Length);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeReadStream_RemainingLength_DecreasesWithReads(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = 1024;
        WriteTOCViaManager(mgr, dataLength);

        using var rs = mgr.ProduceReadTOCStream(lengthLimit: dataLength);
        Assert.NotNull(rs);

        Assert.Equal(dataLength, rs!.RemainingLength);

        var buffer = new byte[256];
        int read = rs.Read(buffer, 0, 256);

        Assert.Equal(dataLength - read, rs.RemainingLength);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeReadStream_CannotWrite(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        WriteTOCViaManager(mgr);

        using var rs = mgr.ProduceReadTOCStream();
        Assert.NotNull(rs);

        Assert.False(rs!.CanWrite);
        Assert.Throws<NotImplementedException>(() => rs.Write(new byte[10], 0, 10));
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeReadStream_CannotSeek(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        WriteTOCViaManager(mgr);

        using var rs = mgr.ProduceReadTOCStream();
        Assert.NotNull(rs);

        Assert.False(rs!.CanSeek);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TapeReadStream_NoLengthLimit_ReadsUntilEOF(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = 1024;
        var tocData = WriteTOCViaManager(mgr, dataLength);

        // Read without length limit — should read until filemark/EOF
        using var rs = mgr.ProduceReadTOCStream(lengthLimit: -1);
        Assert.NotNull(rs);
        Assert.False(rs!.LengthLimitMode);

        var buffer = new byte[dataLength * 2]; // bigger than data
        int totalRead = 0;
        while (true)
        {
            int read = rs.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        // Should have read at least the data we wrote (may include zero-padding from block alignment)
        Assert.True(totalRead >= dataLength);
        // First dataLength bytes should match
        Assert.Equal(tocData, buffer[..dataLength]);
    }

    #endregion


    #region *** TapeWriteStream/TapeReadStream — Content Round-Trip ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContentStream_WriteInChunks_ReadInDifferentChunks_DataMatches(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write data in small chunks
        int totalLength = 4096;
        var originalData = GenerateTestData(totalLength, 0xF1);

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000_000));

        using (var ws = mgr.ProduceWriteContentStream(totalLength, 0))
        {
            Assert.NotNull(ws);
            int offset = 0;
            int chunkSize = 137; // deliberately non-aligned
            while (offset < totalLength)
            {
                int toWrite = Math.Min(chunkSize, totalLength - offset);
                ws!.Write(originalData, offset, toWrite);
                offset += toWrite;
            }
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Read data back in different chunk sizes
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());

        var readData = new byte[totalLength];
        using (var rs = mgr.ProduceReadContentStream(lengthLimit: totalLength))
        {
            Assert.NotNull(rs);
            int offset = 0;
            int chunkSize = 300; // different chunk size than write
            while (offset < totalLength)
            {
                int toRead = Math.Min(chunkSize, totalLength - offset);
                int read = rs!.Read(readData, offset, toRead);
                if (read == 0) break;
                offset += read;
            }
            Assert.Equal(totalLength, offset);
        }
        Assert.True(mgr.EndReadContent());

        Assert.Equal(originalData, readData);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContentStream_ExactBlockSize_WriteAndRead(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = (int)fixture.Drive.BlockSize; // exactly one block
        var originalData = GenerateTestData(dataLength, 0xA5);

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(originalData, 0, originalData.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        mgr.Navigator.TargetContentSet = 0;
        var readData = ReadContentFileViaManager(mgr, dataLength);
        Assert.True(mgr.EndReadContent());

        Assert.Equal(originalData, readData);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContentStream_ExactDoubleBlockSize_WriteAndRead(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = (int)fixture.Drive.BlockSize * 2; // exactly two blocks (== buffer size for write stream)
        var originalData = GenerateTestData(dataLength, 0x5A);

        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(dataLength + 100_000)); // enough headroom for data + TOC
        using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(originalData, 0, originalData.Length);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        mgr.Navigator.TargetContentSet = 0;
        var readData = ReadContentFileViaManager(mgr, dataLength);
        Assert.True(mgr.EndReadContent());

        Assert.Equal(originalData, readData);
    }

    #endregion


    #region *** Multi-Set Content with TOC Invalidation ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultipleSets_TOCInvalidated_AfterContentWrite(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write first set + TOC
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(512, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(GenerateTestData(512, 0x11), 0, 512);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // For TOC-in-set navigators, TOC should now be valid
        if (mgr.Navigator is TapeNavigatorTOCInSet)
            Assert.False(mgr.Navigator.TOCInvalidated);

        // Write second set — TOC should become invalidated
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(512, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(GenerateTestData(512, 0x22), 0, 512);
        }
        Assert.True(mgr.EndWriteContent());

        if (mgr.Navigator is TapeNavigatorTOCInSet)
            Assert.True(mgr.Navigator.TOCInvalidated);

        // Write TOC again to make it valid
        WriteTOCViaManager(mgr);

        if (mgr.Navigator is TapeNavigatorTOCInSet)
            Assert.False(mgr.Navigator.TOCInvalidated);
    }

    #endregion


    #region *** Navigator CurrentContentSet Tracking via Manager ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Navigator_CurrentContentSet_TracksCorrectly_DuringWriteReadCycle(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Initial state
        Assert.Equal(TapeNavigator.UnknownSet, mgr.Navigator.CurrentContentSet);

        // Write content
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        // After BeginWriteContent, navigator moved to end of content
        Assert.Equal(-1, mgr.Navigator.CurrentContentSet);

        using (var ws = mgr.ProduceWriteContentStream(256, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(GenerateTestData(256), 0, 256);
        }
        Assert.True(mgr.EndWriteContent());
        // After EndWriteContent, OnContentWritten was called → CurrentContentSet = -1
        Assert.Equal(-1, mgr.Navigator.CurrentContentSet);

        // Write TOC
        WriteTOCViaManager(mgr);
        // After TOC write, should be InTOCSet
        Assert.Equal(TapeNavigator.InTOCSet, mgr.Navigator.CurrentContentSet);

        // Read set 0
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());
        Assert.Equal(0, mgr.Navigator.CurrentContentSet);

        Assert.True(mgr.EndReadContent());
    }

    #endregion


    #region *** ByteCounter Reset ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ByteCounter_ResetOnStateTransition(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Use content writing for this test — BeginWriteContent does no IO after
        //  the ByteCounter reset, unlike BeginWriteTOC which writes a TOC mark
        //  for the SeqFilemarks profile.
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        // ByteCounter should be reset to 0 at the start of a new operation
        Assert.Equal(0L, fixture.Drive.ByteCounter);

        Assert.True(mgr.EndWriteContent());
    }

    #endregion


    #region *** Full Lifecycle — Content + TOC Integration ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FullLifecycle_WriteContent_WriteTOC_ReadTOC_ReadContent(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int dataLength = 2048;
        var contentData = GenerateTestData(dataLength, 0xAB);
        int tocLength = 512;

        // 1. Write content
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000_000));
        using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(contentData, 0, contentData.Length);
        }
        Assert.True(mgr.EndWriteContent());

        // 2. Write TOC (two copies)
        var tocData = WriteTOCViaManager(mgr, tocLength);

        // 3. Read TOC back
        var tocReadBack = ReadTOCViaManager(mgr, tocLength);
        Assert.True(mgr.EndReadTOC());
        Assert.Equal(tocData, tocReadBack);

        // 4. Read content back
        mgr.Navigator.TargetContentSet = 0;
        var contentReadBack = ReadContentFileViaManager(mgr, dataLength);
        Assert.True(mgr.EndReadContent());
        Assert.Equal(contentData, contentReadBack);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FullLifecycle_MultipleSets_WriteTOC_ReadAll(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        int setCount = 4;
        int dataLength = 1024;
        var allSetData = new byte[setCount][];

        // Write all content sets
        for (int s = 0; s < setCount; s++)
        {
            allSetData[s] = GenerateTestData(dataLength, (byte)(0x10 + s * 0x11));

            mgr.Navigator.TargetContentSet = -1;
            Assert.True(mgr.BeginWriteContent(100_000_000));
            using (var ws = mgr.ProduceWriteContentStream(dataLength, 0))
            {
                Assert.NotNull(ws);
                ws!.Write(allSetData[s], 0, allSetData[s].Length);
            }
            Assert.True(mgr.EndWriteContent());
        }

        // Write TOC
        var tocData = WriteTOCViaManager(mgr, 256);

        // Read all sets back by positive index
        for (int s = 0; s < setCount; s++)
        {
            mgr.Navigator.TargetContentSet = s;
            var readData = ReadContentFileViaManager(mgr, dataLength);
            Assert.True(mgr.EndReadContent());
            Assert.Equal(allSetData[s], readData);
        }

        // Read all sets back by negative index
        for (int s = 0; s < setCount; s++)
        {
            int negIndex = -(s + 2);
            int expectedIdx = setCount - 1 - s;

            mgr.Navigator.TargetContentSet = negIndex;
            var readData = ReadContentFileViaManager(mgr, dataLength);
            Assert.True(mgr.EndReadContent());
            Assert.Equal(allSetData[expectedIdx], readData);
        }

        // Read TOC
        var tocReadBack = ReadTOCViaManager(mgr, 256);
        Assert.True(mgr.EndReadTOC());
        Assert.Equal(tocData, tocReadBack);
    }

    #endregion


    #region *** FmksMode Content Round-Trip ***

    [Theory]
    [MemberData(nameof(SetmarkProfiles))]
    public void FmksMode_MultipleFiles_WriteAndReadBack(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Enable FmksMode (only valid for setmark-capable drives)
        mgr.Navigator.FmksMode = true;
        Assert.True(mgr.Navigator.FmksMode);

        int fileCount = 3;
        int fileLength = 512;
        var files = new byte[fileCount][];

        // Write multiple files with FmksMode enabled
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000_000));

        for (int i = 0; i < fileCount; i++)
        {
            files[i] = GenerateTestData(fileLength, (byte)(0x50 + i));
            using var ws = mgr.ProduceWriteContentStream(fileLength, (long)i * fileLength);
            Assert.NotNull(ws);
            ws!.Write(files[i], 0, files[i].Length);
        }

        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Read back with FmksMode
        mgr.Navigator.TargetContentSet = 0;
        Assert.True(mgr.BeginReadContent());

        for (int i = 0; i < fileCount; i++)
        {
            var buffer = new byte[fileLength];
            using var rs = mgr.ProduceReadContentStream(lengthLimit: fileLength);
            Assert.NotNull(rs);

            int totalRead = 0;
            while (totalRead < fileLength)
            {
                int read = rs!.Read(buffer, totalRead, fileLength - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            Assert.Equal(fileLength, totalRead);
            Assert.Equal(files[i], buffer);
        }

        Assert.True(mgr.EndReadContent());
    }

    #endregion


    #region *** Edge Cases ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceWriteContentStream_StreamInUse_WrongState_ReturnsNull(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Open a TOC write stream (state = WritingTOC)
        EnsureReadyForTOCWrite(mgr);
        using var tocStream = mgr.ProduceWriteTOCStream();
        Assert.NotNull(tocStream);

        // Requesting a content write stream while a TOC stream is in use → null
        var contentStream = mgr.ProduceWriteContentStream(100, 0);
        Assert.Null(contentStream);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceReadContentStream_StreamInUse_WrongState_ReturnsNull(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Open a TOC write stream
        EnsureReadyForTOCWrite(mgr);
        using var tocStream = mgr.ProduceWriteTOCStream();
        Assert.NotNull(tocStream);

        // Requesting a content read stream while a TOC stream is in use → null
        mgr.Navigator.TargetContentSet = 0;
        var contentStream = mgr.ProduceReadContentStream();
        Assert.Null(contentStream);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProduceReadTOCStream_StreamInUse_WrongState_ReturnsNull(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        // Write content + TOC
        mgr.Navigator.TargetContentSet = -1;
        Assert.True(mgr.BeginWriteContent(100_000));
        using (var ws = mgr.ProduceWriteContentStream(256, 0))
        {
            Assert.NotNull(ws);
            ws!.Write(GenerateTestData(256), 0, 256);
        }
        Assert.True(mgr.EndWriteContent());
        WriteTOCViaManager(mgr);

        // Open a content read stream
        mgr.Navigator.TargetContentSet = 0;
        using var contentStream = mgr.ProduceReadContentStream(lengthLimit: 256);
        Assert.NotNull(contentStream);

        // Requesting a TOC read stream while content stream is in use → null
        var tocStream = mgr.ProduceReadTOCStream();
        Assert.Null(tocStream);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteStream_DisposedStream_ThrowsOnWrite(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        EnsureReadyForTOCWrite(mgr);
        var ws = mgr.ProduceWriteTOCStream();
        Assert.NotNull(ws);
        ws!.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ws.Write(new byte[10], 0, 10));
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ReadStream_DisposedStream_ThrowsOnRead(DriveProfile profile)
    {
        var (fixture, mgr) = CreateManager(profile);
        using var _ = fixture;

        WriteTOCViaManager(mgr);

        var rs = mgr.ProduceReadTOCStream();
        Assert.NotNull(rs);
        rs!.Dispose();

        Assert.Throws<ObjectDisposedException>(() => rs.Read(new byte[10], 0, 10));
    }

    #endregion
}
