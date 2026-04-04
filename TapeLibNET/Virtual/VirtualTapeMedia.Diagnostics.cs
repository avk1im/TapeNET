namespace TapeLibNET.Virtual;

/// <summary>
/// Diagnostic and comparison methods for <see cref="VirtualTapeMedia"/>.
/// Used by tests to compare the physical tape layout and content between two media instances
/// (e.g., WithPartitions vs WithSetmarks content areas).
/// </summary>
public partial class VirtualTapeMedia
{
    #region *** Block Layout Snapshot ***

    /// <summary>
    /// Describes a single virtual block in a human-readable snapshot.
    /// </summary>
    /// <param name="Index">Index within the virtual block list.</param>
    /// <param name="IsMark">True if this is a tape mark.</param>
    /// <param name="MarkType">Mark type (only meaningful if IsMark).</param>
    /// <param name="BlockSize">Logical block size (0 for marks).</param>
    /// <param name="BeginAtBlock">Starting logical block number.</param>
    /// <param name="EndBlock">Ending logical block number (exclusive).</param>
    /// <param name="DataLength">Total data bytes (0 for marks).</param>
    /// <param name="StreamOffset">Position in backing stream (-1 for marks).</param>
    internal readonly record struct BlockInfo(
        int Index,
        bool IsMark,
        TapeMarkType MarkType,
        uint BlockSize,
        long BeginAtBlock,
        long EndBlock,
        long DataLength,
        long StreamOffset)
    {
        /// <summary>Human-readable description of this block.</summary>
        public override string ToString() => IsMark
            ? $"[{Index}] MARK({MarkType}) @block {BeginAtBlock}"
            : $"[{Index}] DATA blocks {BeginAtBlock}..{EndBlock - 1} ({DataLength} bytes, bs={BlockSize}, stream@{StreamOffset})";
    }

    /// <summary>
    /// Returns a snapshot of all virtual blocks on this media.
    /// </summary>
    internal List<BlockInfo> GetBlockLayout()
    {
        List<BlockInfo> layout = new(m_virtualBlocks.Count);
        for (int i = 0; i < m_virtualBlocks.Count; i++)
        {
            var vb = m_virtualBlocks[i];
            layout.Add(new BlockInfo(
                Index: i,
                IsMark: vb.IsMark,
                MarkType: vb.MarkType,
                BlockSize: vb.BlockSize,
                BeginAtBlock: vb.BeginAtBlock,
                EndBlock: vb.EndBlock,
                DataLength: vb.DataLength,
                StreamOffset: vb.StreamOffset));
        }
        return layout;
    }

    /// <summary>
    /// Formats the complete block layout as a multi-line string for diagnostic output.
    /// </summary>
    internal string FormatBlockLayout()
    {
        var layout = GetBlockLayout();
        if (layout.Count == 0)
            return $"{m_name}: (empty)";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{m_name}: {layout.Count} virtual blocks, {m_bytesWritten} bytes written, {TotalBlockCount} logical blocks");
        foreach (var info in layout)
            sb.AppendLine($"  {info}");
        return sb.ToString();
    }

    #endregion

    #region *** Content Comparison ***

    /// <summary>
    /// Result of comparing two media instances.
    /// </summary>
    /// <param name="AreEqual">True if layout and data match within the compared range.</param>
    /// <param name="Message">Detailed description of the first mismatch found, or "identical" if equal.</param>
    /// <param name="ThisLayout">Block layout of this media (within compared range).</param>
    /// <param name="OtherLayout">Block layout of the other media (within compared range).</param>
    internal readonly record struct ComparisonResult(
        bool AreEqual,
        string Message,
        List<BlockInfo> ThisLayout,
        List<BlockInfo> OtherLayout)
    {
        /// <summary>Human-readable summary.</summary>
        public override string ToString() => AreEqual
            ? $"Identical ({ThisLayout.Count} blocks)"
            : $"DIFFERENT: {Message}";
    }

