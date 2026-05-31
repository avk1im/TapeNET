using ZstdNet;

namespace TapeLibNET;

// ── Per-file codec flag ───────────────────────────────────────────────────────

/// <summary>
/// Identifies the codec used to compress an individual file body on tape.
/// Stored in <see cref="TapeFileInfo"/> and serialized to the TOC so restore
/// can transparently decompress each file regardless of the set-level setting.
/// </summary>
public enum TapeFileCodec : byte
{
    /// <summary>File body is stored uncompressed (passthrough or auto-store fallback).</summary>
    Stored = 0,

    /// <summary>File body was compressed with ZSTD (ZstdNet/libzstd).</summary>
    Zstd   = 1,
}

// ── ZstdCodec — lifetime-managed compressor/decompressor instances ─────────────

/// <summary>
/// Carries the <see cref="CompressionOptions"/> for a ZSTD compression session.
/// Pass it to <see cref="CompressionFilterStream"/> (compression) or
/// <see cref="DecompressionFilterStream"/> (decompression).
/// Dispose when the session ends to release the native context.
/// </summary>
internal sealed class ZstdCodec : IDisposable
{
    private readonly CompressionOptions? _options; // null on decompression-only instances

    /// <param name="level">ZSTD compression level (clamped to [<see cref="ZstdLevel.Min"/>, <see cref="ZstdLevel.Max"/>]).
    ///   Pass 0 (or omit) when this instance is used only for decompression.</param>
    public ZstdCodec(int level = 0)
    {
        Level = level;
        if (level > 0)
            _options = new CompressionOptions(ZstdLevel.Clamp(level));
    }

    /// <summary>The compression level this instance was created with (0 = decompression-only).</summary>
    internal int Level { get; }

    /// <summary>The <see cref="CompressionOptions"/> for this session, or <c>null</c> for decompression-only use.</summary>
    internal CompressionOptions? Options => _options;

    public void Dispose() => _options?.Dispose();
}

// ── CompressionFilterStream — write side ─────────────────────────────────────

/// <summary>
/// Write-only stream that compresses data written to it via ZSTD and forwards the
///  compressed bytes to <paramref name="inner"/>.
/// <para>
///  The underlying <see cref="ZstdNet.CompressionStream"/> frames are finalized on
///  <see cref="Dispose"/>; callers <em>must</em> dispose this stream before calling
///  <see cref="TapeStreamManager.EndPackedFile"/> so <see cref="TapeFileInfo.SizeOnTape"/>
///  captures the fully-flushed compressed size.
/// </para>
/// <para>Seeking is not supported.</para>
/// </summary>
/// <param name="inner">Destination stream that receives compressed bytes.</param>
/// <param name="codec"><see cref="ZstdCodec"/> whose <see cref="ZstdCodec.Options"/> are passed to the underlying <see cref="ZstdNet.CompressionStream"/>.</param>
internal sealed class CompressionFilterStream(Stream inner, ZstdCodec codec) : Stream
{
    // ZstdNet.CompressionStream owns its write semantics; create lazily so the
    //  instance is only allocated when the first byte is written.
    private readonly ZstdNet.CompressionStream _zstream = codec.Options != null
        ? new(inner, codec.Options)
        : new(inner);
    private bool _disposed;

    // ── Stream overrides ─────────────────────────────────────────────────────

    public override bool CanRead  => false;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek  => false;

    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                 => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("CompressionFilterStream is write-only.");

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _zstream.Write(buffer, offset, count);
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _zstream.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            // Flushing the ZstdNet.CompressionStream finalizes the ZSTD frame trailer.
            _zstream.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}

// ── ProbingCompressionStream — adaptive compress-or-store write stream ────────

/// <summary>
/// Write-only stream that samples the first <see cref="ProbeLength"/> bytes of data to
///  decide at runtime whether ZSTD compression saves space, then commits the decision
///  and streams the remainder directly — avoiding full-file buffering.
/// <para>
/// <b>Probe phase</b> (first ≤ <see cref="ProbeLength"/> bytes written):<br/>
///  Raw bytes accumulate in an internal raw buffer; a ZSTD stream simultaneously compresses
///  them into a compressed buffer. Both buffers live inside a <see cref="Session"/> that is
///  allocated once per backup session and reused across files, avoiding per-file GC pressure.
/// </para>
/// <para>
/// <b>Commit</b> (probe sealed on first <see cref="Write"/> past <see cref="ProbeLength"/>,
///  or on <see cref="Flush"/>/<see cref="Dispose"/>):<br/>
///  If the compressed probe is smaller than the raw probe, the compressed bytes are flushed
///  to <c>inner</c> and — only when more bytes will follow (file &gt; probe window) —
///  subsequent bytes are piped through a live <see cref="ZstdNet.CompressionStream"/>
///  wrapping <c>inner</c>, producing one contiguous ZSTD frame.<br/>
///  For files entirely within the probe window the single probe frame is the complete output.
///  Otherwise the raw probe bytes are written to <c>inner</c> and subsequent bytes pass
///  through unmodified.
/// </para>
/// <para>
/// After <see cref="Dispose"/>, <see cref="FinalCodec"/> reports the chosen
///  <see cref="TapeFileCodec"/>. Always call <see cref="Dispose"/> (or use a
///  <c>using</c> statement) before reading <see cref="FinalCodec"/>; for files
///  smaller than the probe window <see cref="Dispose"/> is the only code path that
///  triggers <see cref="Flush"/>/<c>Commit()</c>.
/// </para>
/// <para>Seeking is not supported.</para>
/// </summary>
internal sealed class ProbingCompressionStream : Stream
{
    /// <summary>
    /// Probe window in bytes: one ZSTD block = <c>ZSTD_BLOCKSIZE_MAX</c> = 128 KiB.
    /// ZstdNet does not expose the C constant, so it is defined explicitly here.
    /// </summary>
    internal const int ProbeLength = 128 * 1024;

