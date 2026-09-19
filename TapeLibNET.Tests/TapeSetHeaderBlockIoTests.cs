using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 3 coverage for <see cref="TapeStreamManager.WriteSetHeaderBlock"/> /
///  <see cref="TapeStreamManager.ReadSetHeaderBlock"/> — the set-header block primitives.
/// <para>
/// These exercise the primitives DIRECTLY, with no agent involvement: the point is that they operate at
///  the current position, leave the navigator untouched on success (SH-6), and reset it on failure.
/// </para>
/// </summary>
public class TapeSetHeaderBlockIoTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

    private static readonly Guid s_mediaId = Guid.Parse("7E1C4A88-2B15-4C9E-9F3A-1D5E8B0C6A24");

    private static TapeSetHeader MakeHeader(int globalSetIndex = 3, int volumeSetIndex = 1) =>
        new()
        {
            MediaId        = s_mediaId,
            CreatedUtc     = new DateTime(2026, 9, 12, 10, 30, 0, DateTimeKind.Utc),
            SetBlockSize   = 64 * 1024,
            Volume         = 2,
            VolumeSetIndex = volumeSetIndex,
            GlobalSetIndex = globalSetIndex,
            Description    = "Weekly full",
        };

    private static byte[] Framed(TapeSetHeader header)
    {
        byte[]? block = TapeHeaderBlock.Frame(header);
        Assert.NotNull(block);
        return block!;
    }

    /// <summary>
    /// Positions at begin-of-content, leaving <c>CurrentContentSet == 0</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a bare <c>Drive.Rewind()</c>: that leaves the navigator at
    ///  <see cref="TapeNavigator.UnknownSet"/>, which is indistinguishable from the value SH-6 resets to —
    ///  so "unchanged on success" would be untestable.
    /// </remarks>
    private static void PositionAtBeginOfContent(TapeFileAgent agent)
    {
        Assert.True(agent.Navigator.MoveToBeginOfContent(), "Failed to position at begin-of-content");
        Assert.Equal(0, agent.Navigator.CurrentContentSet);
    }

    /// <summary>
    /// Re-positions at begin-of-content for a READ in <see cref="TapeState.MediaPrepared"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="TapeNavigator.ResetContentSet"/> first is essential, for exactly the reason
    ///  <see cref="EnterReadingContentAtOldestSet"/> needs it: <c>MoveToBeginOfContent</c> short-circuits
    ///  on <c>CurrentContentSet == 0</c>, and a successful <c>WriteSetHeaderBlock</c> legitimately LEAVES
    ///  it at 0 (SH-6). Without the reset the head stays parked PAST the block just written and the read
    ///  fetches the next one — at EOD on a tape holding nothing else.
    /// </remarks>
    private static void RepositionAtBeginOfContent(TapeFileAgent agent)
    {
        agent.Navigator.ResetContentSet();
        Assert.True(agent.Navigator.MoveToBeginOfContent(), "Failed to re-position at begin-of-content");
        Assert.Equal(0, agent.Navigator.CurrentContentSet);
    }

    /// <summary>
    /// Enters <see cref="TapeState.ReadingContent"/> positioned at the oldest set.
    /// </summary>
    /// <remarks>
    /// The <see cref="TapeNavigator.ResetContentSet"/> first is essential: both
    ///  <c>MoveToBeginOfContent</c> overrides return early when <c>CurrentContentSet == 0</c>, so after a
    ///  write that legitimately LEFT it at 0 (SH-6) the head would stay parked past the written block and
    ///  the read would fetch the following one.
    /// </remarks>
    private static void EnterReadingContentAtOldestSet(TapeFileAgent agent)
    {
        agent.Navigator.ResetContentSet();
        agent.Navigator.TargetContentSet = 0;
        Assert.True(agent.Manager.BeginReadContent(), "Failed to enter ReadingContent");
    }

    #endregion

    #region *** (A) Round trip ***

    /// <summary>
    /// The core of Step 3: a set header written at the current position reads back byte-identical and
    ///  classifies as a <see cref="TapeSetHeader"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WriteThenRead_RoundTripsAtCurrentPosition(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        var original = MakeHeader();

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(original)), "WriteSetHeaderBlock failed");

        EnterReadingContentAtOldestSet(agent);

        var buffer = new byte[TapeHeaderBlock.Size];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(buffer));

        var read = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(buffer, TapeHeaderBlock.Size));
        Assert.Equal(original.MediaId,        read.MediaId);
        Assert.Equal(original.Volume,         read.Volume);
        Assert.Equal(original.VolumeSetIndex, read.VolumeSetIndex);
        Assert.Equal(original.GlobalSetIndex, read.GlobalSetIndex);
        Assert.Equal(original.SetBlockSize,   read.SetBlockSize);
        Assert.Equal(original.Description,    read.Description);
    }

    /// <summary>
    /// An over-sized buffer is legitimate — the block is whatever it is. Pins the deliberate difference
    ///  from <c>ReadBomHeaderBlock</c>, which compares the byte count against <c>buffer.Length</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_AcceptsOversizedBuffer(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        EnterReadingContentAtOldestSet(agent);

        var oversized = new byte[TapeHeaderBlock.Size * 2];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(oversized));
        Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(oversized, TapeHeaderBlock.Size));
    }

    #endregion

    #region *** (B) SH-6 — the navigator on success ***

    /// <summary>
    /// SH-6, write half: a successful write positions nothing and records nothing — the caller owns the
    ///  position, and the set header is invisible to set counting (SH-3).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Write_OnSuccess_LeavesNavigatorUntouched(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        var presenceBefore = agent.Navigator.MediaHeaderPresence;

        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        Assert.Equal(0, agent.Navigator.CurrentContentSet);            // NOT advanced, NOT reset
        Assert.Equal(presenceBefore, agent.Navigator.MediaHeaderPresence);
    }

    /// <summary>SH-6, read half.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_OnSuccess_LeavesNavigatorUntouched(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        EnterReadingContentAtOldestSet(agent);
        int setBefore = agent.Navigator.CurrentContentSet;

        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));

        Assert.Equal(setBefore, agent.Navigator.CurrentContentSet);
    }

    /// <summary>
    /// <see cref="TapeHeaderBlock"/> sets the drive to the 16 KiB header block and restores the previous
    ///  size. Leaving it changed would silently reshape whatever the caller writes or reads next.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_IsRestoredAroundBothOperations(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        uint before = fixture.Drive.BlockSize;

        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));
        Assert.Equal(before, fixture.Drive.BlockSize);

        EnterReadingContentAtOldestSet(agent);
        uint beforeRead = fixture.Drive.BlockSize;

        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));
        Assert.Equal(beforeRead, fixture.Drive.BlockSize);
    }

    #endregion

    #region *** (C) SH-6 — the navigator on failure ***

    /// <summary>
    /// SH-6, write half: a rejected block must reset the content position. A stale
    ///  <c>CurrentContentSet</c> after a torn write would feed a falsehood into the very verification
    ///  this feature exists to power.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Write_OnFailure_ResetsContentSet(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);

        // A block that is not exactly the standard size is rejected by TapeHeaderBlock.WriteFramed,
        //  which sets the drive error — the identical branch a torn write would take.
        Assert.False(agent.Manager.WriteSetHeaderBlock(new byte[TapeHeaderBlock.Size - 1]));

        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
    }

    /// <summary>SH-6, read half: an undersized buffer is rejected and resets the position.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_OnFailure_ResetsContentSet(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        EnterReadingContentAtOldestSet(agent);

        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size - 1]));

        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
    }

    /// <summary>
    /// A GENUINE I/O failure rather than a synthetic one: reading at end-of-data on a blank tape returns
    ///  zero bytes — the same condition <c>ReadBomHeaderBlock</c> meets on blank media.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_AtEndOfData_FailsAndResetsContentSet(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);   // nothing written at all
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        EnterReadingContentAtOldestSet(agent);

        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));

        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
    }

    #endregion

    #region *** (D) Preconditions ***

    /// <summary>
    /// The write requires <see cref="TapeState.MediaPrepared"/> — Step 4 stamps the header BEFORE
    ///  <c>BeginWriteContent</c>, because that call creates the packer with no seam before it. A refusal
    ///  is a precondition violation, not an I/O fault, so the navigator stays as the caller left it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Write_InReadingContentState_Rejected_WithoutResettingNavigator(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        EnterReadingContentAtOldestSet(agent);
        int setBefore = agent.Navigator.CurrentContentSet;

        Assert.False(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        Assert.Equal(setBefore, agent.Navigator.CurrentContentSet);
    }

    /*
    // NOW OBSOLETE after SetHeader Verification for Write Step 1
    //  Replaced by Read_InMediaPreparedState_Succeeds
    /// <summary>The read requires <see cref="TapeState.ReadingContent"/>.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_InMediaPreparedState_Rejected_WithoutResettingNavigator(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        int setBefore = agent.Navigator.CurrentContentSet;

        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));

        Assert.Equal(setBefore, agent.Navigator.CurrentContentSet);
    }
    */

    /// <summary>
    /// SH-7: once the pipelined reader exists, a raw <c>ReadDirect</c> would race its prefetch worker
    ///  thread. The guard is a hard return — not a <c>Debug.Assert</c> — precisely so this test can prove
    ///  it, and so Debug and Release behave identically on a data-race condition.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_AfterPipelinedReaderCreated_Rejected(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        EnterReadingContentAtOldestSet(agent);

        // Constructs the pipelined reader as a side effect; the returned stream is irrelevant here.
        agent.Manager.BeginPackedFileRead(TapeAddress.Zero, TapeHeaderBlock.Size);

        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));
    }

    #endregion

    #region *** (E) SH-6 under injected drive faults ***

    // ═══════════════════════════════════════════════════════════════════════════
    //  Step 3a addendum to TapeSetHeaderBlockIoTests.cs
    // ═══════════════════════════════════════════════════════════════════════════

