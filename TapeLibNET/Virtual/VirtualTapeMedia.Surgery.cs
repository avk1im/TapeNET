#if TEST_HOOKS
namespace TapeLibNET.Virtual;

/// <summary>
/// Tape surgery for tests (Design-TocLocation §10): build a DAMAGED cartridge deterministically — cut the tape short,
///  remove a mark, corrupt a block at rest. Every operation edits the medium's own records directly and leaves the head
///  at BOM; no write path is involved, so nothing the library writes is mimicked by hand.
/// </summary>
public partial class VirtualTapeMedia
{
    /// <summary>The block layout, for a test to find what to cut (marks, data runs).</summary>
    internal IReadOnlyList<BlockInfo> Layout => GetBlockLayout();

    /// <summary>The layout as text, for assertion messages.</summary>
    internal string DescribeLayout() => FormatBlockLayout();

    /// <summary>
    /// Ends the data at <paramref name="block"/>: everything from there on is gone, exactly as if a write had happened
    ///  there and the drive had failed right after.
    /// </summary>
    internal void TruncateAt(long block)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(block);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(block, TotalBlockCount);
        ResetError();
        m_currentBlock = block;
        SyncVirtualBlockIndex();
        TruncateFromCurrentPosition();
        Rewind();
    }

    /// <summary>
    /// Removes the mark at <paramref name="block"/>; everything after it moves one block earlier. The data stays where it
    ///  was in the backing stream, so data blocks keep their contents.
    /// </summary>
    /// <exception cref="ArgumentException">No mark stands at <paramref name="block"/>.</exception>
    internal void RemoveMarkAt(long block)
    {
        int i = m_virtualBlocks.FindIndex(vb => vb.IsMark && vb.BeginAtBlock == block);
        if (i < 0)
            throw new ArgumentException($"No mark at block {block}", nameof(block));
        m_virtualBlocks.RemoveAt(i);
        for (int j = i; j < m_virtualBlocks.Count; j++)
            m_virtualBlocks[j] = m_virtualBlocks[j] with { BeginAtBlock = m_virtualBlocks[j].BeginAtBlock - 1 };
        m_stateDirty = true;
        Rewind();
    }

    /// <summary>
    /// Flips the byte at <paramref name="offset"/> of data block <paramref name="block"/> (XOR <paramref name="xor"/>) —
    ///  damage at rest: every read returns it, unlike an injected read fault.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="block"/> is a mark or beyond the data.</exception>
    internal void CorruptBlockAt(long block, int offset, byte xor = 0xFF)
    {
        int i = FindVirtualBlockIndex(block);
        if (i >= m_virtualBlocks.Count || m_virtualBlocks[i].IsMark)
            throw new ArgumentException($"No data block at {block}", nameof(block));
        VirtualTapeBlock vb = m_virtualBlocks[i];
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, (int)vb.BlockSize);

        long position = vb.GetStreamOffsetForBlock(block) + offset;
        long saved = m_stream.Position;
        m_stream.Position = position;
        int b = m_stream.ReadByte();
        m_stream.Position = position;
        m_stream.WriteByte((byte)(b ^ xor));
        m_stream.Position = saved;
        m_stateDirty = true;
    }
}
#endif
