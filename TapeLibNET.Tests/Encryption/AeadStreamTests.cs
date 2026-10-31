using TapeLibNET.Encryption;

namespace TapeLibNET.Tests.Encryption;

/// <summary>
/// Design-Encryption §15.1: the chunked AES-256-GCM construction (§4.3) — round trips, every refusal, short reads,
///  abandonment, and <see cref="DecryptingStream.VerifyToEnd"/>.
/// </summary>
/// <remarks>
/// Most cases use a tiny chunk (16 bytes) so a few hundred bytes span many chunks; the construction is size-agnostic.
///  Two cases run the production chunk sizes.
/// </remarks>
public class AeadStreamTests
{
    #region *** Helpers ***

    private const int Chunk = 16;

    private static byte[] Bytes(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static readonly byte[] s_key = Bytes(TapeAead.KeySize, 42);
    private static readonly byte[] s_prefix = Bytes(TapeAead.PrefixSize, 43);

    private static byte[] Encrypt(byte[] plain, int chunk = Chunk, byte[]? key = null, byte[]? prefix = null,
        byte[]? aad = null, int slice = int.MaxValue)
    {
        using var ms = new MemoryStream();
        var enc = new EncryptingStream(ms, key ?? s_key, prefix ?? s_prefix, chunk, aad);
        for (int pos = 0; pos < plain.Length; pos += slice)
            enc.Write(plain, pos, Math.Min(slice, plain.Length - pos));
        enc.Complete();
        return ms.ToArray();
    }

    private static byte[] Decrypt(byte[] cipher, int chunk = Chunk, byte[]? key = null, byte[]? prefix = null,
        byte[]? aad = null, Func<Stream, Stream>? wrap = null)
    {
        Stream inner = new MemoryStream(cipher);
        if (wrap is not null)
            inner = wrap(inner);
        using var dec = new DecryptingStream(inner, key ?? s_key, prefix ?? s_prefix, chunk, aad);
        using var output = new MemoryStream();
        dec.CopyTo(output);
        Assert.True(dec.IsAtVerifiedEnd);
        return output.ToArray();
    }

    private static void AssertRefused(Action act)
    {
        var ex = Assert.Throws<TapeIOException>(act);
        Assert.True(ex.IsInvalidData, ex.ToLogString());
    }

    /// <summary>Delivers at most one byte per Read — the worst a packer façade may do.</summary>
    private sealed class TrickleStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion

    #region *** Round trips ***

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    [InlineData(1000)]
    public void RoundTrip_AndCiphertextLengthIsExact(int length)
    {
        byte[] plain = Bytes(length);
        byte[] cipher = Encrypt(plain);

        Assert.Equal(TapeAead.CiphertextLength(length, Chunk), cipher.LongLength);
        Assert.Equal(plain, Decrypt(cipher));
    }

    [Fact]
    public void ExactMultiple_EndsWithAnEmptyFinalChunk()
    {
        byte[] cipher = Encrypt(Bytes(2 * Chunk));
        // 2 full chunks (16 + 16 tag each) and an empty final chunk (tag only).
        Assert.Equal(2 * (Chunk + TapeAead.TagSize) + TapeAead.TagSize, cipher.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 3)]
    public void WriteSlicing_DoesNotChangeTheBytes(int slice)
    {
        byte[] plain = Bytes(500);
        Assert.Equal(Encrypt(plain), Encrypt(plain, slice: slice));
    }

    [Fact]
    public void ShortReads_OneByteAtATime_RoundTrip()
    {
        byte[] plain = Bytes(333);
        Assert.Equal(plain, Decrypt(Encrypt(plain), wrap: s => new TrickleStream(s)));
    }

    [Fact]
    public void ReadByte_AcrossChunkBoundaries()
    {
        byte[] plain = Bytes(5 * Chunk + 3);
        using var dec = new DecryptingStream(new MemoryStream(Encrypt(plain)), s_key, s_prefix, Chunk);
        var back = new List<byte>();
        int b;
        while ((b = dec.ReadByte()) >= 0)
            back.Add((byte)b);
        Assert.Equal(plain, back);
        Assert.True(dec.IsAtVerifiedEnd);
    }

    [Theory]
    [InlineData(TapeAead.FileChunkSize, 200 * 1024)]
    [InlineData(TapeAead.MetaChunkSize, 3 * 1024 * 1024 + 5)]
    public void ProductionChunkSizes_RoundTrip(int chunk, int length)
    {
        byte[] plain = Bytes(length);
        Assert.Equal(plain, Decrypt(Encrypt(plain, chunk), chunk));
    }

    [Fact]
    public void AssociatedData_RoundTrips()
    {
        byte[] aad = Bytes(20, 7);
        byte[] plain = Bytes(100);
        Assert.Equal(plain, Decrypt(Encrypt(plain, aad: aad), aad: aad));
    }

    #endregion

    #region *** Refusals ***

    [Theory]
    [InlineData(0)]                                 // first byte of the first chunk's data
    [InlineData(Chunk)]                             // first byte of the first chunk's tag
    [InlineData(Chunk + TapeAead.TagSize + 5)]      // inside the second chunk
    [InlineData(-1)]                                // last byte: the final chunk's tag
    public void BitFlip_AnywhereIsRefused(int at)
    {
        byte[] cipher = Encrypt(Bytes(3 * Chunk + 5));
        cipher[at < 0 ? cipher.Length + at : at] ^= 0x01;
        AssertRefused(() => Decrypt(cipher));
    }

    [Fact]
    public void SwappedChunks_Refused()
    {
        byte[] cipher = Encrypt(Bytes(3 * Chunk + 5));
        int c = Chunk + TapeAead.TagSize;
        byte[] swapped = [.. cipher[c..(2 * c)], .. cipher[..c], .. cipher[(2 * c)..]];
        AssertRefused(() => Decrypt(swapped));
    }

    [Fact]
    public void FinalChunkMissing_Refused()
    {
        byte[] cipher = Encrypt(Bytes(2 * Chunk));
        AssertRefused(() => Decrypt(cipher[..^TapeAead.TagSize]));    // drop the empty final chunk
    }

    [Fact]
    public void TruncatedInsideTheFinalChunk_Refused()
    {
        byte[] cipher = Encrypt(Bytes(2 * Chunk + 5));
        AssertRefused(() => Decrypt(cipher[..^1]));
    }

    [Fact]
    public void TruncatedInsideAMiddleChunk_Refused()
    {
        byte[] cipher = Encrypt(Bytes(3 * Chunk));
        AssertRefused(() => Decrypt(cipher[..(Chunk + TapeAead.TagSize + 3)]));
    }

    [Fact]
    public void Empty_Refused()
        => AssertRefused(() => Decrypt([]));

    [Fact]
    public void TrailingBytes_Refused()
    {
        byte[] cipher = Encrypt(Bytes(40));
        AssertRefused(() => Decrypt([.. cipher, 0x00]));
    }

    [Fact]
    public void WrongKey_WrongPrefix_WrongAad_WrongChunkSize_Refused()
    {
        byte[] plain = Bytes(50);
        byte[] cipher = Encrypt(plain, aad: [1, 2, 3]);

        AssertRefused(() => Decrypt(cipher, key: Bytes(TapeAead.KeySize, 99), aad: [1, 2, 3]));
        AssertRefused(() => Decrypt(cipher, prefix: new byte[TapeAead.PrefixSize], aad: [1, 2, 3]));
        AssertRefused(() => Decrypt(cipher, aad: [1, 2, 4]));
        AssertRefused(() => Decrypt(cipher));
        AssertRefused(() => Decrypt(cipher, chunk: Chunk * 2, aad: [1, 2, 3]));
    }

    [Fact]
    public void AfterAFailure_TheStreamRefusesFurtherReads()
    {
        byte[] cipher = Encrypt(Bytes(3 * Chunk));
        cipher[2] ^= 0x01;
        using var dec = new DecryptingStream(new MemoryStream(cipher), s_key, s_prefix, Chunk);
        AssertRefused(() => dec.ReadByte());
        Assert.False(dec.CanRead);
        AssertRefused(() => dec.ReadByte());
    }

    #endregion

    #region *** Ending a record ***

    [Fact]
    public void Abandon_WritesNoFinalChunk_SoNoReaderAcceptsIt()
    {
        using var ms = new MemoryStream();
        var enc = new EncryptingStream(ms, s_key, s_prefix, Chunk);
        enc.Write(Bytes(2 * Chunk + 3));
        enc.Abandon();

        Assert.Equal(2 * (Chunk + TapeAead.TagSize), ms.Length);   // the two full chunks went out; nothing else
        Assert.False(enc.IsCompleted);
        AssertRefused(() => Decrypt(ms.ToArray()));
    }

    [Fact]
    public void Abandon_BeforeAFullChunk_WritesNothing()
    {
        using var ms = new MemoryStream();
        var enc = new EncryptingStream(ms, s_key, s_prefix, Chunk);
        enc.Write(Bytes(5));
        enc.Abandon();
        Assert.Equal(0, ms.Length);
    }

    /// <summary>A wrapping codec stream may dispose us; that must neither end nor break the record.</summary>
    [Fact]
    public void Dispose_IsInertWhileOpen()
    {
        byte[] plain = Bytes(40);
        using var ms = new MemoryStream();
        var enc = new EncryptingStream(ms, s_key, s_prefix, Chunk);
        enc.Write(plain);
        long before = ms.Length;
        enc.Dispose();
        Assert.Equal(before, ms.Length);    // no final chunk written by Dispose

        enc.Complete();
        Assert.Equal(plain, Decrypt(ms.ToArray()));
    }

    [Fact]
    public void AfterComplete_WritesAndASecondCompleteAreRefused_AbandonIsANoOp()
    {
        using var ms = new MemoryStream();
        var enc = new EncryptingStream(ms, s_key, s_prefix, Chunk);
        enc.Write(Bytes(3));
        enc.Complete();
        long length = ms.Length;

        Assert.Throws<InvalidOperationException>(() => enc.Write(Bytes(1)));
        Assert.Throws<InvalidOperationException>(() => enc.Complete());
        enc.Abandon();
        Assert.True(enc.IsCompleted);
        Assert.Equal(length, ms.Length);
        Assert.Equal(length, enc.CiphertextLength);
    }

    [Fact]
    public void Flush_NeverEmitsAPartialChunk()
    {
        using var ms = new MemoryStream();
        var enc = new EncryptingStream(ms, s_key, s_prefix, Chunk);
        enc.Write(Bytes(5));
        enc.Flush();
        Assert.Equal(0, ms.Length);
        enc.Abandon();
    }

    #endregion

    #region *** VerifyToEnd ***

    [Fact]
    public void VerifyToEnd_AuthenticatesWhatTheConsumerLeftUnread()
    {
        byte[] plain = Bytes(5 * Chunk + 2);
        using var dec = new DecryptingStream(new MemoryStream(Encrypt(plain)), s_key, s_prefix, Chunk);
        var head = new byte[10];
        dec.ReadExactly(head);
        Assert.Equal(plain[..10], head);

        dec.VerifyToEnd();
        Assert.True(dec.IsAtVerifiedEnd);
        Assert.Equal(-1, dec.ReadByte());
    }

    [Fact]
    public void VerifyToEnd_CatchesACorruptLastChunk()
    {
        byte[] cipher = Encrypt(Bytes(5 * Chunk + 2));
        cipher[^1] ^= 0x01;
        using var dec = new DecryptingStream(new MemoryStream(cipher), s_key, s_prefix, Chunk);
        dec.ReadExactly(new byte[10]);
        AssertRefused(dec.VerifyToEnd);
    }

    [Fact]
    public void VerifyToEnd_CatchesAMissingFinalChunk()
    {
        byte[] cipher = Encrypt(Bytes(4 * Chunk));
        using var dec = new DecryptingStream(new MemoryStream(cipher[..^TapeAead.TagSize]), s_key, s_prefix, Chunk);
        dec.ReadExactly(new byte[10]);
        AssertRefused(dec.VerifyToEnd);
    }

    [Fact]
    public void VerifyToEnd_OnAFullyReadStream_IsANoOp()
    {
        byte[] plain = Bytes(40);
        using var dec = new DecryptingStream(new MemoryStream(Encrypt(plain)), s_key, s_prefix, Chunk);
        dec.CopyTo(Stream.Null);
        dec.VerifyToEnd();
        Assert.True(dec.IsAtVerifiedEnd);
    }

    #endregion

    #region *** Session ***

    [Fact]
    public void Session_ReusedSequentially_RoundTrips()
    {
        using var session = new TapeAeadSession(Chunk);
        for (int i = 0; i < 5; i++)
        {
            byte[] plain = Bytes(37 * i, i);
            using var ms = new MemoryStream();
            var enc = session.BeginEncrypt(ms, s_key, s_prefix);
            enc.Write(plain);
            enc.Complete();

            using var dec = session.BeginDecrypt(new MemoryStream(ms.ToArray()), s_key, s_prefix);
            using var back = new MemoryStream();
            dec.CopyTo(back);
            Assert.Equal(plain, back.ToArray());
        }
    }

    [Fact]
    public void Session_OneLiveStreamAtATime()
    {
        using var session = new TapeAeadSession(Chunk);
        var first = session.BeginEncrypt(Stream.Null, s_key, s_prefix);
        Assert.Throws<InvalidOperationException>(() => session.BeginEncrypt(Stream.Null, s_key, s_prefix));
        first.Abandon();
        session.BeginEncrypt(Stream.Null, s_key, s_prefix).Abandon();     // released → usable again
    }

    [Fact]
    public void Session_ChunkSizeMismatch_Refused()
    {
        using var session = new TapeAeadSession(Chunk);
        Assert.Throws<ArgumentException>(() => new EncryptingStream(Stream.Null, s_key, s_prefix, Chunk * 2, session: session));
    }

    #endregion

    #region *** Construction details ***

    [Fact]
    public void Nonce_IsPrefixIndexBigEndianAndFinalFlag()
    {
        Span<byte> nonce = stackalloc byte[TapeAead.NonceSize];
        TapeAead.BuildNonce([1, 2, 3, 4, 5, 6, 7], 0x01020304, final: true, nonce);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 0x01, 0x02, 0x03, 0x04, 1 }, nonce.ToArray());
    }

    [Theory]
    [InlineData(0L, TapeAead.FileChunkSize, 16L)]
    [InlineData(1L, TapeAead.FileChunkSize, 16L)]
    [InlineData(TapeAead.FileChunkSize, TapeAead.FileChunkSize, 32L)]
    [InlineData(10L * TapeAead.FileChunkSize + 1, TapeAead.FileChunkSize, 176L)]
    public void Overhead_IsOneTagPerChunkIncludingTheFinal(long plain, int chunk, long overhead)
        => Assert.Equal(overhead, TapeAead.Overhead(plain, chunk));

    [Fact]
    public void BadArguments_Refused()
    {
        Assert.Throws<ArgumentException>(() => new EncryptingStream(Stream.Null, new byte[16], s_prefix, Chunk));
        Assert.Throws<ArgumentException>(() => new EncryptingStream(Stream.Null, s_key, new byte[8], Chunk));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncryptingStream(Stream.Null, s_key, s_prefix, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncryptingStream(Stream.Null, s_key, s_prefix, TapeAead.MaxChunkSize + 1));
    }

    #endregion
}