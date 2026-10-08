using Windows.Win32.Foundation;

using TapeLibNET.Drive;
using TapeLibNET.Virtual;
using TapeLibNET.Headers;
using TapeLibNET.Calibration;
using TapeLibNET.Scan;

namespace TapeLibNET.Tests.Scan;


/// <summary>
/// Scan Media Phase 2: calibration cartridges — identified, not walked (SM-7), and optionally enriched by
///  <see cref="TapeCalibrator.InspectMedia"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consistency, not calibration.</b> The calibrator's own behaviour is covered by
///  <c>CalibrationResumeTests</c>. What is tested HERE is only that the scan and a direct inspection agree —
///  so Scan Media and the calibration UI can never describe the same cartridge differently.
/// </para>
/// <para>
/// Same drive setup as <c>CalibrationResumeTests</c>: a small memory-backed cartridge with large blocks, so
///  the run header goes down as a standard header block and a full run takes milliseconds.
/// </para>
/// </remarks>
public class TapeScannerCalibrationTests
{
    // 64 MB content — small enough for memory speed, large enough for several body checkpoints.
    private const long Capacity = 64L * 1024 * 1024;

    /// <summary>How far the calibration run got before the scan looks at the cartridge.</summary>
    public enum TrailState
    {
        /// <summary>Aborted on the very first progress sample — before or around the first checkpoint.</summary>
        InterruptedEarly,

        /// <summary>Aborted halfway — several body checkpoints on tape, resumable.</summary>
        InterruptedMidBody,

        /// <summary>Run to EOM.</summary>
        Complete,
    }

    #region *** Helpers ***

    private static (TapeDrive Drive, VirtualTapeDriveBackend Backend) CreateDrive()
    {
        var backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            Helpers.TestLoggerFactory.Default,
            VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
            contentCapacity: Capacity,
            initiatorPartitionCapacity: 0);

        backend.IoRate = VirtualTapeDriveIoRate.Unlimited;
        backend.EmulatedEarlyWarning = VirtualTapeEwProfile.EmulatedOverreport(Capacity);

        var drive = new TapeDrive(Helpers.TestLoggerFactory.Default, backend);

        Assert.True(drive.ReopenDrive(0), "Failed to open virtual drive");
        Assert.True(drive.ReloadMedia(), "Failed to load virtual media");
        Assert.True(drive.PrepareMedia(), "Failed to prepare virtual media");

