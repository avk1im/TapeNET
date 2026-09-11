using TapeLibNET.Virtual;   // VirtualTapeMedia, TapeMarkType

namespace TapeLibNET.Tests;

/// <summary>
/// Deterministic (no-hardware) tests of the tape write-position restriction that some real drives impose:
///  a WRITE is accepted only at BOP, at EOD, or immediately after a filemark/setmark — writing into the
///  MIDDLE of recorded data is drive-dependent and often rejected (classic on DLT/DDS; uncertain on AIT).
/// <para>
/// The virtual backend models this exactly via <see cref="VirtualTapeMedia.ResumeWriteFromMarkOnly"/>
///  (see <c>CanResumeWrite()</c>). With it enabled the virtual media behaves like a STRICT helical drive,
///  so these tests pin down whether the media-header layout's begin-of-content write survives on such a
///  drive — the exact concern the all-permissive default masks.
/// </para>
/// <para>
/// The finding they encode: the header's content-start write is at logical block 1. Because the header is
///  a lone DATA block with NO trailing mark (design D2), block 1 is "mid-data" ⇒ REJECTED under strict
///  mode (<see cref="StrictDrive_WriteAtBlock1_MidData_IsRejected"/>). Writing at EOD (header-only tape)
///  and writing just after a filemark are BOTH accepted — so a filemark after the header would make every
///  content-start a legal post-mark write on strict drives.
/// </para>
/// </summary>
public class StrictWritePositionTests
{
    private const uint Block = 16 * 1024;                 // matches the fixed header block
    private const long Capacity = 64L * 1024 * 1024;

    private static VirtualTapeMedia NewMedia()
    {
        // ownsStream: media disposes the MemoryStream on Dispose; no metadata stream needed (no SaveState).
        var media = new VirtualTapeMedia(new MemoryStream(), minBlockSize: 512, maxBlockSize: 256 * 1024,
            defaultBlockSize: Block, capacity: Capacity);
        Assert.True(media.SetBlockSize(Block), "SetBlockSize");
        return media;
    }

    private static byte[] Blocks(int count, byte seed)
    {
        var b = new byte[Block * count];
        for (int i = 0; i < b.Length; i++)
            b[i] = (byte)(i + seed);
        return b;
    }

    // ── The core finding: mid-data write is rejected by a strict drive ─────────

    [Fact]
    public void StrictDrive_WriteAtBlock1_MidData_IsRejected()
    {
        using var media = NewMedia();

        // Header (block 0) + following data (blocks 1..3) with NO mark between them — the exact shape of
        //  headed media whose transient TOC / first set sits at block 1.
        Assert.Equal((int)Block, media.WriteBlocks(Blocks(1, 0xA0), 0, (int)Block));   // header @ block 0
        Assert.Equal(3 * (int)Block, media.WriteBlocks(Blocks(3, 0xC0), 0, 3 * (int)Block)); // blocks 1..3

        media.ResumeWriteFromMarkOnly = true;   // ← emulate a strict helical drive (AIT/DLT/DDS-like)

        media.Rewind();
        Assert.True(media.SeekToBlock(1), "SeekToBlock(1)");

        int written = media.WriteBlocks(Blocks(1, 0xB0), 0, (int)Block);

        // A strict drive REFUSES to begin writing mid-data (block 1 is neither BOT, EOD, nor post-mark).
        //  This is the media header's begin-of-content write on such a drive — it would fail.
        Assert.Equal(0, written);
        // (Diagnostic: media.LastErrorMessage ~ "Write must resume from BOT, EOD, or tape mark".)
    }

    // ── Case A: header-only tape ⇒ content-start is EOD ⇒ accepted everywhere ──

    [Fact]
    public void StrictDrive_WriteAtBlock1_AtEod_IsAccepted()
    {
        using var media = NewMedia();

        Assert.Equal((int)Block, media.WriteBlocks(Blocks(1, 0xA0), 0, (int)Block));   // only header; EOD @ block 1

        media.ResumeWriteFromMarkOnly = true;

        media.Rewind();
        Assert.True(media.SeekToBlock(1), "SeekToBlock(1) == EOD");

        int written = media.WriteBlocks(Blocks(1, 0xB0), 0, (int)Block);
        Assert.Equal((int)Block, written);   // EOD write is always legal, even on strict drives
    }

    // ── The insurance: a filemark after the header makes content-start post-mark ─

    [Fact]
    public void StrictDrive_WriteAfterHeaderFilemark_IsAccepted()
    {
        using var media = NewMedia();

        Assert.Equal((int)Block, media.WriteBlocks(Blocks(1, 0xA0), 0, (int)Block));   // header @ block 0
        Assert.True(media.WriteMark(TapeMarkType.Filemark), "header filemark @ block 1");
        Assert.Equal(3 * (int)Block, media.WriteBlocks(Blocks(3, 0xC0), 0, 3 * (int)Block)); // blocks 2..4

        media.ResumeWriteFromMarkOnly = true;

        media.Rewind();
        Assert.True(media.SeekToBlock(2), "SeekToBlock(2) — just after the header filemark");

        int written = media.WriteBlocks(Blocks(1, 0xB0), 0, (int)Block);
        Assert.Equal((int)Block, written);   // post-mark write is legal on strict drives → header-FM is safe

        // …and the header (block 0) survived the truncating overwrite.
        media.Rewind();
        var hdr = new byte[Block];
        Assert.Equal((int)Block, media.ReadBlocks(hdr, 0, (int)Block, out _));
        Assert.Equal(Blocks(1, 0xA0), hdr);
    }

    // ── Baseline: the permissive default (our normal virtual drive) accepts mid-data ─

    [Fact]
    public void PermissiveDrive_WriteAtBlock1_MidData_IsAccepted()
    {
        using var media = NewMedia();   // ResumeWriteFromMarkOnly stays false — the LTO-like default

        media.WriteBlocks(Blocks(1, 0xA0), 0, (int)Block);
        media.WriteBlocks(Blocks(3, 0xC0), 0, 3 * (int)Block);

        media.Rewind();
        Assert.True(media.SeekToBlock(1));
        int written = media.WriteBlocks(Blocks(1, 0xB0), 0, (int)Block);

        Assert.Equal((int)Block, written);   // this is what every current headed test relies on
    }
}
