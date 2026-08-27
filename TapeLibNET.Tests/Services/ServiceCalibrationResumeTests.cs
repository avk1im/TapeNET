using System;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Service-level coverage for the extended calibration surface: the <see cref="CalibrationMode"/>
/// dispatch (New / Resume / Recalibrate) through <see cref="TapeServiceBase.ExecuteCalibrateAsync"/>,
/// result tagging (<see cref="CalibrateResult.Mode"/> / <see cref="CalibrateResult.RecalibrationDelta"/> /
/// <see cref="CalibrateResult.RecalibrationVerdict"/>), and host-pane logging — driven over small
/// memory-backed virtual cartridges.
/// <para>
/// These complement the drive-level <c>CalibrationResumeTests</c> (which prove the mechanism itself);
/// here the focus is the SERVICE plumbing: mode routing, verdict reporting, and mode-appropriate
/// failure messages. The specific recalibration VERDICT is intentionally not pinned on an unchanged
/// small drive — the EW→EOM distance is block-quantized, so an unchanged-profile recalibrate can move
/// by a block; that is production-irrelevant but would make a strict "Holds" assertion flaky. Verdict
/// behavior under a genuine drive-behavior change is covered separately (see the breach test).
/// </para>
/// </summary>
public class ServiceCalibrationResumeTests : ServiceTestBase
{
    private const long MB = 1024L * 1024;
    private const long CalibrationCapacity = 64L * MB;

    private static async Task<(TapeServiceBase service, TestTapeServiceHost host)> OpenCalibrationServiceAsync(
        long capacity = CalibrationCapacity,
        VirtualTapeDriveIoRate? ioRate = null,
        VirtualTapeEwProfile? ewProfile = null)
    {
        var (service, host) = CreateService();

        var vmd = new VirtualMediaDescriptor("memory-calibration", capacity, null, 0, InMemory: true);

        Assert.True(await service.OpenVirtualDriveAsync(
                VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
                vmd,
                ioRate: ioRate,
                ewProfile: ewProfile ?? VirtualTapeEwProfile.EmulatedOverreport(capacity)),
            $"OpenVirtualDriveAsync failed: {service.LastError}");

        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync failed: {service.LastError}");

        return (service, host);
    }

    private static TapeCalibrationOptions FastOptions() => new()
    {
        SampleCount = 40,
        NumCheckpoints = 16,
    };

    // ── New calibration (extra coverage) ──────────────────────────────────────

    [Fact]
    public async Task ExecuteCalibrateAsync_New_ProducesMonotonicCurve_AndDefaultsToNewMode()
    {
        var (service, _) = await OpenCalibrationServiceAsync();
        using (service)
        {
            var result = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions()));

            Assert.True(result.Success);

            // The default mode is New, and the recalibration fields stay null for New/Resume.
            Assert.Equal(CalibrationMode.New, result.Mode);
            Assert.Null(result.RecalibrationDelta);
            Assert.Null(result.RecalibrationVerdict);

            var cal = result.Calibration;
            Assert.NotNull(cal);
            Assert.True(cal!.Curve.Count >= 2, "Curve should have at least two points");

            // Curve is ascending in ReportedRemaining and monotonic non-decreasing in ActualRemaining.
            for (int i = 1; i < cal.Curve.Count; i++)
            {
                Assert.True(cal.Curve[i].ReportedRemaining >= cal.Curve[i - 1].ReportedRemaining,
                    "ReportedRemaining axis must be ascending");
                Assert.True(cal.Curve[i].ActualRemaining >= cal.Curve[i - 1].ActualRemaining,
                    "ActualRemaining must be monotonic non-decreasing");
            }

