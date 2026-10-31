using System.Buffers.Binary;
using System.Security.Cryptography;
using Windows.Win32.Foundation;

namespace TapeLibNET.Encryption;

// ── The construction ─────────────────────────────────────────────────────────

/// <summary>
/// Constants and the nonce layout of the chunked AES-256-GCM stream shared by file records (Software scheme) and sealed
///  TOC metadata (Design-Encryption §4.3).
/// </summary>
/// <remarks>
/// <code>
/// plaintext  = P_0 ‖ P_1 ‖ … ‖ P_n     |P_i| = C for i &lt; n,  0 ≤ |P_n| &lt; C   (final chunk ALWAYS present, ALWAYS short)
/// ciphertext = E_0 ‖ E_1 ‖ … ‖ E_n     E_i = AES-GCM(K, nonce_i, P_i, aad) = ciphertext_i ‖ tag_i(16)
/// nonce_i    = prefix(7) ‖ i (u32 big-endian) ‖ final(1)
/// </code>
/// A full chunk is a middle chunk, a short one is the final one; the reader never needs lookahead.
/// </remarks>
internal static class TapeAead
{
    /// <summary>AES-GCM tag length.</summary>
    public const int TagSize = 16;

    /// <summary>AES-GCM nonce length.</summary>
    public const int NonceSize = 12;

    /// <summary>Per-stream nonce prefix length.</summary>
    public const int PrefixSize = 7;

    /// <summary>AES-256 key length.</summary>
    public const int KeySize = 32;

    /// <summary>Chunk size of a file record (Software scheme).</summary>
    public const int FileChunkSize = 64 * 1024;

    /// <summary>Chunk size of sealed TOC metadata.</summary>
    public const int MetaChunkSize = 1024 * 1024;

    /// <summary>Largest chunk size a stream accepts.</summary>
    public const int MaxChunkSize = MetaChunkSize;

    /// <summary>The nonce prefix of file records: seven zero bytes — the key is unique per file (E3).</summary>
    public static ReadOnlySpan<byte> ZeroPrefix => [0, 0, 0, 0, 0, 0, 0];

    /// <summary>Builds <c>prefix ‖ index (u32 BE) ‖ final</c> into <paramref name="nonce"/>.</summary>
    public static void BuildNonce(ReadOnlySpan<byte> prefix, uint index, bool final, Span<byte> nonce)
    {
        if (prefix.Length != PrefixSize)
            throw new ArgumentException($"A nonce prefix is exactly {PrefixSize} bytes", nameof(prefix));
        if (nonce.Length != NonceSize)
            throw new ArgumentException($"A nonce is exactly {NonceSize} bytes", nameof(nonce));
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[PrefixSize..], index);
        nonce[NonceSize - 1] = final ? (byte)1 : (byte)0;
    }

    /// <summary>Exact ciphertext length of <paramref name="plaintextLength"/> bytes: one tag per chunk, final included.</summary>
    public static long CiphertextLength(long plaintextLength, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        return checked(plaintextLength + TagSize * (plaintextLength / chunkSize + 1));
    }

    /// <summary>Bytes the construction adds to <paramref name="plaintextLength"/> bytes (§5.7 size estimates).</summary>
    public static long Overhead(long plaintextLength, int chunkSize)
        => CiphertextLength(plaintextLength, chunkSize) - plaintextLength;
}

// ── Session ──────────────────────────────────────────────────────────────────

/// <summary>
/// A reusable chunk buffer for a sequence of AEAD streams — one stream at a time. Mirrors
///  <c>ProbingCompressionStream.Session</c>: allocate once per agent, reuse for every file.
/// </summary>
/// <remarks>
/// The buffer is zeroed whenever a stream releases it and when the session is disposed. Not thread-safe; a second
///  stream begun while one is live is a programming error and throws.
/// </remarks>
internal sealed class TapeAeadSession(int chunkSize = TapeAead.FileChunkSize) : IDisposable
{
    private byte[]? m_buffer;
    private bool m_inUse;
    private bool m_disposed;

