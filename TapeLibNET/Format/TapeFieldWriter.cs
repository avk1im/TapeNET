namespace TapeLibNET.Format;

/// <summary>
/// Writes the fields of one record body (or one nested group) - Design-Format-v2 §4.2 / §4.4.
/// Obtained from <see cref="TapeRecordWriter.BeginRecord"/> or <see cref="BeginGroup"/>.
/// </summary>
/// <remarks>
/// Writers emit ascending tags (the <see cref="TapeSchema{T}"/> does so by construction); a DEBUG assertion checks it.
///  Every <c>critical</c> flag is part of the tag - emit it consistently per field number (R3).
/// </remarks>
public sealed class TapeFieldWriter
{
    private readonly TapeBuffer m_buffer;
    private readonly int m_depth;
    private int m_lastNumber;

    // Reusable child for nested groups: created on first use, reused afterwards
    private TapeFieldWriter? m_child;
    private bool m_childOpen;
    private int m_openNumber;
    private bool m_openCritical;

    internal TapeFieldWriter(TapeBuffer buffer, int depth)
    {
        m_buffer = buffer;
        m_depth = depth;
    }

    /// <summary>Largest legal field number (the tag must fit 32 bits).</summary>
    public const int MaxFieldNumber = int.MaxValue;

    /// <summary>Body bytes written so far (for batch size control).</summary>
    public int BytesWritten => m_buffer.Count;

    internal ReadOnlySpan<byte> Written => m_buffer.Written;

    /// <summary>Forgets everything written, including any open group; keeps the buffers.</summary>
    internal void Reset()
    {
        m_buffer.Clear();
        m_lastNumber = 0;
        m_childOpen = false;
        m_child?.Reset();
    }

    /// <summary>Returns the child writers' buffers to the pool.</summary>
    internal void Release()
    {
        if (m_child is null)
            return;
        m_child.Release();
        m_child.m_buffer.Dispose();
        m_child = null;
        m_childOpen = false;
    }

    // Header = tag varuint + length varuint
    private void WriteHeader(int number, bool critical, int valueLength)
    {
        if (number < 1)
            throw new ArgumentOutOfRangeException(nameof(number), number, "field numbers start at 1");
        System.Diagnostics.Debug.Assert(number >= m_lastNumber, $"field {number} written after field {m_lastNumber}: numbers must ascend");
        m_lastNumber = number;

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

    /// <summary>Writes a <c>timestamp</c> field: UTC ticks (<see cref="TapePrimitives.ToUtcTicks"/>).</summary>
    public void WriteTimestamp(int number, DateTime value, bool critical = false)
        => WriteInt(number, TapePrimitives.ToUtcTicks(value), critical);

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
    /// Begins a nested group field and returns the reusable child writer (one per depth, reset here).
    ///  Write the child's fields, then call <see cref="EndGroup"/>. Groups nest up to <see cref="TapeFormat.MaxGroupDepth"/> levels.
    /// </summary>
    public TapeFieldWriter BeginGroup(int number, bool critical = false)
    {
        if (m_depth + 1 > TapeFormat.MaxGroupDepth)
            throw new InvalidOperationException($"group nesting exceeds {TapeFormat.MaxGroupDepth}");
        if (m_childOpen)
            throw new InvalidOperationException("a group is already open on this writer");
        if (number < 1)
            throw new ArgumentOutOfRangeException(nameof(number), number, "field numbers start at 1");

        m_child ??= new TapeFieldWriter(new TapeBuffer(), m_depth + 1);
        m_child.Reset();
        m_childOpen = true;
        m_openNumber = number;
        m_openCritical = critical;
        return m_child;
    }

    /// <summary>Appends the group's tag, length and body; <paramref name="child"/> must be the one from <see cref="BeginGroup"/>.</summary>
    public void EndGroup(TapeFieldWriter child)
    {
        if (!m_childOpen || !ReferenceEquals(child, m_child))
            throw new InvalidOperationException("EndGroup does not match the open BeginGroup");
        if (child.m_childOpen)
            throw new InvalidOperationException("a nested group is still open");

        m_childOpen = false;
        WriteField(m_openNumber, m_openCritical, child.Written);
    }
}
