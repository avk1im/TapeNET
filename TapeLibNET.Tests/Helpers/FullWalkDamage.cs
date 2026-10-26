#if TEST_HOOKS
using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Toc;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Damages a written volume AT REST so that only the full-media walk (S4) can still find its TOC — per layout
///  (Design-TocLocation §4). Optionally also defeats identification steps 1 and 3 of a resume (media header, set header).
/// </summary>
/// <remarks>
/// <para>
/// <b>Works on the medium alone</b> (<see cref="VirtualTapeMedia"/>: surgery, plus its own <c>WriteBlocks</c> /
///  <c>WriteMark</c> at end of data) — so the agent tests and the service tests share it; the service has no raw writes.
///  The medium's block size is put back afterwards: <see cref="TapeDrive"/> caches it.
/// </para>
/// <list type="table">
///  <item><term>Setmarks</term><description>remove the last setmark — S0 finds no setmark back from end-of-data, S3's
///   recorded block is now off by one, S2 has no setmark anchor.</description></item>
///  <item><term>FilemarksOnly</term><description>remove the set's closing filemark (S3 off by one) and append debris:
///   six small files — S0 (3 filemarks back) and every S2 anchor (≤ 5 back) land in it.</description></item>
///  <item><term>SeqFilemarks</term><description>remove one filemark of the TOC mark (S0's forward search for three
///   consecutive filemarks fails), corrupt copy 2 (S3 now lands on it), append debris (S2).</description></item>
///  <item><term>Partitions</term><description>blank the TOC partition — S0, S1 and S2 (the whole partition) find nothing;
///   S3 does not apply. S4 walks the content partition: it recovers a copy only on a mixed-up cartridge, so
///   <c>ownToc</c> writes the volume's own TOC there.</description></item>
/// </list>
/// <para>Debris blocks are small (the medium's minimum, ≥ 512 B): a full volume has little room, and a block of another
///  size than 16 KiB never identifies as ours.</para>
/// </remarks>
internal static class FullWalkDamage
{
    private const int HeaderBlock = 16 * 1024;
    private const int DebrisFiles = 6;          // one more than the S2 anchor chain looks back

    /// <summary>Agent tests: as <see cref="Apply(DriveProfile, VirtualTapeDriveBackend, bool, TapeTOC?)"/>, reading the
    ///  volume's own TOC first when Partitions needs it in the content partition.</summary>
    public static void Apply(DriveProfile profile, TapeDrive drive, VirtualTapeDriveBackend backend,
        bool damageHeaders, bool copiesInContent = true)
    {
        TapeTOC? ownToc = profile == DriveProfile.Partitions && copiesInContent ? ReadOwnToc(drive) : null;
        Apply(profile, backend, damageHeaders, ownToc);
        Assert.True(drive.MoveToPartition(MediaPartition.Content, 0));
    }

    /// <summary>The damage itself, on the loaded medium.</summary>
    /// <param name="ownToc">Partitions only: written into the content partition (a mixed-up cartridge); null = not.</param>
    public static void Apply(DriveProfile profile, VirtualTapeDriveBackend backend, bool damageHeaders,
        TapeTOC? ownToc = null)
    {
        VirtualTapeMedia content = backend.ContentMedia!;
        var layout = content.Layout.ToList();
        var marks = layout.Where(b => b.IsMark).ToList();
        Assert.True(marks.Count >= 3, content.DescribeLayout());

        if (damageHeaders)
        {
            int firstMark = layout.FindIndex(b => b.IsMark);
            Assert.False(layout[firstMark + 1].IsMark, content.DescribeLayout());
            content.CorruptBlockAt(0, offset: 0);                                   // the media header
            content.CorruptBlockAt(layout[firstMark + 1].BeginAtBlock, offset: 0);  // the set header
        }

        switch (profile)
        {
            case DriveProfile.Setmarks:
                content.RemoveMarkAt(marks.Last(m => m.MarkType == TapeMarkType.Setmark).BeginAtBlock);
                break;

            case DriveProfile.FilemarksOnly:
                // [..set][FM][toc1][FM][toc2][FM]: the set's closing filemark is the third from the end
                content.RemoveMarkAt(marks[^3].BeginAtBlock);
                AppendDebris(content);
                break;

            case DriveProfile.SeqFilemarks:
                // [..set][FM][gap][FM][FM][FM][toc1][FM][toc2][FM]: copy 2 first (blocks shift after a removal)
                var toc2 = layout[layout.IndexOf(marks[^1]) - 1];
                content.CorruptBlockAt(toc2.BeginAtBlock, offset: 0);
                content.RemoveMarkAt(marks[^3].BeginAtBlock);                       // the TOC mark's last filemark
                AppendDebris(content);
                break;

            case DriveProfile.Partitions:
                backend.InitiatorMedia!.TruncateAt(0);
                if (ownToc is not null)
                {
                    AppendRawCopy(content, ownToc, copyIndex: 1);
                    AppendRawCopy(content, ownToc, copyIndex: 2);
                }
                break;
        }
        content.Rewind();
    }

    /// <summary>The loaded volume's own TOC, read before the damage — what a mixed-up cartridge would carry.</summary>
    public static TapeTOC ReadOwnToc(TapeDrive drive)
    {
        using var reader = new TapeAgentBase(drive, new TapeTOC());
        Assert.True(reader.RestoreTOC(), reader.LastErrorMessage);
        return reader.TOC;
    }

    private static void AppendDebris(VirtualTapeMedia media)
    {
        uint saved = media.BlockSize;
        uint size = Math.Max(media.MinBlockSize, 512u);
        media.SeekToEnd();
        Assert.True(media.SetBlockSize(size));
        var junk = new byte[size];
        new Random(13).NextBytes(junk);
        for (int i = 0; i < DebrisFiles; i++)
        {
            Assert.Equal(junk.Length, media.WriteBlocks(junk, 0, junk.Length));
            Assert.True(media.WriteMark(TapeMarkType.Filemark));
        }
        Assert.True(media.SetBlockSize(saved));
    }

    /// <summary>One TOC copy written raw: 16 KiB blocks and a closing filemark — exactly as a copy goes on tape.</summary>
    private static void AppendRawCopy(VirtualTapeMedia media, TapeTOC toc, byte copyIndex)
    {
        uint saved = media.BlockSize;
        using var ms = new MemoryStream();
        toc.SaveTo(ms, copyIndex, copyCount: 2);
        var bytes = new byte[(ms.Length + HeaderBlock - 1) / HeaderBlock * HeaderBlock];
        ms.ToArray().CopyTo(bytes, 0);
        media.SeekToEnd();
        Assert.True(media.SetBlockSize(HeaderBlock));
        Assert.Equal(bytes.Length, media.WriteBlocks(bytes, 0, bytes.Length));
        Assert.True(media.WriteMark(TapeMarkType.Filemark));
        Assert.True(media.SetBlockSize(saved));
    }
}
#endif
