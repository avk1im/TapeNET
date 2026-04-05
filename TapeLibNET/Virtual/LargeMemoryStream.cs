using System.IO.MemoryMappedFiles;

namespace TapeLibNET.Virtual;

/// <summary>
/// A fixed-capacity stream backed by an anonymous memory-mapped file, supporting
/// sizes beyond the 2 GB limit of <see cref="MemoryStream"/>.
/// <para>
/// Wraps a <see cref="MemoryMappedViewStream"/> and tracks the logical write length
/// independently from the mapped region capacity. The OS commits physical pages only
/// as they are touched, so the full capacity is not immediately allocated in RAM.
/// </para>
/// </summary>
public sealed class LargeMemoryStream : Stream
{
    #region *** Private Fields ***

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewStream _view;
    private readonly long _capacity;
    private long _length;
    private bool _disposed;

    #endregion

    #region *** Constructor ***

    /// <summary>
    /// Creates a new large memory stream backed by an anonymous memory-mapped file.
    /// </summary>
    /// <param name="capacity">Maximum stream capacity in bytes. Must be positive.</param>
    public LargeMemoryStream(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _mmf = MemoryMappedFile.CreateNew(null, capacity);
        _view = _mmf.CreateViewStream(0, capacity);
    }

    #endregion

    #region *** Properties ***

    /// <summary>Fixed capacity of the underlying memory-mapped region.</summary>
    public long Capacity => _capacity;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;

    /// <summary>
    /// Logical length of data written — not the mapped capacity.
    /// Grows automatically as data is written; can be reset via <see cref="SetLength"/>.
    /// </summary>
    public override long Length => _length;

    public override long Position
    {
        get => _view.Position;
        set => _view.Position = value;
    }

    #endregion

    #region *** Stream Overrides ***

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Clamp to logical length — don't read into unwritten capacity
        long available = _length - _view.Position;
        if (available <= 0) return 0;
        int toRead = (int)Math.Min(count, available);
        return _view.Read(buffer, offset, toRead);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long endPosition = _view.Position + count;
        if (endPosition > _capacity)
            throw new IOException(
                $"Write of {count} bytes at position {_view.Position} " +
                $"would exceed capacity ({_capacity} bytes).");

        _view.Write(buffer, offset, count);

        // Advance logical length to the furthest written position
        if (_view.Position > _length)
            _length = _view.Position;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Handle SeekOrigin.End relative to our logical length, not the view's capacity
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _view.Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (newPosition < 0)
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");

        _view.Position = newPosition;
        return newPosition;
    }

    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (value < 0 || value > _capacity)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"Length must be in [0, {_capacity}], got {value}.");

        _length = value;
        if (_view.Position > _length)
            _view.Position = _length;
    }

    public override void Flush()
    {
        if (!_disposed)
            _view.Flush();
    }

    #endregion

    #region *** Dispose ***

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _view.Dispose();
                _mmf.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    #endregion
}
