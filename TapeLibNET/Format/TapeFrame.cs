using System.IO.Hashing;

namespace TapeLibNET.Format;

/// <summary>
/// <c>Record ‖ CRC-64(Record)</c> frames - block and inline (Design-Format-v2 §4.5, §8.1).
/// </summary>
/// <remarks>
/// A frame holds exactly one record. The CRC covers prologue + body and is verified BEFORE the record is judged
///  (kind, version, fields), so <see cref="TapeFramer.FrameStatus.CrcMismatch"/> (torn / corrupt) is told apart
///  from <see cref="TapeFramer.FrameStatus.Unparseable"/> (intact but not readable by this build).
/// </remarks>
public static class TapeFrame
{
    /// <summary>Size of the CRC-64 trailer.</summary>
    public const int CrcLength = 8;

    /// <summary>Computes the CRC-64 (ECMA-182, <see cref="Crc64"/>) trailer bytes of <paramref name="record"/>.</summary>
    public static byte[] ComputeCrc(ReadOnlySpan<byte> record)
    {
        var crc = new Crc64();
        crc.Append(record);
        return crc.GetCurrentHash();
    }

    /// <summary>Builds the frame bytes <c>Record ‖ CRC-64</c> for <paramref name="record"/>.</summary>
    public static byte[] Pack<T>(T record) where T : ITapeRecord<T>
    {
        using var ms = new MemoryStream();
        using (var writer = new TapeRecordWriter(ms))
            record.WriteTo(writer);

        byte[] recordBytes = ms.ToArray();
        return [.. recordBytes, .. ComputeCrc(recordBytes)];
    }

    /// <summary>Builds a block frame: <c>Record ‖ CRC-64</c> at offset 0, the rest of the block filled with <paramref name="padding"/> (or zeros).</summary>
    /// <param name="blockSize">Block size in bytes; the frame must fit.</param>
    /// <param name="padding">Optional padding source (random bytes for calibration run blocks).</param>
    public static byte[] PackBlock<T>(T record, int blockSize, Random? padding = null) where T : ITapeRecord<T>
    {
        byte[] frame = Pack(record);
        if (frame.Length > blockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, $"frame of {frame.Length} bytes does not fit the block");

        byte[] block = new byte[blockSize];
        frame.CopyTo(block, 0);
        if (padding != null)
            padding.NextBytes(block.AsSpan(frame.Length));
        return block;
    }

    /// <summary>Writes the inline frame of <paramref name="record"/> to <paramref name="stream"/>; returns the bytes written.</summary>
    public static int WriteInline<T>(Stream stream, T record) where T : ITapeRecord<T>
    {
        byte[] frame = Pack(record);
        stream.Write(frame, 0, frame.Length);
        return frame.Length;
    }

    /// <summary>
    /// Verifies a frame at the start of <paramref name="data"/> and returns the record it holds.
    /// Total: every fault becomes a status, never an exception.
    /// </summary>
    /// <param name="frameLength">Length of the frame (record + CRC) when the bounds fit; else 0.</param>
    public static TapeFramer.FrameStatus TryUnpackRecord(ReadOnlySpan<byte> data, out TapeRecord? record, out int frameLength)
    {
        record = null;
        frameLength = 0;

        switch (TapeRecordReader.ParsePrologue(data, out TapeRecordReader.Prologue p))
        {
            case TapeRecordReader.PrologueStatus.Ok:
                break;
            default:
                return TapeFramer.FrameStatus.NotFramed;   // no magic, cut-off or implausible prologue
        }

        int recordLength = p.PrologueLength + p.BodyLength;
        if (recordLength > data.Length - CrcLength)
            return TapeFramer.FrameStatus.NotFramed;        // the declared length runs past the bytes available

        frameLength = recordLength + CrcLength;
        if (!ComputeCrc(data[..recordLength]).AsSpan().SequenceEqual(data.Slice(recordLength, CrcLength)))
            return TapeFramer.FrameStatus.CrcMismatch;

        var parsed = new TapeRecord(p.RawKind, p.Major, p.Minor, data.Slice(p.PrologueLength, p.BodyLength).ToArray());
        try
        {
            parsed.RequireSupportedMajor();
            if (!TapeFormat.IsKnownKind(parsed.Kind))
                return TapeFramer.FrameStatus.Unparseable;  // unknown kind: a frame carries one record, nothing to skip to
        }
        catch (TapeFormatException)
        {
            return TapeFramer.FrameStatus.Unparseable;
        }

        record = parsed;
        return TapeFramer.FrameStatus.Ok;
    }

    /// <summary>
    /// As <see cref="TryUnpackRecord"/>, then interprets the record as <typeparamref name="T"/>.
    /// A wrong kind or a body this build cannot read (unknown critical tag, missing required field, bad enum) is
    ///  <see cref="TapeFramer.FrameStatus.Unparseable"/>.
    /// </summary>
    public static TapeFramer.FrameStatus TryUnpack<T>(ReadOnlySpan<byte> data, out T? value) where T : class, ITapeRecord<T>
    {
        value = null;
        var status = TryUnpackRecord(data, out TapeRecord? record, out _);
        if (status != TapeFramer.FrameStatus.Ok)
            return status;

        try
        {
            value = record!.Read<T>();     // Ok implies a non-null record
            return TapeFramer.FrameStatus.Ok;
        }
        catch (TapeFormatException)
        {
            return TapeFramer.FrameStatus.Unparseable;
        }
    }

    /// <summary>Reads one inline frame from <paramref name="stream"/>, verifying its CRC; consumes exactly the frame.</summary>
    /// <exception cref="TapeFormatException">Truncated, malformed, <see cref="FormatErrorKind.CrcMismatch"/>, or not <typeparamref name="T"/>.</exception>
    public static T ReadInline<T>(Stream stream) where T : ITapeRecord<T>
    {
        var crc = new Crc64();
        TapeRecord? record;
        using (var hashing = new HashingStream(stream, crc))
            record = new TapeRecordReader(hashing).ReadRawRecord();

        if (record == null)
            throw new TapeFormatException(FormatErrorKind.Truncated, "stream ended where a frame was expected");

        byte[] stored = new byte[CrcLength];
        stream.ReadExactly(stored);
        if (!crc.GetCurrentHash().AsSpan().SequenceEqual(stored))
            throw new TapeFormatException(FormatErrorKind.CrcMismatch, "frame CRC-64 does not match");

        return record.Read<T>();
    }
}
