using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Drive;

/// <summary>
/// Design-TocReserve §4.3: a write planned against the reserve its files need. Runs on a virtual drive without
///  early-warning emulation: the estimate IS the truth, so every expectation is exact to the block.
/// </summary>
public class TocReservePlannedWriteTests
{
    private const long KiB = 1024;
    private const long Capacity = 1024 * KiB;

    private static VirtualTapeFixture Fixture() => new(DriveProfile.Setmarks, contentCapacity: Capacity);

    private static (int Written, bool Ew) Write(VirtualTapeFixture fixture, int blocks, Func<int, long>? reserveForBlocks)
    {
        int blockSize = (int)fixture.Drive.BlockSize;
        var buffer = new byte[blocks * blockSize];
        int written = fixture.Drive.WriteDirect(buffer, 0, buffer.Length, honorEwReserve: true,
            out _, out bool ew, out _, reserveForBlocks);
        return (written, ew);
    }

    /// <summary>The largest b with b × blockSize + reserve(b) ≤ available — the planner's contract.</summary>
    private static int Best(long available, int blocks, int blockSize, long desired, Func<int, long> reserve)
    {
        for (int b = blocks; b > 0; b--)
            if ((long)b * blockSize + Math.Max(desired, reserve(b)) <= available)
                return b;
        return 0;
    }

    [Fact]
    public void WriteFits_ReserveGrowsToTheWholeWrite_NoEarlyWarning()
    {
        using var fixture = Fixture();
        Assert.True(fixture.Drive.SetEarlyWarning(64 * KiB));
        Func<int, long> reserve = b => 64 * KiB + b * KiB;

        var (written, ew) = Write(fixture, 4, reserve);

        Assert.Equal(4 * (int)fixture.Drive.BlockSize, written);
        Assert.False(ew);
        Assert.Equal(reserve(4), fixture.Drive.EarlyWarning);
    }

    [Fact]
    public void WriteDoesNotFit_TrimmedToTheLargestFittingPrefix_EarlyWarning()
    {
        using var fixture = Fixture();
        Assert.True(fixture.Drive.SetEarlyWarning(64 * KiB));
        Func<int, long> reserve = b => 64 * KiB + b * 64 * KiB;      // heavy entries: the TOC outgrows the content
        int blockSize = (int)fixture.Drive.BlockSize;
        long available = fixture.Drive.EstimatedContentRemaining;
        int best = Best(available, 16, blockSize, 64 * KiB, reserve);
        Assert.InRange(best, 1, 15);                                    // the case under test: a partial write

        var (written, ew) = Write(fixture, 16, reserve);

        Assert.Equal(best * blockSize, written);
        Assert.True(ew, "a trimmed write must end the set");
        Assert.Equal(reserve(best), fixture.Drive.EarlyWarning);
        Assert.True(fixture.Drive.EstimatedContentRemaining >= fixture.Drive.EarlyWarning,
            "the room the reserve claims must be left on the tape");
    }

    [Fact]
    public void NothingFits_WritesNothing_EarlyWarning_ReserveUnchanged()
    {
        using var fixture = Fixture();
        Assert.True(fixture.Drive.SetEarlyWarning(64 * KiB));

        var (written, ew) = Write(fixture, 4, b => 64 * KiB + b * Capacity);

        Assert.Equal(0, written);
        Assert.True(ew);
        Assert.Equal(64 * KiB, fixture.Drive.EarlyWarning);
    }

    [Fact]
    public void SmallerReserve_NeverLowers()
    {
        using var fixture = Fixture();
        Assert.True(fixture.Drive.SetEarlyWarning(256 * KiB));

        var (written, ew) = Write(fixture, 4, _ => 16 * KiB);

        Assert.Equal(4 * (int)fixture.Drive.BlockSize, written);
        Assert.False(ew);
        Assert.Equal(256 * KiB, fixture.Drive.EarlyWarning);
    }

    [Fact]
    public void NoReserveArmed_FunctionIgnored()
    {
        using var fixture = Fixture();
        Assert.True(fixture.Drive.SetEarlyWarning(0));

        var (written, ew) = Write(fixture, 4, b => 64 * KiB + b * Capacity);

        Assert.Equal(4 * (int)fixture.Drive.BlockSize, written);
        Assert.False(ew);
        Assert.Equal(0L, fixture.Drive.EarlyWarning);
    }

    /// <summary>Writes until the volume ends: every write planned, the reserve only grows, and its room is left.</summary>
    [Fact]
    public void WriteToTheEnd_ReserveMonotone_RoomLeft()
    {
        using var fixture = Fixture();
        Assert.True(fixture.Drive.SetEarlyWarning(32 * KiB));
        long committed = 0;
        long previous = fixture.Drive.EarlyWarning;
        int blockSize = (int)fixture.Drive.BlockSize;
        for (int i = 0; i < 100; i++)
        {
            long c = committed;
            var (written, ew) = Write(fixture, 4, b => 32 * KiB + c + b * 4 * KiB);
            committed += written / blockSize * 4 * KiB;
            Assert.True(fixture.Drive.EarlyWarning >= previous, "TR-2: never lowered");
            previous = fixture.Drive.EarlyWarning;
            if (ew)
                break;
        }
        Assert.True(fixture.Drive.IsEarlyWarning, "the volume must have ended");
        Assert.True(fixture.Drive.EstimatedContentRemaining >= 32 * KiB + committed,
            "TR-1: room for the TOC of everything written is left");
    }
}
