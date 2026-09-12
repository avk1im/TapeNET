using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Direct unit tests for <see cref="VirtualTapeMedia"/> — the emulation layer BELOW the virtual drive.
/// <para>
/// Everything else in the suite reaches the media through a <see cref="TapeDrive"/>, so the media's own
///  invariants were only ever exercised incidentally. Three defects survived that gap, all of the same
///  shape — <b>logical position updated, physical (stream) position forgotten</b>:
/// </para>
/// <list type="bullet">
///   <item><c>SeekToBlock</c> at EOD left the stream stale ⇒ the next write clobbered block 0.</item>
///   <item><c>TruncateFromCurrentPosition</c> drifted <c>m_bytesWritten</c> via per-branch deltas.</item>
///   <item><c>SpaceMarks</c> / <c>SpaceSequentialMarks</c> never synced the stream at all ⇒ a write
///     after spacing landed at a stale offset (surfaced by the header's trailing filemark).</item>
/// </list>
/// <para>
/// These tests therefore assert the <b>invariants</b> — position consistency, byte accounting, block
///  numbering, truncation semantics — not merely the happy-path API. Each writes through the media and
///  then <i>reads back what physically landed</i>, which is what catches a stale stream position.
/// </para>
/// </summary>
public class VirtualTapeMediaTests
{
    #region *** Helpers ***

    private const uint Block = 4096;
    private const long DefaultCapacity = Block * 64;

    private static VirtualTapeMedia NewMedia(long capacity = DefaultCapacity, uint blockSize = Block)
        => new VirtualMediaOnlyFixture(blockSize, capacity).Media;

    /// <summary>A block filled with <paramref name="fill"/> — distinct per block, so reads prove identity.</summary>
    private static byte[] Filled(byte fill, uint size = Block)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    /// <summary>Writes one <paramref name="fill"/>-filled block and asserts the full write landed.</summary>
    private static void WriteBlock(VirtualTapeMedia media, byte fill, uint size = Block)
        => Assert.Equal((int)size, media.WriteBlocks(Filled(fill, size), 0, (int)size));

    /// <summary>Reads one block at the current position and returns it (mark type via out).</summary>
    private static byte[] ReadBlock(VirtualTapeMedia media, out TapeMarkType mark, uint size = Block)
    {
        var buf = new byte[size];
        media.ReadBlocks(buf, 0, (int)size, out mark);
        return buf;
    }

    /// <summary>Asserts that the block at <paramref name="block"/> is entirely <paramref name="fill"/>.</summary>
    private static void AssertBlockAt(VirtualTapeMedia media, long block, byte fill, uint size = Block)
    {
        Assert.True(media.SeekToBlock(block), $"seek to block {block} failed");
        var buf = ReadBlock(media, out _, size);
        Assert.All(buf, b => Assert.Equal(fill, b));
    }

    #endregion

    #region *** Position consistency — the regression family ***