        return (drive, backend);
    }

    // Fast run with fine checkpointing, as in CalibrationResumeTests.
    private static TapeCalibrationOptions FastOptions() => new()
    {
        SampleCount = 40,
        NumCheckpoints = 16,
    };

    /// <summary>Flips the calibrator's abort flag once bytes-written crosses a threshold.</summary>
    /// <remarks>Copied from <c>CalibrationResumeTests</c>, where it is private.</remarks>
    private sealed class AbortAfterBytes(TapeCalibrator calibrator, long thresholdBytes)
        : IProgress<TapeCalibrationProgress>
    {
        public void Report(TapeCalibrationProgress p)
        {
            if (p.BytesWritten >= thresholdBytes)
                calibrator.IsAbortRequested = true;
        }
    }

    /// <summary>Lays down a calibration trail in the requested state.</summary>
    private static void LayTrail(TapeDrive drive, TrailState state)
    {
        var run = new TapeCalibrator(drive) { Options = FastOptions() };

        switch (state)
        {
            case TrailState.Complete:
                Assert.NotNull(run.Run());
                break;

            case TrailState.InterruptedMidBody:
                Assert.Null(run.Run(new AbortAfterBytes(run, Capacity / 2)));
                break;

            case TrailState.InterruptedEarly:
                Assert.Null(run.Run(new AbortAfterBytes(run, 1)));
                break;
        }
    }

    private static MediaScanMap Scan(TapeDrive drive, bool inspect)
    {
        var scanner = new TapeScanner(drive) { Options = new ScanMediaOptions { InspectCalibrationTrail = inspect } };
        MediaScanMap? map = scanner.Scan();

        Assert.True(map is not null, $"Scan returned no map: {scanner.LastResult}");
        return map!;
    }

    /// <summary>
    /// Asserts the cartridge was identified as a calibration cartridge and NOT walked: one fragment, a
    ///  complete (untruncated) map, a clean terminator.
    /// </summary>
    private static TapeMediaFragment AssertIdentifiedNotWalked(MediaScanMap map)
    {
        string describe = Helpers.ScanMapAssert.Describe(map);

        Assert.True(map.Kind == ScannedMediaKind.CalibrationCartridge, describe);
        Assert.True(map.Fragments.Count == 1, $"a calibration trail must not be walked\n{describe}");
        Assert.False(map.Truncated, describe);
        Assert.True(map.TerminatorWin32 == (uint)WIN32_ERROR.NO_ERROR, describe);

        TapeMediaFragment header = map.Fragments[0];
        Assert.True(header.Kind == FragmentKind.CalibrationHeader, describe);

        return header;
    }

    #endregion

    #region *** Tests ***

    /// <summary>
    /// The scan's enrichment IS the direct inspection: every field the calibration UI shows agrees, in every
    ///  trail state.
    /// </summary>
    /// <remarks>
    /// The early case asserts agreement only. Whether the first checkpoint made it to tape depends on the
    ///  run's first chunk versus its checkpoint interval — the calibrator's business, and not this test's.
    /// </remarks>
    [Theory]
    [InlineData(TrailState.InterruptedEarly)]
    [InlineData(TrailState.InterruptedMidBody)]
    [InlineData(TrailState.Complete)]
    public void Scan_AgreesWithDirectInspection(TrailState state)
    {
        var (drive, _) = CreateDrive();
        LayTrail(drive, state);

        // What the calibration UI sees on media load.
        TapeCalibrationMediaInfo? direct = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();
        Assert.NotNull(direct);

        MediaScanMap map = Scan(drive, inspect: true);
        TapeMediaFragment header = AssertIdentifiedNotWalked(map);
        string describe = Helpers.ScanMapAssert.Describe(map);

        // The fragment names the same run...
        Assert.Equal(direct!.RunId, header.Id);
        Assert.Equal(direct.ProfileKey, header.Description);

        // ...and the enrichment carries the same verdict-relevant figures.
        TapeCalibrationMediaInfo? viaScan = map.CalibrationInfo;
        Assert.True(viaScan is not null, $"inspection was requested but not attached\n{describe}");

        Assert.Equal(direct.RunId, viaScan!.RunId);
        Assert.Equal(direct.ProfileKey, viaScan.ProfileKey);
        Assert.Equal(direct.IsResumable, viaScan.IsResumable);
        Assert.Equal(direct.AppearsComplete, viaScan.AppearsComplete);
        Assert.Equal(direct.CheckpointIndex, viaScan.CheckpointIndex);
        Assert.Equal(direct.CheckpointedBytes, viaScan.CheckpointedBytes);
        Assert.Equal(direct.ProgressFraction, viaScan.ProgressFraction);

        // Scenario sanity — so an agreement on the WRONG state cannot pass unnoticed.
        switch (state)
        {
            case TrailState.Complete:
                Assert.True(viaScan.AppearsComplete, describe);
                break;

            case TrailState.InterruptedMidBody:
                Assert.True(viaScan.IsResumable, describe);
                Assert.False(viaScan.AppearsComplete, describe);
                break;
        }
    }

    /// <summary>
    /// Without inspection the cartridge is still fully identified — the header alone is a complete answer
    ///  (SM-7) — but no calibration info is attached.
    /// </summary>
    [Fact]
    public void WithoutInspection_IsIdentified_ButCarriesNoInfo()
    {
        var (drive, _) = CreateDrive();
        LayTrail(drive, TrailState.Complete);

        MediaScanMap map = Scan(drive, inspect: false);
        TapeMediaFragment header = AssertIdentifiedNotWalked(map);

        Assert.Null(map.CalibrationInfo);
        Assert.True(header.Id is { } id && id != Guid.Empty, Helpers.ScanMapAssert.Describe(map));
    }

    /// <summary>
    /// A scan with inspection writes nothing: an interrupted run is still resumable afterwards, under the
    ///  same run id. Mirrors <c>InspectMedia_IsNonDestructive_ResumeStillSucceeds</c>.
    /// </summary>
    [Fact]
    public void Scan_IsNonDestructive_ResumeStillSucceeds()
    {
        var (drive, _) = CreateDrive();
        LayTrail(drive, TrailState.InterruptedMidBody);

        MediaScanMap map = Scan(drive, inspect: true);
        Guid runId = AssertIdentifiedNotWalked(map).Id!.Value;

        ITapeCalibration? resumed = new TapeCalibrator(drive) { Options = FastOptions() }.Resume();

        Assert.NotNull(resumed);
        Assert.InRange(resumed!.CapacityActual, (long)(Capacity * 0.98), Capacity);

        TapeCalibrationMediaInfo? after = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();

        Assert.NotNull(after);
        Assert.Equal(runId, after!.RunId);      // the scan left the run's identity intact
        Assert.True(after.AppearsComplete);
    }

    /// <summary>
    /// SM-9 on the calibration path: <c>InspectMedia</c> sets the RUN block size, and the scanner restores
    ///  the caller's.
    /// </summary>
    [Fact]
    public void Scan_RestoresBlockSize_AfterInspection()
    {
        var (drive, _) = CreateDrive();
        LayTrail(drive, TrailState.Complete);

        // Anything other than the run block size will do; the standard header block size is always valid here.
        Assert.True(drive.SetBlockSize(TapeHeaderBlock.Size));

        Scan(drive, inspect: true);

        Assert.Equal((uint)TapeHeaderBlock.Size, drive.BlockSize);
    }

    #endregion
}
