namespace TapeLibNET.Legacy;

/// <summary>
/// Frozen, read-only reader of the pre-2.1 <c>VirtualTapeMedia</c> metadata layout
///  (<c>TF</c> signature, version ≤ 0x0100; Design-Format-v2-AppendixA §A.5).
/// Normalizes to the 2.1 model: a layout that is not contiguous cannot be represented and is refused, never repaired.
/// </summary>
internal static class LegacyVirtualMediaState
{
    /// <summary>Highest legacy state version this reader accepts.</summary>
    public const ushort MaxVersion = 0x0100;

    /// <summary>Whether <paramref name="head"/> (the first bytes of the metadata) carries the legacy <c>TF</c> signature.</summary>
    public static bool IsLegacy(ReadOnlySpan<byte> head) => head.Length >= 2 && head[0] == (byte)'T' && head[1] == (byte)'F';

    /// <summary>Reads legacy metadata from the start of <paramref name="metadata"/> into the 2.1 state model.</summary>
    /// <exception cref="FormatException">Unsupported, truncated, inconsistent or non-contiguous metadata.</exception>
    public static Virtual.VirtualMediaStateRecord Read(Stream metadata)
    {
        metadata.Position = 0;
        var d = new LegacyDeserializer(metadata);

        if (!d.ValidateSignature(out ushort version) || version > MaxVersion)
            throw new FormatException($"Invalid state signature or unsupported version {version} (max: {MaxVersion})");

        uint min = d.DeserializeUInt32();
        uint max = d.DeserializeUInt32();
        uint def = d.DeserializeUInt32();
        long capacity = d.DeserializeInt64();
        string name = d.DeserializeString();
        long bytesWritten = d.DeserializeInt64();

        int count = d.DeserializeInt32();
        if (count < 0)
            throw new FormatException($"Invalid virtual block count {count} in saved state");

        var blocks = new List<Virtual.VirtualTapeBlock>(Math.Min(count, 1 << 16));
        long nextBlock = 0, nextOffset = 0;
        for (int i = 0; i < count; i++)
        {
            bool isMark = d.DeserializeBoolean();
            var markType = (Virtual.TapeMarkType)d.DeserializeBytes<byte>()[0];
            uint blockSize = d.DeserializeUInt32();
            long beginAt = d.DeserializeInt64();
            long dataLength = d.DeserializeInt64();
            long streamOffset = d.DeserializeInt64();

            const string notContiguous = "legacy virtual media metadata is not contiguous";
            if (beginAt != nextBlock)
                throw new FormatException(notContiguous);

            if (isMark)
            {
                if (markType is not (Virtual.TapeMarkType.Filemark or Virtual.TapeMarkType.Setmark))
                    throw new FormatException($"Invalid mark type {(byte)markType} in saved state");
                blocks.Add(Virtual.VirtualTapeBlock.CreateMark(beginAt, markType));
            }
            else
            {
                if (blockSize == 0 || dataLength < 0 || dataLength % blockSize != 0)
                    throw new FormatException(notContiguous);
                if (dataLength == 0)
                    continue;   // an emptied run occupies no blocks and no bytes; dropped, as the 2.1 writer does
                if (streamOffset != nextOffset)
                    throw new FormatException(notContiguous);
                blocks.Add(Virtual.VirtualTapeBlock.CreateData(beginAt, blockSize, dataLength, streamOffset));
                nextOffset += dataLength;
            }
            nextBlock = blocks[^1].EndBlock;
        }

        return Virtual.VirtualMediaStateRecord.Create(min, max, def, capacity, bytesWritten, nextBlock, name, blocks);
    }
}
