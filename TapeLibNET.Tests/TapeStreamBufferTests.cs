namespace TapeLibNET.Tests;

/// <summary>
/// Unit tests for the buffer infrastructure: <see cref="TapeByteBuffer"/>
/// (backed by <see cref="System.Buffers.ArrayPool{T}"/>).
/// These are pure in-memory classes with no tape dependencies.
/// </summary>
public class TapeStreamBufferTests
{
    #region *** TapeByteBuffer — Basic Properties ***

    [Fact]
    public void Buffer_NewBuffer_IsEmpty()
    {
        using var buf = new TapeByteBuffer(256);

        Assert.Equal(256, buf.Capacity);
        Assert.Equal(0, buf.ContentSize);
        Assert.True(buf.IsEmpty);
        Assert.False(buf.IsNonEmpty);
        Assert.False(buf.IsFull);
        Assert.Equal(256, buf.Remaining);
    }

    [Fact]
    public void Buffer_Dispose_SetsFlag()
    {
        var buf = new TapeByteBuffer(64);
        Assert.False(buf.IsDisposed);

        buf.Dispose();
        Assert.True(buf.IsDisposed);
    }

    [Fact]
    public void Buffer_DoubleDispose_NoThrow()
    {
        var buf = new TapeByteBuffer(64);
        buf.Dispose();
        buf.Dispose(); // should not throw
    }

    #endregion

    #region *** TapeByteBuffer — FillFrom / SpillTo with byte[] ***

    [Fact]
    public void Buffer_FillAndSpill_RoundTrips()
    {
        using var buf = new TapeByteBuffer(128);
        byte[] src = [1, 2, 3, 4, 5];

        int filled = buf.FillFrom(src, 0, src.Length);
        Assert.Equal(5, filled);
        Assert.Equal(5, buf.ContentSize);

        byte[] dst = new byte[10];
        int spilled = buf.SpillTo(dst, 0, 10);
        Assert.Equal(5, spilled);
        Assert.Equal(src, dst[..5]);
        Assert.True(buf.IsEmpty);
    }

    [Fact]
    public void Buffer_FillExceedingCapacity_OnlyFillsRemaining()
    {
        using var buf = new TapeByteBuffer(4);
        byte[] src = [10, 20, 30, 40, 50, 60];

        int filled = buf.FillFrom(src, 0, src.Length);

        Assert.Equal(4, filled);
        Assert.True(buf.IsFull);
        Assert.Equal(0, buf.Remaining);
    }

    [Fact]
    public void Buffer_SpillPartial_LeavesRemainder()
    {
        using var buf = new TapeByteBuffer(64);
        byte[] src = [1, 2, 3, 4, 5, 6, 7, 8];
        buf.FillFrom(src, 0, src.Length);

        byte[] dst = new byte[3];
        int spilled = buf.SpillTo(dst, 0, 3);

        Assert.Equal(3, spilled);
        Assert.Equal(new byte[] { 1, 2, 3 }, dst);
        Assert.Equal(5, buf.ContentSize); // 8 - 3 = 5 remaining
    }

    [Fact]
    public void Buffer_FillSpillFillSpill_HandlesShift()
    {
        // Use a small buffer to force internal shifts
        using var buf = new TapeByteBuffer(8);

        // Fill 6 bytes
        byte[] src1 = [1, 2, 3, 4, 5, 6];
        buf.FillFrom(src1, 0, src1.Length);
        Assert.Equal(6, buf.ContentSize);

        // Spill 4 bytes — readFrom advances, leaving [5, 6] with a gap at the start
        byte[] dst1 = new byte[4];
        buf.SpillTo(dst1, 0, 4);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, dst1);
        Assert.Equal(2, buf.ContentSize);

        // Fill 5 more bytes — should trigger shift since remaining without shift < 5
        byte[] src2 = [7, 8, 9, 10, 11];
        int filled = buf.FillFrom(src2, 0, src2.Length);
        Assert.Equal(5, filled); // remaining = 8 - 2 = 6 >= 5
        Assert.Equal(7, buf.ContentSize);

