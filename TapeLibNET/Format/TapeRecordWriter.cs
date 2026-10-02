namespace TapeLibNET.Format;

/// <summary>
/// Writes complete records - prologue + body - to a stream (Design-Format-v2 §4.2, §8.1).
/// </summary>
/// <remarks>
/// A record is built in a pooled buffer and written with ONE <see cref="Stream.Write(byte[], int, int)"/>:
///  no measuring pass and no seekable stream needed. <see cref="EndRecord"/> is explicit.
/// </remarks>
public sealed class TapeRecordWriter(Stream stream) : IDisposable
{
    private TapeBuffer? m_buffer;
    private TapeFieldWriter? m_root;
    private bool m_active;
    private TapeRecordKind m_kind;

    /// <summary>The destination stream.</summary>
    public Stream Stream => stream;

    /// <summary>Total bytes written to <see cref="Stream"/> by completed records.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Starts a record; fill the returned writer, then call <see cref="EndRecord"/>.</summary>
    public TapeFieldWriter BeginRecord(TapeRecordKind kind)
    {
        if (m_active)
            throw new InvalidOperationException("previous record was not ended");
        if (!TapeFormat.IsKnownKind(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a registered record kind");

        m_buffer ??= new TapeBuffer(TapeFormat.MaxPrologueLength);
        m_root ??= new TapeFieldWriter(m_buffer, 0);
        m_root.Reset();
        m_kind = kind;
        m_active = true;
        return m_root;
    }

    /// <summary>Writes prologue + body of the record begun with <see cref="BeginRecord"/>.</summary>
    public void EndRecord()
    {
        if (!m_active || m_buffer == null)
            throw new InvalidOperationException("no record in progress");

        Span<byte> prologue = stackalloc byte[TapeFormat.MaxPrologueLength];
        int n = WritePrologue(prologue, m_kind, m_buffer.Count);
        m_buffer.WriteTo(stream, prologue[..n]);

        BytesWritten += n + m_buffer.Count;
        m_active = false;
    }

    /// <summary>Discards an unfinished record (e.g. after an exception while filling it).</summary>
    public void AbandonRecord() => m_active = false;

    /// <summary>Begins a record, lets <paramref name="fill"/> write its fields, and ends it.</summary>
    public void Write(TapeRecordKind kind, Action<TapeFieldWriter> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        TapeFieldWriter fields = BeginRecord(kind);
        try
        {
            fill(fields);
        }
        catch
        {
            AbandonRecord();
            throw;
        }
        EndRecord();
    }

    /// <summary>Writes <paramref name="record"/> as a complete record.</summary>
    public void Write<T>(T record) where T : ITapeRecord<T>
    {
        TapeFieldWriter fields = BeginRecord(record.RecordKind);
        try
        {
            record.WriteBody(fields);
        }
        catch
        {
            AbandonRecord();
            throw;
        }
        EndRecord();
    }

    /// <summary>Encodes a record prologue into <paramref name="destination"/>; returns its length.</summary>
    internal static int WritePrologue(Span<byte> destination, TapeRecordKind kind, int bodyLength)
    {
        TapeFormat.Magic.CopyTo(destination);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(destination[TapeFormat.MagicLength..], (ushort)kind);
        destination[6] = TapeFormat.Major;
        destination[7] = TapeFormat.Minor;
        return TapeFormat.FixedPrologueLength + TapePrimitives.WriteVarUInt(destination[TapeFormat.FixedPrologueLength..], (ulong)bodyLength);
    }

    public void Dispose()
    {
        m_root?.Release();
        m_root = null;
        m_buffer?.Dispose();
        m_buffer = null;
        m_active = false;
    }
}
