using System.Buffers.Binary;

namespace TapeLibNET.Format;

/// <summary>
/// Reads records - prologue + body - from a stream or a span (Design-Format-v2 §4.2, §8.1).
/// </summary>
/// <remarks>
/// <b>Never reads ahead</b>: exactly the prologue and <c>BodyLength</c> bytes are consumed, so a CRC trailer or the
///  file body that follows is untouched. Declared lengths are bounded by <see cref="TapeFormat.MaxRecordBody"/>
///  and the body buffer grows only as bytes actually arrive. Unknown kinds with the skippable bit are skipped;
///  any other unknown kind, or a newer major, is refused.
/// </remarks>
public sealed class TapeRecordReader(Stream stream)
{
    private const int InitialBodyChunk = 64 * 1024;

    /// <summary>
    /// Reads the next record this build handles, skipping unknown skippable ones.
    /// </summary>
    /// <returns>The record, or <see langword="null"/> at a clean end of stream (on a record boundary).</returns>
    /// <exception cref="TapeFormatException">Truncated or malformed input, newer major, unknown kind.</exception>
    public TapeRecord? ReadRecord()
    {
        while (true)
        {
            TapeRecord? record = ReadRawRecord();
            if (record == null)
                return null;
            if (record.Admit())
                return record;
        }
    }

    /// <summary>Reads the next record, which must be of <paramref name="expected"/> kind.</summary>
    /// <exception cref="TapeFormatException">Also <see cref="FormatErrorKind.Truncated"/> at end of stream.</exception>
    public TapeRecord ReadRecord(TapeRecordKind expected)
    {
        TapeRecord record = ReadRecord()
            ?? throw new TapeFormatException(FormatErrorKind.Truncated, $"stream ended where a {expected} record was expected");
        if (record.Kind != expected)
            throw new TapeFormatException(FormatErrorKind.UnexpectedKind, $"expected record kind {expected}, found {record.Kind}");
        return record;
    }

    /// <summary>Reads the next record and interprets it as <typeparamref name="T"/>.</summary>
    public T Read<T>() where T : ITapeRecord<T>
        => (ReadRecord() ?? throw new TapeFormatException(FormatErrorKind.Truncated,
            $"stream ended where a {typeof(T).Name} record was expected")).Read<T>();

    /// <summary>
    /// Reads prologue + body with structural checks only (magic, length bound) - no kind / version judgement.
    /// Used by frames, which must verify the CRC BEFORE judging the record.
    /// </summary>
    /// <returns><see langword="null"/> at a clean end of stream.</returns>
    internal TapeRecord? ReadRawRecord()
    {
        Span<byte> fixedPart = stackalloc byte[TapeFormat.FixedPrologueLength];
        int got = ReadUpTo(fixedPart);
        if (got == 0)
            return null;
        if (got < fixedPart.Length)
            throw new TapeFormatException(FormatErrorKind.Truncated, "stream ended inside a record prologue");
        if (!TapeFormat.IsV2(fixedPart))
            throw new TapeFormatException(FormatErrorKind.BadMagic, "not a format 2.1 record (magic missing)");

        ushort rawKind = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[TapeFormat.MagicLength..]);
        byte major = fixedPart[6];
        byte minor = fixedPart[7];

        ulong bodyLength = ReadVarUIntFromStream();
        if (bodyLength > TapeFormat.MaxRecordBody)
            throw new TapeFormatException(FormatErrorKind.LimitExceeded, $"record body of {bodyLength} bytes exceeds {TapeFormat.MaxRecordBody}");

