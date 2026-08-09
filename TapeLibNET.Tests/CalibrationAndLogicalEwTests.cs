using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Phase 2 coverage for the calibration + logical-early-warning pipeline, driven end-to-end over the
/// Phase-1 virtual EW emulation. Unlike <see cref="VirtualDriveEarlyWarningTests"/> (which pokes the
/// backend directly), these tests exercise the full <see cref="TapeDrive"/> / <see cref="TapeCalibrator"/>
/// surface: a real calibration run against an emulated LTO-like cartridge, JSON persistence, a-priori
/// baselines, multi-profile auto-selection, both logical-EW triggering regimes, estimator accuracy, and
/// runtime-state reset.
/// </summary>
public class CalibrationAndLogicalEwTests
{
    // 64 MB content — large enough for a meaningful curve, small enough to run at memory speed.
    private const long Capacity = 64L * 1024 * 1024;

    /// <summary>
    /// Builds a fully-open <see cref="TapeDrive"/> over a memory-backed virtual cartridge with the given
    /// (optional) EW emulation profile already applied to the loaded medium.
    /// </summary>
    private static (TapeDrive Drive, VirtualTapeDriveBackend Backend) CreateDrive(
        VirtualTapeEwProfile? profile, long capacity = Capacity)
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
        return (drive, backend);
    }

    private static byte[] IncompressibleBlock(int size, int seed)
    {
        var buffer = new byte[size];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }

    #region *** Calibration Run ***

    [Fact]
    public void CalibrationRun_ProducesUsableMonotonicCurve_WithEwLandmark()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // Faster run: fewer samples, small interval so a 64 MB cartridge still yields several points.
        var calibrator = new TapeCalibrator(drive)
        {
            Options = new TapeCalibrationOptions
            {
                SampleCount = 40,
                //MinSampleInterval = 1L * 1024 * 1024,
                //ChunkBytesTarget = 1L * 1024 * 1024,
            },
        };

        ITapeCalibration? cal = calibrator.Run();
        Assert.NotNull(cal);

        // Actual capacity measured at hard EOM ≈ the emulated true capacity.
        Assert.InRange(cal!.CapacityActual, (long)(Capacity * 0.98), Capacity);

        // The emulated profile asserts EW before EOM, so a landmark must have been captured.
        Assert.NotNull(cal.EarlyWarning);
        Assert.True(cal.EwToEomDistance > 0, "EW→EOM distance should be positive");

        // Curve is sorted ascending by ReportedRemaining and monotonic non-decreasing in ActualRemaining.
        Assert.True(cal.Curve.Count >= 2, "Curve should have at least two points");
        for (int i = 1; i < cal.Curve.Count; i++)
        {
            Assert.True(cal.Curve[i].ReportedRemaining >= cal.Curve[i - 1].ReportedRemaining,
                "ReportedRemaining axis must be ascending");
            Assert.True(cal.Curve[i].ActualRemaining >= cal.Curve[i - 1].ActualRemaining,
                "ActualRemaining must be monotonic non-decreasing");
        }

        // Profile key matches the drive, so a fresh calibration would auto-select.
        Assert.Equal(drive.DriveProfileKey, cal.ProfileKey);
    }

    [Fact]
    public void CalibrationRun_RestoresPriorReserveAndCalibrations()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // Pre-existing reserve + a matching loaded calibration that the run must NOT taint or discard.
        var preloaded = TapeCalibration.Apriori(drive.DriveProfileKey, Capacity);
        Assert.True(drive.AddCalibration(preloaded));
        const long reserve = 2L * 1024 * 1024;
        Assert.True(drive.SetEarlyWarning(reserve));

        var calibrator = new TapeCalibrator(drive)
        {
            Options = new TapeCalibrationOptions
            {
                SampleCount = 20,
                //MinSampleInterval = 2L * 1024 * 1024,
                //ChunkBytesTarget = 1L * 1024 * 1024,
            },
        };
        Assert.NotNull(calibrator.Run());

        // The caller's reserve and calibration are back exactly as before the run.
        Assert.Equal(reserve, drive.EarlyWarning);
        Assert.Contains(preloaded, drive.Calibrations);
    }

    [Theory]
    // phantomFreePercent, reportedBoostPercent — the two INDEPENDENT over-report axes.
    [InlineData(10.0, 0.0)]   // faithful LTO shape: truthful at BOM, phantom free space at EOM
    [InlineData(0.0, 10.0)]   // inflated capacity at BOM only: constant overshoot, honest at EOM
    [InlineData(10.0, 5.0)]   // both axes at once
    public void CalibrationRun_WithOverreport_CapturesBothBomAndEomAnchors(
        double phantomFreePercent, double reportedBoostPercent)
    {
        var profile = VirtualTapeEwProfile.Lto4Like(
            Capacity, ewZonePercent: 4.0,
            phantomFreePercent: phantomFreePercent,
            reportedBoostPercent: reportedBoostPercent);
        var (drive, _) = CreateDrive(profile);

        var calibrator = new TapeCalibrator(drive)
        {
            Options = new TapeCalibrationOptions
            {
                SampleCount = 40,
                //MinSampleInterval = 1L * 1024 * 1024,
                //ChunkBytesTarget = 1L * 1024 * 1024,
            },
        };

        ITapeCalibration? cal = calibrator.Run();
        Assert.NotNull(cal);

        // TrueRemaining still drives hard EOM at the cartridge's real capacity.
        Assert.InRange(cal!.CapacityActual, (long)(Capacity * 0.98), Capacity);

        // (a) BOM anchor — the driver's claim on the virgin cartridge, inflated by the boost only.
        long expectedBom = Capacity + (long)(Capacity * reportedBoostPercent / 100.0);
        Assert.InRange(cal.ReportedCapacityAtBom, (long)(expectedBom * 0.98), (long)(expectedBom * 1.02));

        // (b) EOM anchor — the phantom free space still claimed at hard EOM, driven by the phantom knob only.
        long expectedPhantom = (long)(Capacity * phantomFreePercent / 100.0);
        Assert.InRange(cal.PhantomFreeAtEom,
            (long)(expectedPhantom * 0.98), (long)(expectedPhantom * 1.02) + 1L);

        // The two anchors are independent: neither knob may leak into the other's measurement.
        if (reportedBoostPercent == 0.0)
            Assert.InRange(cal.ReportedCapacityAtBom, (long)(Capacity * 0.98), (long)(Capacity * 1.02));
        if (phantomFreePercent == 0.0)
            Assert.InRange(cal.PhantomFreeAtEom, 0L, (long)(Capacity * 0.01));

        Assert.Equal(0L, cal.Curve[0].ActualRemaining);
    }

    #endregion

    #region *** Persistence ***

    [Fact]
    public void CalibrationJson_RoundTrips_AndRejectsUnknownFormat()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));
        var calibrator = new TapeCalibrator(drive)
        {
            Options = new TapeCalibrationOptions
            {
                SampleCount = 20,
                //MinSampleInterval = 2L * 1024 * 1024,
                //ChunkBytesTarget = 1L * 1024 * 1024,
            },
        };
        ITapeCalibration? cal = calibrator.Run();
        Assert.NotNull(cal);

        using var ms = new MemoryStream();
        cal!.SaveTo(ms);
        ms.Position = 0;

        TapeCalibration? loaded = TapeCalibration.LoadFrom(ms);
        Assert.NotNull(loaded);
        Assert.Equal(cal.FormatId, loaded!.FormatId);
        Assert.Equal(cal.ProfileKey, loaded.ProfileKey);
        Assert.Equal(cal.ReportedCapacityAtBom, loaded.ReportedCapacityAtBom);
        Assert.Equal(cal.PhantomFreeAtEom, loaded.PhantomFreeAtEom);
        Assert.Equal(cal.CapacityActual, loaded.CapacityActual);
        Assert.Equal(cal.EwToEomDistance, loaded.EwToEomDistance);
        Assert.Equal(cal.Curve.Count, loaded.Curve.Count);
        for (int i = 0; i < cal.Curve.Count; i++)
            Assert.Equal(cal.Curve[i], loaded.Curve[i]);

        // A blob with an unrecognized FormatId must be rejected.
        using var bad = new MemoryStream();
        using (var writer = new StreamWriter(bad, leaveOpen: true))
            writer.Write("""{"FormatId":"unknown/9","ProfileKey":"x","ReportedCapacityAtBom":1,"PhantomFreeAtEom":0,"CapacityActual":1,"Curve":[],"EarlyWarning":null}""");
        bad.Position = 0;
        Assert.Null(TapeCalibration.LoadFrom(bad));
    }

    #endregion

    #region *** A-priori Baseline ***

    [Fact]
    public void Apriori_ProducesConservativeUsableCurve_WithoutRun()
    {
        var (drive, _) = CreateDrive(profile: null);

        var apriori = TapeCalibration.Apriori(drive.DriveProfileKey, drive.Capacity);
        Assert.Equal(drive.DriveProfileKey, apriori.ProfileKey);
        Assert.True(apriori.CapacityActual > 0);
        Assert.NotNull(apriori.EarlyWarning);

        // Conservative: the translated actual never exceeds the reported figure at any point.
        long reported = drive.Capacity;
        Assert.True(apriori.TranslateRemaining(reported) <= reported);
        Assert.True(apriori.TranslateRemaining(0) <= 0 + 1); // clamps at/near zero near EOM
    }

    #endregion

    #region *** Multi-profile Auto-selection ***

    [Fact]
    public void MultiProfile_SelectsMatchingKey_AndTracksLoadUnload()
    {
        var (drive, backend) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        string matchingKey = drive.DriveProfileKey;
        var matching = TapeCalibration.Apriori(matchingKey, Capacity);
        var other = TapeCalibration.Apriori(matchingKey + "|different", Capacity);

        Assert.False(drive.AddCalibration(other));    // wrong key → not matched
        Assert.True(drive.AddCalibration(matching));  // right key → matched now
        Assert.True(drive.IsCalibrationMatched);
        Assert.Same(matching, drive.Calibration);

        // A snapshot round-trip (eject → re-insert) preserves the profile key, so PrepareMedia's
        //  SelectCalibration re-applies the same matching calibration. Memory media must be captured
        //  before unload (it is discarded on eject) and re-inserted afterwards.
        var snapshot = backend.CaptureMemorySnapshot();
        Assert.NotNull(snapshot);
        Assert.True(drive.UnloadMedia());

        backend.InsertMemoryMedia(snapshot!);
        Assert.True(drive.ReloadMedia());
        Assert.True(drive.PrepareMedia());
        Assert.True(drive.IsCalibrationMatched);       // same profile key → re-selected
        Assert.Same(matching, drive.Calibration);

        // Removing the matching calibration deselects it.
        drive.RemoveCalibration(matching);
        Assert.False(drive.IsCalibrationMatched);
        Assert.Null(drive.Calibration);
    }

    #endregion

    #region *** Logical EW Triggering ***

    [Fact]
    public void LogicalEw_BeforePhysicalEw_FiresFromCurveWithLargeReserve()
    {
        // Capacity must exceed the internal ReportedRemaining poll interval (64 MB) so the throttled
        //  before-EW curve poll fires at least once before the physical EW zone near the tail.
        const long largeCapacity = 256L * 1024 * 1024;
        var profile = VirtualTapeEwProfile.Lto4Like(largeCapacity);
        var (drive, _) = CreateDrive(profile, capacity: largeCapacity);

        // A LARGE reserve so the calibrated curve trips logical EW well before the physical EW zone.
        long reserve = 80L * 1024 * 1024; // ~31% of capacity, far larger than the ~10 MB physical EW zone
        Assert.True(drive.AddCalibration(TapeCalibration.Apriori(drive.DriveProfileKey, largeCapacity)));
        Assert.True(drive.SetEarlyWarning(reserve));
        Assert.Equal(EarlyWarningMechanism.Calibrated, drive.EarlyWarningMechanism);

        int block = (int)drive.MaximumBlockSize;
        var data = IncompressibleBlock(block, seed: 11);

        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());

        long written = 0;
        bool firedBeforePhysical = false;
        while (true)
        {
            int n = drive.WriteDirect(data, 0, block, out _, out bool ew, out bool eom);
            if (eom || n == 0)
                break;
            written += n;

            if (ew)
            {
                // Logical EW asserted while the true remaining is still comfortably large — i.e. the
                //  curve tripped it, not the physical EW zone near the tail.
                long trueRemaining = largeCapacity - written;
                if (trueRemaining > profile.EarlyWarningZone)
                    firedBeforePhysical = true;
                break;
            }
        }

        Assert.True(drive.IsEarlyWarning, "Logical EW should have fired");
        Assert.True(firedBeforePhysical, "Logical EW should trip from the curve before the physical EW zone");
    }

    [Fact]
    public void LogicalEw_AfterPhysicalEw_FiresFromByteCountWithSmallReserve()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // A SMALL reserve so logical EW only trips in the precise after-physical-EW byte-count regime.
        long reserve = 256L * 1024;
        Assert.True(drive.AddCalibration(TapeCalibration.Apriori(drive.DriveProfileKey, Capacity)));
        Assert.True(drive.SetEarlyWarning(reserve));

        int block = (int)drive.MaximumBlockSize;
        var data = IncompressibleBlock(block, seed: 12);

        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());

        bool sawPhysicalEwBeforeLogical = false;
        bool physicalSeen = false;
        while (true)
        {
            int n = drive.WriteDirect(data, 0, block, out _, out bool ew, out bool eom);

            // Check the EW flags BEFORE the loop-exit guard: a write clamped down to zero bytes to
            //  preserve the reserve still reports ew, and would otherwise be swallowed by n == 0.
            //  IsPhysicalEarlyWarningSeen is the drive's actual physical landmark -- unlike comparing
            //  the estimate to the reported value, which with an a-priori calibration is ALWAYS true
            //  (the curve models actual ˜ reported - margin) and so detects nothing.
            physicalSeen |= drive.IsPhysicalEarlyWarningSeen;

            if (ew)
            {
                sawPhysicalEwBeforeLogical = physicalSeen;
                break;
            }

            if (eom || n == 0)
                break;
        }

        Assert.True(drive.IsEarlyWarning, "Logical EW should have fired near the tail");
        Assert.True(sawPhysicalEwBeforeLogical,
            "With a tiny reserve, logical EW should fire only after the physical EW landmark");
    }

    #endregion

    #region *** Estimator Accuracy ***

    [Fact]
    public void EstimateActualRemaining_TracksTrueRemaining_AcrossRegimes()
    {
        // Calibrate first so the drive has a measured curve to translate with.
        var (calDrive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));
        ITapeCalibration? cal = new TapeCalibrator(calDrive)
        {
            Options = new TapeCalibrationOptions
            {
                SampleCount = 60,
                //MinSampleInterval = 512L * 1024,
                //ChunkBytesTarget = 1L * 1024 * 1024,
            },
        }.Run();
        Assert.NotNull(cal);

        // Fresh cartridge, load the measured calibration, then write and compare estimate vs ground truth.
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));
        Assert.True(drive.AddCalibration(cal!));
        Assert.True(drive.SetEarlyWarning(1L * 1024 * 1024));

        int block = (int)drive.MaximumBlockSize;
        var data = IncompressibleBlock(block, seed: 21);

        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());

        long written = 0;
        double worstTailErrorFraction = 0.0;
        while (true)
        {
            int n = drive.WriteDirect(data, 0, block, out _, out _, out bool eom);
            if (eom || n == 0)
                break;
            written += n;

            long trueRemaining = cal!.CapacityActual - written;
            if (trueRemaining <= 0)
                continue;

            long estimate = drive.EstimateActualRemaining();
            // Estimate should never wildly exceed the truth; track the relative error in the tail.
            double errorFraction = Math.Abs(estimate - trueRemaining) / (double)Capacity;
            worstTailErrorFraction = Math.Max(worstTailErrorFraction, errorFraction);
        }

        // Across the whole write the estimate stays within a few percent of the emulated ground truth.
        Assert.True(worstTailErrorFraction < 0.10,
            $"Estimator error {worstTailErrorFraction:P1} exceeded tolerance");
    }

    #endregion

    #region *** Runtime State Reset ***

    [Fact]
    public void EarlyWarningRuntime_ResetsOnMediaReload()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));
        Assert.True(drive.SetEarlyWarning(256L * 1024));

        int block = (int)drive.MaximumBlockSize;
        var data = IncompressibleBlock(block, seed: 31);

        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());

        // Write until logical EW latches.
        while (!drive.IsEarlyWarning)
        {
            int n = drive.WriteDirect(data, 0, block, out _, out _, out bool eom);
            if (eom || n == 0)
                break;
        }
        Assert.True(drive.IsEarlyWarning, "Precondition: EW should latch before reset");

        // Reload clears the sticky latch and physical-EW anchor.
        Assert.True(drive.ReloadMedia());
        Assert.False(drive.IsEarlyWarning);
        Assert.False(drive.IsProgrammableEarlyWarning);
    }

    #endregion
}
