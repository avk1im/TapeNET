namespace TapeLibNET.Format;

/// <summary>
/// Read-only stream wrapper that peeks the first bytes of a stream and replays them (Design-Format-v2 §5.7), so a
///  loader can pick the 2.1 or legacy reader without a seekable source.
/// </summary>
public sealed class TapePeekStream : Stream
{
    private readonly Stream m_inner;
    private readonly bool m_ownInner;
    private readonly byte[] m_peeked;
    private int m_replayPos;

    private TapePeekStream(Stream inner, byte[] peeked, bool ownInner)
    {
        m_inner = inner;
        m_peeked = peeked;
        m_ownInner = ownInner;
    }

    /// <summary>The peeked bytes (fewer than requested if the stream is shorter).</summary>
    public ReadOnlySpan<byte> Peeked => m_peeked;

    /// <summary>Peeks up to <paramref name="count"/> bytes of <paramref name="inner"/> and returns the replaying wrapper.</summary>
    public static TapePeekStream Wrap(Stream inner, int count = TapeFormat.MagicLength, bool ownInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        byte[] buffer = new byte[count];
        int got = inner.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return new TapePeekStream(inner, got == count ? buffer : buffer[..got], ownInner);
    }

    /// <summary>Peeks the leading bytes; <paramref name="peeked"/> is valid as long as the returned stream lives.</summary>
    public static Stream Wrap(Stream inner, out ReadOnlySpan<byte> peeked)
    {
        TapePeekStream wrapper = Wrap(inner);
        peeked = wrapper.Peeked;
        return wrapper;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        int fromPeek = Math.Min(buffer.Length, m_peeked.Length - m_replayPos);
        if (fromPeek > 0)
        {
            m_peeked.AsSpan(m_replayPos, fromPeek).CopyTo(buffer);
            m_replayPos += fromPeek;
            return fromPeek;
        }
        return m_inner.Read(buffer);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && m_ownInner)
            m_inner.Dispose();
        base.Dispose(disposing);
    }
}
