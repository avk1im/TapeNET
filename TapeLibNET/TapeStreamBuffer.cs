using System.Buffers;
using System.Diagnostics;


namespace TapeLibNET;

// Wrapper around ArrayPool<byte> that exposes the logical Capacity
//  rather than the (potentially oversized) array Length.
//  Allows swapping the underlying allocation mechanism later.
internal sealed class ByteBufferCache(int capacity) : IDisposable
{
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(capacity);

    public int Capacity => capacity;
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(ByteBufferCache));

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}

public class TapeByteBuffer(int capacity) : IDisposable
{
    private readonly byte[] m_buffer = ArrayPool<byte>.Shared.Rent(capacity);
    private int m_writeFrom = 0; // index of the first free element
    private int m_readFrom = 0; // index of the first occupied element
    public bool IsDisposed { get; private set; } = false;


    // implement IDisposable - do not override
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // overridable IDisposable implementation via virtual Dispose(bool)
    protected virtual void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            if (disposing)
            {
                // return rented buffer to the shared pool
                ArrayPool<byte>.Shared.Return(m_buffer);
            }
            // no unmanaged resources to dispose

            IsDisposed = true;
        }
    }
    // do not override
    ~TapeByteBuffer()
    {
        Dispose(disposing: false);
    }


    public int Capacity => capacity;
    public int ContentSize => m_writeFrom - m_readFrom;
    public bool IsEmpty => ContentSize == 0;
    public bool IsNonEmpty => !IsEmpty;
    public bool IsFull => ContentSize == Capacity;
    public int Remaining => Capacity - ContentSize;
    public void Reset()
    {
        Debug.Assert(!IsDisposed, "Attempting to reset disposed buffer");

        m_writeFrom = 0;
        m_readFrom = 0;
    }

    // For lazy content shifting to the begining of the buffer
    private int RemainingWithoutShift => capacity - m_writeFrom;
    private void ShiftContentToBegining()
    {
        if (m_readFrom > 0)
        {
            Buffer.BlockCopy(m_buffer, m_readFrom, m_buffer, 0, ContentSize); // BlockCopy can handle overlapping ranges
            m_writeFrom = ContentSize;
            m_readFrom = 0;
        }
    }
    private void MakeRoomFor(int count)
    {
        if (RemainingWithoutShift < count)
            ShiftContentToBegining();
    }

    // Core spill: moves bytes from the beginning of the buffer (first in, first out).
    //  Delegates the actual data transfer to the provided function.
    //  Returns the number of bytes moved.
    private int SpillCore(int count, Func<byte[], int, int, int> transfer)
    {
        int toMove = Math.Min(count, ContentSize);
        if (toMove <= 0)
            return 0;

        int moved = transfer(m_buffer, m_readFrom, toMove);
        ArgumentOutOfRangeException.ThrowIfNegative(moved, nameof(moved));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(moved, toMove, nameof(moved));

        m_readFrom += moved;
        Debug.Assert(m_readFrom >= 0 && m_readFrom <= m_writeFrom);
        Debug.Assert(m_writeFrom >= 0 && m_writeFrom <= Capacity);

        return moved;
    }

    // Core fill: adds to the end of the buffer from the provided source function.
    //  Returns the number of bytes copied.
    private int FillCore(int count, Func<byte[], int, int, int> transfer)
    {
        int toMove = Math.Min(count, Remaining);
        if (toMove <= 0)
            return 0;

        MakeRoomFor(toMove);

        int moved = transfer(m_buffer, m_writeFrom, toMove);
        ArgumentOutOfRangeException.ThrowIfNegative(moved, nameof(moved));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(moved, toMove, nameof(moved));

        m_writeFrom += moved;
        Debug.Assert(m_writeFrom >= 0 && m_writeFrom <= Capacity);
        return moved;
    }


    // Operations with byte[]
    public int SpillTo(byte[] dst, int offset, int count)
    {
        return SpillCore(count, (src, srcOffset, n) =>
            { Buffer.BlockCopy(src, srcOffset, dst, offset, n); return n; });
    }

    public int FillFrom(byte[] src, int offset, int count)
    {
        return FillCore(count, (dst, dstOffset, n) =>
            { Buffer.BlockCopy(src, offset, dst, dstOffset, n); return n; });
    }


    // Virtual owner I/O — override in subclasses to route to the actual stream
    protected virtual int ReadFromOwner(byte[] dst, int dstOffset, int count)
        => throw new NotSupportedException("No owner configured for reading");
    protected virtual int WriteToOwner(byte[] src, int srcOffset, int count)
        => throw new NotSupportedException("No owner configured for writing");

    // Owner operations (use the virtual methods above)
    public int FillFromOwner(int count) => FillCore(count, ReadFromOwner);
    public int SpillToOwner(int count) => SpillCore(count, WriteToOwner);
    public int FlushToOwner() => SpillToOwner(ContentSize);
    public int SpillZeroPaddedToOwner(int count)
    {
        ZeroPadTo(count);
        return SpillToOwner(count);
    }


    // Zero-pad the content up to count elements, of course not exceeding Capacity
    protected void ZeroPadTo(int count)
    {
        if (count > ContentSize)
        {
            count = Math.Min(count, Capacity);
            int toPad = count - ContentSize;

            MakeRoomFor(toPad);

            Array.Clear(m_buffer, m_writeFrom, toPad);
            m_writeFrom += toPad;

            Debug.Assert(ContentSize == count);
        }
    }
}

// The buffer class that knows to use ReadDirect and WriteDirect methods of the TapeReadStream and TapeWriteStream
public class TapeStreamBuffer(TapeStream owner, int capacity) : TapeByteBuffer(capacity)
{
    protected override int ReadFromOwner(byte[] dst, int dstOffset, int count)
    {
        if (owner is TapeReadStream rstream)
            return rstream.ReadDirect(dst, dstOffset, count);
        throw new InvalidOperationException("Attempting to read from non-readable owner stream");
    }

    protected override int WriteToOwner(byte[] src, int srcOffset, int count)
    {
        if (owner is TapeWriteStream wstream)
            return wstream.WriteDirect(src, srcOffset, count);
        throw new InvalidOperationException("Attempting to write to non-writable owner stream");
    }
} // class TapeStreamBuffer
