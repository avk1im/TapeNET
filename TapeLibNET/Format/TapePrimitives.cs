using System.Buffers.Binary;
using System.Text;

namespace TapeLibNET.Format;


/// <summary>
/// Span-based encode / decode of the format primitives (Design-Format-v2 §4.1).
/// </summary>
/// <remarks>
/// Inside a field the field's own <c>Length</c> delimits a string or bytes value, so the <c>Decode*</c> helpers
///  take the exact value span and refuse anything that does not consume it entirely
///  (<see cref="FormatErrorKind.Underrun"/>) - no second, inner length prefix is written.
/// </remarks>
public static class TapePrimitives
{
    /// <summary>UTF-8 without BOM that refuses invalid input in both directions.</summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Smallest timestamp: zero ticks, UTC. The schema default for <see cref="DateTime"/> fields.</summary>
    public static readonly DateTime MinUtc = new(0, DateTimeKind.Utc);

    /// <summary>
    /// UTC ticks of <paramref name="value"/>: <c>Utc</c> as is, <c>Local</c> converted,
    ///  <c>Unspecified</c> taken as UTC and never shifted.
    /// </summary>
    public static long ToUtcTicks(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value.Ticks,
        DateTimeKind.Local => value.ToUniversalTime().Ticks,
        _ => value.Ticks,
    };

    #region *** varuint / varint ***

    private enum VarUIntStatus { Ok, Truncated, Overlong }