    /// <summary>The chunk size every stream of this session uses.</summary>
    public int ChunkSize { get; } = chunkSize is >= 1 and <= TapeAead.MaxChunkSize
        ? chunkSize
        : throw new ArgumentOutOfRangeException(nameof(chunkSize));

    /// <summary>Begins an encrypting stream over <paramref name="inner"/> using this session's buffer.</summary>
    public EncryptingStream BeginEncrypt(Stream inner, ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> aad = default)
        => new(inner, key, prefix, ChunkSize, aad, this);

    /// <summary>Begins a decrypting stream over <paramref name="inner"/> using this session's buffer.</summary>
    public DecryptingStream BeginDecrypt(Stream inner, ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> aad = default)
        => new(inner, key, prefix, ChunkSize, aad, this);

    internal byte[] Acquire()
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        if (m_inUse)
            throw new InvalidOperationException("An AEAD stream of this session is still live");
        m_inUse = true;
        return m_buffer ??= new byte[ChunkSize + TapeAead.TagSize];
    }

    internal void Release(byte[] buffer)
    {
        CryptographicOperations.ZeroMemory(buffer);
        m_inUse = false;
    }

    public void Dispose()
    {
        if (m_disposed)
            return;
        if (m_buffer is not null)
            CryptographicOperations.ZeroMemory(m_buffer);
        m_buffer = null;
        m_disposed = true;
    }
}

// ── Common base ──────────────────────────────────────────────────────────────

/// <summary>
/// Shared plumbing of the two AEAD streams: key schedule, nonce prefix, associated data, chunk buffer and its release.
/// </summary>
/// <remarks>
/// Chunks are processed IN PLACE in one buffer of <c>chunkSize + 16</c> bytes: the data occupies <c>[0, n)</c>, its tag
///  <c>[n, n + 16)</c>, so a chunk leaves (or arrives) as ONE contiguous write (read). <see cref="AesGcm"/> explicitly
///  permits identical plaintext and ciphertext spans.
/// </remarks>
internal abstract class TapeAeadStreamBase : Stream
{
    #region *** Fields ***

    protected readonly Stream m_inner;
    protected readonly int m_chunkSize;
    private readonly AesGcm m_aes;
    private readonly byte[] m_prefix = new byte[TapeAead.PrefixSize];
    private readonly byte[]? m_aad;
    private readonly TapeAeadSession? m_session;
    protected byte[] m_buffer;
    protected uint m_index;
    private bool m_released;

    #endregion

    #region *** Construction ***

