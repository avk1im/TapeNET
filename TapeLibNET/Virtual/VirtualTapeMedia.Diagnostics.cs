using Microsoft.Extensions.Logging;

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


    #region *** Fault Injection (DEBUG) ***

#if DEBUG

    /// <summary>
    /// Optional block-level fault injectors, owned by <see cref="VirtualTapeDriveBackend"/> and shared by
    ///  reference so settings survive media reload. Null when nothing is injected.
    /// </summary>
    internal VirtualMediaFaultInjector? WriteFaults { get; set; }
    internal VirtualMediaFaultInjector? ReadFaults { get; set; }

    //  ══ Injected write faults: what actually lands on the medium ══════════════════
    //
    //  Example in all three: WriteBlocks(buf, 0, 2*BS) at EOD, one earlier block A on tape.
    //
    //  Legend   ▓ bytes committed AND described by a virtual block
    //           ▒ block committed, payload truncated (real data, then padding)
    //           ▨ block committed, structurally perfect, a few bits wrong
    //           · untouched      ^ logical head (m_currentBlock) after the call
    //
    //  ── 1 · CORRECT WRITE ─────────────────────────── returns 2*BS, WentOK ────────
    //
    //      stream   │▓▓▓ A ▓▓▓│▓▓▓ b0 ▓▓▓│▓▓▓ b1 ▓▓▓│· · · ·
    //      VB list  │ VB0 data│◄──── VB1 data ─────►│
    //      blocks   0         1          2          3
    //      head                                     ^  (EOD)
    //
    //      reader at block 1 → b0, b1.
    //
    //  ── 2 · PARTIAL ── PartialOnce(blocks: 1) ─── returns 1*BS, ERROR_WRITE_FAULT ─
    //      The prefix goes through the NORMAL path: written, described, committed.
    //      Only the remainder is lost.
    //
    //      stream   │▓▓▓ A ▓▓▓│▓▓▓ b0 ▓▓▓│· · · · · ·
    //      VB list  │ VB0 data│ VB1 data │
    //      blocks   0         1          2
    //      head                          ^  (EOD) — advanced by what SURVIVED
    //
    //      reader at block 1 → b0, then EOD. b1 never existed.
    //
    //  ── 3 · TORN ── TearOnce() ────────── returns 0, ERROR_WRITE_FAULT ────────────
    //      ▒ = block committed, content corrupt (half real data, half padding)
    //
    //      stream   │▓▓▓ A ▓▓▓│▒▒▒ b0' ▒▒│· · · · · ·
    //      VB list  │ VB0 data│ VB1 data │
    //      blocks   0         1          2
    //      head                          ^  EOD — ADVANCED, though the write "failed"
    //
    //      reader at block 1 → a full block of bytes that fails CRC. Not EOD. Not silence.
    //
    //      The library believes nothing was written and the head is at block 1.
    //      The medium says otherwise. That gap is why SH-6 must reset CurrentContentSet —
    //      and it is the ONLY thing Torn models that Fail does not.
    //
    //  ── 4 · CORRUPT ── CorruptOnce(bits: 2) ──── returns 2*BS, WentOK — NO ERROR ──
    //      ▨ = block committed, structurally perfect, two bits wrong near the front
    //
    //      stream   │▓▓▓ A ▓▓▓│▨▓▓ b0 ▓▓▓│▓▓▓ b1 ▓▓▓│· · · ·
    //      VB list  │ VB0 data│◄──── VB1 data ─────►│
    //      blocks   0         1          2          3
    //      head                                     ^  EOD — exactly as a clean write
    //
    //      Every observable says the write succeeded. A reader gets a full, correctly
    //      sized block. Only the CRC one layer up knows anything is wrong.
    //
    //  ── The contrast that matters ─────────────────────────────────────────────────
    //      Every mode leaves the medium in a state the library's own invariants accept.
    //      What separates them is the GAP between what the caller is told and what is
    //      true:
    //
    //        Fail     nothing written, error      → caller knows, medium unchanged
    //        Partial  prefix written, error       → caller knows, medium advanced by prefix
    //        Torn     block written, error        → caller MISINFORMED about position
    //        Corrupt  block written, NO error     → caller MISINFORMED about integrity
    //
    //      The bottom two are the ones worth testing against: they are the only states
    //      in which correct-looking library code proceeds on a false premise. Torn is
    //      why SH-6 must reset CurrentContentSet; Corrupt is why the framing CRC exists.
    //
    //  ── Why the invariants still hold in case 3 ───────────────────────────────────
    //      m_bytesWritten and CalculateStreamLength() are BOTH derived from the VB list
    //      (= 1*BS here), so AssertByteTotalConsistent passes. The stream position is
    //      restored to the pre-write offset, matching CurrentPositionBytes() at block 1,
    //      so AssertPositionConsistent passes. The physical stream is longer than either
    //      figure — that excess IS the orphan, and no invariant inspects it.
    //      The next write at this position truncates it away, exactly as on real tape.

    /// <summary>
    /// Applies an injected write fault. Returns the byte count to report to the caller.
    /// </summary>
    /// <remarks>
    /// <b>Every mode routes its surviving bytes through the normal <see cref="WriteBlocks"/> path</b>
    ///  (disarmed, so it cannot re-trigger), so all bookkeeping rules apply and both DEBUG invariants hold
    ///  with no position trickery. The modes differ only in WHAT is written and WHAT is reported:
    ///  <list type="bullet">
    ///   <item><see cref="VirtualFaultMode.Fail"/> — nothing written, error reported.</item>
    ///   <item><see cref="VirtualFaultMode.Partial"/> — a verbatim prefix written, error reported; the
    ///    returned count tells the caller exactly how far the medium got.</item>
    ///   <item><see cref="VirtualFaultMode.Torn"/> — one full block committed with a truncated payload,
    ///    error reported and zero returned. The caller therefore believes nothing was written while the
    ///    medium advanced: that DIVERGENCE is the hazard being modelled.</item>
    ///   <item><see cref="VirtualFaultMode.Corrupt"/> — everything written, a few bits wrong, and NO error
    ///    reported at all.</item>
    ///  </list>
    /// </remarks>
    private int ApplyInjectedWriteFault(VirtualMediaFaultInjector wf, byte[] buffer, int offset, int count)
    {
        switch (wf.Mode)
        {
            case VirtualFaultMode.Torn:
                {
                    // A real torn write commits a block that EXISTS and reads back, but whose content is
                    //  incomplete, so it fails framing/CRC. The medium advanced; the caller was told the write
                    //  failed. THAT divergence is the hazard — not debris beyond EOD, which is unreachable
                    //  and which the next write truncates anyway.
                    int blockBytes = (int)m_blockSize;
                    int goodBytes = wf.TornBytes >= 0 ? Math.Min(wf.TornBytes, blockBytes) : blockBytes / 2;

                    // One structurally whole, semantically corrupt block: real data, then padding.
                    byte[] torn = new byte[blockBytes];
                    Array.Copy(buffer, offset, torn, 0, Math.Min(goodBytes, Math.Min(blockBytes, count)));

                    // Route it through the NORMAL path (disarmed, so it cannot re-trigger) — every bookkeeping
                    //  rule then applies and both DEBUG invariants hold with no position trickery.
                    bool armed = wf.Enabled;
                    wf.Enabled = false;
                    try { WriteBlocks(torn, 0, blockBytes); }
                    finally { wf.Enabled = armed; }

                    SetError(wf.Error, $"INJECTED torn write: block committed, only {goodBytes} of {blockBytes} B valid");
                    LogErrorAsDebug("Injected torn write");
                    return 0;   // the CALLER's write failed — but the medium moved
                }

            case VirtualFaultMode.Partial:
                {
                    long partialBytes = (long)Math.Max(0, wf.PartialBlocks) * m_blockSize;
                    int partialCount = (int)Math.Min(partialBytes, count);
                    if (partialCount <= 0)
                        goto case VirtualFaultMode.Fail;

                    // Route the surviving prefix through the NORMAL path so every bookkeeping rule still
                    //  applies; disarm first so the recursive call cannot re-trigger.
                    bool armed = wf.Enabled;
                    wf.Enabled = false;
                    int written;
                    try { written = WriteBlocks(buffer, offset, partialCount); }
                    finally { wf.Enabled = armed; }

                    SetError(wf.Error, $"INJECTED partial write: {written} of {count} B written, then faulted");
                    LogErrorAsDebug("Injected partial write");
                    return written;
                }

            case VirtualFaultMode.Corrupt:
                {
                    // The ONLY mode that reports no error at all. Models host-path corruption (RAM/HBA/driver)
                    //  landing before the drive computes ECC: the drive faithfully stores the wrong bytes with
                    //  valid ECC and reads them back forever. Nothing below the application can catch it —
                    //  which is precisely what TapeFramer's CRC is for, and what this mode finally exercises.
                    byte[] copy = new byte[count];
                    Array.Copy(buffer, offset, copy, 0, count);
                    var flipped = wf.ApplyCorruption(copy, (int)m_blockSize);

                    bool armed = wf.Enabled;
                    wf.Enabled = false;
                    int written;
                    try { written = WriteBlocks(copy, 0, count); }   // normal path — all bookkeeping applies
                    finally { wf.Enabled = armed; }

                    // Deliberately NO SetError: the drive is happy, the library is happy, the bytes are wrong.
                    m_logger.LogTrace("{Prefix}: INJECTED corruption — {Bits} bit(s) flipped at offset(s) {Offsets}",
                        LogPrefix, wf.CorruptBits, string.Join(", ", flipped));
                    return written;
                }

            case VirtualFaultMode.Fail:
            default:
                SetError(wf.Error, "INJECTED write fault");
                LogErrorAsDebug("Injected write fault");
                return 0;
        }
    }

    //  ══ Injected read faults: what the caller gets back ═══════════════════════════
    //
    //  Example in all three: ReadBlocks(buf, 0, 2*BS, out mark) positioned at block 1,
    //  with blocks b0, b1 on tape following an earlier block A.
    //
    //  Legend   ▓ bytes delivered to the caller's buffer      · buffer left untouched
    //           ▨ bytes read, a few bits wrong
    //           ^ logical head (m_currentBlock) after the call
    //           The medium is READ-ONLY here — the stream and VB list never change.
    //
    //      on tape  │▓▓▓ A ▓▓▓│▓▓▓ b0 ▓▓▓│▓▓▓ b1 ▓▓▓│      (identical in all 3 cases)
    //      blocks   0         1          2          3
    //      head               ^  at entry
    //
    //  ── 1 · CORRECT READ ──────────────────────── returns 2*BS, WentOK ────────────
    //
    //      buffer   │▓▓▓ b0 ▓▓▓│▓▓▓ b1 ▓▓▓│
    //      head                            ^  block 3
    //      mark     None
    //
    //  ── 2 · PARTIAL ── PartialOnce(blocks: 1) ─── returns 1*BS, ERROR_READ_FAULT ──
    //      The prefix goes through the NORMAL path: real blocks, real head advance.
    //      Then the fault is reported on top.
    //
    //      buffer   │▓▓▓ b0 ▓▓▓│· · · · · ·│
    //      head                 ^  block 2 — advanced by what WAS delivered
    //      mark     None (a fault is not a tapemark)
    //
    //      A retry resumes at block 2 and gets b1 — the medium is undamaged.
    //
    //  ── 3 · FAIL ── FailOnce()  [also where Torn lands] ─── returns 0, ERROR_… ────
    //      Nothing transferred, nothing moved. The read simply did not happen.
    //
    //      buffer   │· · · · · ·│· · · · · ·│
    //      head               ^  block 1 — unchanged
    //      mark     None
    //
    //  ── 4 · CORRUPT ── CorruptOnce(bits: 2) ──── returns 2*BS, WentOK — NO ERROR ──
    //      The medium is untouched; only the delivered bytes are wrong. A re-read after
    //      re-positioning returns clean data — read-side corruption is TRANSIENT, exactly
    //      like a read fault, and unlike its write-side twin.
    //
    //      on tape  │▓▓▓ A  ▓▓▓│▓▓▓ b0 ▓▓▓│▓▓▓ b1 ▓▓▓│   ← unchanged, still correct
    //      buffer   │▨▓▓ b0 ▓▓▓│▓▓▓ b1 ▓▓▓│           ← two bits wrong near the front
    //      head                            ^  block 3 — a normal, successful read
    //      mark     None
    //
    //  ── How the four modes differ, in one line each ───────────────────────────────
    //      Fail     nothing written, error      → caller knows, medium unchanged
    //      Partial  prefix written, error       → caller knows, medium advanced by prefix
    //      Torn     block written, error        → caller MISINFORMED about position
    //      Corrupt  block written, NO error     → caller MISINFORMED about integrity
    //
    //  ── Why there is no read-side Torn ────────────────────────────────────────────
    //      Torn means "bytes on the medium that no virtual block describes" — a state
    //      only a WRITE can create. A read mutates neither the stream nor the VB list,
    //      so the concept has no referent; ApplyInjectedReadFault maps it to Fail.
    //
    //  ── Asymmetry worth remembering ───────────────────────────────────────────────
    //      Every read fault is TRANSIENT: the tape still holds b0 and b1, so a caller
    //      that re-positions and retries succeeds. Contrast the write side, where a
    //      torn write leaves permanent debris. That is why the restore path may retry
    //      a set-header read (Step 5 'Unreadable' → warn and proceed), while the write
    //      path never self-corrects (SH-10).
    //
    //  ── Injection point ───────────────────────────────────────────────────────────
    //      AFTER the end-of-data check, so an injected fault is always a MEDIUM fault
    //      and never a disguised EOD. markEncountered was set to None at the top of
    //      ReadBlocks, before any injection point, so both paths leave it correct.

    /// <summary>
    /// Applies an injected read fault. <see cref="VirtualFaultMode.Torn"/> has no meaning on the read side
    ///  — nothing is committed, so there is nothing to leave half-written — and is treated as
    ///  <see cref="VirtualFaultMode.Fail"/>.
    /// </summary>
    private int ApplyInjectedReadFault(VirtualMediaFaultInjector rf, byte[] buffer, int offset, int count)
    {
        switch (rf.Mode)
        {
            case VirtualFaultMode.Partial:
                {
                    long partialBytes = (long)Math.Max(0, rf.PartialBlocks) * m_blockSize;
                    int partialCount = (int)Math.Min(partialBytes, count);
                    if (partialCount <= 0)
                        goto default;

                    bool armed = rf.Enabled;
                    rf.Enabled = false;
                    int read;
                    try { read = ReadBlocks(buffer, offset, partialCount, out _); }
                    finally { rf.Enabled = armed; }

                    SetError(rf.Error, $"INJECTED short read: {read} of {count} B read, then faulted");
                    LogErrorAsDebug("Injected short read");
                    return read;
                }

            case VirtualFaultMode.Corrupt:
                {
                    // Models corruption on the RETURN path (HBA, driver, cable, RAM) — the tape itself is
                    //  undamaged, so a caller that re-reads gets clean data. Contrast write-side Corrupt,
                    //  which is permanent. Together they let a test distinguish "the medium is bad" from
                    //  "the path is bad" — a distinction no error code ever surfaces.
                    bool armed = rf.Enabled;
                    rf.Enabled = false;
                    int read;
                    try { read = ReadBlocks(buffer, offset, count, out _); }
                    finally { rf.Enabled = armed; }

                    if (read > 0)
                    {
                        // Corrupt only what was actually delivered, in place, at the caller's offset.
                        var delivered = new byte[read];
                        Array.Copy(buffer, offset, delivered, 0, read);
                        rf.ApplyCorruption(delivered, (int)m_blockSize);
                        Array.Copy(delivered, 0, buffer, offset, read);
                    }

                    // Deliberately NO SetError: the drive is happy, the library is happy, the bytes are wrong.
                    m_logger.LogTrace("{Prefix}: INJECTED read corruption — {Bits} bit(s) flipped in {Read} B",
                        LogPrefix, rf.CorruptBits, read);
                    return read;
                }

            default:
                SetError(rf.Error, "INJECTED read fault");
                LogErrorAsDebug("Injected read fault");
                return 0;
        }
    }

#endif

    #endregion

}