    /// <summary>Number of bytes <see cref="WriteVarUInt"/> needs for <paramref name="value"/>.</summary>
    public static int VarUIntSize(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }
        return size;
    }

    /// <summary>Writes an unsigned LEB128; returns the byte count (at most <see cref="TapeFormat.MaxVarUIntBytes"/>).</summary>
    public static int WriteVarUInt(Span<byte> destination, ulong value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[i++] = (byte)value;
        return i;
    }

    // Strict LEB128 decoder: refuses > 64 bits and non-canonical (overlong) encodings.
    private static VarUIntStatus TryReadVarUIntCore(ReadOnlySpan<byte> source, out ulong value, out int consumed)
    {
        value = 0;
        consumed = 0;
        int shift = 0;

        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];

            if (i == TapeFormat.MaxVarUIntBytes - 1 && b > 1)
                return VarUIntStatus.Overlong;      // 10th byte may only carry bit 63

            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                if (i > 0 && b == 0)
                    return VarUIntStatus.Overlong;  // trailing zero group: non-canonical
                consumed = i + 1;
                return VarUIntStatus.Ok;
            }
            shift += 7;
        }
        return VarUIntStatus.Truncated;
    }

    /// <summary>Non-throwing <see cref="ReadVarUInt"/>: false when truncated, overlong or overflowing.</summary>
    public static bool TryReadVarUInt(ReadOnlySpan<byte> source, out ulong value, out int consumed)
        => TryReadVarUIntCore(source, out value, out consumed) == VarUIntStatus.Ok;

    /// <summary>Reads an unsigned LEB128 from the start of <paramref name="source"/>.</summary>
    /// <exception cref="TapeFormatException"><see cref="FormatErrorKind.Truncated"/> or <see cref="FormatErrorKind.BadValue"/>.</exception>
    public static ulong ReadVarUInt(ReadOnlySpan<byte> source, out int consumed)
    {
        return TryReadVarUIntCore(source, out ulong value, out consumed) switch
        {
            VarUIntStatus.Ok => value,
            VarUIntStatus.Truncated => throw new TapeFormatException(FormatErrorKind.Truncated, "varuint is cut off"),
            _ => throw TapeFormatException.Bad("varuint is overlong or overflows 64 bits"),
        };
    }

    /// <summary>ZigZag: maps small magnitudes of either sign to small unsigned values.</summary>
    public static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>Inverse of <see cref="ZigZagEncode"/>.</summary>
    public static long ZigZagDecode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Writes a ZigZag varint; returns the byte count.</summary>
    public static int WriteVarInt(Span<byte> destination, long value) => WriteVarUInt(destination, ZigZagEncode(value));

    /// <summary>Reads a ZigZag varint.</summary>
    public static long ReadVarInt(ReadOnlySpan<byte> source, out int consumed)
        => ZigZagDecode(ReadVarUInt(source, out consumed));

    #endregion

    #region *** Exact-value decoders (a field value must be consumed entirely) ***

    /// <summary>Decodes a varuint that fills <paramref name="value"/> exactly.</summary>
    public static ulong DecodeVarUInt(ReadOnlySpan<byte> value)
    {
        ulong result = ReadVarUInt(value, out int consumed);
        if (consumed != value.Length)
            throw new TapeFormatException(FormatErrorKind.Underrun, "varuint value has trailing bytes");
        return result;
    }

    /// <summary>Decodes a varint that fills <paramref name="value"/> exactly.</summary>
    public static long DecodeVarInt(ReadOnlySpan<byte> value) => ZigZagDecode(DecodeVarUInt(value));

    /// <summary>Decodes a varuint that must fit 32 bits.</summary>
    public static uint DecodeUInt32(ReadOnlySpan<byte> value)
    {
        ulong v = DecodeVarUInt(value);
        return v <= uint.MaxValue ? (uint)v : throw TapeFormatException.Bad($"value {v} exceeds 32 bits");
    }

    /// <summary>Decodes a varuint that must fit a non-negative <see cref="int"/>.</summary>
    public static int DecodeInt32(ReadOnlySpan<byte> value)
    {
        ulong v = DecodeVarUInt(value);
        return v <= int.MaxValue ? (int)v : throw TapeFormatException.Bad($"value {v} exceeds Int32");
    }

    /// <summary>Decodes a varuint that must fit a non-negative <see cref="long"/>.</summary>
    public static long DecodeNonNegativeInt64(ReadOnlySpan<byte> value)
    {
        ulong v = DecodeVarUInt(value);
        return v <= long.MaxValue ? (long)v : throw TapeFormatException.Bad($"value {v} exceeds Int64");
    }

    /// <summary>Decodes a strict bool: one byte, 0 or 1.</summary>
    public static bool DecodeBool(ReadOnlySpan<byte> value)
    {
        if (value.Length != 1)
            throw new TapeFormatException(value.Length == 0 ? FormatErrorKind.Truncated : FormatErrorKind.Underrun,
                "bool value must be exactly one byte");
        return value[0] switch
        {
            0 => false,
            1 => true,
            _ => throw TapeFormatException.Bad($"bool value {value[0]} is neither 0 nor 1"),
        };
    }

    /// <summary>Decodes an IEEE 754 binary64, little-endian.</summary>
    public static double DecodeF64(ReadOnlySpan<byte> value)
    {
        RequireLength(value, sizeof(double), "f64");
        return BinaryPrimitives.ReadDoubleLittleEndian(value);
    }

    /// <summary>Decodes a 16-byte GUID in <see cref="Guid.TryWriteBytes(Span{byte})"/> order.</summary>
    public static Guid DecodeGuid(ReadOnlySpan<byte> value)
    {
        RequireLength(value, 16, "guid");
        return new Guid(value);
    }

    /// <summary>Decodes UTC ticks (varint) into a <see cref="DateTimeKind.Utc"/> value.</summary>
    public static DateTime DecodeTimestamp(ReadOnlySpan<byte> value)
    {
        long ticks = DecodeVarInt(value);
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            throw TapeFormatException.Bad($"timestamp ticks {ticks} out of range");
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>Decodes a validated UTF-8 string (<see cref="TapeFormat.MaxStringBytes"/> bound).</summary>
    public static string DecodeString(ReadOnlySpan<byte> value)
    {
        if (value.Length > TapeFormat.MaxStringBytes)
            throw new TapeFormatException(FormatErrorKind.LimitExceeded, $"string of {value.Length} bytes exceeds {TapeFormat.MaxStringBytes}");
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (ArgumentException ex)    // DecoderFallbackException
        {
            throw new TapeFormatException(FormatErrorKind.BadValue, "string is not valid UTF-8", ex);
        }
    }

    /// <summary>Copies a bytes value (<see cref="TapeFormat.MaxBytesField"/> bound).</summary>
    public static byte[] DecodeBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length > TapeFormat.MaxBytesField)
            throw new TapeFormatException(FormatErrorKind.LimitExceeded, $"bytes value of {value.Length} bytes exceeds {TapeFormat.MaxBytesField}");
        return value.ToArray();
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int expected, string what)
    {
        if (value.Length < expected)
            throw new TapeFormatException(FormatErrorKind.Truncated, $"{what} value is {value.Length} bytes, expected {expected}");
        if (value.Length > expected)
            throw new TapeFormatException(FormatErrorKind.Underrun, $"{what} value is {value.Length} bytes, expected {expected}");
    }

    #endregion

    #region *** Helpers ***

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting
    ///  a surrogate pair. For fields the format clamps (descriptions, original names - §5.1, §5.4).
    /// </summary>
    public static string ClampToUtf8Bytes(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        int bytes = 0;
        int i = 0;
        while (i < text.Length)
        {
            int step = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            int stepBytes = Encoding.UTF8.GetByteCount(text.AsSpan(i, step));
            if (bytes + stepBytes > maxBytes)
                break;
            bytes += stepBytes;
            i += step;
        }
        return text[..i];
    }

    #endregion
}