    protected TapeAeadStreamBase(Stream inner, ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix, int chunkSize,
        ReadOnlySpan<byte> aad, TapeAeadSession? session)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (key.Length != TapeAead.KeySize)
            throw new ArgumentException($"An AEAD key is exactly {TapeAead.KeySize} bytes", nameof(key));
        if (prefix.Length != TapeAead.PrefixSize)
            throw new ArgumentException($"A nonce prefix is exactly {TapeAead.PrefixSize} bytes", nameof(prefix));
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, TapeAead.MaxChunkSize);
        if (session is not null && session.ChunkSize != chunkSize)
            throw new ArgumentException("The session's chunk size differs from the stream's", nameof(session));

        m_inner = inner;
        m_chunkSize = chunkSize;
        prefix.CopyTo(m_prefix);
        m_aad = aad.IsEmpty ? null : aad.ToArray();
        m_aes = new AesGcm(key, TapeAead.TagSize);         // first: if this throws, no buffer is held
        m_session = session;
        m_buffer = session?.Acquire() ?? new byte[chunkSize + TapeAead.TagSize];
    }

    #endregion

    #region *** Chunk primitives ***

    /// <summary>Whether the key schedule and the buffer have been released.</summary>
    protected bool IsReleased => m_released;

    /// <summary>Encrypts <c>m_buffer[0, length)</c> in place and writes the tag right after it.</summary>
    protected void SealChunk(int length, bool final)
    {
        Span<byte> nonce = stackalloc byte[TapeAead.NonceSize];
        TapeAead.BuildNonce(m_prefix, m_index, final, nonce);
        Span<byte> data = m_buffer.AsSpan(0, length);
        m_aes.Encrypt(nonce, data, data, m_buffer.AsSpan(length, TapeAead.TagSize), m_aad);
    }

    /// <summary>
    /// Authenticates and decrypts <c>m_buffer[0, cipherLength)</c> (data ‖ tag) in place. Returns <see langword="false"/>
    ///  when the tag does not verify; the buffer content is then undefined and must not be released to anyone.
    /// </summary>
    protected bool TryOpenChunk(int cipherLength, bool final)
    {
        int length = cipherLength - TapeAead.TagSize;
        Span<byte> nonce = stackalloc byte[TapeAead.NonceSize];
        TapeAead.BuildNonce(m_prefix, m_index, final, nonce);
        Span<byte> data = m_buffer.AsSpan(0, length);
        try
        {
            m_aes.Decrypt(nonce, data, m_buffer.AsSpan(length, TapeAead.TagSize), data, m_aad);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(m_buffer);
            return false;
        }
    }

    /// <summary>Advances to the next chunk index; a stream of more than 2³² chunks is refused.</summary>
    protected void NextIndex()
    {
        if (m_index == uint.MaxValue)
            throw new IOException("AEAD stream exceeds 2^32 chunks");
        m_index++;
    }

    /// <summary>Disposes the key schedule and zeroes / returns the buffer. Idempotent.</summary>
    protected void Release()
    {
        if (m_released)
            return;
        m_released = true;
        m_aes.Dispose();
        if (m_session is not null)
            m_session.Release(m_buffer);
        else
            CryptographicOperations.ZeroMemory(m_buffer);
    }

    #endregion

    #region *** Stream overrides common to both ***

    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    #endregion
}

// ── Encrypting ───────────────────────────────────────────────────────────────

/// <summary>
/// Write-only stream that turns everything written into the chunked AEAD construction on <c>inner</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A record ends ONLY by <see cref="Complete"/> or <see cref="Abandon"/>.</b> <see cref="Complete"/> writes the final
///  (short, possibly empty) chunk; <see cref="Abandon"/> writes nothing more, leaving a stream no reader will accept —
///  exactly right for a file whose source failed mid-way (Design-Compression lesson 12.1).
/// </para>
/// <para>
/// <b><see cref="Stream.Dispose()"/> is deliberately inert while the stream is open.</b> A wrapping codec stream may
///  dispose its inner stream when IT is disposed; if that meant "complete", a failed file could get a valid-looking
///  final chunk, and if it meant "abandon", every compressed file would lose its final chunk. The owner — the cipher —
///  decides, by calling one of the two methods. Once ended, Dispose is a harmless no-op too.
/// </para>
/// <para>
/// Full chunks are flushed eagerly: a full chunk is never the final one, so nothing is lost by sending it on.
///  <see cref="Flush"/> never emits a partial chunk — that would break the construction; it forwards to <c>inner</c>.
/// </para>
/// </remarks>
internal sealed class EncryptingStream : TapeAeadStreamBase
{
    private enum State { Open, Completed, Abandoned }

    private State m_state = State.Open;
    private int m_fill;
    private long m_plaintext;
    private long m_ciphertext;

    /// <param name="inner">Destination of the ciphertext (e.g. the packer's write façade).</param>
    /// <param name="key">The 32-byte AEAD key; copied into the key schedule.</param>
    /// <param name="prefix">The 7-byte nonce prefix (<see cref="TapeAead.ZeroPrefix"/> for file records).</param>
    /// <param name="chunkSize">Plaintext bytes per full chunk.</param>
    /// <param name="aad">Associated data, authenticated with every chunk; empty for file records.</param>
    /// <param name="session">Optional session lending its buffer; <see langword="null"/> = own buffer.</param>
    public EncryptingStream(Stream inner, ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix, int chunkSize,
        ReadOnlySpan<byte> aad = default, TapeAeadSession? session = null)
        : base(inner, key, prefix, chunkSize, aad, session)
    {
    }

