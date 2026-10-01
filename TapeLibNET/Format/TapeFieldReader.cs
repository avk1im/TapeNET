namespace TapeLibNET.Format;

/// <summary>
/// Reads the fields of one record body or nested group (Design-Format-v2 §4.2 / §4.4): field iteration with
///  bounds checks, typed value accessors, nested groups.
/// </summary>
/// <remarks>
/// Usage: <c>while (f.MoveNext()) switch (f.Number) { case 1: x = f.ReadGuid(); break; ... }</c>. Readers accept any
///  field order and ignore the critical bit when matching a known field (R3); deciding about UNKNOWN fields
///  (skip or refuse by <see cref="IsCritical"/>) is the caller's job - <see cref="TapeSchema{T}"/> does it.
/// </remarks>
public sealed class TapeFieldReader
{
    private readonly ReadOnlyMemory<byte> m_body;
    private readonly int m_depth;
    private int m_pos;
    private int m_valueStart;
    private int m_valueLength;
    private bool m_hasCurrent;

    /// <summary>Creates a reader over a record body or group body.</summary>
    public TapeFieldReader(ReadOnlyMemory<byte> body) : this(body, 0)
    {
    }

    private TapeFieldReader(ReadOnlyMemory<byte> body, int depth)
    {
        m_body = body;
        m_depth = depth;
    }

    /// <summary>Number of the current field.</summary>
    public int Number { get; private set; }

    /// <summary>Whether the current field's tag carries the critical bit.</summary>
    public bool IsCritical { get; private set; }

    /// <summary>The raw value bytes of the current field.</summary>
    public ReadOnlySpan<byte> Value
        => m_hasCurrent ? m_body.Span.Slice(m_valueStart, m_valueLength)
                        : throw new InvalidOperationException("no current field - call MoveNext first");

    /// <summary>Advances to the next field; false at the end of the body.</summary>
    /// <exception cref="TapeFormatException">Overrun, or a malformed tag / length.</exception>
    public bool MoveNext()
    {
        m_hasCurrent = false;
        ReadOnlySpan<byte> span = m_body.Span;
        if (m_pos >= span.Length)
            return false;

        ulong tag = ReadVarUIntInBody(span, ref m_pos);
        if (tag > uint.MaxValue || (tag >> 1) == 0)
            throw TapeFormatException.Bad($"invalid field tag {tag}");

        ulong length = ReadVarUIntInBody(span, ref m_pos);
        if (length > (ulong)(span.Length - m_pos))
            throw new TapeFormatException(FormatErrorKind.Overrun, $"field {tag >> 1} runs past the end of its body");

        Number = (int)(tag >> 1);
        IsCritical = (tag & 1) != 0;
        m_valueStart = m_pos;
        m_valueLength = (int)length;
        m_pos += m_valueLength;
        m_hasCurrent = true;
        return true;
    }

    // A varuint that ends mid-body is an overrun of the body, not of the stream.
    private static ulong ReadVarUIntInBody(ReadOnlySpan<byte> span, ref int pos)
    {
        try
        {
            ulong v = TapePrimitives.ReadVarUInt(span[pos..], out int consumed);
            pos += consumed;
            return v;
        }
        catch (TapeFormatException ex) when (ex.Kind == FormatErrorKind.Truncated)
        {
            throw new TapeFormatException(FormatErrorKind.Overrun, "field header runs past the end of its body", ex);
        }
    }

    #region *** Typed value accessors (current field) ***

    /// <summary>The value as <c>varuint</c>.</summary>
    public ulong ReadUInt64() => TapePrimitives.DecodeVarUInt(Value);

    /// <summary>The value as <c>varuint</c> that must fit 32 bits.</summary>
    public uint ReadUInt32() => TapePrimitives.DecodeUInt32(Value);

    /// <summary>The value as <c>varuint</c> that must fit a non-negative Int32.</summary>
    public int ReadInt32() => TapePrimitives.DecodeInt32(Value);

    /// <summary>The value as <c>varuint</c> that must fit a non-negative Int64.</summary>
    public long ReadNonNegativeInt64() => TapePrimitives.DecodeNonNegativeInt64(Value);

    /// <summary>The value as ZigZag <c>varint</c>.</summary>
    public long ReadInt64() => TapePrimitives.DecodeVarInt(Value);

    /// <summary>The value as strict <c>bool</c>.</summary>
    public bool ReadBool() => TapePrimitives.DecodeBool(Value);

    /// <summary>The value as <c>f64</c>.</summary>
    public double ReadF64() => TapePrimitives.DecodeF64(Value);

    /// <summary>The value as <c>guid</c>.</summary>
    public Guid ReadGuid() => TapePrimitives.DecodeGuid(Value);

    /// <summary>The value as <c>timestamp</c> (<see cref="DateTimeKind.Utc"/>).</summary>
    public DateTime ReadTimestamp() => TapePrimitives.DecodeTimestamp(Value);

    /// <summary>The value as validated UTF-8 <c>string</c>.</summary>
    public string ReadString() => TapePrimitives.DecodeString(Value);

    /// <summary>The value as a copied <c>bytes</c> array.</summary>
    public byte[] ReadBytes() => TapePrimitives.DecodeBytes(Value);

    /// <summary>The value as a nested group; groups nest up to <see cref="TapeFormat.MaxGroupDepth"/> levels.</summary>
    public TapeFieldReader ReadGroup()
    {
        if (!m_hasCurrent)
            throw new InvalidOperationException("no current field - call MoveNext first");
        if (m_depth + 1 > TapeFormat.MaxGroupDepth)
            throw TapeFormatException.Bad($"group nesting exceeds {TapeFormat.MaxGroupDepth}");

        return new TapeFieldReader(m_body.Slice(m_valueStart, m_valueLength), m_depth + 1);
    }

    #endregion
}
