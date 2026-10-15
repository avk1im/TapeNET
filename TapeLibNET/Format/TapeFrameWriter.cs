using System.IO.Hashing;

namespace TapeLibNET.Format;


/// <summary>
/// Builds <c>Record ‖ CRC-64</c> frames in a reusable scratch buffer — no allocation per frame once warmed up
///  (Design-FormatV21 §4.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not thread-safe; one per writer of frames.</b> In the product each agent owns one through its
///  <c>TapeRecordEmitter21</c>; file headers are written on the agent's thread only.
/// </para>
/// <para>
/// <b>What it reuses:</b> the scratch <see cref="MemoryStream"/> (its buffer survives <c>SetLength(0)</c>) and one
///  <see cref="TapeRecordWriter"/> bound to it, which in turn keeps its pooled <c>TapeBuffer</c> and field writers. The
///  CRC-64 is computed with the static <see cref="Crc64.Hash(ReadOnlySpan{byte}, Span{byte})"/> into a stack buffer.
/// </para>
/// <para>
/// <b>Bytes are identical</b> to the static <see cref="TapeFrame"/> path — which now runs through a temporary instance of
///  this class, so there is one implementation.
/// </para>
/// </remarks>
public sealed class TapeFrameWriter : IDisposable
{
    /// <summary>
    /// Scratch capacity kept between frames. A file header with a maximal path is ~96 KiB; anything above this limit is
    ///  released after use, so one huge record does not pin memory for the rest of the session.
    /// </summary>
    private const int RetainLimit = 128 * 1024;

    private readonly MemoryStream m_scratch = new();
    private readonly TapeRecordWriter m_records;
    private bool m_disposed;

    public TapeFrameWriter()
    {
        m_records = new TapeRecordWriter(m_scratch);
    }

    /// <summary>
    /// Writes the inline frame of <paramref name="record"/> to <paramref name="destination"/> with one
    ///  <see cref="Stream.Write(byte[], int, int)"/>; returns the bytes written.
    /// </summary>
    public int WriteInline<T>(Stream destination, T record) where T : ITapeRecord<T>
    {
        ArgumentNullException.ThrowIfNull(destination);
        int length = Build(record);
        destination.Write(m_scratch.GetBuffer(), 0, length);
        return length;
    }

    /// <summary>Returns the frame bytes of <paramref name="record"/> as a new array (the one inherent allocation).</summary>
    public byte[] Pack<T>(T record) where T : ITapeRecord<T>
    {
        int length = Build(record);
        return m_scratch.GetBuffer().AsSpan(0, length).ToArray();
    }

    /// <summary>Builds <c>Record ‖ CRC-64</c> at the start of the scratch buffer; returns its length.</summary>
    /// <remarks>
    /// A record whose writer throws (e.g. its validate hook) is abandoned by <see cref="TapeRecordWriter"/> and leaves the
    ///  scratch empty; the next call starts clean (F13).
    /// <patra>
    /// The CRC goes through the STATIC <see cref="Crc64.Hash(ReadOnlySpan{byte}, Span{byte})"/> into a stack buffer —
    ///  never <see cref="TapeFrame.ComputeCrc"/>, which allocates a hasher and an 8-byte array (64 B per frame).
    /// </patra>
    /// </remarks>
    private int Build<T>(T record) where T : ITapeRecord<T>
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);

        m_scratch.SetLength(0);                         // keeps the buffer; Position follows to 0
        if (m_scratch.Capacity > RetainLimit)
            m_scratch.Capacity = 0;                     // release what one oversized record grew

        m_records.Write(record);                        // prologue + body, one Write into the scratch
        int recordLength = (int)m_scratch.Length;

        Span<byte> crc = stackalloc byte[TapeFrame.CrcLength];
        Crc64.Hash(m_scratch.GetBuffer().AsSpan(0, recordLength), crc);
        m_scratch.Write(crc);

        return recordLength + TapeFrame.CrcLength;
    }

    public void Dispose()
    {
        if (m_disposed)
            return;
        m_disposed = true;
        m_records.Dispose();                            // returns the pooled TapeBuffer
        m_scratch.Dispose();
    }
}