    /// <summary>Plaintext bytes accepted so far.</summary>
    public long PlaintextLength => m_plaintext;

    /// <summary>Ciphertext bytes written to <c>inner</c> so far (all of it after <see cref="Complete"/>).</summary>
    public long CiphertextLength => m_ciphertext;

    /// <summary>Whether the record was ended by <see cref="Complete"/>.</summary>
    public bool IsCompleted => m_state == State.Completed;

    public override bool CanRead => false;
    public override bool CanWrite => m_state == State.Open;

    /// <summary>Plaintext bytes accepted so far; not settable.</summary>
    public override long Position
    {
        get => m_plaintext;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("EncryptingStream is write-only.");

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfNotOpen();
        while (!buffer.IsEmpty)
        {
            int take = Math.Min(m_chunkSize - m_fill, buffer.Length);
            buffer[..take].CopyTo(m_buffer.AsSpan(m_fill));
            m_fill += take;
            m_plaintext += take;
            buffer = buffer[take..];
            if (m_fill == m_chunkSize)
                EmitChunk(final: false);    // a full chunk is never final — send it on
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> one = [value];
        Write(one);
    }

    /// <summary>Forwards to <c>inner</c>; never emits a partial chunk.</summary>
    public override void Flush()
    {
        if (m_state == State.Open)
            m_inner.Flush();
    }

    /// <summary>
    /// Ends the record: writes the final chunk (shorter than a full chunk, possibly empty) and releases the key schedule.
    /// </summary>
    /// <exception cref="InvalidOperationException">The record was already completed or abandoned.</exception>
    public void Complete()
    {
        ThrowIfNotOpen();
        try
        {
            EmitChunk(final: true);
            m_state = State.Completed;
        }
        catch
        {
            m_state = State.Abandoned;
            throw;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// Ends the record WITHOUT a final chunk and releases the key schedule; the buffer is zeroed. Idempotent, and a
    ///  no-op after <see cref="Complete"/>.
    /// </summary>
    public void Abandon()
    {
        if (m_state == State.Open)
            m_state = State.Abandoned;
        Release();
    }

    /// <summary>Inert while open — see the class remarks. Never writes.</summary>
    protected override void Dispose(bool disposing)
    {
        // Deliberately nothing while Open: ending the record is the owner's call (Complete / Abandon).
        //  After either, everything is already released.
        base.Dispose(disposing);
    }

    private void EmitChunk(bool final)
    {
        int length = m_fill;
        SealChunk(length, final);
        m_inner.Write(m_buffer, 0, length + TapeAead.TagSize);
        m_ciphertext += length + TapeAead.TagSize;
        m_fill = 0;
        if (!final)
            NextIndex();
    }

    private void ThrowIfNotOpen()
    {
        if (m_state != State.Open)
            throw new InvalidOperationException($"The encrypted record was already {(m_state == State.Completed ? "completed" : "abandoned")}");
    }
}

// ── Decrypting ───────────────────────────────────────────────────────────────

/// <summary>
/// Read-only stream that authenticates and decrypts the chunked AEAD construction from <c>inner</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Verify before release.</b> A chunk's plaintext is handed out only after its tag verified (E5).
/// </para>
/// <para>
/// <b>Short reads.</b> Each chunk is filled with a read-until-full loop — the packer façade may return fewer bytes than
///  asked. A full chunk is a middle chunk, a short one the final; end of <c>inner</c> before a final chunk is truncation,
///  and any byte after the final chunk is an extension. Both are refused.
/// </para>
/// <para>
/// <b><see cref="VerifyToEnd"/> closes the gap consumers leave.</b> ZSTD's decoder stops at its frame end and a byte
///  comparison stops when the disk file ends; without draining, the last chunks — and the final flag that proves the
///  record is complete — would never be authenticated.
/// </para>
/// <para>
/// Every failure is a <see cref="TapeIOException"/> with <c>ERROR_INVALID_DATA</c>; the caller adds the file context.
///  After a failure the stream refuses further reads.
/// </para>
/// </remarks>
internal sealed class DecryptingStream : TapeAeadStreamBase
{
    private int m_pos;
    private int m_len;
    private bool m_finalSeen;
    private bool m_failed;
    private long m_plaintext;

    /// <param name="inner">Source of the ciphertext, bounded to the record (e.g. the packer's read façade).</param>
    /// <param name="key">The 32-byte AEAD key.</param>
    /// <param name="prefix">The 7-byte nonce prefix the record was written with.</param>
    /// <param name="chunkSize">Plaintext bytes per full chunk, as written.</param>
    /// <param name="aad">Associated data, as written.</param>
    /// <param name="session">Optional session lending its buffer; <see langword="null"/> = own buffer.</param>
    public DecryptingStream(Stream inner, ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix, int chunkSize,
        ReadOnlySpan<byte> aad = default, TapeAeadSession? session = null)
        : base(inner, key, prefix, chunkSize, aad, session)
    {
    }

    /// <summary>Whether the final chunk has been read and authenticated.</summary>
    public bool IsAtVerifiedEnd => m_finalSeen && m_pos == m_len;

    public override bool CanRead => !IsReleased && !m_failed;
    public override bool CanWrite => false;

    /// <summary>Plaintext bytes delivered so far; not settable.</summary>
    public override long Position
    {
        get => m_plaintext;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("DecryptingStream is read-only.");

    public override void Flush() { /* read-only */ }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfUnusable();
        int total = 0;
        while (!buffer.IsEmpty)
        {
            if (m_pos == m_len)
            {
                if (m_finalSeen || total > 0)
                    break;              // the end — or hand out what we have before touching the medium again
                FillChunk();
                continue;
            }
            int take = Math.Min(m_len - m_pos, buffer.Length);
            m_buffer.AsSpan(m_pos, take).CopyTo(buffer);
            m_pos += take;
            total += take;
            buffer = buffer[take..];
        }
        m_plaintext += total;
        return total;
    }

    /// <summary>Byte-wise read without the base class's per-call array; record headers are read this way.</summary>
    public override int ReadByte()
    {
        ThrowIfUnusable();
        while (m_pos == m_len)
        {
            if (m_finalSeen)
                return -1;
            FillChunk();
        }
        m_plaintext++;
        return m_buffer[m_pos++];
    }

    /// <summary>
    /// Reads and authenticates every remaining chunk, discarding its plaintext, and requires the final one (E5).
    ///  Leftover plaintext is not an error.
    /// </summary>
    /// <exception cref="TapeIOException"><c>ERROR_INVALID_DATA</c> — a failed tag, truncation or extension.</exception>
    public void VerifyToEnd()
    {
        ThrowIfUnusable();
        while (!m_finalSeen)
        {
            m_pos = m_len;
            FillChunk();
        }
        m_pos = m_len;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Release();
        base.Dispose(disposing);
    }

    private void FillChunk()
    {
        int want = m_chunkSize + TapeAead.TagSize;
        int got = 0;
        while (got < want)
        {
            int n = m_inner.Read(m_buffer, got, want - got);
            if (n <= 0)
                break;
            got += n;
        }

        if (got == 0)
            Fail($"the encrypted record ends without its final chunk (after chunk {m_index})");
        if (got < TapeAead.TagSize)
            Fail($"the encrypted record is truncated inside chunk {m_index}");

        bool final = got < want;
        if (!TryOpenChunk(got, final))
            Fail($"authentication failed at chunk {m_index} — the data is corrupted or has been tampered with");

        m_pos = 0;
        m_len = got - TapeAead.TagSize;

        if (final)
        {
            m_finalSeen = true;
            Span<byte> probe = stackalloc byte[1];
            if (m_inner.Read(probe) > 0)
                Fail("the encrypted record continues past its final chunk");
        }
        else
        {
            NextIndex();
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(IsReleased, this);
        if (m_failed)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA, "The encrypted record failed authentication earlier");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string reason)
    {
        m_failed = true;
        m_pos = m_len = 0;
        CryptographicOperations.ZeroMemory(m_buffer);
        throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA, $"Encrypted record invalid: {reason}");
    }
}