    /// <summary>
    /// Worst-case compressed size for a <see cref="ProbeLength"/>-byte input, as returned by
    ///  <c>ZSTD_compressBound</c> (via <see cref="Compressor.GetCompressBound"/>).
    /// Used as the initial capacity of <see cref="Session.CompBuf"/> so it never has to
    ///  reallocate even when the probe data is incompressible and ZSTD adds its frame overhead.
    /// </summary>
    internal static readonly int ProbeBufLength = Compressor.GetCompressBound(ProbeLength);

    // ── Session — lifetime-managed, reusable probe state ─────────────────────

    /// <summary>
    /// Carries the session-scoped resources shared across all files in a single backup
    ///  set: the <see cref="ZstdCodec"/> (which owns its native ZSTD context) and the two
    ///  <see cref="MemoryStream"/> probe buffers (raw and compressed).
    /// <para>
    ///  Allocate once per session and pass to each <see cref="ProbingCompressionStream"/>
    ///  constructor. The session automatically re-creates the <see cref="ZstdCodec"/> when the
    ///  compression level changes (multi-volume edge case) and resets both buffers before
    ///  each file so no external bookkeeping is required.
    /// </para>
    /// <para>Dispose when the backup session ends to release the native ZSTD context.</para>
    /// </summary>
    internal sealed class Session : IDisposable
    {
        private ZstdCodec? _codec;

        /// <summary>
        /// Returns the session-scoped <see cref="ZstdCodec"/>, re-creating it only if
        ///  <paramref name="level"/> differs from the current level.
        /// </summary>
        internal ZstdCodec GetOrUpdateCodec(int level)
        {
            if (_codec == null || _codec.Level != level)
            {
                _codec?.Dispose();
                _codec = new ZstdCodec(level);
            }
            return _codec;
        }

        /// <summary>Raw-bytes probe buffer (capacity = <see cref="ProbeLength"/>).</summary>
        internal MemoryStream RawBuf  { get; } = new(ProbeLength);
        /// <summary>
        /// Compressed-bytes probe buffer (capacity = <see cref="ProbeBufLength"/> =
        ///  <c>ZSTD_compressBound(<see cref="ProbeLength"/>)</c>), large enough to hold the
        ///  worst-case ZSTD output for incompressible probe data without reallocation.
        /// </summary>
        internal MemoryStream CompBuf { get; } = new(ProbeBufLength);

        /// <summary>Resets both probe buffers to empty, ready for the next file.</summary>
        internal void ResetBuffers()
        {
            RawBuf .SetLength(0);
            CompBuf.SetLength(0);
        }

