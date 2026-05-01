namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Thin <see cref="Stream"/> façade returned by <see cref="TapeFileWritePacker.BeginFile"/>.
/// Forwards <see cref="Write(byte[], int, int)"/> calls to the packer; ignores
/// <see cref="Flush"/> (the packer decides when to flush blocks); becomes inert after
/// the corresponding <see cref="TapeFileWritePacker.EndFile"/>, <see cref="TapeFileWritePacker.DiscardOpenFile"/>,
/// or packer disposal.
/// <para>
/// The agent reads <see cref="CommitToken"/> to correlate the file with the
/// later <see cref="TapeFileWritePacker.FilesCommitted"/> notification carrying the
/// resolved <see cref="TapeAddress"/>.
/// </para>
/// </summary>
internal sealed class TapeWriteStreamFacade : Stream
{
    private readonly TapeFileWritePacker _packer;
    private long _written;
    private bool _closed;

    internal TapeWriteStreamFacade(TapeFileWritePacker packer, CommitToken token)
    {
        _packer = packer;
        CommitToken = token;
    }

    /// <summary>The commit token assigned to this file at <see cref="TapeFileWritePacker.BeginFile"/> time.</summary>
    public CommitToken CommitToken { get; }

    /// <summary>Marks the façade closed (called by the packer); further writes throw.</summary>
    internal void MarkClosed() => _closed = true;

    public override bool CanRead => false;
    public override bool CanWrite => !_closed;
    public override bool CanSeek => false;

    public override long Length => _written;
    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (_closed)
            throw new ObjectDisposedException(nameof(TapeWriteStreamFacade));
        if (count == 0)
            return;

        _packer.WriteFromOpenFile(buffer, offset, count);
        _written += count;
    }

    /// <summary>No-op: flushing is the packer's responsibility, not per-stream.</summary>
    public override void Flush() { /* intentional no-op */ }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();
    public override void SetLength(long value)
        => throw new NotSupportedException();
}
