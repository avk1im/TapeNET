using System.Security.Cryptography;
using TapeLibNET.Format;

namespace TapeLibNET.Encryption;

/// <summary>
/// The sealed metadata of one encrypted set, as stored in its <c>TocSealedChunk</c> records (Design-Encryption §5.4):
///  the nonce prefix of the seal and the AEAD chunks, each <c>ciphertext ‖ tag</c>. Immutable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Knows nothing about TOC records.</b> It seals and opens an opaque content buffer; what the buffer holds — today's
///  <c>TocSet</c> record plus its <c>TocFileBatch</c> records — is the TOC's business (<c>TapeSetTOC</c>).
/// </para>
/// <para>
/// <b>Plaintext layout</b> (before chunking): <c>PayloadLength (varuint) ‖ content ‖ zero padding</c>, padded to a
///  multiple of <see cref="PadUnit"/> (at least one unit), so the sealed size reveals the file count only coarsely.
/// </para>
/// <para>
/// <b>Bound to its set.</b> Keyed by <c>K_meta = HKDF(DEK, SetId)</c>; the associated data of every chunk is
///  <c>SetId ‖ Volume (u32 little-endian)</c>, so a payload moved to another set or volume stub never opens.
/// </para>
/// <para>
/// <b>Immutable, hence shareable.</b> A TOC copy (<c>TapeSetTOC.CopyFrom</c>) shares the instance; re-emitting it is what
///  keeps a locked set byte-identical across TOC rewrites (E7).
/// </para>
/// </remarks>
internal sealed class TapeSealedPayload
{
    #region *** Constants ***

    /// <summary>Padding granule of the sealed plaintext.</summary>
    public const int PadUnit = 64 * 1024;

    /// <summary>Size of every non-final stored chunk: a full metadata chunk plus its tag.</summary>
    public const int ChunkCipherSize = TapeAead.MetaChunkSize + TapeAead.TagSize;

    /// <summary>Associated data: SetId (16) ‖ Volume (u32 little-endian).</summary>
    private const int AadSize = 16 + sizeof(uint);

    #endregion

    #region *** State ***

    private readonly byte[][] m_chunks;

    private TapeSealedPayload(byte[] noncePrefix, byte[][] chunks)
    {
        NoncePrefix = noncePrefix;
        m_chunks = chunks;
    }

    /// <summary>The 7-byte nonce prefix of this seal — fresh per seal.</summary>
    public byte[] NoncePrefix { get; }

    /// <summary>The stored chunks, in order; the last one is the final (short) chunk.</summary>
    public IReadOnlyList<byte[]> Chunks => m_chunks;

    /// <summary>Total stored bytes.</summary>
    public long CiphertextLength
    {
        get
        {
            long length = 0;
            foreach (byte[] chunk in m_chunks)
                length += chunk.Length;
            return length;
        }
    }

    /// <summary>Length of the sealed plaintext — a multiple of <see cref="PadUnit"/> for a well-formed seal.</summary>
    public long PlaintextLength => CiphertextLength - (long)TapeAead.TagSize * m_chunks.Length;

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Rebuilds a payload from stored chunks, checking the STRUCTURE only: prefix size, at least one chunk, every middle
    ///  chunk exactly <see cref="ChunkCipherSize"/>, the final one at least a tag and shorter than a full chunk.
    /// </summary>
    /// <exception cref="TapeFormatException"><see cref="FormatErrorKind.CrossCheck"/> on any violation.</exception>
    public static TapeSealedPayload FromStored(byte[] noncePrefix, IReadOnlyList<byte[]> chunks)
    {
        ArgumentNullException.ThrowIfNull(noncePrefix);
        ArgumentNullException.ThrowIfNull(chunks);
        if (noncePrefix.Length != TapeAead.PrefixSize)
            throw Structure($"the seal nonce prefix has {noncePrefix.Length} bytes, not {TapeAead.PrefixSize}");
        if (chunks.Count == 0)
            throw Structure("a sealed set holds no chunks");

        for (int i = 0; i < chunks.Count; i++)
        {
            int length = chunks[i]?.Length ?? 0;
            bool final = i == chunks.Count - 1;
            if (final ? length < TapeAead.TagSize || length >= ChunkCipherSize : length != ChunkCipherSize)
                throw Structure(final
                    ? $"final sealed chunk {i} has {length} bytes — it must hold a tag and be shorter than a full chunk"
                    : $"sealed chunk {i} has {length} bytes — a middle chunk is exactly {ChunkCipherSize}");
        }
        return new([.. noncePrefix], [.. chunks]);
    }