    /// <summary>
    /// Compares the full content of this media with another media.
    /// Checks both virtual block layout (structure) and backing stream data (bytes).
    /// </summary>
    internal ComparisonResult CompareWith(VirtualTapeMedia other)
    {
        return CompareRange(other, fromVirtualBlockIndex: 0, toVirtualBlockIndex: null,
            otherFromVirtualBlockIndex: 0, otherToVirtualBlockIndex: null);
    }

    /// <summary>
    /// Compares a range of virtual blocks (and their data) between this media and another.
    /// The range is specified by virtual block indices (inclusive start, exclusive end).
    /// Null end means "to the last block".
    /// </summary>
    /// <param name="other">The other media to compare with.</param>
    /// <param name="fromVirtualBlockIndex">Start index in this media (inclusive).</param>
    /// <param name="toVirtualBlockIndex">End index in this media (exclusive, null = all).</param>
    /// <param name="otherFromVirtualBlockIndex">Start index in the other media (inclusive).</param>
    /// <param name="otherToVirtualBlockIndex">End index in the other media (exclusive, null = all).</param>
    internal ComparisonResult CompareRange(
        VirtualTapeMedia other,
        int fromVirtualBlockIndex, int? toVirtualBlockIndex,
        int otherFromVirtualBlockIndex, int? otherToVirtualBlockIndex)
    {
        int thisEnd = toVirtualBlockIndex ?? m_virtualBlocks.Count;
        int otherEnd = otherToVirtualBlockIndex ?? other.m_virtualBlocks.Count;

        int thisCount = thisEnd - fromVirtualBlockIndex;
        int otherCount = otherEnd - otherFromVirtualBlockIndex;

        // Get layout snapshots for the compared ranges
        var thisLayout = GetBlockLayoutRange(fromVirtualBlockIndex, thisEnd);
        var otherLayout = other.GetBlockLayoutRange(otherFromVirtualBlockIndex, otherEnd);

        // Compare virtual block count
        if (thisCount != otherCount)
        {
            return new ComparisonResult(false,
                $"Virtual block count differs: {m_name} has {thisCount}, {other.m_name} has {otherCount}",
                thisLayout, otherLayout);
        }

        // Compare each virtual block structure and data
        for (int i = 0; i < thisCount; i++)
        {
            var thisVb = m_virtualBlocks[fromVirtualBlockIndex + i];
            var otherVb = other.m_virtualBlocks[otherFromVirtualBlockIndex + i];
            string ctx = $"virtual block [{i}]";

            // Compare structure
            if (thisVb.IsMark != otherVb.IsMark)
                return new ComparisonResult(false,
                    $"{ctx}: type differs — {(thisVb.IsMark ? "mark" : "data")} vs {(otherVb.IsMark ? "mark" : "data")}",
                    thisLayout, otherLayout);

            if (thisVb.IsMark)
            {
                if (thisVb.MarkType != otherVb.MarkType)
                    return new ComparisonResult(false,
                        $"{ctx}: mark type differs — {thisVb.MarkType} vs {otherVb.MarkType}",
                        thisLayout, otherLayout);
                continue; // Marks have no data to compare
            }

            // Data block — compare structural properties
            if (thisVb.BlockSize != otherVb.BlockSize)
                return new ComparisonResult(false,
                    $"{ctx}: block size differs — {thisVb.BlockSize} vs {otherVb.BlockSize}",
                    thisLayout, otherLayout);

            if (thisVb.DataLength != otherVb.DataLength)
                return new ComparisonResult(false,
                    $"{ctx}: data length differs — {thisVb.DataLength} vs {otherVb.DataLength}",
                    thisLayout, otherLayout);

            // Compare actual data bytes
            string? dataDiff = CompareStreamData(other, thisVb, otherVb);
            if (dataDiff != null)
                return new ComparisonResult(false, $"{ctx}: {dataDiff}", thisLayout, otherLayout);
        }

        return new ComparisonResult(true, "Identical", thisLayout, otherLayout);
    }