#if DEBUG

    /// <summary>
    /// SH-6 against a GENUINE torn write — the case the size-rejection test can only approximate. Bytes
    ///  physically land, no block is registered, the drive reports a write fault, and the manager must
    ///  therefore declare the position unknowable.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Write_TornByDrive_FailsAndResetsContentSet(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);

        fixture.Backend.ContentWriteFaults.TearOnce();

        Assert.False(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        Assert.Equal(1, fixture.Backend.ContentWriteFaults.Occurrences);
        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
    }

    /// <summary>Length of the framed record proper, before the block's zero padding.</summary>
    private static int FramedRecordLength(byte[] framed)
    {
        int end = framed.Length;
        while (end > 0 && framed[end - 1] == 0)
            end--;
        return end;
    }

    /// <summary>
    /// A torn header write leaves a block that the MANAGER reads successfully — full size, no I/O error —
    ///  and that the AGENT then rejects on CRC. Exactly the division of labour INV-12 specifies, and the
    ///  genuine <c>Unreadable</c> input the Step 5 verdict ladder needs.
    /// </summary>
    /// <remarks>
    /// The tear point is derived from the record, not left at the half-block default: a header frame
    ///  occupies only tens of bytes of a 16 KiB block, so the default cut falls in the padding and damages
    ///  nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Write_TornByDrive_LeavesAnUnclassifiableBlock(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        byte[] framed = Framed(MakeHeader());
        int tearAt = FramedRecordLength(framed) / 2;        // squarely INSIDE the record

        PositionAtBeginOfContent(agent);
        fixture.Backend.ContentWriteFaults.TearOnce(bytes: tearAt);
        Assert.False(agent.Manager.WriteSetHeaderBlock(framed));

        EnterReadingContentAtOldestSet(agent);

        var buffer = new byte[TapeHeaderBlock.Size];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(buffer));   // manager succeeds…
        Assert.Null(TapeHeaderBlock.Classify(buffer, TapeHeaderBlock.Size));            // …agent rejects
    }

    /// <summary>
    /// Recovery: a torn header write costs nothing permanently. Re-positioning and re-writing produces a
    ///  header that reads back intact — the orphaned bytes are simply overwritten.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Write_TornByDrive_ThenRewritten_ReadsBackIntact(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        var header = MakeHeader();

        PositionAtBeginOfContent(agent);
        fixture.Backend.ContentWriteFaults.TearOnce();      // AutoDisable — the retry is clean
        Assert.False(agent.Manager.WriteSetHeaderBlock(Framed(header)));
        Assert.True(fixture.Drive.WentBad);

        // The position is unknowable after SH-6. Rewind first: it re-establishes a known position AND
        //  clears the drive error the injected fault left behind — without which the Partitions path
        //  fails to position.
        //Assert.True(fixture.Drive.Rewind());
        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(header)), "the retry must succeed");
        EnterReadingContentAtOldestSet(agent);

        var buffer = new byte[TapeHeaderBlock.Size];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(buffer));

        var read = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(buffer, TapeHeaderBlock.Size));
        Assert.Equal(header.GlobalSetIndex, read.GlobalSetIndex);
        Assert.Equal(header.VolumeSetIndex, read.VolumeSetIndex);
        Assert.Equal(header.MediaId, read.MediaId);
    }

    /// <summary>SH-6, read half, driven by a genuine drive-level read fault.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_FaultedByDrive_FailsAndResetsContentSet(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        EnterReadingContentAtOldestSet(agent);

        fixture.Backend.ContentReadFaults.FailOnce();

        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));

        Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);
        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
    }

    /// <summary>
    /// A read fault is transient: the header on tape is undamaged, so a re-read after re-positioning
    ///  succeeds. Distinguishes "the drive stumbled" from "the record is gone" — the distinction Step 5's
    ///  verdict ladder rests on.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_FaultedByDrive_ThenRetried_Succeeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        EnterReadingContentAtOldestSet(agent);
        fixture.Backend.ContentReadFaults.FailOnce();       // AutoDisable
        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));

        EnterReadingContentAtOldestSet(agent);              // re-establish the position after SH-6

        var buffer = new byte[TapeHeaderBlock.Size];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(buffer));
        Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(buffer, TapeHeaderBlock.Size));
    }

