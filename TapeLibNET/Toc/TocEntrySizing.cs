using System.Text;

using TapeLibNET.Format;

namespace TapeLibNET.Toc;


/// <summary>
/// Tight upper bounds of TOC file entries, computed — not serialized (Design-TocReserve §9.4). Every 2.1 field is
///  <c>tag varuint ‖ length varuint ‖ value</c>; a tag of a field number ≤ 63 is one byte. So an entry's size follows
///  from its values by arithmetic: exact for what is known when a file is handed to the packer (name and its shared
///  prefix, id, length, attributes, times), maximal for what is not (address, size on tape, hash, codec).
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule for writer and meter.</b> <see cref="TapeSetTOC.WriteBatches"/> closes a batch on the sum of
///  <see cref="GroupBound"/>, not on the bytes it actually wrote — so the writer and <see cref="TocEntryMeter"/> agree on
///  every batch break, and with it on every front-coding reset. A break on ACTUAL bytes could fall where the meter
///  credits a shared prefix the TOC does not get. Readers accept any batching; the format is unchanged.
/// </para>
/// <para><b>Allocation-free</b> (TB-4): spans and arithmetic only.</para>
/// </remarks>
internal static class TocEntrySizing
{
    /// <summary>Largest prologue of a <c>TocFileBatch</c> record.</summary>
    internal const int BatchPrologueMax = TapeFormat.MaxPrologueLength;

    /// <summary>The largest hash any algorithm writes (XxHash128).</summary>
    internal const int MaxHashBytes = 16;

    private static int Field(int valueLength) => 1 + TapePrimitives.VarUIntSize((ulong)valueLength) + valueLength;
    private static int UIntField(ulong value) => Field(TapePrimitives.VarUIntSize(value));
    private static int OptionalUIntField(ulong value) => value == 0 ? 0 : UIntField(value);
    private static int TimeField(DateTime time)
        => UIntField(TapePrimitives.ZigZagEncode(TapePrimitives.ToUtcTicks(time)));

    /// <summary>
    /// Upper bound of <paramref name="file"/>'s entry group inside a batch — group tag and length included — when the
    ///  previous entry of its batch is <paramref name="previous"/> ("" at a batch start). Mirrors <c>TocFileEntryWire</c>.
    /// </summary>
    internal static int GroupBound(TapeFileInfo file, string previous)
    {
        TapeFileDescriptor d = file.FileDescr;
        string name = d.FullName ?? "";
        int body = UIntField(file.FileId)                                     //  1 FileId (required)
            + Field(TapeFormat.MaxVarUIntBytes)                               //  2 AddressBlock: unknown at hand-off
            + Field(5)                                                        //  3 AddressOffset (uint): unknown
            + OptionalUIntField(d.Length > 0 ? (ulong)d.Length : 0UL)         //  4 Length
            + Field(TapeFormat.MaxVarUIntBytes)                               //  5 SizeOnTape: unknown
            + OptionalUIntField((uint)d.Attributes)                           //  6 Attributes (bitmask)
            + Field(1)                                                        //  7 Codec: unknown, assumed written
            + TimeField(d.CreationTime)                                       //  8
            + TimeField(d.LastWriteTime)                                      //  9
            + TimeField(d.LastAccessTime)                                     // 10
            + Field(MaxHashBytes);                                            // 33 Hash: unknown, the largest
        if (TapeTextRules.IsWellFormedUtf16(name))
        {
            int shared = SharedPrefix(name, previous);
            body += OptionalUIntField((ulong)shared)                          // 11 NameShared
                + Field(Encoding.UTF8.GetByteCount(name.AsSpan(shared)));     // 32 NameSuffix (required)
        }
        else
            body += Field(0) + Field(2 * name.Length);                        // 32 "" and 34 NameUtf16
        return Field(body);                                                   // 48: the group itself
    }

    /// <summary>The front coder's shared prefix — <see cref="TapeNameFrontCoder.Encode"/> without its state.</summary>
    internal static int SharedPrefix(string name, string previous)
    {
        int limit = Math.Min(name.Length, previous.Length);
        int n = 0;
        while (n < limit && name[n] == previous[n])
            n++;
        if (n > 0 && n < name.Length && char.IsHighSurrogate(name[n - 1]) && char.IsLowSurrogate(name[n]))
            n--;
        return n;
    }
}

/// <summary>
/// The TOC bytes each file of a set adds, measured as it is handed to the packer (Design-TocReserve §9.4): mirrors
///  <see cref="TapeSetTOC.WriteBatches"/> — its batch breaks and its front coder — with <see cref="TocEntrySizing"/>.
///  One per agent, reset per set; allocation-free per file.
/// </summary>
/// <remarks>
/// Two steps per file: <see cref="Measure"/> before the hand-off (the packer needs the figure with the file's first
///  byte), <see cref="Accept"/> after a successful one. A file that fails is never accepted: the next file is measured
///  against the name the TOC will actually encode it against.
/// </remarks>
internal sealed class TocEntryMeter
{
    private int m_inBatch;
    private long m_batchBound;
    private string m_previous = "";

    private string? m_pendingName;
    private int m_pendingGroup;
    private bool m_pendingOpensBatch;

    /// <summary>Starts a set: mirrors its files already on record (a loaded or reused set) — usually none.</summary>
    public void Reset(TapeSetTOC set)
    {
        m_inBatch = 0;
        m_batchBound = 0;
        m_previous = "";
        m_pendingName = null;
        if (set.IsLocked)
            return;                     // never appended to (E14); a locked set has no visible files anyway
        foreach (TapeFileInfo file in set)
        {
            Measure(file);
            Accept();
        }
    }

    /// <summary>
    /// The TOC bytes <paramref name="file"/> would add as the next entry of the set: its group, plus a batch prologue
    ///  when it opens a batch. Plain-set bytes; an encrypted set grows them with <see cref="TapeSetTOC.EstimateEntryGrowth"/>.
    /// </summary>
    public long Measure(TapeFileInfo file)
    {
        bool opens = m_inBatch == 0 || m_inBatch >= TapeSetTOC.MaxFilesPerBatch
            || m_batchBound >= TapeSetTOC.MaxBatchBodyBytes;
        int group = TocEntrySizing.GroupBound(file, opens ? "" : m_previous);
        m_pendingName = file.FileDescr.FullName ?? "";
        m_pendingGroup = group;
        m_pendingOpensBatch = opens;
        return group + (opens ? TocEntrySizing.BatchPrologueMax : 0);
    }

    /// <summary>The file last measured will be in the TOC: advance. A no-op without a pending measurement.</summary>
    public void Accept()
    {
        if (m_pendingName is null)
            return;
        if (m_pendingOpensBatch)
        {
            m_inBatch = 0;
            m_batchBound = 0;
        }
        m_inBatch++;
        m_batchBound += m_pendingGroup;
        m_previous = m_pendingName;
        m_pendingName = null;
    }
}
