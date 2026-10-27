using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Calibration;

/// <summary>
/// Design-TocReserve, Step 3: <see cref="TapeDrive.RaiseEarlyWarning"/> grows the logical-EW reserve of a running set
///  without disturbing the EW mapping — in the curve regime, in the byte-count regime after physical EW, for a large
///  write right after the raise, and from another thread while writes run.
/// </summary>
/// <remarks>
/// The tests on an HONEST virtual drive (no EW profile) rely on its identity calibration: the estimate equals the true
///  remaining, so a reserve is honored to the block.
/// </remarks>
public class LogicalEwReserveRaiseTests
{
    private const long MiB = 1024L * 1024;
    private const long Capacity = 256 * MiB;        // > the 64 MB poll ceiling, so the curve regime is exercised

    #region *** Helpers ***

    private static (TapeDrive Drive, VirtualTapeDriveBackend Backend) CreateDrive(
        VirtualTapeEwProfile? profile = null, long capacity = Capacity)
    {
        var backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            Helpers.TestLoggerFactory.Default,
            VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
            contentCapacity: capacity,
            initiatorPartitionCapacity: 0);
        backend.IoRate = VirtualTapeDriveIoRate.Unlimited;
        backend.EmulatedEarlyWarning = profile;

        var drive = new TapeDrive(Helpers.TestLoggerFactory.Default, backend);
        Assert.True(drive.ReopenDrive(0), "Failed to open virtual drive");
        Assert.True(drive.ReloadMedia(), "Failed to load virtual media");
        Assert.True(drive.PrepareMedia(), "Failed to prepare virtual media");
        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());
        return (drive, backend);
    }

    private static byte[] Data(int size, int seed)
    {
        var buffer = new byte[size];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }

    private static long TrueRemaining(VirtualTapeDriveBackend backend) => backend.ContentMedia!.TrueRemaining;

    /// <summary>Writes blocks until logical EW is reported; returns whether it was.</summary>
    private static bool WriteUntilEw(TapeDrive drive, byte[] block)
    {
        while (true)
        {
            int n = drive.WriteDirect(block, 0, block.Length, out _, out bool ew, out bool eom);
            if (ew)
                return true;
            if (eom || n == 0)
                return false;
        }
    }

    #endregion

    #region *** Regimes ***

    /// <summary>Curve regime: the raised reserve is honored to the block.</summary>
    [Fact]
    public void Raise_BeforePhysicalEw_FiresAtTheLargerReserve()
    {
        var (drive, backend) = CreateDrive();
        int block = (int)drive.MaximumBlockSize;
        var data = Data(block, 1);

        Assert.True(drive.SetEarlyWarning(8 * MiB));
        for (int i = 0; i < 16; i++)
            Assert.Equal(block, drive.WriteDirect(data, 0, block));

        drive.RaiseEarlyWarning(32 * MiB);
        Assert.Equal(8 * MiB, drive.EarlyWarning);                  // applied by the next write, not before
        Assert.Equal(block, drive.WriteDirect(data, 0, block));
        Assert.Equal(32 * MiB, drive.EarlyWarning);

        Assert.True(WriteUntilEw(drive, data), "logical EW should fire");
        Assert.InRange(TrueRemaining(backend), 32 * MiB, 32 * MiB + block - 1);
    }

    /// <summary>Byte-count regime: raised after the physical landmark — the anchor survives, the larger reserve fires.</summary>
    [Fact]
    public void Raise_AfterPhysicalEw_FiresAtTheLargerReserve_AnchorKept()
    {
        const long small = 64 * MiB;
        var (calDrive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(small), small);
        ITapeCalibration? cal = new TapeCalibrator(calDrive) { Options = new() { SampleCount = 60 } }.Run();
        Assert.NotNull(cal);

        var (drive, backend) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(small), small);
        Assert.True(drive.AddCalibration(cal!));
        Assert.True(drive.SetEarlyWarning(256L * 1024));
        int block = (int)drive.MaximumBlockSize;
        var data = Data(block, 2);

        while (!drive.IsPhysicalEarlyWarningSeen)
        {
            int n = drive.WriteDirect(data, 0, block, out _, out bool ew, out bool eom);
            Assert.False(ew, "the small reserve must not fire before the physical landmark");
            Assert.False(eom || n == 0, "physical EW should be reached before EOM");
        }

        const long raised = 1 * MiB;                                 // below the ~2.56 MB physical EW zone
        drive.RaiseEarlyWarning(raised);
        Assert.True(WriteUntilEw(drive, data), "logical EW should fire at the larger reserve");
        Assert.Equal(raised, drive.EarlyWarning);
        Assert.True(drive.IsPhysicalEarlyWarningSeen, "a raise keeps the physical anchor");
        Assert.True(TrueRemaining(backend) >= raised - block,
            $"fired with {TrueRemaining(backend)} B left, reserve {raised} B");
    }

    /// <summary>
    /// The invalidated headroom: one large write right after a raise is clamped to the NEW reserve, even though the cached
    ///  headroom from the last poll still described plenty of room.
    /// </summary>
    [Fact]
    public void Raise_ThenLargeWrite_IsClampedToTheNewReserve()
    {
        var (drive, backend) = CreateDrive();
        int block = (int)drive.MaximumBlockSize;
        var data = Data(block, 3);

        Assert.True(drive.SetEarlyWarning(8 * MiB));
        while (TrueRemaining(backend) > 40 * MiB)
            Assert.Equal(block, drive.WriteDirect(data, 0, block));
        Assert.Equal(40 * MiB, TrueRemaining(backend));

        drive.RaiseEarlyWarning(32 * MiB);
        var big = Data((int)(16 * MiB), 4);
        int written = drive.WriteDirect(big, 0, big.Length, out _, out bool ew, out _);

        Assert.Equal((int)(8 * MiB), written);                       // 40 − 32: up to the new reserve, not one byte more
        Assert.True(ew);
        Assert.Equal(32 * MiB, TrueRemaining(backend));
    }

    #endregion

    #region *** Never lowers, never arms ***

    [Fact]
    public void Raise_Smaller_IsIgnored()
    {
        var (drive, _) = CreateDrive();
        int block = (int)drive.MaximumBlockSize;
        Assert.True(drive.SetEarlyWarning(32 * MiB));

        drive.RaiseEarlyWarning(8 * MiB);
        Assert.Equal(block, drive.WriteDirect(Data(block, 5), 0, block));
        Assert.Equal(32 * MiB, drive.EarlyWarning);
    }

    [Fact]
    public void Raise_WithoutReserve_IsIgnored()
    {
        var (drive, _) = CreateDrive();
        int block = (int)drive.MaximumBlockSize;
        var data = Data(block, 6);
        Assert.True(drive.SetEarlyWarning(0));

        drive.RaiseEarlyWarning(32 * MiB);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(block, drive.WriteDirect(data, 0, block, out _, out bool ew, out _));
            Assert.False(ew);
        }
        Assert.Equal(0L, drive.EarlyWarning);
    }

    /// <summary>A raise pending when a calibration run starts is dropped: the run measures raw EW, and restores the reserve.</summary>
    [Fact]
    public void Raise_PendingBeforeCalibrationRun_IsDropped()
    {
        const long small = 64 * MiB;
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(small), small);
        Assert.True(drive.SetEarlyWarning(2 * MiB));
        drive.RaiseEarlyWarning(8 * MiB);

        Assert.NotNull(new TapeCalibrator(drive) { Options = new() { SampleCount = 20 } }.Run());

        Assert.Equal(2 * MiB, drive.EarlyWarning);
        Assert.True(drive.Rewind());
        int block = (int)drive.MaximumBlockSize;
        drive.WriteDirect(Data(block, 7), 0, block);
        Assert.Equal(2 * MiB, drive.EarlyWarning);                   // the dropped raise did not resurface
    }

    #endregion

    #region *** Threads and lifetime ***

    /// <summary>Raises from many threads while another writes: the largest one wins, nothing is lost, nothing throws.</summary>
    [Fact]
    public async Task Raise_ConcurrentWithWrites_KeepsTheMaximum()
    {
        var (drive, _) = CreateDrive();
        int block = (int)drive.MaximumBlockSize;
        var data = Data(block, 8);
        Assert.True(drive.SetEarlyWarning(4 * MiB));

        const int raises = 1_000;
        const long step = 16L * 1024;
        var writer = Task.Run(() => WriteUntilEw(drive, data));
        Parallel.For(1, raises + 1, i => drive.RaiseEarlyWarning(4 * MiB + i * step));
        Assert.True(await writer.WaitAsync(TimeSpan.FromMinutes(1)), "logical EW should fire");

        drive.WriteDirect(data, 0, block);                           // applies a raise that arrived after the last write
        Assert.Equal(4 * MiB + raises * step, drive.EarlyWarning);
    }

    [Fact]
    public void Raise_PendingIsDropped_BySetEarlyWarning_AndByReload()
    {
        var (drive, _) = CreateDrive();
        int block = (int)drive.MaximumBlockSize;
        var data = Data(block, 9);

        Assert.True(drive.SetEarlyWarning(8 * MiB));
        drive.RaiseEarlyWarning(32 * MiB);
        Assert.True(drive.SetEarlyWarning(8 * MiB));                 // a new set
        drive.WriteDirect(data, 0, block);
        Assert.Equal(8 * MiB, drive.EarlyWarning);

        drive.RaiseEarlyWarning(32 * MiB);
        Assert.True(drive.ReloadMedia());                            // a new medium
        Assert.True(drive.PrepareMedia());
        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());
        drive.WriteDirect(data, 0, block);
        Assert.Equal(8 * MiB, drive.EarlyWarning);
    }

    #endregion
}