#endif // DEBUG

    #endregion

    #region *** (F) Step 1 — the MediaPrepared window (SH-17) ***

    //  Step 1 opens ReadSetHeaderBlock to TapeState.MediaPrepared, because the verification read that
    //   precedes a DESTRUCTIVE write runs there — before Manager.BeginWriteContent() exists to put us
    //   in WritingContent, and deliberately so (the packer must not yet be anchored).
    //  These tests pin the two halves of that: the window is genuinely open, and the packer guard that
    //   now carries the whole safety argument is genuinely closed.

    /// <summary>
    /// The window Step 1 opens: a set header written in <see cref="TapeState.MediaPrepared"/> reads
    ///  back in the same state, with no content session in between.
    /// </summary>
    /// <remarks>
    /// This REPLACES the former <c>Read_InMediaPreparedState_Rejected_WithoutResettingNavigator</c>,
    ///  which asserted the opposite. Its underlying claim — that a REFUSAL leaves the navigator alone —
    ///  survives below, re-pointed at the refusal condition that still exists (the packer guard).
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_InMediaPreparedState_Succeeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        var original = MakeHeader();
        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(original)));

        // No BeginReadContent: stay in MediaPrepared, exactly as the destructive-write path does.
        Assert.Equal(TapeState.MediaPrepared, (TapeState)agent.Manager.State);
        RepositionAtBeginOfContent(agent);

        var buffer = new byte[TapeHeaderBlock.Size];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(buffer));

        var read = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(buffer, TapeHeaderBlock.Size));
        Assert.Equal(original.GlobalSetIndex, read.GlobalSetIndex);
        Assert.Equal(original.VolumeSetIndex, read.VolumeSetIndex);
        Assert.Equal(original.MediaId, read.MediaId);
    }

    /// <summary>
    /// The verification read must be INVISIBLE to the write that follows it. All three quantities the
    ///  destructive path depends on are checked together, because each fails differently:
    ///  <list type="bullet">
    ///  <item>block size — a stale 16 KiB would be captured by <c>EnsurePackerCreated</c> moments later,
    ///        producing zero committed blocks and no <c>FilesCommitted</c> events;</item>
    ///  <item>byte counter — feeds the early-warning reserve;</item>
    ///  <item>current block — SH-15: the write begins where the read began.</item>
    ///  </list>
    /// </summary>
    /// <remarks>
    /// The set block size is deliberately set to something OTHER than <see cref="TapeHeaderBlock.Size"/>.
    ///  With the two equal, a missing restore would be indistinguishable from a correct one and the
    ///  test would pass for the wrong reason.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_InMediaPreparedState_DisturbsNothingTheWriteNeeds(DriveProfile profile)
    {
        const uint setBlockSize = 64 * 1024;      // ≠ TapeHeaderBlock.Size (16 KiB)

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        RepositionAtBeginOfContent(agent);
        Assert.True(fixture.Drive.SetBlockSize(setBlockSize));
        Assert.NotEqual((uint)TapeHeaderBlock.Size, fixture.Drive.BlockSize);

        uint blockSizeBefore = fixture.Drive.BlockSize;
        long byteCountBefore = fixture.Drive.ByteCounter;
        long currentBlockBefore = fixture.Drive.CurrentBlock;

        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));

        Assert.Equal(blockSizeBefore, fixture.Drive.BlockSize);
        Assert.Equal(byteCountBefore, fixture.Drive.ByteCounter);

        // The read DOES advance the head by one block — that is expected, and SH-15 makes undoing it
        //  the CALLER's job. Pin the shape so the caller's MoveToBlock is provably necessary rather
        //  than defensive, and so a future "helpful" restore inside the manager is caught here.
        Assert.Equal(currentBlockBefore + 1, fixture.Drive.CurrentBlock);
        Assert.True(fixture.Drive.MoveToBlock(currentBlockBefore));
        Assert.Equal(currentBlockBefore, fixture.Drive.CurrentBlock);
    }

    /// <summary>
    /// SH-8's write-side echo: two verification reads at the same position return the same header.
    ///  The read is repeatable because the caller restores the block — proving the restore of SH-15 is
    ///  sufficient, not merely present.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_InMediaPreparedState_IsRepeatableAfterRestoringTheBlock(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader(globalSetIndex: 5, volumeSetIndex: 2))));

        RepositionAtBeginOfContent(agent);
        long at = fixture.Drive.CurrentBlock;

        var first = new byte[TapeHeaderBlock.Size];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(first));
        Assert.True(fixture.Drive.MoveToBlock(at));            // SH-15, as the agent will do

        var second = new byte[TapeHeaderBlock.Size];
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(second));

        var a = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(first, TapeHeaderBlock.Size));
        var b = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(second, TapeHeaderBlock.Size));
        Assert.Equal(a.GlobalSetIndex, b.GlobalSetIndex);
        Assert.Equal(a.VolumeSetIndex, b.VolumeSetIndex);
    }

    /// <summary>
    /// The TOC states remain excluded. Widening to <see cref="TapeState.MediaPrepared"/> must not be
    ///  read as "any state will do" — a raw content-block read while positioned in the TOC area is
    ///  meaningless, and a refusal is a precondition violation rather than an I/O fault, so the
    ///  navigator stays as the caller left it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_InTocState_Rejected_WithoutResettingNavigator(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        agent.Navigator.AssumeBlankMedia(); // exactly the case here: blank media
        // If we skip AssumeBlankMedia(), the TOCMark navigator cannot locate a mark that was never written.
        //  Production reaches this through BackupInitialTOC, which calls AssumeBlankMedia itself.
        Assert.True(agent.Manager.BeginWriteTOC(), "Failed to enter WritingTOC");
        int setBefore = agent.Navigator.CurrentContentSet;

        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));
        Assert.Equal(setBefore, agent.Navigator.CurrentContentSet);
    }

    /// <summary>
    /// SH-17's new half. With the state test widened, the packer guard carries the entire race
    ///  argument — so the WRITE packer must be excluded as explicitly as the read one already is.
    /// </summary>
    /// <remarks>
    /// A refusal here is a precondition violation, not an I/O fault: the navigator must survive it
    ///  untouched. This is the claim the replaced region-(D) test used to make, re-pointed at the
    ///  refusal condition that still exists.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_WithWritePackerActive_Rejected_WithoutResettingNavigator(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        // Opens WritingContent and constructs the write packer as a side effect — the exact condition
        //  the destructive-write path is careful to stay in FRONT of.
        agent.Navigator.TargetContentSet = 0;
        Assert.True(agent.Manager.BeginWriteContent(-1L), "Failed to enter WritingContent");

        Assert.NotNull(agent.Manager.WritePacker_FORTESTINGONLY); // the packer has been constructed

        int setBefore = agent.Navigator.CurrentContentSet;
        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));
        Assert.Equal(setBefore, agent.Navigator.CurrentContentSet);

        Assert.True(agent.Manager.EndWriteContent());
    }

    /// <summary>
    /// The window closes again. After a content write session opens and closes, the packer is disposed
    ///  and <see cref="TapeState.MediaPrepared"/> is restored — so the read is legal once more. Pins the
    ///  structural claim Step 1 rests on: "no packer in MediaPrepared" holds because both packers are
    ///  disposed on leaving their content state, not because nothing ever created one.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_AfterWriteSessionClosed_IsLegalAgain(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        agent.Navigator.TargetContentSet = 0;
        Assert.True(agent.Manager.BeginWriteContent(-1L));
        Assert.True(agent.Manager.EndWriteContent());
        Assert.Equal(TapeState.MediaPrepared, (TapeState)agent.Manager.State);

        PositionAtBeginOfContent(agent);
        Assert.Equal(TapeHeaderBlock.Size, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));
    }

#if DEBUG
    /// <summary>
    /// §8.3's <c>finally</c>, which is the property this whole design leans on and the one that only
    ///  shows up on the failure path. A faulted read must still restore the drive's block size —
    ///  otherwise a refused verification would leave the packer to be built at 16 KiB, and the set
    ///  would be written at the wrong block size after an error that was supposed to change nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Read_FaultedInMediaPrepared_StillRestoresBlockSize(DriveProfile profile)
    {
        const uint setBlockSize = 64 * 1024;      // ≠ TapeHeaderBlock.Size

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        PositionAtBeginOfContent(agent);
        Assert.True(agent.Manager.WriteSetHeaderBlock(Framed(MakeHeader())));

        RepositionAtBeginOfContent(agent);
        Assert.True(fixture.Drive.SetBlockSize(setBlockSize));
        uint before = fixture.Drive.BlockSize;

        fixture.Backend.ContentReadFaults.FailOnce();
        Assert.Equal(-1, agent.Manager.ReadSetHeaderBlock(new byte[TapeHeaderBlock.Size]));
        Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);

        Assert.Equal(before, fixture.Drive.BlockSize);                       // the finally did its job
        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);   // SH-6 still applies
    }
#endif // DEBUG

    #endregion
}
