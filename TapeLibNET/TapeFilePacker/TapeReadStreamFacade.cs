namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Thin <see cref="Stream"/> façade returned by <see cref="TapeFileReadPacker.BeginRead"/>.
/// Forwards <see cref="Read(byte[], int, int)"/> calls to the packer; becomes inert after
/// the corresponding <see cref="TapeFileReadPacker.EndRead"/> or packer disposal.
/// </summary>
internal sealed class TapeReadStreamFacade : Stream
{
    private readonly TapeFileReadPacker _packer;
    private readonly long _length;
    private long _position;
    private bool _closed;

    internal TapeReadStreamFacade(TapeFileReadPacker packer, long length)
    {
        _packer = packer;
        _length = length;
    }

    /// <summary>Marks the façade closed (called by the packer); further reads throw.</summary>
    internal void MarkClosed() => _closed = true;

    public override bool CanRead => !_closed;
    public override bool CanWrite => false;
    public override bool CanSeek => false;

    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        ObjectDisposedException.ThrowIf(_closed, nameof(TapeReadStreamFacade));
        if (count == 0)
            return 0;

        int read = _packer.ReadIntoOpenFile(buffer, offset, count);
        _position += read;
        return read;
    }

    public override void Flush() { /* intentional no-op */ }

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();
    public override void SetLength(long value)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            // Closing the stream signals end-of-read; tell the packer to release the slot.
            _packer.EndRead();
        }
        base.Dispose(disposing);
    }
}
