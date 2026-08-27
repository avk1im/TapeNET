using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Phase 1 coverage for the imprecise-remaining + early-warning emulation on the virtual backend.
/// Exercises <see cref="VirtualTapeDriveBackend"/> / <see cref="VirtualTapeMedia"/> directly (below any
/// agent) to validate that the emulation reproduces the two real-world LTO behaviors the estimator tames.
/// </summary>
public class VirtualDriveEarlyWarningTests
{
    private const long Capacity = 10L * 1024 * 1024; // 10 MB — exact multiple of the 128 KB block size

    private static VirtualTapeDriveBackend CreateBackend(VirtualTapeEwProfile? profile, bool report)
    {
        var backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            Helpers.TestLoggerFactory.Default,
            VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
            contentCapacity: Capacity,
            initiatorPartitionCapacity: 0);

        backend.IoRate = VirtualTapeDriveIoRate.Unlimited;
        backend.EmulatedEarlyWarning = profile;

        Assert.True(backend.Open(0));
        Assert.True(backend.LoadMedia());

        // ReportEarlyWarning returns whether the request is honored (i.e. an EW zone is being emulated),
        //  independent of the requested on/off value.
        bool honored = backend.ReportEarlyWarning(report);
        Assert.Equal(profile is { EarlyWarningZone: > 0 }, honored);
        return backend;
    }

    private static byte[] IncompressibleBlock(int size, int seed)
    {
        var buffer = new byte[size];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }

    [Fact]
    public void NoProfile_PreservesLegacyExactRemaining()
    {
        using var backend = CreateBackend(profile: null, report: true);

        Assert.Equal(EarlyWarningMechanism.None, backend.EarlyWarningMechanism);
        Assert.False(backend.ReportsEarlyWarning);
        Assert.Equal(Capacity, backend.Remaining);

        int block = (int)backend.DefaultBlockSize;
        var data = IncompressibleBlock(block, seed: 1);

        long written = 0;
        bool anyEw = false;
        while (true)
        {
            int n = backend.Write(data, 0, block, out _, out bool pew, out bool ew, out bool eom);
            Assert.False(pew);
            if (eom)
            {
                Assert.Equal(0, n);
                break;
            }

            written += n;
            anyEw |= ew;

            // Legacy behavior: reported remaining is exactly capacity - bytesWritten.
            Assert.Equal(Capacity - written, backend.Remaining);
        }

        Assert.False(anyEw, "No EW should ever fire without a profile");
        Assert.Equal(Capacity, written);
    }

    [Fact]
    public void Lto4LikeProfile_RemainingOvershootsThenFloors_EwStickyBeforeEom()
    {
        var profile = VirtualTapeEwProfile.EmulatedOverreport(Capacity);
        using var backend = CreateBackend(profile, report: true);

        Assert.Equal(EarlyWarningMechanism.HardwareEarlyWarning, backend.EarlyWarningMechanism);
        Assert.True(backend.ReportsEarlyWarning);

        int block = (int)backend.DefaultBlockSize;
        var data = IncompressibleBlock(block, seed: 2);

        long written = 0;
        long firstEwAtBytes = -1;
        bool sawEom = false;
        long minReported = long.MaxValue;

        while (true)
        {
            long reportedBefore = backend.Remaining;
            int n = backend.Write(data, 0, block, out _, out bool pew, out bool ew, out bool eom);
            Assert.False(pew); // Phase 2 stub

            if (eom)
            {
                Assert.Equal(0, n);
                sawEom = true;
                break;
            }

            written += n;

            // Reported remaining overshoots: the driver reports MORE free space than truly remains.
            long trueRemaining = Capacity - written;
            Assert.True(backend.Remaining >= trueRemaining,
                $"Reported {backend.Remaining} should overshoot true {trueRemaining}");

            minReported = Math.Min(minReported, backend.Remaining);

            if (ew)
            {
                if (firstEwAtBytes < 0)
                    firstEwAtBytes = written;
            }
            else
            {
                // EW must be sticky: it never turns off once it has fired.
                Assert.True(firstEwAtBytes < 0, "EW must stay asserted once it fires");
            }
        }

        Assert.True(sawEom, "Hard EOM must eventually fire");
        Assert.Equal(Capacity, written);

        // EW fired before hard EOM, roughly one EW-zone before the true end.
        Assert.True(firstEwAtBytes > 0, "EW should have fired");
        Assert.True(firstEwAtBytes < Capacity, "EW should fire before the true end");
        Assert.InRange(Capacity - firstEwAtBytes, 0, profile.EarlyWarningZone + block);

        // Reported remaining floored well above zero at the tail (the LTO overshoot at hard EOM).
        Assert.True(minReported > 0, "Reported remaining should floor above zero near the tail");
    }

    [Fact]
    public void ReportEarlyWarningFalse_SuppressesEwFlag()
    {
        var profile = VirtualTapeEwProfile.EmulatedOverreport(Capacity);
        using var backend = CreateBackend(profile, report: false);

        // Mechanism still advertises the zone, but the flag is gated off until reporting is requested.
        Assert.Equal(EarlyWarningMechanism.HardwareEarlyWarning, backend.EarlyWarningMechanism);
        Assert.False(backend.ReportsEarlyWarning);

        int block = (int)backend.DefaultBlockSize;
        var data = IncompressibleBlock(block, seed: 3);

        bool anyEw = false;
        while (true)
        {
            int n = backend.Write(data, 0, block, out _, out _, out bool ew, out bool eom);
            if (eom)
            {
                Assert.Equal(0, n);
                break;
            }
            anyEw |= ew;
        }

        Assert.False(anyEw, "EW flag must stay off while reporting is disabled");
    }

    [Fact]
    public void FromCalibration_RescalesProfileToSmallVirtualCapacity()
    {
        // A large a-priori LTO-like profile rescaled onto the tiny virtual cartridge.
        const long largeCapacity = 780L * 1024 * 1024 * 1024; // ~780 GB
        var cal = TapeCalibration.Apriori("test|profile|rev|780GB", largeCapacity);

        var profile = VirtualTapeEwProfile.FromCalibration(cal, Capacity);
        using var backend = CreateBackend(profile, report: true);

        Assert.Equal(EarlyWarningMechanism.HardwareEarlyWarning, backend.EarlyWarningMechanism);

        int block = (int)backend.DefaultBlockSize;
        var data = IncompressibleBlock(block, seed: 4);

        long written = 0;
        bool anyEw = false;
        bool sawEom = false;
        while (true)
        {
            int n = backend.Write(data, 0, block, out _, out _, out bool ew, out bool eom);
            if (eom)
            {
                sawEom = true;
                break;
            }
            written += n;
            anyEw |= ew;
        }

        Assert.True(sawEom);
        Assert.True(anyEw, "Rescaled EW zone should fire before EOM");
        Assert.Equal(Capacity, written);
    }
}