    /// <summary>
    /// Compares a range of content sets between this media and another.
    /// Sets are delimited by setmarks. Set 0 starts at the beginning of the media.
    /// </summary>
    /// <param name="other">The other media to compare with.</param>
    /// <param name="fromSet">First content set to compare (0-based, inclusive).</param>
    /// <param name="toSet">Last content set to compare (0-based, inclusive). -1 = last set.</param>
    internal ComparisonResult CompareContentSets(VirtualTapeMedia other, int fromSet = 0, int toSet = -1)
    {
        // Find set boundaries in this media
        var thisBounds = FindSetBoundaries(fromSet, toSet);
        var otherBounds = other.FindSetBoundaries(fromSet, toSet);

        if (thisBounds.ErrorMessage != null)
            return new ComparisonResult(false,
                $"{m_name}: {thisBounds.ErrorMessage}", GetBlockLayout(), other.GetBlockLayout());

        if (otherBounds.ErrorMessage != null)
            return new ComparisonResult(false,
                $"{other.m_name}: {otherBounds.ErrorMessage}", GetBlockLayout(), other.GetBlockLayout());

        return CompareRange(other,
            thisBounds.StartIndex, thisBounds.EndIndex,
            otherBounds.StartIndex, otherBounds.EndIndex);
    }

    #endregion

    #region *** Set Boundary Detection ***

    /// <summary>
    /// Result of finding set boundaries.
    /// StartIndex and EndIndex are virtual block indices (inclusive, exclusive).
    /// </summary>
    private readonly record struct SetBounds(int StartIndex, int EndIndex, string? ErrorMessage);

    /// <summary>
    /// Finds the virtual block index range spanning the given content sets.
    /// Sets are delimited by setmarks. Set 0 starts at VB index 0.
    /// </summary>
    private SetBounds FindSetBoundaries(int fromSet, int toSet)
    {
        // Find all setmark positions (these are the set delimiters)
        List<int> setmarkIndices = [];
        for (int i = 0; i < m_virtualBlocks.Count; i++)
        {
            if (m_virtualBlocks[i].IsMark && m_virtualBlocks[i].MarkType == TapeMarkType.Setmark)
                setmarkIndices.Add(i);
        }

        // Number of content sets = number of setmarks + 1 (last set has no trailing setmark before TOC)
        //  But the "last" region might be TOC data, not a content set — caller must account for that.
        int totalSets = setmarkIndices.Count + 1;

        if (toSet < 0) toSet = totalSets - 1;

        if (fromSet < 0 || fromSet >= totalSets)
            return new SetBounds(0, 0, $"fromSet {fromSet} out of range (total sets: {totalSets})");

        if (toSet < fromSet || toSet >= totalSets)
            return new SetBounds(0, 0, $"toSet {toSet} out of range (total sets: {totalSets}, fromSet: {fromSet})");

        // Start index: for set 0, start at VB 0; otherwise start after the (fromSet-1)th setmark
        int startIndex = fromSet == 0 ? 0 : setmarkIndices[fromSet - 1] + 1;

        // End index: for the last set, end at the end of virtual blocks;
        //  otherwise end at (and including) the toSet-th setmark
        int endIndex = toSet < setmarkIndices.Count
            ? setmarkIndices[toSet] + 1  // Include the trailing setmark
            : m_virtualBlocks.Count;     // Last set — goes to the end

        return new SetBounds(startIndex, endIndex, null);
    }

    /// <summary>
    /// Returns the number of content sets detected on this media.
    /// A content set is a region of data/marks between consecutive setmarks,
    /// or before the first setmark, or after the last setmark.
    /// </summary>
    internal int CountContentSets()
    {
        int setmarks = 0;
        for (int i = 0; i < m_virtualBlocks.Count; i++)
        {
            if (m_virtualBlocks[i].IsMark && m_virtualBlocks[i].MarkType == TapeMarkType.Setmark)
                setmarks++;
        }
        return m_virtualBlocks.Count > 0 ? setmarks + 1 : 0;
    }

