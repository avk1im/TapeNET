using TapeLibNET.Streams;
using System.IO.Hashing;

namespace TapeLibNET.Format;

/// <summary>
/// <c>Record ‖ CRC-64(Record)</c> frames - block and inline (Design-Format-v2 §4.5, §8.1).
/// </summary>
/// <remarks>
/// A frame holds exactly one record. The CRC covers prologue + body and is verified BEFORE the record is judged
///  (kind, version, fields), so <see cref="TapeFrameStatus.CrcMismatch"/> (torn / corrupt) is told apart
///  from <see cref="TapeFrameStatus.Unparseable"/> (intact but not readable by this build).
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
#warning Consider reusing single instances of MemoryStream and TapeRecordWriter (per thread / session) for multiple calls to TapeFrame.Pack().
        using var ms = new MemoryStream();
        using (var writer = new TapeRecordWriter(ms))
            writer.Write(record);

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
        padding?.NextBytes(block.AsSpan(frame.Length));
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
    public static TapeFrameStatus TryUnpackRecord(ReadOnlySpan<byte> data, out TapeRecord? record, out int frameLength)
        => TryUnpackRecord(data, out record, out frameLength, out _);

    internal static TapeFrameStatus TryUnpackRecord(ReadOnlySpan<byte> data, out TapeRecord? record,
        out int frameLength, out TapeFormatException? error)
    {
        record = null;
        frameLength = 0;
        error = null;

        switch (TapeRecordReader.ParsePrologue(data, out TapeRecordReader.Prologue p))
        {
            case TapeRecordReader.PrologueStatus.Ok:
                break;
            default:
                return TapeFrameStatus.NotFramed;   // no magic, cut-off or implausible prologue
        }

        int recordLength = p.PrologueLength + p.BodyLength;
        if (recordLength > data.Length - CrcLength)
            return TapeFrameStatus.NotFramed;        // the declared length runs past the bytes available

        frameLength = recordLength + CrcLength;
        if (!ComputeCrc(data[..recordLength]).AsSpan().SequenceEqual(data.Slice(recordLength, CrcLength)))
            return TapeFrameStatus.CrcMismatch;

        var parsed = new TapeRecord(p.RawKind, p.Major, p.Minor, data.Slice(p.PrologueLength, p.BodyLength).ToArray());
        try
        {
            parsed.RequireSupportedMajor();
            if (!TapeFormat.IsKnownKind(parsed.Kind))
            {
                error = new TapeFormatException(FormatErrorKind.UnknownKind, $"record kind 0x{p.RawKind:X4} is unknown to this build");
                return TapeFrameStatus.Unparseable;  // a frame carries one record, nothing to skip to
            }
        }
        catch (TapeFormatException ex)
        {
            error = ex;
            return TapeFrameStatus.Unparseable;
        }

        record = parsed;
        return TapeFrameStatus.Ok;
    }

    /// <summary>Status-only form of <c>TryUnpack</c>.</summary>
    public static TapeFrameStatus TryUnpack<T>(ReadOnlySpan<byte> data, out T? value) where T : class, ITapeRecord<T>
        => TryUnpack(data, out value, out _, out _);

    /// <summary>
    /// As <see cref="TryUnpackRecord(ReadOnlySpan{byte}, out TapeRecord?, out int, out TapeFormatException?)"/>,
    ///  then interprets the record as <typeparamref name="T"/>.
    ///  A wrong kind or a body this build cannot read (unknown critical tag, missing required field, bad enum) is
    ///  <see cref="TapeFrameStatus.Unparseable"/>.
    /// </summary>
    public static TapeFrameStatus TryUnpack<T>(ReadOnlySpan<byte> data, out T? value,
        out int frameLength, out TapeFormatException? error) where T : class, ITapeRecord<T>
    {
        value = null;
        error = null;
        var status = TryUnpackRecord(data, out TapeRecord? record, out frameLength, out error);
        if (status != TapeFrameStatus.Ok)
            return status;

        try
        {
            value = record!.Read<T>();     // Ok implies a non-null record
            return TapeFrameStatus.Ok;
        }
        catch (TapeFormatException ex)
        {
            error = ex;
            return TapeFrameStatus.Unparseable;
        }
    }

    /// <summary>
    /// 2.1 frame, or the legacy form via <see cref="ITapeFramedRecord{TSelf}.TryReadLegacy"/> when the magic is absent.
    /// Never throws.
    /// </summary>
    public static TapeFrameStatus TryUnpackWithLegacy<T>(ReadOnlySpan<byte> data, out T? value,
        out int frameLength, out TapeFormatException? error) where T : class, ITapeFramedRecord<T>
    {
        if (!TapeFormat.IsV2(data))
        {
            error = null;
            frameLength = 0;
            return T.TryReadLegacy(data, out value);
        }
        return TryUnpack(data, out value, out frameLength, out error);
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
        if (stream.ReadAtLeast(stored, CrcLength, throwOnEndOfStream: false) < CrcLength)
            throw new TapeFormatException(FormatErrorKind.Truncated, "stream ended inside the frame CRC-64");
        if (!crc.GetCurrentHash().AsSpan().SequenceEqual(stored))
            throw new TapeFormatException(FormatErrorKind.CrcMismatch, "frame CRC-64 does not match");

        return record.Read<T>();
    }
}
