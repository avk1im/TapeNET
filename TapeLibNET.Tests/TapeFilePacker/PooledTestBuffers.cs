namespace TapeLibNET.Tests.TapeFilePacker;

/// <summary>
/// Test helper that owns a <see cref="TapeWriteBufferPool"/> and hands out page-aligned
/// <see cref="TapeWriteBuffer"/> instances, tracking every rental so they are all returned (and the
/// pool disposed) when the fixture is disposed.
/// <para>
/// Fixture-scoped rather than per-buffer on purpose: in these tests a buffer's OWNERSHIP transfers to
/// the backend across <c>StartWriting</c> / <c>AwaitCompletion</c>, and assertions compare the raw
/// <see cref="TapeWriteBuffer"/> reference (<c>Assert.Same</c>). A per-buffer <c>using</c> wrapper would
/// both obscure that identity and race the ownership hand-off. Centralizing rent/return here keeps the
/// tests reading like the old <c>byte[]</c> versions while guaranteeing no buffer is leaked.
/// </para>
/// </summary>
internal sealed class PooledTestBuffers : IDisposable
{
    private readonly TapeWriteBufferPool _pool = new();
    private readonly List<TapeWriteBuffer> _live = [];

    /// <summary>Block size used to translate "block counts" into byte lengths.</summary>
    public uint BlockSize { get; }

    public PooledTestBuffers(uint blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfZero(blockSize);

        BlockSize = blockSize;
    }

    /// <summary>Byte length of <paramref name="blocks"/> whole blocks.</summary>
    public int Bytes(int blocks) => blocks * (int)BlockSize;

    /// <summary>
    /// Rents a page-aligned buffer large enough for <paramref name="blocks"/> blocks and fills its usable
    /// window with <paramref name="fill"/>. The buffer is tracked and returned automatically on dispose.
    /// </summary>
    public TapeWriteBuffer Make(int blocks, byte fill)
    {
        int len = Bytes(blocks);
        TapeWriteBuffer buf = _pool.Rent(len);
        buf.Data(len).Fill(fill);
        _live.Add(buf);
        return buf;
    }

    /// <summary>Snapshot of the first <paramref name="length"/> bytes of <paramref name="buf"/>'s window.</summary>
    public static byte[] Content(TapeWriteBuffer buf, int length) => buf.Data(length).ToArray();

    public void Dispose()
    {
        // Return everything we handed out, then drop the pool (POH arrays are GC-reclaimed).
        foreach (TapeWriteBuffer b in _live)
        {
            try { b.Return(); } catch { /* ignore double-return in edge-case tests */ }
        }
        _live.Clear();
        _pool.Dispose();
    }
}