    #endregion

    #region *** Diagnostic Helpers ***

    /// <summary>
    /// Returns block layout for a range of virtual blocks.
    /// </summary>
    private List<BlockInfo> GetBlockLayoutRange(int fromIndex, int toIndex)
    {
        int count = toIndex - fromIndex;
        List<BlockInfo> layout = new(count);
        for (int i = fromIndex; i < toIndex && i < m_virtualBlocks.Count; i++)
        {
            var vb = m_virtualBlocks[i];
            layout.Add(new BlockInfo(
                Index: i,
                IsMark: vb.IsMark,
                MarkType: vb.MarkType,
                BlockSize: vb.BlockSize,
                BeginAtBlock: vb.BeginAtBlock,
                EndBlock: vb.EndBlock,
                DataLength: vb.DataLength,
                StreamOffset: vb.StreamOffset));
        }
        return layout;
    }

    /// <summary>
    /// Compares backing stream data for two data virtual blocks.
    /// Returns null if identical, or a description of the first difference
    ///  including a hex dump of the surrounding bytes (±<paramref name="contextRadius"/> around the diff).
    /// </summary>
    private string? CompareStreamData(
        VirtualTapeMedia other, VirtualTapeBlock thisVb, VirtualTapeBlock otherVb,
        int contextRadius = 4)
    {
        const int chunkSize = 64 * 1024; // 64 KB comparison chunks
        byte[] thisBuf = new byte[chunkSize];
        byte[] otherBuf = new byte[chunkSize];

        long remaining = thisVb.DataLength;
        long thisOffset = thisVb.StreamOffset;
        long otherOffset = otherVb.StreamOffset;
        long compared = 0;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(chunkSize, remaining);

            m_stream.Position = thisOffset;
            int thisRead = m_stream.Read(thisBuf, 0, toRead);

            other.m_stream.Position = otherOffset;
            int otherRead = other.m_stream.Read(otherBuf, 0, toRead);

            if (thisRead != otherRead)
                return $"stream read size differs at offset {compared}: {thisRead} vs {otherRead}";

            if (thisRead == 0)
                return $"unexpected end of stream at offset {compared}";

            // Byte-by-byte comparison
            for (int j = 0; j < thisRead; j++)
            {
                if (thisBuf[j] != otherBuf[j])
                {
                    long diffOffset = compared + j;

                    // Extract context range: ±contextRadius around the difference
                    int ctxStart = Math.Max(0, j - contextRadius);
                    int ctxEnd = Math.Min(thisRead - 1, j + contextRadius);

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"data differs at byte offset {diffOffset}: 0x{thisBuf[j]:X2} vs 0x{otherBuf[j]:X2}");
                    sb.AppendLine($"  Context (offsets {compared + ctxStart}..{compared + ctxEnd}):");
                    sb.Append("  this:  ");
                    FormatHexContext(sb, thisBuf, ctxStart, ctxEnd, j);
                    sb.AppendLine();
                    sb.Append("  other: ");
                    FormatHexContext(sb, otherBuf, ctxStart, ctxEnd, j);

                    return sb.ToString();
                }
            }

            thisOffset += thisRead;
            otherOffset += otherRead;
            remaining -= thisRead;
            compared += thisRead;
        }

        return null; // Identical
    }

    /// <summary>
    /// Formats a hex dump of bytes from <paramref name="startIdx"/> to <paramref name="endIdx"/> (inclusive),
    /// marking the byte at <paramref name="diffIdx"/> with square brackets.
    /// </summary>
    private static void FormatHexContext(
        System.Text.StringBuilder sb, byte[] buf, int startIdx, int endIdx, int diffIdx)
    {
        for (int k = startIdx; k <= endIdx; k++)
        {
            if (k > startIdx) sb.Append(' ');
            if (k == diffIdx)
                sb.Append($"[{buf[k]:X2}]");
            else
                sb.Append($" {buf[k]:X2} ");
        }
    }

    #endregion
}