    /// <summary>
    /// THE regression test for the <c>SpaceMarks</c> defect: spacing forward past a filemark must leave the
    ///  BACKING STREAM positioned there too, so the next write overwrites the data after the mark rather
    ///  than whatever the stream last touched. Mirrors the header layout ‹MH›&lt;FM&gt;[content].
    /// </summary>
    [Fact]
    public void SpaceMarks_Forward_PositionsStreamAfterTheMark_SoNextWriteAppendsThere()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);                          // "header" block
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xC0);                          // content
        WriteBlock(media, 0xC1);

        media.Rewind();
        Assert.Equal(1, media.SpaceMarks(TapeMarkType.Filemark, 1));   // land just past the mark

        // The write must land AFTER the mark. With the stream stale at 0 it would clobber the header.
        WriteBlock(media, 0xB0);

        AssertBlockAt(media, 0, 0xA0);                    // header survived — the actual bug
    }

    /// <summary>
    /// Backward spacing lands AT (before) the mark; a write there must truncate the tail and overwrite
    ///  the mark's position, leaving the data before it intact.
    /// </summary>
    [Fact]
    public void SpaceMarks_Backward_PositionsStreamBeforeTheMark()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xC0);

        Assert.Equal(-1, media.SpaceMarks(TapeMarkType.Filemark, -1));   // back to before the mark

        WriteBlock(media, 0xB0);                          // overwrites from the mark position onward

        AssertBlockAt(media, 0, 0xA0);                    // the block before the mark is untouched
        AssertBlockAt(media, 1, 0xB0);                    // the mark's slot now holds data
    }

    /// <summary>
    /// <c>SeekToBlock</c> at EOD must position the stream at the end, so the next write APPENDS.
    ///  (Previously the stream stayed stale and the write clobbered block 0.)
    /// </summary>
    [Fact]
    public void SeekToBlock_AtEod_PositionsStreamAtEnd_SoNextWriteAppends()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xAB);
        media.Rewind();

        Assert.True(media.SeekToBlock(media.TotalBlockCount));   // EOD
        WriteBlock(media, 0xCD);

        AssertBlockAt(media, 0, 0xAB);                    // first block survived
        Assert.Equal(2, media.TotalBlockCount);
    }

    /// <summary>Seeking onto a MARK must position the stream where the surrounding data ends.</summary>
    [Fact]
    public void SeekToBlock_OnAMark_PositionsStreamAtEndOfPrecedingData()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        WriteBlock(media, 0xA1);
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xC0);

        Assert.True(media.SeekToBlock(2));                // the mark's block
        WriteBlock(media, 0xB0);                          // overwrite from the mark onward

        AssertBlockAt(media, 0, 0xA0);                    // both preceding data blocks survive
        AssertBlockAt(media, 1, 0xA1);
        AssertBlockAt(media, 2, 0xB0);
    }

    /// <summary>Rewind must reset the stream too, so a write from BOT overwrites block 0.</summary>
    [Fact]
    public void Rewind_PositionsStreamAtZero_SoNextWriteOverwritesFirstBlock()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        WriteBlock(media, 0xA1);

        media.Rewind();
        WriteBlock(media, 0xB0);

        AssertBlockAt(media, 0, 0xB0);                    // block 0 replaced
        Assert.Equal(1, media.TotalBlockCount);           // …and the tail was truncated
    }

    /// <summary><c>SeekToEnd</c> must position the stream at the data end, so writes append.</summary>
    [Fact]
    public void SeekToEnd_PositionsStreamAtDataEnd_SoNextWriteAppends()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        media.Rewind();
        media.SeekToEnd();

        WriteBlock(media, 0xB0);

        AssertBlockAt(media, 0, 0xA0);
        AssertBlockAt(media, 1, 0xB0);
    }

    /// <summary>
    /// Read → space → write: the composite path the agent actually walks when it reads the media header,
    ///  spaces over its filemark, and writes content. Each step must leave both positions coherent.
    /// </summary>
    [Fact]
    public void ReadThenSpaceThenWrite_KeepsPositionsCoherent()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);                          // header
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xC0);                          // old content

        media.Rewind();
        var header = ReadBlock(media, out _);             // read the header (head now BEFORE the mark)
        Assert.All(header, b => Assert.Equal(0xA0, b));

        Assert.Equal(1, media.SpaceMarks(TapeMarkType.Filemark, 1));   // step over the mark
        WriteBlock(media, 0xB0);                          // new content replaces the old

        AssertBlockAt(media, 0, 0xA0);
        AssertBlockAt(media, 2, 0xB0);
    }

    #endregion

    #region *** Block numbering & layout ***

    /// <summary>
    /// Pins the emulation's block-numbering rule: a tape MARK occupies one logical block position.
    ///  (Real drives generally do not number marks — this is a deliberate emulation simplification, and
    ///  the constant that <c>VirtualTapeFixture.FirstContentBlock</c> is derived from.)
    /// </summary>
    [Fact]
    public void Marks_OccupyOneLogicalBlockPosition()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.Equal(1, media.TotalBlockCount);

        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        Assert.Equal(2, media.TotalBlockCount);           // the mark advanced the block count

        WriteBlock(media, 0xC0);
        Assert.Equal(3, media.TotalBlockCount);

        AssertBlockAt(media, 2, 0xC0);                    // content sits at block 2, past block+mark
    }

    /// <summary>Contiguous same-size writes coalesce into one virtual block but keep per-block identity.</summary>
    [Fact]
    public void ContiguousWrites_CoalesceYetRemainIndividuallyAddressable()
    {
        using var media = NewMedia();

        for (byte i = 0; i < 5; i++)
            WriteBlock(media, (byte)(0xD0 + i));

        Assert.Equal(5, media.TotalBlockCount);

        for (byte i = 0; i < 5; i++)
            AssertBlockAt(media, i, (byte)(0xD0 + i));
    }

    /// <summary>Reading stops AT a mark, reports its type, and leaves the head past it.</summary>
    [Theory]
    [InlineData(TapeMarkType.Filemark)]
    [InlineData(TapeMarkType.Setmark)]
    public void ReadBlocks_StopsAtMark_AndReportsItsType(TapeMarkType markType)
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.True(media.WriteMark(markType));
        WriteBlock(media, 0xC0);

        media.Rewind();

        var buf = new byte[Block * 3];
        int read = media.ReadBlocks(buf, 0, (int)Block * 3, out var mark);

        Assert.Equal((int)Block, read);                   // only the data before the mark
        Assert.Equal(markType, mark);

        // The head is past the mark: the next read returns the following block.
        var next = ReadBlock(media, out _);
        Assert.All(next, b => Assert.Equal(0xC0, b));
    }

    /// <summary>Reading at EOD yields no data and reports <see cref="TapeMarkType.EndOfData"/>.</summary>
    [Fact]
    public void ReadBlocks_AtEod_ReportsEndOfData()
    {
        using var media = NewMedia();
        WriteBlock(media, 0xA0);

        media.SeekToEnd();

        var buf = new byte[Block];
        Assert.Equal(0, media.ReadBlocks(buf, 0, (int)Block, out var mark));
        Assert.Equal(TapeMarkType.EndOfData, mark);
    }

    #endregion

    #region *** Truncation & byte accounting ***

    /// <summary>An overwrite mid-tape truncates the tail — new EOD, reclaimed capacity.</summary>
    [Fact]
    public void WriteAfterBackwardSeek_TruncatesTail_AndReclaimsCapacity()
    {
        using var media = NewMedia();

        for (byte i = 0; i < 6; i++)
            WriteBlock(media, (byte)(0xE0 + i));

        long remainingWhenFull = media.Remaining;

        Assert.True(media.SeekToBlock(2));
        WriteBlock(media, 0x99);

        Assert.Equal(3, media.TotalBlockCount);           // blocks 0,1 + the rewritten block 2
        Assert.True(media.Remaining > remainingWhenFull,  // the tail's space came back
            "overwriting mid-tape must reclaim the truncated tail");

        AssertBlockAt(media, 0, 0xE0);
        AssertBlockAt(media, 1, 0xE1);
        AssertBlockAt(media, 2, 0x99);
    }

    /// <summary>Truncation at a mark boundary drops the mark and everything after it.</summary>
    [Fact]
    public void WriteAtMarkPosition_DropsMarkAndTail()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xC0);

        Assert.True(media.SeekToBlock(1));                // the mark
        WriteBlock(media, 0xB0);

        Assert.Equal(2, media.TotalBlockCount);

        // Reading from BOT now returns two DATA blocks with no intervening mark.
        media.Rewind();
        var buf = new byte[Block * 2];
        Assert.Equal((int)Block * 2, media.ReadBlocks(buf, 0, (int)Block * 2, out var mark));
        Assert.Equal(TapeMarkType.None, mark);
    }

    /// <summary>Repeated rewind-and-overwrite cycles must not drift the byte accounting.</summary>
    [Fact]
    public void RepeatedOverwriteCycles_KeepByteAccountingStable()
    {
        using var media = NewMedia();

        long remainingAfterFirstCycle = 0;

        for (int cycle = 0; cycle < 5; cycle++)
        {
            media.Rewind();
            for (byte i = 0; i < 4; i++)
                WriteBlock(media, (byte)(0xF0 + i));

            if (cycle == 0)
                remainingAfterFirstCycle = media.Remaining;
            else
                Assert.Equal(remainingAfterFirstCycle, media.Remaining);   // no drift

            Assert.Equal(4, media.TotalBlockCount);
        }
    }

    /// <summary><c>Reset</c> returns the medium to a pristine, fully writable state.</summary>
    [Fact]
    public void Reset_ClearsContentAndRestoresFullCapacity()
    {
        using var media = NewMedia();

        for (byte i = 0; i < 4; i++)
            WriteBlock(media, (byte)(0x50 + i));
        Assert.True(media.WriteMark(TapeMarkType.Filemark));

        media.Reset();

        Assert.Equal(0, media.TotalBlockCount);
        Assert.Equal(0, media.CurrentBlock);
        Assert.Equal(DefaultCapacity, media.Remaining);
    }

    #endregion

    #region *** Capacity enforcement ***

    /// <summary>Hard EOM lands at the TRUE capacity: the last fitting write succeeds, the next fails.</summary>
    [Fact]
    public void WriteBlocks_StopsExactlyAtCapacity()
    {
        const int blocks = 4;
        using var media = NewMedia(capacity: Block * blocks);

        for (int i = 0; i < blocks; i++)
            WriteBlock(media, (byte)(0x60 + i));

        Assert.Equal(0, media.Remaining);
        Assert.Equal(0, media.WriteBlocks(Filled(0xFF), 0, (int)Block));   // refused at hard EOM
    }

    /// <summary>A mark cannot be written on a genuinely full medium (the capacity guard is not over-relaxed).</summary>
    [Fact]
    public void WriteMark_AtHardEom_IsRefused()
    {
        const int blocks = 3;
        using var media = NewMedia(capacity: Block * blocks);

        for (int i = 0; i < blocks; i++)
            WriteBlock(media, (byte)(0x70 + i));

        Assert.False(media.WriteMark(TapeMarkType.Filemark));
    }

    /// <summary>
    /// …but on a FULL medium, seeking back and overwriting must succeed — truncation reclaims the tail
    ///  first (the resumable-calibration scenario).
    /// </summary>
    [Fact]
    public void WriteAfterBackwardSeek_OnFullMedium_Succeeds()
    {
        const int blocks = 4;
        using var media = NewMedia(capacity: Block * blocks);

        for (int i = 0; i < blocks; i++)
            WriteBlock(media, (byte)(0x80 + i));
        Assert.Equal(0, media.Remaining);

        Assert.True(media.SeekToBlock(1));
        WriteBlock(media, 0x99);                          // must NOT report end-of-media

        AssertBlockAt(media, 0, 0x80);
        AssertBlockAt(media, 1, 0x99);
    }

    #endregion

    #region *** Strict write positioning (ResumeWriteFromMarkOnly) ***

    /// <summary>
    /// Under the strict-drive rule a write is accepted only at BOT, at EOD, or immediately after a mark.
    ///  These pin the emulation that validates the header's on-tape shape (see the header design doc).
    /// </summary>
    [Fact]
    public void StrictMode_WriteMidData_IsRejected()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        WriteBlock(media, 0xA1);
        WriteBlock(media, 0xA2);

        media.ResumeWriteFromMarkOnly = true;

        Assert.True(media.SeekToBlock(1));                // mid-data: neither BOT, EOD, nor post-mark
        Assert.Equal(0, media.WriteBlocks(Filled(0xB0), 0, (int)Block));
    }

    [Fact]
    public void StrictMode_WriteAtBot_AtEod_AndAfterMark_AreAccepted()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xC0);

        media.ResumeWriteFromMarkOnly = true;

        // EOD
        media.SeekToEnd();
        Assert.Equal((int)Block, media.WriteBlocks(Filled(0xE0), 0, (int)Block));

        // Just after the mark (the header's content-start position).
        media.Rewind();
        Assert.Equal(1, media.SpaceMarks(TapeMarkType.Filemark, 1));
        Assert.Equal((int)Block, media.WriteBlocks(Filled(0xB0), 0, (int)Block));

        // BOT
        media.Rewind();
        Assert.Equal((int)Block, media.WriteBlocks(Filled(0xF0), 0, (int)Block));
    }

    #endregion

    #region *** Sequential mark spacing ***

    /// <summary>Sequential spacing requires ADJACENT marks; an interrupted run does not count.</summary>
    [Fact]
    public void SpaceSequentialMarks_RequiresAdjacentMarks()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xC0);

        media.Rewind();
        Assert.Equal(3, media.SpaceSequentialMarks(TapeMarkType.Filemark, 3));

        // The head is past the run: the next read returns the following data block.
        var next = ReadBlock(media, out _);
        Assert.All(next, b => Assert.Equal(0xC0, b));
    }

    [Fact]
    public void SpaceSequentialMarks_InterruptedRun_IsNotFound()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        WriteBlock(media, 0xA1);                          // data breaks the run
        Assert.True(media.WriteMark(TapeMarkType.Filemark));

        media.Rewind();
        Assert.Equal(0, media.SpaceSequentialMarks(TapeMarkType.Filemark, 2));
    }

    /// <summary>Spacing past the end of data reports failure and does not wedge the position.</summary>
    [Fact]
    public void SpaceMarks_BeyondAvailable_FailsAndStaysUsable()
    {
        using var media = NewMedia();

        WriteBlock(media, 0xA0);
        Assert.True(media.WriteMark(TapeMarkType.Filemark));

        media.Rewind();
        Assert.Equal(1, media.SpaceMarks(TapeMarkType.Filemark, 5));   // only one mark exists

        // The medium is still usable: a write at the (EOD) position succeeds and appends.
        WriteBlock(media, 0xB0);
        AssertBlockAt(media, 0, 0xA0);
    }

    #endregion

    #region *** State persistence ***

    /// <summary>
    /// Saved state round-trips: block layout, byte accounting, and marks all survive a
    ///  serialize → deserialize cycle, and the reloaded medium reads back identical content.
    /// </summary>
    [Fact]
    public void SaveState_RoundTrips_LayoutAndContent()
    {
        var dataStream = new MemoryStream();
        var metaStream = new MemoryStream();

        long totalBlocks;
        {
            using var media = new VirtualTapeMedia(
                dataStream, minBlockSize: Block, maxBlockSize: Block, defaultBlockSize: Block,
                capacity: DefaultCapacity, ownsStream: false,
                metadataStream: metaStream, ownsMetadataStream: false, name: "RoundTrip");

            WriteBlock(media, 0xA0);
            Assert.True(media.WriteMark(TapeMarkType.Filemark));
            WriteBlock(media, 0xC0);

            totalBlocks = media.TotalBlockCount;
            Assert.True(media.SaveState());
        }

        using var reloaded = new VirtualTapeMedia(dataStream, ownsStream: false, metaStream, ownsMetadataStream: false);

        Assert.Equal(totalBlocks, reloaded.TotalBlockCount);
        Assert.Equal("RoundTrip", reloaded.Name);

        AssertBlockAt(reloaded, 0, 0xA0);
        AssertBlockAt(reloaded, 2, 0xC0);                 // past block 0 + the mark
    }

    #endregion

    #region *** Argument validation ***

    [Fact]
    public void WriteBlocks_NonBlockAlignedCount_IsRejected()
    {
        using var media = NewMedia();
        Assert.Equal(0, media.WriteBlocks(new byte[Block], 0, (int)Block - 1));
    }

    [Fact]
    public void ReadBlocks_NonBlockAlignedCount_IsRejected()
    {
        using var media = NewMedia();
        WriteBlock(media, 0xA0);
        media.Rewind();

        Assert.Equal(0, media.ReadBlocks(new byte[Block], 0, (int)Block - 1, out _));
    }

    [Fact]
    public void SeekToBlock_NegativeOrPastEnd_IsRejected()
    {
        using var media = NewMedia();
        WriteBlock(media, 0xA0);

        Assert.False(media.SeekToBlock(-1));
        Assert.False(media.SeekToBlock(media.TotalBlockCount + 1));
        Assert.True(media.SeekToBlock(media.TotalBlockCount));   // EOD itself IS valid
    }

    [Fact]
    public void SetBlockSize_OutOfRange_IsRejected()
    {
        using var media = NewMedia();

        Assert.False(media.SetBlockSize(Block / 2));      // below min (fixture pins min == max == Block)
        Assert.False(media.SetBlockSize(Block * 2));      // above max
        Assert.True(media.SetBlockSize(Block));
    }

    #endregion
}
