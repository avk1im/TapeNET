using TapeLibNET.Format;

namespace TapeLibNET.Virtual;


/// <summary>
/// Format 2.1 record 0x0F01 holding the persisted state of a <see cref="VirtualTapeMedia"/>
///  (Design-Format-v2-AppendixA §A.4). Framed inline as <c>Record ‖ CRC-64</c>.
/// Only what cannot be derived is stored per virtual block; positions are rebuilt and cross-checked on load.
/// </summary>
internal sealed class VirtualMediaStateRecord : ITapeRecord<VirtualMediaStateRecord>
{
    // Run-coded entry kinds; identical to TapeMarkType values, with Data = 0 (= TapeMarkType.None)
    private const byte KindData = 0;
    private const byte KindFilemark = (byte)TapeMarkType.Filemark;
    private const byte KindSetmark = (byte)TapeMarkType.Setmark;

    public uint MinBlockSize { get; set; }
    public uint MaxBlockSize { get; set; }
    public uint DefaultBlockSize { get; set; }
    public ulong Capacity { get; set; }
    public ulong BytesWritten { get; set; }
    public ulong BlockCount { get; set; }
    public ulong TotalLogicalBlocks { get; set; }
    public string Name { get; set; } = "";
    public byte[] Blocks { get; set; } = [];

    private static readonly TapeSchema<VirtualMediaStateRecord> s_schema = new(TapeRecordKind.VirtualMediaState)
    {
        { 1, r => r.MinBlockSize, (r, v) => r.MinBlockSize = v, FieldFlags.Required },
        { 2, r => r.MaxBlockSize, (r, v) => r.MaxBlockSize = v, FieldFlags.Required },
        { 3, r => r.DefaultBlockSize, (r, v) => r.DefaultBlockSize = v, FieldFlags.Required },
        { 4, r => r.Capacity, (r, v) => r.Capacity = v, FieldFlags.Required },
        { 5, r => r.BytesWritten, (r, v) => r.BytesWritten = v, FieldFlags.Required },
        { 6, r => r.BlockCount, (r, v) => r.BlockCount = v, FieldFlags.Required },
        { 7, r => r.TotalLogicalBlocks, (r, v) => r.TotalLogicalBlocks = v, FieldFlags.Required },
        { 32, r => r.Name, (r, v) => r.Name = v },
        { 48, r => r.Blocks, (r, v) => r.Blocks = v, FieldFlags.Required },
    };

    public TapeRecordKind RecordKind => TapeRecordKind.VirtualMediaState;

    public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);

    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.VirtualMediaState;

    public static VirtualMediaStateRecord ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new VirtualMediaStateRecord());

    #region *** Run coding ***

    /// <summary>Builds the record for a media's current state; zero-length data blocks are not stored.</summary>
    public static VirtualMediaStateRecord Create(uint minBlockSize, uint maxBlockSize, uint defaultBlockSize,
        long capacity, long bytesWritten, long totalLogicalBlocks, string name, IReadOnlyList<VirtualTapeBlock> blocks)
    {
        var buffer = new List<byte>(blocks.Count * 4);
        Span<byte> scratch = stackalloc byte[TapeFormat.MaxVarUIntBytes];
        ulong entries = 0;

        foreach (VirtualTapeBlock vb in blocks)
        {
            if (vb.IsMark)
            {
                buffer.Add((byte)vb.MarkType);
            }
            else
            {
                if (vb.BlockCount == 0)
                    continue;
                buffer.Add(KindData);
                buffer.AddRange(scratch[..TapePrimitives.WriteVarUInt(scratch, vb.BlockSize)].ToArray());
                buffer.AddRange(scratch[..TapePrimitives.WriteVarUInt(scratch, (ulong)vb.BlockCount)].ToArray());
            }
            entries++;
        }

        return new VirtualMediaStateRecord
        {
            MinBlockSize = minBlockSize,
            MaxBlockSize = maxBlockSize,
            DefaultBlockSize = defaultBlockSize,
            Capacity = checked((ulong)capacity),
            BytesWritten = checked((ulong)bytesWritten),
            BlockCount = entries,
            TotalLogicalBlocks = checked((ulong)totalLogicalBlocks),
            Name = name,
            Blocks = [.. buffer],
        };
    }

    /// <summary>
    /// Rebuilds the virtual blocks (positions and stream offsets) and cross-checks them against the stored totals.
    /// </summary>
    /// <exception cref="FormatException">The state is inconsistent.</exception>
    public List<VirtualTapeBlock> Rebuild(long contentStreamLength)
    {
        if (MinBlockSize == 0)
            throw new FormatException("Invalid minBlockSize (0) in saved state");
        if (MaxBlockSize < MinBlockSize)
            throw new FormatException($"Invalid block size range in saved state: max {MaxBlockSize} < min {MinBlockSize}");
        if (DefaultBlockSize < MinBlockSize || DefaultBlockSize > MaxBlockSize)
            throw new FormatException($"Invalid defaultBlockSize {DefaultBlockSize} not in range [{MinBlockSize}..{MaxBlockSize}]");
        if (Capacity > long.MaxValue)
            throw new FormatException("Invalid capacity in saved state");

        var list = new List<VirtualTapeBlock>();
        long block = 0, streamOffset = 0;
        ulong entries = 0;
        ReadOnlySpan<byte> span = Blocks;
        int pos = 0;

        try
        {
            while (pos < span.Length)
            {
                byte kind = span[pos++];
                VirtualTapeBlock vb;
                switch (kind)
                {
                    case KindData:
                        {
                            ulong size = TapePrimitives.ReadVarUInt(span[pos..], out int n1);
                            pos += n1;
                            ulong count = TapePrimitives.ReadVarUInt(span[pos..], out int n2);
                            pos += n2;
                            if (size < MinBlockSize || size > MaxBlockSize)
                                throw new FormatException($"Block size {size} not in range [{MinBlockSize}..{MaxBlockSize}]");
                            if (count == 0 || count > long.MaxValue / size)
                                throw new FormatException($"Invalid block count {count} in saved state");
                            long dataLength = checked((long)count * (long)size);
                            vb = VirtualTapeBlock.CreateData(block, (uint)size, dataLength, streamOffset);
                            streamOffset = checked(streamOffset + dataLength);
                            break;
                        }
                    case KindFilemark:
                    case KindSetmark:
                        vb = VirtualTapeBlock.CreateMark(block, (TapeMarkType)kind);
                        break;
                    default:
                        throw new FormatException($"Invalid block kind {kind} in saved state");
                }
                list.Add(vb);
                block = vb.EndBlock;
                entries++;
            }
        }
        catch (TapeFormatException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        catch (OverflowException ex)
        {
            throw new FormatException("Block list overflows", ex);
        }

        if (entries != BlockCount)
            throw new FormatException($"Block count mismatch: {entries} entries, {BlockCount} recorded");
        if ((ulong)block != TotalLogicalBlocks)
            throw new FormatException($"Total logical blocks mismatch: {block} rebuilt, {TotalLogicalBlocks} recorded");
        if ((ulong)streamOffset != BytesWritten)
            throw new FormatException($"Bytes written mismatch: {streamOffset} rebuilt, {BytesWritten} recorded");
        if (contentStreamLength < streamOffset)
            throw new FormatException($"Content stream is shorter ({contentStreamLength}) than described ({streamOffset})");

        return list;
    }

    #endregion
}
