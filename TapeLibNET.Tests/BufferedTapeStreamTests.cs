namespace TapeLibNET.Tests;

/// <summary>
/// Unit tests for <see cref="BufferedTapeWriteStream"/> and <see cref="BufferedTapeReadStream"/>.
/// These are double-buffering wrappers over any <see cref="Stream"/> — they don't depend on tape at all.
/// We test them against <see cref="MemoryStream"/> to isolate the double-buffering logic.
/// </summary>
public class BufferedTapeStreamTests
{
    // Small block/buffer sizes so tests exercise SubmitAndSwap / SwapBuffers boundaries quickly.
    // bufferSize = blockSize * bufferMultiplier = 64 * 2 = 128 bytes per buffer.
    private const uint BlockSize = 64;
    private const int BufferMultiplier = 2;
    private const int BufferSize = (int)BlockSize * BufferMultiplier; // 128

    /// <summary>
    /// Produces a deterministic test pattern: byte[i] = (byte)(i % 251) using a prime
    /// to avoid aliasing with power-of-two buffer sizes.
    /// </summary>
    private static byte[] MakePattern(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(i % 251);
        return data;
    }


    #region *** BufferedTapeWriteStream — Constructor Validation ***

    [Fact]
    public void Write_Constructor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BufferedTapeWriteStream(null!, BlockSize, BufferMultiplier));
    }

    [Fact]
    public void Write_Constructor_ZeroBlockSize_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BufferedTapeWriteStream(ms, 0, BufferMultiplier));
    }

    [Fact]
    public void Write_Constructor_BufferMultiplierLessThan2_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BufferedTapeWriteStream(ms, BlockSize, 1));
    }

    [Fact]
    public void Write_Constructor_ValidArgs_Succeeds()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        Assert.True(bws.CanWrite);
        Assert.False(bws.CanRead);
        Assert.False(bws.CanSeek);
        Assert.Equal(0L, bws.Length);
        Assert.Equal(0L, bws.Position);
    }

    #endregion


    #region *** BufferedTapeWriteStream — Unsupported Operations ***

    [Fact]
    public void Write_Read_Throws()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => bws.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void Write_Seek_Throws()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => bws.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void Write_SetLength_Throws()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => bws.SetLength(100));
    }

    [Fact]
    public void Write_PositionSet_Throws()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => bws.Position = 10);
    }

    #endregion


    #region *** BufferedTapeWriteStream — Write + Flush Round-Trips ***

    [Fact]
    public void Write_ZeroLength_NoDataWritten()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(new byte[10], 0, 0);
        bws.Flush();

        Assert.Equal(0, ms.Length);
        Assert.Equal(0L, bws.Length);
    }

    [Fact]
    public void Write_SubBuffer_FlushRoundTrips()
    {
        // Write less than one buffer → data stays in fill buffer until Flush
        var pattern = MakePattern(50);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(pattern, 0, pattern.Length);
        Assert.Equal(50L, bws.Length);

        // Inner stream may not have data yet (still in fill buffer)
        bws.Flush();

        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_ExactBufferSize_TriggersSubmitAndSwap()
    {
        // Writing exactly BufferSize (128) bytes fills the buffer completely and triggers SubmitAndSwap
        var pattern = MakePattern(BufferSize);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(pattern, 0, pattern.Length);
        // SubmitAndSwap should have been triggered; data is being written in the background
        bws.Flush();

        Assert.Equal(BufferSize, (int)bws.Length);
        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_MultiBuffer_RoundTrips()
    {
        // Write 3× buffer size → triggers SubmitAndSwap multiple times
        int totalSize = BufferSize * 3;
        var pattern = MakePattern(totalSize);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(pattern, 0, pattern.Length);
        bws.Flush();

        Assert.Equal(totalSize, (int)bws.Length);
        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_NonAligned_RoundTrips()
    {
        // Non-aligned: 300 bytes = 2 × 128 + 44 remainder
        var pattern = MakePattern(300);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(pattern, 0, pattern.Length);
        bws.Flush();

        Assert.Equal(300L, bws.Length);
        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_SingleByte_RoundTrips()
    {
        // Minimal write: 1 byte
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write([42], 0, 1);
        bws.Flush();

        Assert.Equal(1L, bws.Length);
        Assert.Equal("*"u8.ToArray(), ms.ToArray());
    }

    [Fact]
    public void Write_ManySmallChunks_RoundTrips()
    {
        // Write 1000 bytes, 7 bytes at a time → forces many partial fills and several SubmitAndSwap calls
        int total = 1000;
        int chunkSize = 7;
        var pattern = MakePattern(total);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        int offset = 0;
        while (offset < total)
        {
            int count = Math.Min(chunkSize, total - offset);
            bws.Write(pattern, offset, count);
            offset += count;
        }
        bws.Flush();

        Assert.Equal(total, (int)bws.Length);
        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_LargerThanBuffer_SingleCall_RoundTrips()
    {
        // Single Write call with count > buffer size → multiple SubmitAndSwap calls within one Write
        int size = BufferSize * 5 + 37; // 677 bytes
        var pattern = MakePattern(size);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(pattern, 0, pattern.Length);
        bws.Flush();

        Assert.Equal(size, (int)bws.Length);
        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_WithOffset_CopiesCorrectSlice()
    {
        // Write from a non-zero offset in the source buffer
        var source = MakePattern(200);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(source, 50, 100); // bytes 50..149
        bws.Flush();

        Assert.Equal(100L, bws.Length);
        Assert.Equal(source[50..150], ms.ToArray());
    }

    #endregion


    #region *** BufferedTapeWriteStream — Flush Behavior ***

    [Fact]
    public void Write_Flush_AfterSubBufferWrite_SendsDataToInner()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(MakePattern(10), 0, 10);
        // Before flush, inner might be empty
        bws.Flush();

        Assert.Equal(10, ms.Length);
    }

    [Fact]
    public void Write_Flush_WhenEmpty_NoOp()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Flush(); // nothing written, should be a no-op

        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void Write_DoubleFlush_Idempotent()
    {
        var pattern = MakePattern(50);
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(pattern, 0, pattern.Length);
        bws.Flush();
        bws.Flush(); // second flush — should be a no-op

        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_FlushAfterDispose_Throws()
    {
        using var ms = new MemoryStream();
        var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        bws.Dispose();

        Assert.Throws<ObjectDisposedException>(() => bws.Flush());
    }

    #endregion


    #region *** BufferedTapeWriteStream — Length/Position Tracking ***

    [Fact]
    public void Write_Length_TracksAccumulatedBytes()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        Assert.Equal(0L, bws.Length);

        bws.Write(MakePattern(50), 0, 50);
        Assert.Equal(50L, bws.Length);

        bws.Write(MakePattern(80), 0, 80);
        Assert.Equal(130L, bws.Length);

        bws.Write(MakePattern(BufferSize), 0, BufferSize);
        Assert.Equal(130L + BufferSize, bws.Length);
    }

    [Fact]
    public void Write_Position_EqualsLength()
    {
        using var ms = new MemoryStream();
        using var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);

        bws.Write(MakePattern(75), 0, 75);
        Assert.Equal(bws.Length, bws.Position);
    }

    #endregion


    #region *** BufferedTapeWriteStream — Dispose ***

    [Fact]
    public void Write_Dispose_FlushesRemainingData()
    {
        var pattern = MakePattern(200);
        using var ms = new MemoryStream();

        // Dispose the write stream — it should flush any pending data
        var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        bws.Write(pattern, 0, pattern.Length);
        bws.Dispose();

        Assert.Equal(pattern, ms.ToArray());
    }

    [Fact]
    public void Write_DoubleDispose_NoThrow()
    {
        using var ms = new MemoryStream();
        var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        bws.Write(MakePattern(10), 0, 10);
        bws.Dispose();
        bws.Dispose(); // should not throw
    }

    [Fact]
    public void Write_CanWrite_FalseAfterDispose()
    {
        using var ms = new MemoryStream();
        var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        Assert.True(bws.CanWrite);

        bws.Dispose();
        Assert.False(bws.CanWrite);
    }

    [Fact]
    public void Write_AfterDispose_Throws()
    {
        using var ms = new MemoryStream();
        var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier);
        bws.Dispose();

        Assert.Throws<ObjectDisposedException>(() => bws.Write(new byte[1], 0, 1));
    }

    #endregion


    #region *** BufferedTapeWriteStream — Fault Propagation ***

    [Fact]
    public void Write_InnerThrows_SetsFaulted_SubsequentWriteThrows()
    {
        // The faulting stream will throw after the first N bytes
        using var faulter = new FaultingStream(failAfterBytes: BufferSize / 2);
        var bws = new BufferedTapeWriteStream(faulter, BlockSize, BufferMultiplier);

        // First write fills the buffer and triggers SubmitAndSwap → background write to faulting stream
        // The faulting stream will throw during the background write or on the next WaitForPendingWrite
        var pattern = MakePattern(BufferSize * 3);

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            bws.Write(pattern, 0, pattern.Length);
            bws.Flush();
        });

        // Stream should now be faulted
        Assert.ThrowsAny<InvalidOperationException>(() => bws.Write(new byte[1], 0, 1));

        // Dispose should not throw even when faulted
        bws.Dispose();
    }

    [Fact]
    public void Write_Faulted_Dispose_DoesNotThrow()
    {
        using var faulter = new FaultingStream(failAfterBytes: 0); // fails immediately
        var bws = new BufferedTapeWriteStream(faulter, BlockSize, BufferMultiplier);

        // Write exactly buffer size to trigger SubmitAndSwap → background write fails
        try
        {
            bws.Write(MakePattern(BufferSize * 2), 0, BufferSize * 2);
            bws.Flush();
        }
        catch { /* expected */ }

        // Dispose should handle faulted state gracefully
        bws.Dispose();
        bws.Dispose(); // double dispose also safe
    }

    #endregion


    #region *** BufferedTapeReadStream — Constructor Validation ***

    [Fact]
    public void Read_Constructor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BufferedTapeReadStream(null!, BlockSize, BufferMultiplier));
    }

    [Fact]
    public void Read_Constructor_ZeroBlockSize_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BufferedTapeReadStream(ms, 0, BufferMultiplier));
    }

    [Fact]
    public void Read_Constructor_BufferMultiplierLessThan2_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BufferedTapeReadStream(ms, BlockSize, 1));
    }

    [Fact]
    public void Read_Constructor_ValidArgs_Succeeds()
    {
        using var ms = new MemoryStream(MakePattern(10));
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        Assert.True(brs.CanRead);
        Assert.False(brs.CanWrite);
        Assert.False(brs.CanSeek);
        Assert.Equal(0L, brs.Length);
        Assert.Equal(0L, brs.Position);
    }

    #endregion


    #region *** BufferedTapeReadStream — Unsupported Operations ***

    [Fact]
    public void Read_Write_Throws()
    {
        using var ms = new MemoryStream(new byte[10]);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => brs.Write(new byte[1], 0, 1));
    }

    [Fact]
    public void Read_Seek_Throws()
    {
        using var ms = new MemoryStream(new byte[10]);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => brs.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void Read_SetLength_Throws()
    {
        using var ms = new MemoryStream(new byte[10]);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => brs.SetLength(100));
    }

    [Fact]
    public void Read_PositionSet_Throws()
    {
        using var ms = new MemoryStream(new byte[10]);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        Assert.Throws<NotSupportedException>(() => brs.Position = 10);
    }

    #endregion


    #region *** BufferedTapeReadStream — Read Round-Trips ***

    [Fact]
    public void Read_ZeroLength_ReturnsZero()
    {
        using var ms = new MemoryStream(MakePattern(100));
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        int read = brs.Read(new byte[10], 0, 0);
        Assert.Equal(0, read);
        Assert.Equal(0L, brs.Length);
    }

    [Fact]
    public void Read_EmptyStream_ReturnsZero()
    {
        using var ms = new MemoryStream();
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] buf = new byte[64];
        int read = brs.Read(buf, 0, buf.Length);
        Assert.Equal(0, read);
    }

    [Fact]
    public void Read_SubBuffer_RoundTrips()
    {
        // Source smaller than one buffer
        var pattern = MakePattern(50);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[50];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(50, totalRead);
        Assert.Equal(pattern, result);
    }

    [Fact]
    public void Read_ExactBufferSize_RoundTrips()
    {
        var pattern = MakePattern(BufferSize);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[BufferSize];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(BufferSize, totalRead);
        Assert.Equal(pattern, result);
    }

    [Fact]
    public void Read_MultiBuffer_RoundTrips()
    {
        int totalSize = BufferSize * 3;
        var pattern = MakePattern(totalSize);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[totalSize];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(totalSize, totalRead);
        Assert.Equal(pattern, result);
    }

    [Fact]
    public void Read_NonAligned_RoundTrips()
    {
        // 300 bytes = 2 × 128 + 44 remainder
        var pattern = MakePattern(300);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[300];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(300, totalRead);
        Assert.Equal(pattern, result);
    }

    [Fact]
    public void Read_SingleByte_RoundTrips()
    {
        using var ms = new MemoryStream([42]);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[1];
        int read = brs.Read(result, 0, 1);

        Assert.Equal(1, read);
        Assert.Equal(42, result[0]);
    }

    [Fact]
    public void Read_InSmallChunks_RoundTrips()
    {
        // Read 500 bytes, 7 bytes at a time → forces many partial reads and buffer swaps
        int total = 500;
        int chunkSize = 7;
        var pattern = MakePattern(total);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        var result = new MemoryStream();
        byte[] chunk = new byte[chunkSize];
        int read;
        while ((read = brs.Read(chunk, 0, chunkSize)) > 0)
            result.Write(chunk, 0, read);

        Assert.Equal(pattern, result.ToArray());
    }

    [Fact]
    public void Read_LargerThanSource_ReturnsAvailableData()
    {
        // Source is 50 bytes, but we request 200 bytes
        var pattern = MakePattern(50);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[200];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(50, totalRead);
        Assert.Equal(pattern, result[..50]);
    }

    [Fact]
    public void Read_WithOffset_WritesToCorrectPosition()
    {
        var pattern = MakePattern(30);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[50];
        int read = brs.Read(result, 10, 30);

        Assert.Equal(30, read);
        Assert.Equal(pattern, result[10..40]);
        // Bytes before offset should be untouched
        Assert.Equal(new byte[10], result[..10]);
    }

    [Fact]
    public void Read_PastEOF_ReturnsZero()
    {
        var pattern = MakePattern(10);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] buf = new byte[10];
        int read1 = ReadFully(brs, buf);
        Assert.Equal(10, read1);

        // Second read past EOF should return 0
        int read2 = brs.Read(buf, 0, 10);
        Assert.Equal(0, read2);
    }

    #endregion


    #region *** BufferedTapeReadStream — Length/Position Tracking ***

    [Fact]
    public void Read_Length_TracksConsumedBytes()
    {
        var pattern = MakePattern(200);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        Assert.Equal(0L, brs.Length);

        byte[] buf = new byte[50];
        brs.Read(buf, 0, 50);
        Assert.Equal(50L, brs.Length);

        brs.Read(buf, 0, 50);
        Assert.Equal(100L, brs.Length);
    }

    [Fact]
    public void Read_Position_EqualsLength()
    {
        var pattern = MakePattern(200);
        using var ms = new MemoryStream(pattern);
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] buf = new byte[75];
        brs.Read(buf, 0, 75);
        Assert.Equal(brs.Length, brs.Position);
    }

    #endregion


    #region *** BufferedTapeReadStream — Dispose ***

    [Fact]
    public void Read_Dispose_Succeeds()
    {
        var pattern = MakePattern(200);
        using var ms = new MemoryStream(pattern);

        var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        // Read some data to trigger background pre-fetch
        byte[] buf = new byte[50];
        brs.Read(buf, 0, 50);

        // Dispose should wait for any pending background read and recycle buffers
        brs.Dispose();
    }

    [Fact]
    public void Read_DoubleDispose_NoThrow()
    {
        using var ms = new MemoryStream(MakePattern(10));
        var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        brs.Dispose();
        brs.Dispose(); // should not throw
    }

    [Fact]
    public void Read_CanRead_FalseAfterDispose()
    {
        using var ms = new MemoryStream(new byte[10]);
        var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        Assert.True(brs.CanRead);

        brs.Dispose();
        Assert.False(brs.CanRead);
    }

    [Fact]
    public void Read_AfterDispose_Throws()
    {
        using var ms = new MemoryStream(new byte[10]);
        var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        brs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => brs.Read(new byte[1], 0, 1));
    }

    #endregion


    #region *** BufferedTapeReadStream — Fault Propagation ***

    [Fact]
    public void Read_InnerThrows_SetsFaulted_SubsequentReadThrows()
    {
        // The faulting stream will throw after delivering some bytes
        using var faulter = new FaultingStream(failAfterBytes: BufferSize / 2, isReadStream: true);
        var brs = new BufferedTapeReadStream(faulter, BlockSize, BufferMultiplier);

        byte[] buf = new byte[BufferSize * 3];

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            ReadFully(brs, buf);
        });

        // Stream should now be faulted
        Assert.ThrowsAny<InvalidOperationException>(() => brs.Read(new byte[1], 0, 1));

        // Dispose should not throw even when faulted
        brs.Dispose();
    }

    [Fact]
    public void Read_Faulted_Dispose_DoesNotThrow()
    {
        using var faulter = new FaultingStream(failAfterBytes: 0, isReadStream: true);
        var brs = new BufferedTapeReadStream(faulter, BlockSize, BufferMultiplier);

        try { brs.Read(new byte[BufferSize], 0, BufferSize); }
        catch { /* expected */ }

        brs.Dispose();
        brs.Dispose(); // double dispose also safe
    }

    #endregion


    #region *** Write → Read Integration Round-Trip ***

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(128)]    // = BufferSize
    [InlineData(129)]    // BufferSize + 1
    [InlineData(256)]    // 2 × BufferSize
    [InlineData(300)]    // non-aligned
    [InlineData(1000)]   // many swaps
    [InlineData(4096)]   // stress
    public void WriteRead_RoundTrip_VariousSizes(int dataSize)
    {
        var pattern = MakePattern(dataSize);

        // Write through BufferedTapeWriteStream → MemoryStream
        using var ms = new MemoryStream();
        using (var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier))
        {
            if (dataSize > 0)
                bws.Write(pattern, 0, pattern.Length);
            // Dispose flushes
        }

        // Read back through BufferedTapeReadStream
        ms.Position = 0;
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        byte[] result = new byte[dataSize];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(dataSize, totalRead);
        Assert.Equal(pattern, result);
    }

    [Theory]
    [InlineData(7)]      // small chunks
    [InlineData(64)]     // = BlockSize
    [InlineData(128)]    // = BufferSize
    [InlineData(200)]    // larger than buffer
    public void WriteRead_RoundTrip_VaryingChunkSizes(int writeChunkSize)
    {
        int total = 2000;
        var pattern = MakePattern(total);

        // Write in chunks of writeChunkSize
        using var ms = new MemoryStream();
        using (var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier))
        {
            int offset = 0;
            while (offset < total)
            {
                int count = Math.Min(writeChunkSize, total - offset);
                bws.Write(pattern, offset, count);
                offset += count;
            }
        }

        // Read back in chunks of 13 (prime, non-aligned)
        ms.Position = 0;
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        var result = new MemoryStream();
        byte[] chunk = new byte[13];
        int read;
        while ((read = brs.Read(chunk, 0, chunk.Length)) > 0)
            result.Write(chunk, 0, read);

        Assert.Equal(pattern, result.ToArray());
    }

    #endregion


    #region *** Stress Tests ***

    [Fact]
    public void WriteRead_Stress_RandomSizedWrites()
    {
        // Write data in random-sized chunks, then read back in random-sized chunks
        var rng = new Random(42);
        int total = 10_000;
        var pattern = MakePattern(total);

        // Write phase
        using var ms = new MemoryStream();
        using (var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier))
        {
            int offset = 0;
            while (offset < total)
            {
                int count = Math.Min(rng.Next(1, BufferSize * 2), total - offset);
                bws.Write(pattern, offset, count);
                offset += count;
            }
        }

        // Read phase
        ms.Position = 0;
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);

        var result = new MemoryStream();
        while (true)
        {
            int chunkSize = rng.Next(1, BufferSize * 2);
            byte[] chunk = new byte[chunkSize];
            int read = brs.Read(chunk, 0, chunkSize);
            if (read == 0) break;
            result.Write(chunk, 0, read);
        }

        Assert.Equal(pattern, result.ToArray());
    }

    [Fact]
    public void WriteRead_Stress_LargerBufferMultiplier()
    {
        // Test with a larger buffer multiplier to exercise different buffer sizes
        uint blockSize = 512;
        int multiplier = 8;
        int total = 50_000;
        var pattern = MakePattern(total);

        using var ms = new MemoryStream();
        using (var bws = new BufferedTapeWriteStream(ms, blockSize, multiplier))
        {
            bws.Write(pattern, 0, pattern.Length);
        }

        ms.Position = 0;
        using var brs = new BufferedTapeReadStream(ms, blockSize, multiplier);

        byte[] result = new byte[total];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(total, totalRead);
        Assert.Equal(pattern, result);
    }

    [Fact]
    public void WriteRead_Stress_AlternatingSmallAndLargeWrites()
    {
        // Alternate between very small writes (1-3 bytes) and large writes (> buffer size)
        // to stress the SubmitAndSwap path with varying fill levels
        int total = 5_000;
        var pattern = MakePattern(total);

        using var ms = new MemoryStream();
        using (var bws = new BufferedTapeWriteStream(ms, BlockSize, BufferMultiplier))
        {
            int offset = 0;
            bool small = true;
            while (offset < total)
            {
                int count = small
                    ? Math.Min(3, total - offset)
                    : Math.Min(BufferSize + 50, total - offset);
                bws.Write(pattern, offset, count);
                offset += count;
                small = !small;
            }
        }

        ms.Position = 0;
        using var brs = new BufferedTapeReadStream(ms, BlockSize, BufferMultiplier);
        byte[] result = new byte[total];
        int totalRead = ReadFully(brs, result);

        Assert.Equal(total, totalRead);
        Assert.Equal(pattern, result);
    }

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Reads from the stream until the buffer is full or EOF is reached.
    /// Returns the total number of bytes read.
    /// </summary>
    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// A stream that throws <see cref="IOException"/> after a certain number of bytes have been
    /// written to (or read from) it. Used to test fault propagation in the buffered streams.
    /// </summary>
    private sealed class FaultingStream(int failAfterBytes, bool isReadStream = false) : Stream
    {
        private readonly MemoryStream _backing = new(MakePattern(failAfterBytes + 4096));
        private int _bytesProcessed;

        public override bool CanRead => isReadStream;
        public override bool CanWrite => !isReadStream;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (isReadStream) throw new NotSupportedException();
            _bytesProcessed += count;
            if (_bytesProcessed > failAfterBytes)
                throw new IOException("Simulated write failure");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!isReadStream) throw new NotSupportedException();
            if (_bytesProcessed >= failAfterBytes)
                throw new IOException("Simulated read failure");

            int toRead = Math.Min(count, failAfterBytes - _bytesProcessed);
            if (toRead <= 0)
                throw new IOException("Simulated read failure");

            int read = _backing.Read(buffer, offset, toRead);
            _bytesProcessed += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _backing.Dispose();
            base.Dispose(disposing);
        }
    }

    #endregion
}