            Assert.NotNull(cal.EarlyWarning);
            Assert.Equal(service.DriveProfileKey, result.ProfileKey);
        }
    }

    // ── Resume ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCalibrateAsync_Resume_OnTrailedCartridge_Completes()
    {
        var (service, host) = await OpenCalibrationServiceAsync();
        using (service)
        {
            // A completed run leaves the resumable trail (header + body checkpoints) on the cartridge.
            var first = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions()));
            Assert.True(first.Success);
            Assert.Equal(CalibrationMode.New, first.Mode);

            // Resume deterministically restarts from the last body checkpoint and re-measures the tail to
            //  EOM — no timing dependency, since the trail is already present.
            var resumed = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions(),
                    Mode: CalibrationMode.Resume));

            Assert.True(resumed.Success, $"Resume failed: {resumed.Message}");
            Assert.Equal(CalibrationMode.Resume, resumed.Mode);
            Assert.NotNull(resumed.Calibration);
            Assert.True(resumed.CapacityActual > 0);
            Assert.Null(resumed.RecalibrationDelta);      // Resume carries no recalibration delta
            Assert.Null(resumed.RecalibrationVerdict);
            Assert.True(host.ContainsMessage("Resuming calibration"));
        }
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_Resume_OnBlankCartridge_FailsGracefully()
    {
        var (service, _ /*host*/) = await OpenCalibrationServiceAsync();
        using (service)
        {
            // No run has been performed, so there is no header/trail to resume from.
            var resumed = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions(),
                    Mode: CalibrationMode.Resume));

            Assert.False(resumed.Success);
            Assert.Equal(CalibrationMode.Resume, resumed.Mode);
            Assert.Contains("no resumable run", resumed.Message ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Recalibrate ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCalibrateAsync_Recalibrate_ReportsDeltaVerdictAndAssessment()
    {
        var (service, host) = await OpenCalibrationServiceAsync();
        using (service)
        {
            var first = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions()));
            Assert.True(first.Success);
            Assert.NotNull(first.Calibration);

            var recal = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions(),
                    Mode: CalibrationMode.Recalibrate, ExistingCalibration: first.Calibration));

            // Plumbing: the mode is tagged, the delta and verdict are populated, and the assessment is
            //  logged to the host pane. The specific verdict is drive-quantization-dependent on a small
            //  cartridge, so it is not pinned here (see the breach test for verdict behavior).
            Assert.Equal(CalibrationMode.Recalibrate, recal.Mode);
            Assert.NotNull(recal.RecalibrationDelta);
            Assert.NotNull(recal.RecalibrationVerdict);
            Assert.NotNull(recal.Calibration);
            Assert.True(host.ContainsMessage("Recalibration assessment"));

            // The delta reports the raw before/after values verdict-free.
            var d = recal.RecalibrationDelta!.Value;
            Assert.Equal(first.Calibration!.EwToEomDistance, d.OldEwToEomDistance);
            Assert.Equal(first.Calibration.CapacityActual, d.OldCapacityActual);
        }
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_Recalibrate_OnBlankCartridge_Fails()
    {
        var (service, _) = await OpenCalibrationServiceAsync();
        using (service)
        {
            // A baseline to compare against exists, but the cartridge carries no calibration trail.
            var existing = TapeCalibration.Apriori(service.DriveProfileKey, CalibrationCapacity);

            var recal = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions(),
                    Mode: CalibrationMode.Recalibrate, ExistingCalibration: existing));

            Assert.False(recal.Success);
            Assert.Equal(CalibrationMode.Recalibrate, recal.Mode);
            Assert.Contains("no calibration trail", recal.Message ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_Recalibrate_WithoutExistingOrTrail_Fails()
    {
        var (service, _) = await OpenCalibrationServiceAsync();
        using (service)
        {
            // No explicit existing calibration, none loaded on the drive, none in the store → the service
            //  refuses before touching the tape.
            var recal = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions(),
                    Mode: CalibrationMode.Recalibrate));

            Assert.False(recal.Success);
            Assert.Equal(CalibrationMode.Recalibrate, recal.Mode);
        }
    }

    // ── Recalibrate — threshold breach + host confirm chain ──────────────────
    // The breach is induced with a divergent STORED baseline (a pre-firmware-change calibration),
    //  which drives the same service judge → confirm → chain path as a live drive-behavior change while
    //  needing only public API. The drive-level profile-swap test proves the calibrator itself surfaces
    //  a real behavior shift as a large delta.

    private static TapeCalibration StaleBaseline(TapeServiceBase service)
    {
        // Hand-craft a plausible PRE-firmware-change baseline via the public FromMeasurements factory:
        //  a normal measured-shape calibration whose EW→EOM distance (~22 MB) is an order of magnitude
        //  larger than the drive's actual ~2.5 MB, so recalibration's EW-shift comfortably breaches every
        //  service tolerance and drives FullRecalibrationAdvised.
        //  (Replaces the removed Apriori(marginPercent, remainingAtEwPercent) overload the test abused.)
        const long capacityActual = CalibrationCapacity;            // 64 MB — matches the emulated drive
        const long ewToEomDistance = 22L * 1024 * 1024;             // 22 MB, vs the drive's ~2.5 MB
        const long reportedAtBom = capacityActual;                  // truthful BOM (no boost)

        // Samples as (ActualWritten, ReportedRemaining): BOM, the EW landmark, hard EOM.
        var samples = new List<(long ActualWritten, long ReportedRemaining)>
        {
            (0L,                             reportedAtBom),        // BOM
            (capacityActual - ewToEomDistance, ewToEomDistance),    // EW landmark (42 MB written)
            (capacityActual,                 0L),                   // hard EOM
        };

        (long ActualWritten, long ReportedRemaining) ew =
            (capacityActual - ewToEomDistance, ewToEomDistance);

        return TapeCalibration.FromMeasurements(
            service.DriveProfileKey, reportedAtBom, capacityActual, samples, ew);
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_Recalibrate_Breach_Confirmed_ChainsFullRun()
    {
        var (service, host) = await OpenCalibrationServiceAsync();
        using (service)
        {
            // A completed run leaves the resumable trail on the cartridge.
            var first = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions()));
            Assert.True(first.Success);

            // Script the host to CONFIRM the destructive full re-run.
            host.ConfirmAnswers.Enqueue(true);

            var recal = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions(),
                    Mode: CalibrationMode.Recalibrate, ExistingCalibration: StaleBaseline(service)));

            Assert.Equal(CalibrationMode.Recalibrate, recal.Mode);
            Assert.Equal(RecalibrationVerdict.FullRecalibrationAdvised, recal.RecalibrationVerdict);
            Assert.NotNull(recal.RecalibrationDelta);

            // Confirmed → the service chained a fresh full run; the result is a NEW calibration re-tagged
            //  as a recalibration outcome, the confirm was consumed, and the chain was logged.
            Assert.True(recal.Success, $"Chained full run should succeed: {recal.Message}");
            Assert.NotNull(recal.Calibration);
            Assert.True(host.ContainsMessage("Full recalibration confirmed"));
            Assert.Empty(host.ConfirmAnswers);
        }
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_Recalibrate_Breach_Declined_KeepsReassessed()
    {
        var (service, host) = await OpenCalibrationServiceAsync();
        using (service)
        {
            var first = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions()));
            Assert.True(first.Success);

            // No queued Confirm answer → the host returns its safe default (false): DECLINE the full re-run.
            //  This is the exact non-interactive guard that stops a quiet host launching a destructive run.
            var recal = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(EjectWhenDone: false, Options: FastOptions(),
                    Mode: CalibrationMode.Recalibrate, ExistingCalibration: StaleBaseline(service)));

            Assert.Equal(CalibrationMode.Recalibrate, recal.Mode);
            Assert.Equal(RecalibrationVerdict.FullRecalibrationAdvised, recal.RecalibrationVerdict);

            // Declined → no chained run; the reassessed calibration is kept, and the decline is logged.
            Assert.True(recal.Success);
            Assert.NotNull(recal.Calibration);
            Assert.True(host.ContainsMessage("Full recalibration declined"));
            Assert.False(host.ContainsMessage("Full recalibration confirmed"));
        }
    }
}
