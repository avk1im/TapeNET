using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Unit tests for <see cref="TapeNavigator"/> and all four descendant navigator types:
/// <list type="bullet">
///   <item><see cref="TapeNavigatorTOCInSetWithSmks"/> — setmark-capable drives (AIT/DAT)</item>
///   <item><see cref="TapeNavigatorTOCInPartition"/> — setmarks + initiator partition</item>
///   <item><see cref="TapeNavigatorTOCInSetWithFmks"/> — sequential filemark drives, no TOC mark</item>
///   <item><see cref="TapeNavigatorTOCInSetWithFmksAndTOCMark"/> — sequential filemark drives with TOC mark</item>
/// </list>
/// <para>
/// These tests exercise navigation, mark handling, and <c>CurrentContentSet</c> tracking
/// through the virtual tape drive. They do not use <c>TapeStream</c> — only
/// <see cref="TapeDrive"/> positioning and mark-writing methods.
/// </para>
/// </summary>
public class TapeNavigatorTests
{
    #region *** Test Data ***

    /// <summary>All three drive profiles for parameterized theories.</summary>
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
    ];

    /// <summary>Profiles that use actual setmarks (not emulated via filemarks).</summary>
    public static TheoryData<DriveProfile> SetmarkProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
    ];

    /// <summary>Profiles that use filemarks to emulate setmarks.</summary>
    public static TheoryData<DriveProfile> FilemarkProfiles =>
    [
        DriveProfile.SeqFilemarks,
    ];

    /// <summary>
    /// Profiles where the TOC resides "in set" (content partition), meaning
    /// writing content invalidates the TOC.
    /// </summary>
    public static TheoryData<DriveProfile> TOCInSetProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.SeqFilemarks,
    ];

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Creates a <see cref="VirtualTapeFixture"/> and produces a navigator via the factory method.
    /// </summary>
    private static (VirtualTapeFixture fixture, TapeNavigator nav) CreateNavigator(DriveProfile profile)
    {
        var fixture = new VirtualTapeFixture(profile);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive);
        Assert.NotNull(nav);
        return (fixture, nav!);
    }

    /// <summary>
    /// Writes <paramref name="blockCount"/> blocks of deterministic data to the tape at the current position.
    /// Returns the starting block position.
    /// </summary>
    private static long WriteDataBlocks(TapeDrive drive, int blockCount, byte fillByte = 0xAA)
    {
        long startBlock = drive.GetCurrentBlock();
        var buffer = new byte[drive.BlockSize];
        Array.Fill(buffer, fillByte);
        for (int i = 0; i < blockCount; i++)
        {
            int written = drive.WriteDirect(buffer, 0, buffer.Length, out _, out _);
            Assert.Equal(buffer.Length, written);
        }
        return startBlock;
    }

    /// <summary>
    /// Simulates writing a complete content set: data blocks + setmark (via navigator).
    /// Returns the block at which data writing started.
    /// </summary>
    private static long WriteContentSet(TapeNavigator nav, int blockCount, byte fillByte = 0xAA)
    {
        long start = WriteDataBlocks(nav.Drive, blockCount, fillByte);
        Assert.True(nav.WriteContentSetmark(), "Failed to write content setmark");
        return start;
    }

    /// <summary>
    /// Simulates writing a TOC region: two copies separated by a filemark.
    /// Invokes the appropriate navigator notifications.
    /// </summary>
    private static void WriteTOCRegion(TapeNavigator nav, int blocksPerCopy = 2)
    {
        nav.OnBeginWriteTOC();
        WriteDataBlocks(nav.Drive, blocksPerCopy, 0xCC); // TOC copy 1
        Assert.True(nav.WriteTOCFilemark(), "Failed to write TOC filemark after copy 1");
        WriteDataBlocks(nav.Drive, blocksPerCopy, 0xDD); // TOC copy 2
        Assert.True(nav.WriteTOCFilemark(), "Failed to write TOC filemark after copy 2");
        nav.OnTOCWritten();
    }

    /// <summary>
    /// Simulates a full tape layout: writes N content sets, then writes the TOC.
    /// Returns the array of starting blocks for each content set.
    /// </summary>
    private static long[] WriteFullTapeLayout(TapeNavigator nav, int setCount, int blocksPerSet = 4)
    {
        var starts = new long[setCount];
        for (int i = 0; i < setCount; i++)
        {
            nav.OnBeginWriteContent();
            starts[i] = WriteContentSet(nav, blocksPerSet, (byte)(0x10 * (i + 1)));
            nav.OnContentWritten();
        }
        WriteTOCRegion(nav);
        return starts;
    }

    #endregion


    #region *** ProduceNavigator — Factory Dispatching ***

    [Fact]
    public void ProduceNavigator_Setmarks_ReturnsTOCInSetWithSmks()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive);

        Assert.NotNull(nav);
        Assert.IsType<TapeNavigatorTOCInSetWithSmks>(nav);
    }

    [Fact]
    public void ProduceNavigator_Partitions_ReturnsTOCInPartition()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive);

        Assert.NotNull(nav);
        Assert.IsType<TapeNavigatorTOCInPartition>(nav);
    }

    [Fact]
    public void ProduceNavigator_SeqFilemarks_ReturnsTOCInSetWithFmksAndTOCMark()
    {
        // Default: UseTOCMark = true → should produce WithFmksAndTOCMark
        bool originalUseTOCMark = TapeNavigator.UseTOCMark;
        try
        {
            TapeNavigator.UseTOCMark = true;
            using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
            var nav = TapeNavigator.ProduceNavigator(fixture.Drive);

            Assert.NotNull(nav);
            Assert.IsType<TapeNavigatorTOCInSetWithFmksAndTOCMark>(nav);
        }
        finally
        {
            TapeNavigator.UseTOCMark = originalUseTOCMark;
        }
    }

    [Fact]
    public void ProduceNavigator_SeqFilemarks_NoTOCMark_ReturnsTOCInSetWithFmks()
    {
        bool originalUseTOCMark = TapeNavigator.UseTOCMark;
        try
        {
            TapeNavigator.UseTOCMark = false;
            using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
            var nav = TapeNavigator.ProduceNavigator(fixture.Drive);

            Assert.NotNull(nav);
            Assert.IsType<TapeNavigatorTOCInSetWithFmks>(nav);
        }
        finally
        {
            TapeNavigator.UseTOCMark = originalUseTOCMark;
        }
    }

    #endregion


    #region *** Initial State ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Navigator_InitialState_CurrentContentSetIsUnknown(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        Assert.Equal(TapeNavigator.UnknownSet, nav.CurrentContentSet);
        Assert.Equal(0, nav.TargetContentSet);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Navigator_StaticConstants_AreCorrect(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        Assert.Equal(int.MinValue, TapeNavigator.UnknownSet);
        Assert.Equal(int.MinValue + 1, TapeNavigator.InTOCSet);
        Assert.True(TapeNavigator.TOCCapacity > 0);
    }

    #endregion


    #region *** FmksMode Property ***

    [Theory]
    [MemberData(nameof(SetmarkProfiles))]
    public void FmksMode_WithSetmarks_CanBeSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        Assert.False(nav.FmksMode); // default off
        nav.FmksMode = true;
        Assert.True(nav.FmksMode);
        nav.FmksMode = false;
        Assert.False(nav.FmksMode);
    }

    [Theory]
    [MemberData(nameof(FilemarkProfiles))]
    public void FmksMode_WithoutSetmarks_CannotBeSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        nav.FmksMode = true;
        Assert.False(nav.FmksMode); // silently ignored
    }

    #endregion


    #region *** TOCInvalidated ***

    [Theory]
    [MemberData(nameof(TOCInSetProfiles))]
    public void TOCInvalidated_InitiallyTrue_ForTOCInSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // TOCInSet navigators start with TOCInvalidated = true (no TOC on tape yet)
        Assert.True(nav.TOCInvalidated);
    }

    [Fact]
    public void TOCInvalidated_InitiallyFalse_ForPartitions()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

        // Partition navigator uses the base class default (false)
        Assert.False(nav.TOCInvalidated);
    }

    [Theory]
    [MemberData(nameof(TOCInSetProfiles))]
    public void TOCInvalidated_SetFalseAfterTOCWritten(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        Assert.True(nav.TOCInvalidated);
        nav.OnTOCWritten();
        Assert.False(nav.TOCInvalidated);
    }

    [Theory]
    [MemberData(nameof(TOCInSetProfiles))]
    public void TOCInvalidated_SetTrueAfterContentWritten(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // First mark as valid
        nav.OnTOCWritten();
        Assert.False(nav.TOCInvalidated);

        // Content write invalidates it
        nav.OnContentWritten();
        Assert.True(nav.TOCInvalidated);
    }

    #endregion


    #region *** Notification Callbacks ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnBeginWriteTOC_SetsCurrentContentSetToInTOCSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        nav.OnBeginWriteTOC();
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTOCWritten_SetsCurrentContentSetToInTOCSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        nav.OnTOCWritten();
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnContentWritten_SetsCurrentContentSetToEndOfContent(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        nav.OnContentWritten();
        Assert.Equal(-1, nav.CurrentContentSet);
    }

    #endregion


    #region *** ResetContentSet ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ResetContentSet_SetsToUnknown(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        nav.OnContentWritten();
        Assert.Equal(-1, nav.CurrentContentSet);

        nav.ResetContentSet();
        Assert.Equal(TapeNavigator.UnknownSet, nav.CurrentContentSet);
    }

    #endregion


    #region *** MoveToBeginOfContent — Empty Tape ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToBeginOfContent_EmptyTape_Succeeds(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        bool result = nav.MoveToBeginOfContent();

        Assert.True(result);
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(0, nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToBeginOfContent_CalledTwice_ShortCircuits(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        Assert.True(nav.MoveToBeginOfContent());
        Assert.Equal(0, nav.CurrentContentSet);

        // Second call should short-circuit (already at set 0)
        Assert.True(nav.MoveToBeginOfContent());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(0, nav.GetCurrentBlock());
    }

    #endregion


    #region *** MoveToEndOfContent — Empty Tape ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToEndOfContent_EmptyTape_Succeeds(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        bool result = nav.MoveToEndOfContent();

        Assert.True(result);
        Assert.Equal(-1, nav.CurrentContentSet);
        // On an empty tape, end of content is at the beginning
        Assert.Equal(0, nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToEndOfContent_CalledTwice_ShortCircuits(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        Assert.True(nav.MoveToEndOfContent());
        Assert.Equal(-1, nav.CurrentContentSet);

        // Second call should short-circuit
        Assert.True(nav.MoveToEndOfContent());
        Assert.Equal(-1, nav.CurrentContentSet);
    }

    #endregion


    #region *** WriteContentSetmark + Basic Navigation ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteContentSetmark_AdvancesPosition(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        long blockBefore = nav.GetCurrentBlock();
        Assert.True(nav.WriteContentSetmark());
        long blockAfter = nav.GetCurrentBlock();

        // Writing a mark should advance the position
        Assert.True(blockAfter > blockBefore,
            $"Expected position to advance after writing setmark: before={blockBefore}, after={blockAfter}");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteAndNavigateBack_ContentSetmark_RoundTrips(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Write some data, then a setmark
        long dataStart = WriteDataBlocks(nav.Drive, 4);
        Assert.True(nav.WriteContentSetmark());
        long afterSetmark = nav.GetCurrentBlock();

        // Navigate back by one setmark
        Assert.True(nav.MoveToNextContentSetmark(-1));
        long beforeSetmark = nav.GetCurrentBlock();

        // Should be between data start and setmark position
        Assert.True(beforeSetmark > dataStart);
        Assert.True(beforeSetmark < afterSetmark);

        // Navigate forward by one setmark
        Assert.True(nav.MoveToNextContentSetmark(1));
        Assert.Equal(afterSetmark, nav.GetCurrentBlock());
    }

    #endregion


    #region *** Single-Set Layout: Content + TOC ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_WriteAndNavigate_RoundTrips(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Write: [set0_data][SM][toc1][FM][toc2][FM]
        var starts = WriteFullTapeLayout(nav, setCount: 1);
        Assert.Single(starts);

        // Navigate to beginning of content
        Assert.True(nav.MoveToBeginOfContent());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_MoveToEndOfContent_LandsAfterSetmark(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 1, blocksPerSet: 4);

        Assert.True(nav.MoveToEndOfContent());
        Assert.Equal(-1, nav.CurrentContentSet);

        long endOfContentBlock = nav.GetCurrentBlock();
        Assert.True(endOfContentBlock > starts[0],
            "End of content should be past the content set data");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_MoveToBeginOfTOC_Succeeds(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        WriteFullTapeLayout(nav, setCount: 1);

        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
    }

    #endregion


    #region *** Multi-Set Layout: Positive Indexing (from beginning) ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_PositiveIndex_NavigatesToCorrectSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Write: [set0][SM][set1][SM][set2][SM][toc]
        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);
        Assert.Equal(3, starts.Length);

        // Navigate to set 0 (first set)
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());

        // Navigate to set 1 (second set)
        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(1, nav.CurrentContentSet);
        Assert.Equal(starts[1], nav.GetCurrentBlock());

        // Navigate to set 2 (third set)
        nav.TargetContentSet = 2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_PositiveIndex_NavigateForwardThenBack(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Navigate forward to set 2
        nav.TargetContentSet = 2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());

        // Navigate back to set 0
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_PositiveIndex_SameTarget_ShortCircuits(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(1, nav.CurrentContentSet);
        long pos = nav.GetCurrentBlock();

        // Same target — should short-circuit
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(1, nav.CurrentContentSet);
        Assert.Equal(pos, nav.GetCurrentBlock());
    }

    #endregion


    #region *** Multi-Set Layout: Negative Indexing (from end) ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_NegativeIndex_NavigatesToCorrectSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Write: [set0][SM][set1][SM][set2][SM][toc]
        //         0         1         2        -1 (end)
        //                             -2       -1 (from end)
        //                   -3
        //         -4
        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Navigate to set -2 (the most recently written content set = set2)
        nav.TargetContentSet = -2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());

        // Navigate to set -3 (second-to-last = set1)
        nav.TargetContentSet = -3;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-3, nav.CurrentContentSet);
        Assert.Equal(starts[1], nav.GetCurrentBlock());

        // Navigate to set -4 (oldest = set0)
        nav.TargetContentSet = -4;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-4, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_NegativeToEnd_ThenNegativeBack(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // First navigate to end of content
        nav.TargetContentSet = -1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-1, nav.CurrentContentSet);

        // Then navigate to most recent content set
        nav.TargetContentSet = -2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_NegativeIndex_SameTarget_ShortCircuits(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 2, blocksPerSet: 4);

        nav.TargetContentSet = -2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-2, nav.CurrentContentSet);
        long pos = nav.GetCurrentBlock();

        // Same target — should short-circuit
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-2, nav.CurrentContentSet);
        Assert.Equal(pos, nav.GetCurrentBlock());
    }

    #endregion


    #region *** Multi-Set: Cross-Direction Navigation ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_PositiveThenNegative_BothLandCorrectly(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Navigate forward to set 1 (positive indexing)
        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(starts[1], nav.GetCurrentBlock());

        // Now navigate from end to -2 (most recent set)
        nav.TargetContentSet = -2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(starts[2], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_NegativeThenPositive_BothLandCorrectly(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Navigate from end to -3 (set1)
        nav.TargetContentSet = -3;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(starts[1], nav.GetCurrentBlock());

        // Now navigate forward from beginning to set 0
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(starts[0], nav.GetCurrentBlock());
    }

    #endregion


    #region *** TOC Navigation After Content ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToBeginOfTOC_AfterContentSets_Succeeds(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        WriteFullTapeLayout(nav, setCount: 2);

        // Navigate to some content set first
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(0, nav.CurrentContentSet);

        // Now navigate to TOC
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToBeginOfTOC_FromEndOfContent_Succeeds(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        WriteFullTapeLayout(nav, setCount: 2);

        // Move to end of content
        Assert.True(nav.MoveToEndOfContent());
        Assert.Equal(-1, nav.CurrentContentSet);

        // Move to TOC — for TOCInSet navigators, end-of-content IS beginning of TOC
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
    }

    #endregion


    #region *** ContentSet Tracking Through Lifecycle ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CurrentContentSet_TracksFullLifecycle(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Initial state
        Assert.Equal(TapeNavigator.UnknownSet, nav.CurrentContentSet);

        // Move to begin of content
        Assert.True(nav.MoveToBeginOfContent());
        Assert.Equal(0, nav.CurrentContentSet);

        // Write a content set
        nav.OnBeginWriteContent();
        WriteContentSet(nav, 4);
        nav.OnContentWritten();
        Assert.Equal(-1, nav.CurrentContentSet);

        // Write TOC
        WriteTOCRegion(nav);
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Navigate back to content
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(0, nav.CurrentContentSet);

        // Move to end
        Assert.True(nav.MoveToEndOfContent());
        Assert.Equal(-1, nav.CurrentContentSet);

        // Reset
        nav.ResetContentSet();
        Assert.Equal(TapeNavigator.UnknownSet, nav.CurrentContentSet);
    }

    #endregion


    #region *** MoveToNextContentSetmark Incremental Tracking ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToNextContentSetmark_ZeroCount_NoOp(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        WriteFullTapeLayout(nav, setCount: 2);

        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());

        long posBefore = nav.GetCurrentBlock();
        Assert.True(nav.MoveToNextContentSetmark(0));
        Assert.Equal(posBefore, nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToNextContentSetmark_UpdatesCurrentContentSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Start at set 0
        Assert.True(nav.MoveToBeginOfContent());
        Assert.Equal(0, nav.CurrentContentSet);

        // Move forward by 1 setmark → should update CurrentContentSet to 1
        Assert.True(nav.MoveToNextContentSetmark(1));
        Assert.Equal(1, nav.CurrentContentSet);
        Assert.Equal(starts[1], nav.GetCurrentBlock());

        // Move forward by 1 more → should be at set 2
        Assert.True(nav.MoveToNextContentSetmark(1));
        Assert.Equal(2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());
    }

    #endregion


    #region *** Content Filemark Handling (FmksMode) ***

    [Theory]
    [MemberData(nameof(SetmarkProfiles))]
    public void ContentFilemark_FmksModeOn_WritesAndNavigates(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        nav.FmksMode = true;

        // Write data, filemark, data, filemark
        WriteDataBlocks(nav.Drive, 2);
        Assert.True(nav.WriteContentFilemark());
        long afterFirstFm = nav.GetCurrentBlock();
        WriteDataBlocks(nav.Drive, 2);
        Assert.True(nav.WriteContentFilemark());

        // Navigate back by 1 filemark
        Assert.True(nav.MoveToNextContentFilemark(-1));
        long pos = nav.GetCurrentBlock();
        Assert.True(pos < nav.GetCurrentBlock() || pos >= afterFirstFm);
    }

    [Theory]
    [MemberData(nameof(SetmarkProfiles))]
    public void ContentFilemark_FmksModeOff_WritesNoOp(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        nav.FmksMode = false;

        long posBefore = nav.GetCurrentBlock();

        // With FmksMode off, WriteContentFilemark and MoveToNextContentFilemark are no-ops
        Assert.True(nav.WriteContentFilemark());
        Assert.Equal(posBefore, nav.GetCurrentBlock());

        Assert.True(nav.MoveToNextContentFilemark(1));
        Assert.Equal(posBefore, nav.GetCurrentBlock());
    }

    #endregion


    #region *** TOC Filemark Handling ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOCFilemark_WriteAndNavigate_RoundTrips(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Write some data, then a TOC filemark
        WriteDataBlocks(nav.Drive, 2);
        long beforeFm = nav.GetCurrentBlock();
        Assert.True(nav.WriteTOCFilemark());
        long afterFm = nav.GetCurrentBlock();
        Assert.True(afterFm > beforeFm);

        // Write more data and another filemark
        WriteDataBlocks(nav.Drive, 2);
        Assert.True(nav.WriteTOCFilemark());

        // Rewind and navigate forward past the first filemark to verify round-trip
        nav.Drive.Rewind();
        Assert.True(nav.MoveToNextTOCFilemark());
        Assert.Equal(afterFm, nav.GetCurrentBlock());
    }

    #endregion


    #region *** WithSmks: Optimized MoveToTargetContentSet from InTOCSet ***

    [Fact]
    public void WithSmks_MoveToTargetFromTOC_UsesOptimizedPath()
    {
        var (fixture, nav) = CreateNavigator(DriveProfile.Setmarks);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Move to TOC
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Use negative indexing from TOC — this triggers the optimized path in WithSmks
        nav.TargetContentSet = -2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());
    }

    [Fact]
    public void WithSmks_MoveToTargetFromTOC_NegativeIndex_AllSets()
    {
        var (fixture, nav) = CreateNavigator(DriveProfile.Setmarks);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Move to TOC
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Navigate to -4 (oldest set) from TOC — still optimized path
        nav.TargetContentSet = -4;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-4, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());
    }

    [Fact]
    public void WithSmks_MoveToTargetFromTOC_PositiveIndex_UsesBasePath()
    {
        var (fixture, nav) = CreateNavigator(DriveProfile.Setmarks);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Move to TOC
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Positive indexing from TOC — should use base class path (rewind first)
        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(1, nav.CurrentContentSet);
        Assert.Equal(starts[1], nav.GetCurrentBlock());
    }

    #endregion


    #region *** WithPartitions: Partition-Based Navigation ***

    [Fact]
    public void WithPartitions_MoveToBeginOfTOC_SwitchesToInitiatorPartition()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
        // Should be at block 0 in the initiator partition
        Assert.Equal(0, nav.GetCurrentBlock());
    }

    [Fact]
    public void WithPartitions_MoveToBeginOfContent_SwitchesToContentPartition()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

        // First go to TOC partition
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Then switch to content
        Assert.True(nav.MoveToBeginOfContent());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(0, nav.GetCurrentBlock());
    }

    [Fact]
    public void WithPartitions_TOCInvalidated_AlwaysFalse()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

        // TOC is in partition, so content writes don't invalidate it
        Assert.False(nav.TOCInvalidated);
        nav.OnContentWritten();
        Assert.False(nav.TOCInvalidated);
    }

    [Fact]
    public void WithPartitions_MultiSet_NavigatesBetweenSetsAndTOC()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

        // Write content sets + TOC
        var starts = WriteFullTapeLayout(nav, setCount: 2, blocksPerSet: 4);

        // Navigate to TOC (switches to initiator partition)
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Navigate back to content set 0 (switches to content partition)
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());

        // Navigate to content set 1
        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(1, nav.CurrentContentSet);
        Assert.Equal(starts[1], nav.GetCurrentBlock());
    }

    #endregion


    #region *** WithFmksAndTOCMark: TOC Mark Handling ***

    [Fact]
    public void WithFmksAndTOCMark_OnBeginWriteTOC_WritesTocMark()
    {
        bool originalUseTOCMark = TapeNavigator.UseTOCMark;
        try
        {
            TapeNavigator.UseTOCMark = true;
            using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
            var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

            // Write a content set first
            nav.OnBeginWriteContent();
            WriteContentSet(nav, 4);
            nav.OnContentWritten();

            long beforeTOCMark = nav.GetCurrentBlock();
            nav.OnBeginWriteTOC(); // This should write the TOC mark
            long afterTOCMark = nav.GetCurrentBlock();

            Assert.True(afterTOCMark > beforeTOCMark,
                "OnBeginWriteTOC should write a TOC mark, advancing the position");
            Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
        }
        finally
        {
            TapeNavigator.UseTOCMark = originalUseTOCMark;
        }
    }

    [Fact]
    public void WithFmksAndTOCMark_WriteContentThenTOC_NavigateBack()
    {
        bool originalUseTOCMark = TapeNavigator.UseTOCMark;
        try
        {
            TapeNavigator.UseTOCMark = true;
            using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
            var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

            var starts = WriteFullTapeLayout(nav, setCount: 2, blocksPerSet: 4);

            // Navigate to begin of content
            Assert.True(nav.MoveToBeginOfContent());
            Assert.Equal(0, nav.CurrentContentSet);
            Assert.Equal(starts[0], nav.GetCurrentBlock());

            // Navigate to end of content
            Assert.True(nav.MoveToEndOfContent());
            Assert.Equal(-1, nav.CurrentContentSet);

            // Navigate to TOC
            Assert.True(nav.MoveToBeginOfTOC());
            Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
        }
        finally
        {
            TapeNavigator.UseTOCMark = originalUseTOCMark;
        }
    }

    [Fact]
    public void WithFmksAndTOCMark_TOCInvalidated_InteractionWithNavigation()
    {
        bool originalUseTOCMark = TapeNavigator.UseTOCMark;
        try
        {
            TapeNavigator.UseTOCMark = true;
            using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
            var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

            // Write initial layout
            var starts = WriteFullTapeLayout(nav, setCount: 1, blocksPerSet: 4);
            Assert.False(nav.TOCInvalidated); // TOC was just written

            // Now write more content → TOC becomes invalidated
            nav.OnBeginWriteContent();
            WriteContentSet(nav, 4, 0x55);
            nav.OnContentWritten();
            Assert.True(nav.TOCInvalidated);

            // Write the TOC again
            WriteTOCRegion(nav);
            Assert.False(nav.TOCInvalidated);
        }
        finally
        {
            TapeNavigator.UseTOCMark = originalUseTOCMark;
        }
    }

    #endregion


    #region *** WithFmks (no TOC mark): Navigation ***

    [Fact]
    public void WithFmks_NoTOCMark_NavigatesCorrectly()
    {
        bool originalUseTOCMark = TapeNavigator.UseTOCMark;
        try
        {
            TapeNavigator.UseTOCMark = false;
            using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
            var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;
            Assert.IsType<TapeNavigatorTOCInSetWithFmks>(nav);

            var starts = WriteFullTapeLayout(nav, setCount: 2, blocksPerSet: 4);

            // Navigate to set 0
            nav.TargetContentSet = 0;
            Assert.True(nav.MoveToTargetContentSet());
            Assert.Equal(0, nav.CurrentContentSet);
            Assert.Equal(starts[0], nav.GetCurrentBlock());

            // Navigate to set 1
            nav.TargetContentSet = 1;
            Assert.True(nav.MoveToTargetContentSet());
            Assert.Equal(1, nav.CurrentContentSet);
            Assert.Equal(starts[1], nav.GetCurrentBlock());

            // Navigate to end of content
            Assert.True(nav.MoveToEndOfContent());
            Assert.Equal(-1, nav.CurrentContentSet);

            // Navigate to TOC
            Assert.True(nav.MoveToBeginOfTOC());
            Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);
        }
        finally
        {
            TapeNavigator.UseTOCMark = originalUseTOCMark;
        }
    }

    [Fact]
    public void WithFmks_NoTOCMark_NegativeIndexing()
    {
        bool originalUseTOCMark = TapeNavigator.UseTOCMark;
        try
        {
            TapeNavigator.UseTOCMark = false;
            using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks);
            var nav = TapeNavigator.ProduceNavigator(fixture.Drive)!;

            var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

            // Navigate to -2 (most recent)
            nav.TargetContentSet = -2;
            Assert.True(nav.MoveToTargetContentSet());
            Assert.Equal(-2, nav.CurrentContentSet);
            Assert.Equal(starts[2], nav.GetCurrentBlock());

            // Navigate to -4 (oldest)
            nav.TargetContentSet = -4;
            Assert.True(nav.MoveToTargetContentSet());
            Assert.Equal(-4, nav.CurrentContentSet);
            Assert.Equal(starts[0], nav.GetCurrentBlock());
        }
        finally
        {
            TapeNavigator.UseTOCMark = originalUseTOCMark;
        }
    }

    #endregion


    #region *** MoveToBlock — Direct Block Positioning ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToBlock_SeeksToCorrectPosition(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Write some data to have addressable blocks
        WriteDataBlocks(nav.Drive, 10);

        // Navigate to a specific block
        Assert.True(nav.MoveToBlock(5));
        Assert.Equal(5, nav.GetCurrentBlock());

        // Navigate to block 0
        Assert.True(nav.MoveToBlock(0));
        Assert.Equal(0, nav.GetCurrentBlock());
    }

    #endregion


    #region *** Multi-Set: Many Sets Stress Test ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_FiveSets_AllNavigableByPositiveIndex(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 5, blocksPerSet: 2);

        for (int i = 0; i < 5; i++)
        {
            nav.TargetContentSet = i;
            Assert.True(nav.MoveToTargetContentSet(), $"Failed to navigate to set {i}");
            Assert.Equal(i, nav.CurrentContentSet);
            Assert.Equal(starts[i], nav.GetCurrentBlock());
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_FiveSets_AllNavigableByNegativeIndex(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // 5 sets: negative indices: -2 (last), -3, -4, -5, -6 (oldest)
        var starts = WriteFullTapeLayout(nav, setCount: 5, blocksPerSet: 2);

        for (int i = 0; i < 5; i++)
        {
            int negIndex = -(i + 2); // -2, -3, -4, -5, -6
            int expectedSetIndex = 5 - 1 - i; // 4, 3, 2, 1, 0

            nav.TargetContentSet = negIndex;
            Assert.True(nav.MoveToTargetContentSet(), $"Failed to navigate to set {negIndex}");
            Assert.Equal(negIndex, nav.CurrentContentSet);
            Assert.Equal(starts[expectedSetIndex], nav.GetCurrentBlock());
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_FiveSets_ReversePositiveNavigation(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 5, blocksPerSet: 2);

        // Navigate in reverse order: 4, 3, 2, 1, 0
        for (int i = 4; i >= 0; i--)
        {
            nav.TargetContentSet = i;
            Assert.True(nav.MoveToTargetContentSet(), $"Failed to navigate to set {i} (reverse)");
            Assert.Equal(i, nav.CurrentContentSet);
            Assert.Equal(starts[i], nav.GetCurrentBlock());
        }
    }

    #endregion


    #region *** Sequential Navigation Patterns (like real backup/restore) ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RealWorldPattern_WriteThreeSets_RestoreEach_InOrder(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Simulate restore: read TOC first, then each set
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Restore set 0
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());

        // Restore set 1
        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(1, nav.CurrentContentSet);
        Assert.Equal(starts[1], nav.GetCurrentBlock());

        // Restore set 2
        nav.TargetContentSet = 2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RealWorldPattern_WriteThreeSets_RestoreLastFirst(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        var starts = WriteFullTapeLayout(nav, setCount: 3, blocksPerSet: 4);

        // Simulate: read TOC, then restore most recent set first
        Assert.True(nav.MoveToBeginOfTOC());
        Assert.Equal(TapeNavigator.InTOCSet, nav.CurrentContentSet);

        // Restore most recent set (negative index -2)
        nav.TargetContentSet = -2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-2, nav.CurrentContentSet);
        Assert.Equal(starts[2], nav.GetCurrentBlock());

        // Then restore the oldest set
        nav.TargetContentSet = -4;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-4, nav.CurrentContentSet);
        Assert.Equal(starts[0], nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RealWorldPattern_IncrementalBackup_AppendSet(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // First backup: 2 sets + TOC
        var starts1 = WriteFullTapeLayout(nav, setCount: 2, blocksPerSet: 4);

        // Incremental backup: navigate to end of content, write a new set, rewrite TOC
        Assert.True(nav.MoveToEndOfContent());
        Assert.Equal(-1, nav.CurrentContentSet);
        long newSetStart = nav.GetCurrentBlock();

        nav.OnBeginWriteContent();
        WriteContentSet(nav, 4, 0x77);
        nav.OnContentWritten();

        WriteTOCRegion(nav);

        // Now verify all 3 sets are navigable
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(starts1[0], nav.GetCurrentBlock());

        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(starts1[1], nav.GetCurrentBlock());

        nav.TargetContentSet = 2;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(newSetStart, nav.GetCurrentBlock());
    }

    #endregion


    #region *** Edge Cases ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MoveToTargetContentSet_NegativeOne_MovesToEndOfContent(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        WriteFullTapeLayout(nav, setCount: 2);

        nav.TargetContentSet = -1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(-1, nav.CurrentContentSet);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_PositiveAndNegativeIndex_LandAtSameBlock(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // With only 1 set: set[0] by positive index = set[-2] by negative index
        var starts = WriteFullTapeLayout(nav, setCount: 1, blocksPerSet: 4);

        // Positive: set 0
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        long posBlock = nav.GetCurrentBlock();
        Assert.Equal(starts[0], posBlock);

        // Negative: set -2 (the only content set, from the end)
        nav.TargetContentSet = -2;
        Assert.True(nav.MoveToTargetContentSet());
        long negBlock = nav.GetCurrentBlock();
        Assert.Equal(starts[0], negBlock);

        Assert.Equal(posBlock, negBlock);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EmptyContentSets_NavigateBySetmarks(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        // Write "empty" sets — just setmarks with no data
        nav.OnBeginWriteContent();
        long start0 = nav.GetCurrentBlock();
        Assert.True(nav.WriteContentSetmark());
        nav.OnContentWritten();

        nav.OnBeginWriteContent();
        long start1 = nav.GetCurrentBlock();
        Assert.True(nav.WriteContentSetmark());
        nav.OnContentWritten();

        WriteTOCRegion(nav);

        // Navigate to set 0
        nav.TargetContentSet = 0;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(0, nav.CurrentContentSet);
        Assert.Equal(start0, nav.GetCurrentBlock());

        // Navigate to set 1
        nav.TargetContentSet = 1;
        Assert.True(nav.MoveToTargetContentSet());
        Assert.Equal(1, nav.CurrentContentSet);
        Assert.Equal(start1, nav.GetCurrentBlock());
    }

    #endregion


    #region *** GetCurrentBlock ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void GetCurrentBlock_StartsAtZero(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        Assert.Equal(0, nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void GetCurrentBlock_AdvancesAfterWrite(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        WriteDataBlocks(nav.Drive, 3);
        Assert.Equal(3, nav.GetCurrentBlock());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void GetCurrentBlock_ResetsAfterRewind(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        WriteDataBlocks(nav.Drive, 5);
        Assert.True(nav.Drive.Rewind());
        Assert.Equal(0, nav.GetCurrentBlock());
    }

    #endregion


    #region *** Consistency: All Navigators Agree on Set Positions ***

    /// <summary>
    /// Verifies that for all profiles, positive-indexed navigation to each set
    /// lands at the same block as negative-indexed navigation to the same physical set.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_PositiveAndNegativeIndex_AgreesOnPhysicalBlock(DriveProfile profile)
    {
        var (fixture, nav) = CreateNavigator(profile);
        using var _ = fixture;

        int setCount = 3;
        var starts = WriteFullTapeLayout(nav, setCount: setCount, blocksPerSet: 4);

        // For each set, navigate by positive index and record the block
        var positiveBlocks = new long[setCount];
        for (int i = 0; i < setCount; i++)
        {
            nav.TargetContentSet = i;
            Assert.True(nav.MoveToTargetContentSet());
            positiveBlocks[i] = nav.GetCurrentBlock();
        }

        // For each set, navigate by negative index and compare
        // Negative mapping: set[0] = -(setCount+1), set[1] = -setCount, ... set[N-1] = -2
        for (int i = 0; i < setCount; i++)
        {
            int negIndex = -(setCount - i + 1); // set0 → -4, set1 → -3, set2 → -2
            nav.TargetContentSet = negIndex;
            Assert.True(nav.MoveToTargetContentSet());
            long negBlock = nav.GetCurrentBlock();

            Assert.Equal(positiveBlocks[i], negBlock);
            Assert.Equal(starts[i], negBlock);
        }
    }

    #endregion
}