        // Spill all 7 bytes
        byte[] dst2 = new byte[7];
        buf.SpillTo(dst2, 0, 7);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11 }, dst2);
        Assert.True(buf.IsEmpty);
    }

    [Fact]
    public void Buffer_Reset_ClearsContent()
    {
        using var buf = new TapeByteBuffer(64);
        buf.FillFrom([1, 2, 3], 0, 3);
        Assert.Equal(3, buf.ContentSize);

        buf.Reset();

        Assert.True(buf.IsEmpty);
        Assert.Equal(0, buf.ContentSize);
    }

    [Fact]
    public void Buffer_FillFromOffset_CopiesCorrectSlice()
    {
        using var buf = new TapeByteBuffer(64);
        byte[] src = [10, 20, 30, 40, 50];

        // Fill from offset 2, count 3 → should copy [30, 40, 50]
        int filled = buf.FillFrom(src, 2, 3);
        Assert.Equal(3, filled);

        byte[] dst = new byte[3];
        buf.SpillTo(dst, 0, 3);
        Assert.Equal(new byte[] { 30, 40, 50 }, dst);
    }

    [Fact]
    public void Buffer_SpillToOffset_WritesCorrectPosition()
    {
        using var buf = new TapeByteBuffer(64);
        buf.FillFrom([1, 2, 3], 0, 3);

        byte[] dst = new byte[10];
        int spilled = buf.SpillTo(dst, 5, 3);

        Assert.Equal(3, spilled);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 1, 2, 3, 0, 0 }, dst);
    }

    [Fact]
    public void Buffer_SpillZero_ReturnsZero()
    {
        using var buf = new TapeByteBuffer(64);
        buf.FillFrom([1, 2], 0, 2);

        byte[] dst = new byte[4];
        int spilled = buf.SpillTo(dst, 0, 0);

        Assert.Equal(0, spilled);
        Assert.Equal(2, buf.ContentSize);
    }

    [Fact]
    public void Buffer_FillZero_ReturnsZero()
    {
        using var buf = new TapeByteBuffer(64);

        int filled = buf.FillFrom([1, 2, 3], 0, 0);

        Assert.Equal(0, filled);
        Assert.True(buf.IsEmpty);
    }

    #endregion

    #region *** TapeByteBuffer — ZeroPadTo ***

    [Fact]
    public void Buffer_ZeroPadTo_PadsToRequestedSize()
    {
        using var buf = new TapeByteBuffer(16);
        buf.FillFrom([1, 2, 3], 0, 3);

        // ZeroPadTo is protected, so we test it indirectly through a subclass helper
        // Use reflection or test via the public SpillZeroPaddedTo pattern
        // Since TapeByteBuffer.ZeroPadTo is protected, we'll test via TestableByteBuffer
        var testBuf = new TestableByteBuffer(16);
        testBuf.FillFrom([1, 2, 3], 0, 3);
        testBuf.ZeroPad(8);

        Assert.Equal(8, testBuf.ContentSize);

        byte[] dst = new byte[8];
        testBuf.SpillTo(dst, 0, 8);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0 }, dst);
        testBuf.Dispose();
    }

    [Fact]
    public void Buffer_ZeroPadTo_LessThanContentSize_NoOp()
    {
        var testBuf = new TestableByteBuffer(16);
        testBuf.FillFrom([1, 2, 3, 4, 5], 0, 5);
        testBuf.ZeroPad(3); // less than current content → no-op

        Assert.Equal(5, testBuf.ContentSize);
        testBuf.Dispose();
    }

    [Fact]
    public void Buffer_ZeroPadTo_ExceedingCapacity_ClampsToCapacity()
    {
        var testBuf = new TestableByteBuffer(8);
        testBuf.FillFrom([1, 2], 0, 2);
        testBuf.ZeroPad(100); // exceeds capacity → clamped to 8

        Assert.Equal(8, testBuf.ContentSize);

        byte[] dst = new byte[8];
        testBuf.SpillTo(dst, 0, 8);
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 0, 0, 0 }, dst);
        testBuf.Dispose();
    }

    /// <summary>
    /// Subclass to expose <see cref="TapeByteBuffer.ZeroPadTo"/> for testing.
    /// </summary>
    private sealed class TestableByteBuffer(int capacity) : TapeByteBuffer(capacity)
    {
        public void ZeroPad(int count) => ZeroPadTo(count);
    }

    #endregion

    #region *** TapeByteBuffer — Stress / Repeated Cycles ***

    [Fact]
    public void Buffer_RepeatedFillSpill_MaintainsIntegrity()
    {
        using var buf = new TapeByteBuffer(32);
        var rng = new Random(42);

        for (int cycle = 0; cycle < 100; cycle++)
        {
            // Fill a random amount
            int fillCount = rng.Next(1, 20);
            byte[] src = new byte[fillCount];
            rng.NextBytes(src);

            int filled = buf.FillFrom(src, 0, fillCount);
            Assert.True(filled >= 0 && filled <= fillCount);
            Assert.True(buf.ContentSize <= buf.Capacity);

            // Spill a random amount
            int spillCount = rng.Next(0, buf.ContentSize + 1);
            byte[] dst = new byte[spillCount];
            int spilled = buf.SpillTo(dst, 0, spillCount);
            Assert.True(spilled >= 0 && spilled <= spillCount);
            Assert.True(buf.ContentSize >= 0);
        }
    }

    [Fact]
    public void Buffer_AlternatingFillSpill_PreservesFIFOOrder()
    {
        using var buf = new TapeByteBuffer(16);
        var expected = new List<byte>();

        // Write in small chunks, read back, verify FIFO
        for (byte i = 0; i < 50; i++)
        {
            byte[] src = [i];
            if (buf.Remaining > 0)
            {
                buf.FillFrom(src, 0, 1);
                expected.Add(i);
            }

            // Every 5 iterations, drain half
            if (i % 5 == 4 && buf.ContentSize > 0)
            {
                int drainCount = buf.ContentSize / 2;
                if (drainCount == 0) drainCount = 1;
                byte[] dst = new byte[drainCount];
                int spilled = buf.SpillTo(dst, 0, drainCount);

                for (int j = 0; j < spilled; j++)
                {
                    Assert.Equal(expected[0], dst[j]);
                    expected.RemoveAt(0);
                }
            }
        }

        // Drain remaining
        while (buf.ContentSize > 0)
        {
            byte[] dst = new byte[1];
            buf.SpillTo(dst, 0, 1);
            Assert.Equal(expected[0], dst[0]);
            expected.RemoveAt(0);
        }

        Assert.Empty(expected);
    }

    #endregion
}
