namespace TapeLibNET.Format;

/// <summary>
/// Writes the fields of one record body (or one nested group) - Design-Format-v2 §4.2 / §4.4.
/// Obtained from <see cref="TapeRecordWriter.BeginRecord"/> or handed to a <see cref="WriteGroup"/> callback.
/// </summary>
/// <remarks>
/// Writers emit ascending tags (the <see cref="TapeSchema{T}"/> does so by construction); this class does not
///  enforce the order. Every <c>critical</c> flag is part of the tag - emit it consistently per field number (R3).
/// </remarks>
public sealed class TapeFieldWriter
{
    private readonly TapeBuffer m_buffer;
    private readonly int m_depth;

    internal TapeFieldWriter(TapeBuffer buffer, int depth)
    {
        m_buffer = buffer;
        m_depth = depth;
    }

    /// <summary>Largest legal field number (the tag must fit 32 bits).</summary>
    public const int MaxFieldNumber = int.MaxValue;

    // Header = tag varuint + length varuint
    private void WriteHeader(int number, bool critical, int valueLength)
    {
        if (number < 1)
            throw new ArgumentOutOfRangeException(nameof(number), number, "field numbers start at 1");

        Span<byte> header = stackalloc byte[2 * TapeFormat.MaxVarUIntBytes];
        int n = TapePrimitives.WriteVarUInt(header, ((ulong)number << 1) | (critical ? 1UL : 0UL));
        n += TapePrimitives.WriteVarUInt(header[n..], (ulong)valueLength);
        m_buffer.Append(header[..n]);
    }

    private void WriteField(int number, bool critical, ReadOnlySpan<byte> value)
    {
        WriteHeader(number, critical, value.Length);
        m_buffer.Append(value);
    }

    /// <summary>Writes a <c>varuint</c> field.</summary>
    public void WriteUInt(int number, ulong value, bool critical = false)
    {
        Span<byte> v = stackalloc byte[TapeFormat.MaxVarUIntBytes];
        int n = TapePrimitives.WriteVarUInt(v, value);
        WriteField(number, critical, v[..n]);
    }

    /// <summary>Writes a ZigZag <c>varint</c> field.</summary>
    public void WriteInt(int number, long value, bool critical = false)
        => WriteUInt(number, TapePrimitives.ZigZagEncode(value), critical);

    /// <summary>Writes a <c>bool</c> field.</summary>
    public void WriteBool(int number, bool value, bool critical = false)
        => WriteField(number, critical, [value ? (byte)1 : (byte)0]);

    /// <summary>Writes an <c>f64</c> field.</summary>
    public void WriteF64(int number, double value, bool critical = false)
    {
        Span<byte> v = stackalloc byte[sizeof(double)];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(v, value);
        WriteField(number, critical, v);
    }

    /// <summary>Writes a <c>guid</c> field.</summary>
    public void WriteGuid(int number, Guid value, bool critical = false)
    {
        Span<byte> v = stackalloc byte[16];
        value.TryWriteBytes(v);
        WriteField(number, critical, v);
    }

    /// <summary>Writes a <c>timestamp</c> field: UTC ticks (the value is converted with <c>ToUniversalTime()</c>).</summary>
    public void WriteTimestamp(int number, DateTime value, bool critical = false)
        => WriteInt(number, value.ToUniversalTime().Ticks, critical);

    /// <summary>Writes a UTF-8 <c>string</c> field. Callers clamp long strings first (<see cref="TapePrimitives.ClampToUtf8Bytes"/>).</summary>
    public void WriteString(int number, string value, bool critical = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        int length;
        try
        {
            length = TapePrimitives.StrictUtf8.GetByteCount(value);
        }
        catch (ArgumentException ex)    // EncoderFallbackException: lone surrogate
        {
            throw new TapeFormatException(FormatErrorKind.BadValue, "string cannot be encoded as UTF-8", ex);
        }

        if (length > TapeFormat.MaxStringBytes)
            throw TapeFormatException.Bad($"string of {length} bytes exceeds {TapeFormat.MaxStringBytes}");

        WriteHeader(number, critical, length);
        TapePrimitives.StrictUtf8.GetBytes(value, m_buffer.GetSpan(length));
        m_buffer.Advance(length);
    }

    /// <summary>Writes a <c>bytes</c> field.</summary>
    public void WriteBytes(int number, ReadOnlySpan<byte> value, bool critical = false)
    {
        if (value.Length > TapeFormat.MaxBytesField)
            throw TapeFormatException.Bad($"bytes value of {value.Length} bytes exceeds {TapeFormat.MaxBytesField}");
        WriteField(number, critical, value);
    }

    /// <summary>
    /// Writes a nested group field. The group body is built by <paramref name="write"/> into a scratch buffer, then
    ///  emitted with its length. Groups may nest up to <see cref="TapeFormat.MaxGroupDepth"/> levels.
    /// </summary>
    public void WriteGroup(int number, Action<TapeFieldWriter> write, bool critical = false)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (m_depth + 1 > TapeFormat.MaxGroupDepth)
            throw new InvalidOperationException($"group nesting exceeds {TapeFormat.MaxGroupDepth}");

        using var child = new TapeBuffer();
        write(new TapeFieldWriter(child, m_depth + 1));
        WriteField(number, critical, child.Written);
    }
}
