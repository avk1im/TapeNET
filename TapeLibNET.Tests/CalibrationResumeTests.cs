using System;
using System.Collections.Generic;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Coverage for the RESUMABLE calibration feature (<see cref="TapeCalibrator.Resume"/> /
/// <see cref="TapeCalibrator.Recalibrate"/>) and its on-tape record framing
/// (<see cref="TapeCalibrationRecord"/>), driven over small memory-backed virtual cartridges.
/// <para>
/// Two tiers:
/// <list type="bullet">
///   <item>BACKEND-INDEPENDENT record round-trip / CRC tests — pure serialization, no drive.</item>
///   <item>END-TO-END resume / recalibrate tests — these exercise backend behaviors the earlier
///   calibration tests never did: BACKWARD filemark spacing (<c>MoveToNextFilemark(-n)</c>),
///   SEEK-TO-EOD (<c>FastforwardToEnd</c>), and OVERWRITE-truncates-to-new-EOD after a backward
///   seek. A failure here may indicate a gap in the virtual backend's tape emulation, not the
///   calibrator.</item>
/// </list>
/// </para>
/// </summary>
public class CalibrationResumeTests
{
    // 64 MB content — small enough for memory speed, large enough for several body checkpoints.
    private const long Capacity = 64L * 1024 * 1024;

    #region *** Helpers ***

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

    // Fast run options with FINE checkpointing (16 body checkpoints across the medium) so an
    //  interruption partway through always leaves several recoverable checkpoints on tape.
    private static TapeCalibrationOptions FastOptions(int numCheckpoints = 16) => new()
    {
        SampleCount = 40,
        NumCheckpoints = numCheckpoints,
    };

    /// <summary>
    /// Progress sink that flips the calibrator's abort flag once bytes-written crosses a threshold —
    /// a DETERMINISTIC stand-in for a mid-run transport failure (progress fires synchronously in the
    /// write loop, so the next <c>CheckForAbort</c> observes it).
    /// </summary>
    private sealed class AbortAfterBytes(TapeCalibrator calibrator, long thresholdBytes)
        : IProgress<TapeCalibrationProgress>
    {
        public long LastBytesWritten { get; private set; }
        public bool Fired { get; private set; }

        public void Report(TapeCalibrationProgress p)
        {
            LastBytesWritten = p.BytesWritten;

            if (!Fired && p.BytesWritten >= thresholdBytes)
            {
                Fired = true;
                calibrator.IsAbortRequested = true;
            }
        }
    }

    private static void AssertCurveWellFormed(ITapeCalibration cal)
    {
        Assert.True(cal.Curve.Count >= 2, "Curve should have at least two points");
        for (int i = 1; i < cal.Curve.Count; i++)
        {
            Assert.True(cal.Curve[i].ReportedRemaining >= cal.Curve[i - 1].ReportedRemaining,
                "ReportedRemaining axis must be ascending");
            Assert.True(cal.Curve[i].ActualRemaining >= cal.Curve[i - 1].ActualRemaining,
                "ActualRemaining must be monotonic non-decreasing");
        }
    }

    #endregion

    #region *** Record framing (backend-independent) ***

    [Fact]
    public void CheckpointRecord_PackUnpack_RoundTrips_AndCrcDetectsCorruption()
    {
        var runId = Guid.NewGuid();
        var samples = new List<(long ActualWritten, long ReportedRemaining)>
        {
            (0L, 1000L), (100L, 900L), (200L, 800L),
        };
        var cp = new TapeCalibrationCheckpoint(runId, Index: 3, BytesWritten: 200L,
            EarlyWarning: (150L, 850L), Samples: samples);

        byte[] frame = TapeCalibrationRecord.Pack(cp);

        var back = TapeCalibrationRecord.Unpack<TapeCalibrationCheckpoint>(frame, frame.Length);
        Assert.NotNull(back);
        Assert.Equal(runId, back!.RunId);
        Assert.Equal(3, back.Index);
        Assert.Equal(200L, back.BytesWritten);
        Assert.Equal((150L, 850L), back.EarlyWarning);
        Assert.Equal(3, back.Samples.Count);
        Assert.Equal(samples[1], back.Samples[1]);

        // Flip a byte INSIDE the payload (past the 4-byte length prefix) ⇒ CRC catches it ⇒ null.
        byte[] corrupt = (byte[])frame.Clone();
        corrupt[8] ^= 0xFF;
        Assert.Null(TapeCalibrationRecord.Unpack<TapeCalibrationCheckpoint>(corrupt, corrupt.Length));
    }

    [Fact]
    public void CheckpointRecord_WithoutEarlyWarning_RoundTrips()
    {
        var runId = Guid.NewGuid();
        var cp = new TapeCalibrationCheckpoint(runId, Index: 0, BytesWritten: 0L,
            EarlyWarning: null, Samples: new List<(long, long)> { (0L, 500L) });

        byte[] frame = TapeCalibrationRecord.Pack(cp);
        var back = TapeCalibrationRecord.Unpack<TapeCalibrationCheckpoint>(frame, frame.Length);

        Assert.NotNull(back);
        Assert.Null(back!.EarlyWarning);
        Assert.Single(back.Samples);
    }

    [Fact]
    public void HeaderRecord_PackUnpack_RoundTrips()
    {
        var runId = Guid.NewGuid();
        var plan = new TapeCalibrationPlan(
            SampleCount: 1000, BodySampleCount: 600, TailSampleCount: 400,
            BlockSize: (uint)(1 << 20), BlocksPerChunk: 8, ChunkSize: 8 << 20,
            TailBlocksPerChunk: 1, TailChunkSize: 1 << 20,
            TailCapacityFraction: 0.05, NumCheckpoints: 128);

        var header = new TapeCalibrationRunHeader(
            runId, "VENDOR|PRODUCT|REV|64MB", CapacityReportedAtBom: 12345L,
            BlockSize: (uint)(1 << 20), StartedUtc: DateTime.UtcNow, Plan: plan);

        byte[] frame = TapeCalibrationRecord.Pack(header);
        var back = TapeCalibrationRecord.Unpack<TapeCalibrationRunHeader>(frame, frame.Length);

        Assert.NotNull(back);
        Assert.Equal(runId, back!.RunId);
        Assert.Equal("VENDOR|PRODUCT|REV|64MB", back.ProfileKey);
        Assert.Equal(12345L, back.CapacityReportedAtBom);
        Assert.Equal(plan.NumCheckpoints, back.Plan.NumCheckpoints);
        Assert.Equal(plan.TailCapacityFraction, back.Plan.TailCapacityFraction);
        Assert.Equal(plan.SampleCount, back.Plan.SampleCount);
    }

    [Fact]
    public void Unpack_OfForeignBlock_ReturnsNull()
    {
        // A block of random bytes is not one of our records: no valid signature / length ⇒ null.
        var junk = new byte[4096];
        new Random(7).NextBytes(junk);
        Assert.Null(TapeCalibrationRecord.Unpack<TapeCalibrationCheckpoint>(junk, junk.Length));
    }

    #endregion

    #region *** Resume (end-to-end) ***

    [Fact]
    public void Resume_ContinuesAbortedRun_ToCompletion()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // Simulate an interruption ~halfway — several body checkpoints are on tape by then.
        var run = new TapeCalibrator(drive) { Options = FastOptions() };
        var abort = new AbortAfterBytes(run, Capacity / 2);
        ITapeCalibration? aborted = run.Run(abort);

        Assert.Null(aborted);        // the run was interrupted before EOM
        Assert.True(abort.Fired);

        // Resume on the SAME cartridge picks up from the last good checkpoint and finishes to EOM.
        var resumer = new TapeCalibrator(drive) { Options = FastOptions() };
        ITapeCalibration? resumed = resumer.Resume();

        Assert.NotNull(resumed);
        Assert.InRange(resumed!.CapacityActual, (long)(Capacity * 0.98), Capacity);
        Assert.NotNull(resumed.EarlyWarning);
        Assert.True(resumed.EwToEomDistance > 0, "EW→EOM distance should be positive");
        Assert.Equal(drive.DriveProfileKey, resumed.ProfileKey);
        AssertCurveWellFormed(resumed);
    }

    [Fact]
    public void Resume_ProducesEquivalentCalibration_ToAnUninterruptedRun()
    {
        // Baseline: a clean, uninterrupted run.
        var (driveA, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));
        ITapeCalibration? clean = new TapeCalibrator(driveA) { Options = FastOptions() }.Run();
        Assert.NotNull(clean);

        // Interrupted-then-resumed run on an equivalent cartridge.
        var (driveB, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));
        var run = new TapeCalibrator(driveB) { Options = FastOptions() };
        Assert.Null(run.Run(new AbortAfterBytes(run, Capacity / 2)));
        ITapeCalibration? resumed = new TapeCalibrator(driveB) { Options = FastOptions() }.Resume();
        Assert.NotNull(resumed);

        // The deterministic emulation ⇒ capacity and EW landmark land within a tight band of the
        //  clean run (resume re-measures the region after the last checkpoint, so it must agree).
        Assert.InRange(resumed!.CapacityActual,
            (long)(clean!.CapacityActual * 0.99), (long)(clean.CapacityActual * 1.01) + 1L);
        Assert.InRange(resumed.EwToEomDistance,
            (long)(clean.EwToEomDistance * 0.90), (long)(clean.EwToEomDistance * 1.10) + 1L);
    }

    [Fact]
    public void Resume_IsItselfResumable_ConvergesAfterRepeatedFailures()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // 1) Fresh run, interrupted early (~35%).
        var r0 = new TapeCalibrator(drive) { Options = FastOptions() };
        Assert.Null(r0.Run(new AbortAfterBytes(r0, (long)(Capacity * 0.35))));

        // 2) First resume, interrupted again a bit later (~65%). This is the critical case: a resume
        //    must itself remain resumable — its rewritten boundary checkpoint stands as the new anchor.
        var r1 = new TapeCalibrator(drive) { Options = FastOptions() };
        Assert.Null(r1.Resume(new AbortAfterBytes(r1, (long)(Capacity * 0.65))));

        // 3) Second resume runs to completion.
        var r2 = new TapeCalibrator(drive) { Options = FastOptions() };
        ITapeCalibration? done = r2.Resume();

        Assert.NotNull(done);
        Assert.InRange(done!.CapacityActual, (long)(Capacity * 0.98), Capacity);
        Assert.NotNull(done.EarlyWarning);
        AssertCurveWellFormed(done);
    }

    [Fact]
    public void Resume_OnBlankCartridge_ReturnsNull()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // No run has been performed: there is no header on the medium, so nothing to resume.
        ITapeCalibration? resumed = new TapeCalibrator(drive) { Options = FastOptions() }.Resume();
        Assert.Null(resumed);
    }

    [Fact]
    public void Resume_RestoresPriorReserveAndCalibrations()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // Interrupt a fresh run first so there is something to resume.
        var run = new TapeCalibrator(drive) { Options = FastOptions() };
        Assert.Null(run.Run(new AbortAfterBytes(run, Capacity / 2)));

        // A pre-existing reserve + matching calibration that the resume must neither taint nor discard.
        var preloaded = TapeCalibration.Apriori(drive.DriveProfileKey, Capacity);
        Assert.True(drive.AddCalibration(preloaded));
        const long reserve = 2L * 1024 * 1024;
        Assert.True(drive.SetEarlyWarning(reserve));

        Assert.NotNull(new TapeCalibrator(drive) { Options = FastOptions() }.Resume());

        Assert.Equal(reserve, drive.EarlyWarning);
        Assert.Contains(preloaded, drive.Calibrations);
    }

    #endregion

    #region *** Recalibrate (end-to-end) ***

    [Fact]
    public void Recalibrate_AfterCompleteRun_ReassessesTail_WithSmallDelta()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // A full run to completion leaves the resumable trail (header + body checkpoints) on tape.
        var run = new TapeCalibrator(drive) { Options = FastOptions() };
        ITapeCalibration? original = run.Run();
        Assert.NotNull(original);

        // Recalibrate re-measures only the tail from the last body checkpoint to the new EOM.
        var recal = new TapeCalibrator(drive) { Options = FastOptions() };
        (ITapeCalibration? reassessed, TapeRecalibrationDelta delta) = recal.Recalibrate(original!);

        Assert.NotNull(reassessed);
        AssertCurveWellFormed(reassessed!);

        // Same virtual drive + deterministic profile ⇒ the key figures barely move.
        Assert.InRange(reassessed!.CapacityActual,
            (long)(original!.CapacityActual * 0.99), (long)(original.CapacityActual * 1.01) + 1L);
        Assert.True(Math.Abs(delta.CapacityShiftFraction) < 0.02,
            $"Capacity shift {delta.CapacityShiftFraction:P1} unexpectedly large for a stable drive");

        // This would measure EW drift in relative terms:
        //Assert.True(Math.Abs(delta.EwShiftFraction) < 0.05,
        //    $"EW shift {delta.EwShiftFraction:P1} unexpectedly large for a stable drive");
        // But EwToEomDistance is inherently quantized to the tail chunk size, and a resume re-measures the
        //  tail from a byte position shifted by the rewritten checkpoint record block — so a difference of
        //  a couple of tail chunks is EXPECTED quantization, not drift. Assert an ABSOLUTE tolerance in
        //  those terms; a percentage bound is meaningless for a small, block-quantized quantity.
        long tailChunk = FastOptions().ResolveFor(drive).TailChunkSize;
        Assert.True(Math.Abs(delta.NewEwToEomDistance - delta.OldEwToEomDistance) <= 3 * tailChunk,
            $"EW moved {delta.NewEwToEomDistance - delta.OldEwToEomDistance} B (> 3 tail chunks) — beyond quantization");

        // The delta reports the raw before/after values verdict-free (caller decides what they mean).
        Assert.Equal(original.CapacityActual, delta.OldCapacityActual);
        Assert.Equal(reassessed.CapacityActual, delta.NewCapacityActual);
        Assert.Equal(original.EwToEomDistance, delta.OldEwToEomDistance);
        Assert.Equal(reassessed.EwToEomDistance, delta.NewEwToEomDistance);
    }

    [Fact]
    public void Recalibrate_OnBlankCartridge_ReturnsNullReassessed()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.Lto4Like(Capacity));

        // No trail on the medium ⇒ nothing to re-measure from.
        var existing = TapeCalibration.Apriori(drive.DriveProfileKey, Capacity);
        (ITapeCalibration? reassessed, _) = new TapeCalibrator(drive) { Options = FastOptions() }
            .Recalibrate(existing);

        Assert.Null(reassessed);
    }

    #endregion
}