    /// <summary>
    /// Seals <paramref name="content"/> for the set <paramref name="setId"/> on <paramref name="volume"/> under the chain
    ///  key <paramref name="key"/>, with a fresh nonce prefix (E3).
    /// </summary>
    public static TapeSealedPayload Seal(ReadOnlySpan<byte> content, TapeSetKey key, Guid setId, int volume)
    {
        ArgumentNullException.ThrowIfNull(key);

        int lengthBytes = TapePrimitives.VarUIntSize((ulong)content.Length);
        long framed = lengthBytes + (long)content.Length;
        long padded = Math.Max(PadUnit, (framed + PadUnit - 1) / PadUnit * PadUnit);

        var plain = new byte[checked((int)padded)];
        var prefix = new byte[TapeAead.PrefixSize];
        Span<byte> metaKey = stackalloc byte[TapeSetKey.Size];
        Span<byte> aad = stackalloc byte[AadSize];
        try
        {
            int n = TapePrimitives.WriteVarUInt(plain, (ulong)content.Length);
            content.CopyTo(plain.AsSpan(n));                // the rest stays zero: the padding
            TapeCrypto.Fill(prefix);
            key.DeriveMetaKey(setId, metaKey);
            BuildAad(setId, volume, aad);

            using var cipher = new MemoryStream(checked((int)TapeAead.CiphertextLength(plain.Length, TapeAead.MetaChunkSize)));
            var encrypting = new EncryptingStream(cipher, metaKey, prefix, TapeAead.MetaChunkSize, aad);
            try
            {
                encrypting.Write(plain);
                encrypting.Complete();
            }
            catch
            {
                encrypting.Abandon();
                throw;
            }
            return new(prefix, Split(cipher.GetBuffer(), (int)cipher.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(metaKey);
        }
    }

    #endregion

    #region *** Opening ***

    /// <summary>
    /// Authenticates and decrypts the seal with <paramref name="key"/> and returns the content (the caller zeroes it), or
    ///  <see langword="null"/> when the key does not fit, the seal is bound to another set or volume, or the plaintext
    ///  layout is broken. Never throws for any of these.
    /// </summary>
    public byte[]? TryOpen(TapeSetKey key, Guid setId, int volume)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.IsDisposed || setId == Guid.Empty)
            return null;

        var cipher = new byte[CiphertextLength];
        int pos = 0;
        foreach (byte[] chunk in m_chunks)
        {
            chunk.CopyTo(cipher, pos);
            pos += chunk.Length;
        }

        var plain = new byte[PlaintextLength];
        Span<byte> metaKey = stackalloc byte[TapeSetKey.Size];
        Span<byte> aad = stackalloc byte[AadSize];
        try
        {
            key.DeriveMetaKey(setId, metaKey);
            BuildAad(setId, volume, aad);
            using var decrypting = new DecryptingStream(new MemoryStream(cipher, writable: false),
                metaKey, NoncePrefix, TapeAead.MetaChunkSize, aad);
            decrypting.ReadExactly(plain);
            decrypting.VerifyToEnd();
        }
        catch (Exception ex) when (ex is TapeIOException or EndOfStreamException)
        {
            CryptographicOperations.ZeroMemory(plain);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metaKey);
        }

        try
        {
            return Unframe(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    #endregion

    #region *** Helpers ***

    /// <summary>The content of an authenticated plaintext — or null when its framing or padding is broken.</summary>
    private static byte[]? Unframe(byte[] plain)
    {
        if (plain.Length < PadUnit || plain.Length % PadUnit != 0)
            return null;

        ulong length;
        int consumed;
        try
        {
            length = TapePrimitives.ReadVarUInt(plain, out consumed);
        }
        catch (TapeFormatException)
        {
            return null;
        }
        if (length > (ulong)(plain.Length - consumed))
            return null;

        int end = consumed + (int)length;
        if (plain.AsSpan(end).ContainsAnyExcept((byte)0))
            return null;                                    // padding must be zero
        return plain[consumed..end];
    }

    private static byte[][] Split(byte[] buffer, int length)
    {
        // The construction always ends with a final chunk shorter than a full one (at least its tag), so the
        //  remainder after the full chunks is never empty.
        int full = length / ChunkCipherSize;
        var chunks = new byte[full + 1][];
        for (int i = 0; i < full; i++)
            chunks[i] = buffer[(i * ChunkCipherSize)..((i + 1) * ChunkCipherSize)];
        chunks[full] = buffer[(full * ChunkCipherSize)..length];
        return chunks;
    }

    private static void BuildAad(Guid setId, int volume, Span<byte> aad)
    {
        setId.TryWriteBytes(aad);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(aad[16..], (uint)volume);
    }

    private static TapeFormatException Structure(string message)
        => new(FormatErrorKind.CrossCheck, message) { Record = TapeRecordKind.TocSealedChunk };

    #endregion
}