        return new TapeRecord(rawKind, major, minor, ReadBody((int)bodyLength));
    }

    // Reads a varuint byte by byte: the stream must not be read past its last byte.
    private ulong ReadVarUIntFromStream()
    {
        Span<byte> buffer = stackalloc byte[TapeFormat.MaxVarUIntBytes];
        int n = 0;
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
                throw new TapeFormatException(FormatErrorKind.Truncated, "stream ended inside a record prologue");

            buffer[n++] = (byte)b;
            if ((b & 0x80) == 0)
                break;
            if (n == buffer.Length)
                throw TapeFormatException.Bad("varuint is overlong or overflows 64 bits");
        }
        return TapePrimitives.ReadVarUInt(buffer[..n], out _);
    }

    private byte[] ReadBody(int length)
    {
        // Grow with the data that actually arrives: a lying length must not pre-allocate its full size.
        byte[] body = new byte[Math.Min(length, InitialBodyChunk)];
        int filled = 0;
        while (filled < length)
        {
            if (filled == body.Length)
                Array.Resize(ref body, (int)Math.Min(length, (long)body.Length * 2));

            int n = stream.Read(body, filled, body.Length - filled);
            if (n == 0)
                throw new TapeFormatException(FormatErrorKind.Truncated, "stream ended inside a record body");
            filled += n;
        }
        return body;
    }

    private int ReadUpTo(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    #region *** Span variant ***

    internal enum PrologueStatus { Ok, NotRecord, Truncated, BadLength, TooLarge }

    internal readonly record struct Prologue(ushort RawKind, byte Major, byte Minor, int BodyLength, int PrologueLength);

    /// <summary>Parses the prologue at the start of <paramref name="data"/> without judging kind or version.</summary>
    internal static PrologueStatus ParsePrologue(ReadOnlySpan<byte> data, out Prologue prologue)
    {
        prologue = default;
        if (data.Length < TapeFormat.MagicLength || !TapeFormat.IsV2(data))
            return PrologueStatus.NotRecord;
        if (data.Length < TapeFormat.FixedPrologueLength)
            return PrologueStatus.Truncated;

        if (!TapePrimitives.TryReadVarUInt(data[TapeFormat.FixedPrologueLength..], out ulong bodyLength, out int consumed))
        {
            // Cut off (fewer than 10 bytes left, continuation bit set) is truncation; anything else is malformed.
            ReadOnlySpan<byte> rest = data[TapeFormat.FixedPrologueLength..];
            return rest.Length < TapeFormat.MaxVarUIntBytes && (rest.Length == 0 || (rest[^1] & 0x80) != 0)
                ? PrologueStatus.Truncated : PrologueStatus.BadLength;
        }
        if (bodyLength > TapeFormat.MaxRecordBody)
            return PrologueStatus.TooLarge;

        prologue = new Prologue(BinaryPrimitives.ReadUInt16LittleEndian(data[TapeFormat.MagicLength..]),
            data[6], data[7], (int)bodyLength, TapeFormat.FixedPrologueLength + consumed);
        return PrologueStatus.Ok;
    }

    /// <summary>
    /// Parses the first record this build handles from <paramref name="data"/>, skipping unknown skippable ones.
    /// </summary>
    /// <param name="consumed">Bytes up to and including the returned record (skipped records included).</param>
    public static TapeRecord Parse(ReadOnlySpan<byte> data, out int consumed)
    {
        int pos = 0;
        while (true)
        {
            switch (ParsePrologue(data[pos..], out Prologue p))
            {
                case PrologueStatus.NotRecord:
                    throw new TapeFormatException(FormatErrorKind.BadMagic, "not a format 2.1 record (magic missing)");
                case PrologueStatus.Truncated:
                    throw new TapeFormatException(FormatErrorKind.Truncated, "data ends inside a record prologue");
                case PrologueStatus.BadLength:
                    throw TapeFormatException.Bad("record body length is malformed");
                case PrologueStatus.TooLarge:
                    throw new TapeFormatException(FormatErrorKind.LimitExceeded, $"record body exceeds {TapeFormat.MaxRecordBody} bytes");
            }

            int bodyStart = pos + p.PrologueLength;
            if (p.BodyLength > data.Length - bodyStart)
                throw new TapeFormatException(FormatErrorKind.Truncated, "data ends inside a record body");

            var record = new TapeRecord(p.RawKind, p.Major, p.Minor, data.Slice(bodyStart, p.BodyLength).ToArray());
            pos = bodyStart + p.BodyLength;
            if (record.Admit())
            {
                consumed = pos;
                return record;
            }
        }
    }

    #endregion
}