        public void Dispose()
        {
            _codec?.Dispose();
            _codec = null;
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new <see cref="ProbingCompressionStream"/> for one file.
    /// </summary>
    /// <param name="inner">Destination stream (packer write façade) that receives the final bytes.</param>
    /// <param name="session">Session carrying the <see cref="ZstdCodec"/> and reusable probe buffers.
    ///   Call <see cref="Session.ResetBuffers"/> before constructing each instance, or pass
    ///   <paramref name="resetSession"/><c>=true</c> to have the constructor do it.</param>
    /// <param name="level">ZSTD compression level; passed to <see cref="Session.GetOrUpdateCodec"/>.</param>
    /// <param name="resetSession">When <c>true</c> (default), the constructor calls
    ///   <see cref="Session.ResetBuffers"/> automatically.</param>
    internal ProbingCompressionStream(Stream inner, Session session, int level, bool resetSession = true)
    {
        _inner   = inner;
        _session = session;
        _codec   = session.GetOrUpdateCodec(level);
        if (resetSession) session.ResetBuffers();

        _probeZstream = _codec.Options != null
            ? new ZstdNet.CompressionStream(session.CompBuf, _codec.Options)
            : new ZstdNet.CompressionStream(session.CompBuf);
    }

    // ── state ─────────────────────────────────────────────────────────────────

    private readonly Stream  _inner;
    private readonly Session _session;
    private readonly ZstdCodec _codec;

    // Active during the probe phase; null once the decision has been committed.
    private ZstdNet.CompressionStream? _probeZstream;

    // Active after the probe decision if compression won; null in stored mode.
    private ZstdNet.CompressionStream? _liveZstream;

    // True once the probe phase has been sealed and the decision flushed to inner.
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// The codec chosen after the probe.  Meaningful only after <see cref="Dispose"/> has been called.
    /// </summary>
    public TapeFileCodec FinalCodec { get; private set; } = TapeFileCodec.Stored;

    // ── Stream overrides ─────────────────────────────────────────────────────

    public override bool CanRead  => false;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek  => false;

    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                 => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("ProbingCompressionStream is write-only.");

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (count > 0)
        {
            if (!_committed)
            {
                // How many bytes can still go into the probe window?
                int probeRemaining = ProbeLength - (int)_session.RawBuf.Length;

                if (probeRemaining > 0)
                {
                    // Feed as many bytes as fit into the probe window.
                    int take = Math.Min(count, probeRemaining);
                    _session.RawBuf.Write(buffer, offset, take);
                    _probeZstream!.Write(buffer, offset, take);
                    offset += take;
                    count  -= take;
                    probeRemaining -= take;
                }

                // Seal the probe as soon as the window is full OR this is the last chunk
                //  that exactly fills it (probeRemaining hits 0 here).
                if (probeRemaining == 0 && _session.RawBuf.Length >= ProbeLength)
                {
                    Commit();
                    // Loop continues: any leftover bytes in [offset..offset+count) go through
                    //  the live path on the next iteration.
                }
                else
                {
                    // Still in probe mode and buffer not yet full — done for this call.
                    break;
                }
            }
            else
            {
                // Live path: route directly to inner (compressed or raw).
                if (_liveZstream != null)
                    _liveZstream.Write(buffer, offset, count);
                else
                    _inner.Write(buffer, offset, count);
                break;
            }
        }
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_committed)
            Commit();
        _liveZstream?.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            if (!_committed)
                Commit();

            // Finalize the live ZSTD frame (writes the frame trailer to inner).
            _liveZstream?.Dispose();
            _liveZstream = null;
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    // ── Probe commit ─────────────────────────────────────────────────────────

    // Seals the probe compression stream, compares sizes, and flushes the winning
    //  content to inner. Opens the live path (compressed or raw) for the remainder.
    private void Commit()
    {
        // Finalize the probe ZSTD frame so CompBuf.Length is accurate.
        _probeZstream!.Dispose();
        _probeZstream = null;

        long rawLen  = _session.RawBuf.Length;
        long compLen = _session.CompBuf.Length;

        if (compLen < rawLen)
        {
            // Compression wins: flush the compressed probe to inner.
            FinalCodec = TapeFileCodec.Zstd;
            _session.CompBuf.Position = 0;
            _session.CompBuf.CopyTo(_inner);

            // Open a live ZSTD stream for the remainder ONLY when the probe window was
            //  fully filled — meaning there are more bytes yet to be written via Write().
            //  For sub-probe files (entire content already in CompBuf) no live stream is
            //  needed; creating one would emit a spurious empty ZSTD frame that may
            //  confuse the decompressor on restore.
            if (_session.RawBuf.Length >= ProbeLength)
            {
                _liveZstream = _codec.Options != null
                    ? new ZstdNet.CompressionStream(_inner, _codec.Options)
                    : new ZstdNet.CompressionStream(_inner);
            }
        }
        else
        {
            // Store wins: flush raw probe bytes, live path is a direct pass-through.
            FinalCodec = TapeFileCodec.Stored;
            _session.RawBuf.Position = 0;
            _session.RawBuf.CopyTo(_inner);
            // _liveZstream stays null; Write() will call _inner.Write() directly.
        }

        _committed = true;
    }
}


/// <summary>
/// Read-only stream that transparently decompresses ZSTD-compressed bytes read from
///  <paramref name="inner"/> and returns the original uncompressed data to the caller.
/// <para>Seeking is not supported.</para>
/// </summary>
/// <param name="inner">Source stream containing compressed bytes (sized to <see cref="TapeFileInfo.SizeOnTape"/>).</param>
internal sealed class DecompressionFilterStream(Stream inner) : Stream
{
    private readonly ZstdNet.DecompressionStream _zstream = new(inner);
    private bool _disposed;

    // ── Stream overrides ─────────────────────────────────────────────────────

    public override bool CanRead  => !_disposed;
    public override bool CanWrite => false;
    public override bool CanSeek  => false;

    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                 => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("DecompressionFilterStream is read-only.");

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _zstream.Read(buffer, offset, count);
    }

    public override void Flush() { /* no-op for read-only stream */ }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _zstream.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
