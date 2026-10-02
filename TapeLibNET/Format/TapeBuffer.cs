using System.Buffers;

namespace TapeLibNET.Format;

/// <summary>
/// Growable pooled byte buffer behind <see cref="TapeFieldWriter"/>. Optionally reserves a gap in front of
///  the content so a record's prologue can be written right-aligned before its body and the whole record
///  emitted with a single <see cref="Stream.Write(byte[], int, int)"/> - no measuring pass.
/// </summary>
internal sealed class TapeBuffer : IDisposable
{
    private const int InitialCapacity = 256;

    private byte[] m_array;
    private readonly int m_start;
    private int m_length;

    /// <param name="reserve">Bytes kept free in front of the content (for the prologue).</param>
    public TapeBuffer(int reserve = 0)
    {
        m_array = ArrayPool<byte>.Shared.Rent(reserve + InitialCapacity);
        m_start = m_length = reserve;
    }

    /// <summary>Content length, excluding the reserved gap.</summary>
    public int Count => m_length - m_start;

    /// <summary>The content written so far.</summary>
    public ReadOnlySpan<byte> Written => m_array.AsSpan(m_start, Count);

    /// <summary>Forgets the content; keeps the buffer.</summary>
    public void Clear() => m_length = m_start;

    /// <summary>Returns a span of <paramref name="size"/> bytes to fill, then call <see cref="Advance"/>.</summary>
    public Span<byte> GetSpan(int size)
    {
        Ensure(size);
        return m_array.AsSpan(m_length, size);
    }

    /// <summary>Commits <paramref name="count"/> bytes filled via <see cref="GetSpan"/>.</summary>
    public void Advance(int count) => m_length += count;

    /// <summary>Appends <paramref name="data"/>.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(m_array.AsSpan(m_length));
        m_length += data.Length;
    }

    /// <summary>Writes <paramref name="prologue"/> right-aligned into the reserved gap, then gap tail + content in one call.</summary>
    public void WriteTo(Stream stream, ReadOnlySpan<byte> prologue)
    {
        if (prologue.Length > m_start)
            throw new InvalidOperationException("prologue does not fit the reserved gap");

        int first = m_start - prologue.Length;
        prologue.CopyTo(m_array.AsSpan(first));
        stream.Write(m_array, first, prologue.Length + Count);
    }

    private void Ensure(int extra)
    {
        if (Count + extra > TapeFormat.MaxRecordBody)
            throw new TapeFormatException(FormatErrorKind.LimitExceeded, $"record body would exceed {TapeFormat.MaxRecordBody} bytes");

        if (m_length + extra <= m_array.Length)
            return;

        byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(m_array.Length * 2, m_length + extra));
        Buffer.BlockCopy(m_array, 0, bigger, 0, m_length);
        ArrayPool<byte>.Shared.Return(m_array);
        m_array = bigger;
    }

    public void Dispose()
    {
        byte[] array = m_array;
        m_array = [];
        if (array.Length > 0)
            ArrayPool<byte>.Shared.Return(array);
    }
}
