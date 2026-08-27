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

    /// <summary>
    /// Corrupts the LAST checkpoint block on the trail in place, simulating a run torn while writing its
    /// final checkpoint. Navigates to the block the last filemark precedes and overwrites it with random
    /// bytes (no valid signature/CRC), which also truncates any trailing payload — leaving
    /// <c>… FM_{N-1} cp_{N-1} … FM_N [garbage] EOD</c>. The header at BOM is untouched, so a resume still
    /// finds a valid header, then must step back past the garbage (the <c>-2</c> walk) to <c>cp_{N-1}</c>.
    /// </summary>
    private static void CorruptLastCheckpointBlocks(TapeDrive drive, int count = 1)
    {
        Assert.InRange(count, 1, int.MaxValue);
        Assert.True(drive.SetBlockSize(drive.MaximumBlockSize));
        int blk = (int)drive.BlockSize;

        Assert.True(drive.FastforwardToEnd(MediaPartition.Content));

        Assert.True(drive.MoveToNextFilemark(-1),
            $"expected at least one checkpoint filemark on the aborted trail");

        for (int i = 1; ; )
        {
            Assert.True(drive.MoveToNextFilemark(1)); // over the FM → start of the last checkpoint block

            var garbage = new byte[blk];
            new Random(4242).NextBytes(garbage);      // random ⇒ no valid record signature/CRC
            Assert.Equal(blk, drive.WriteDirect(garbage, 0, blk));

            if (++i > count)
                break;

            Assert.True(drive.MoveToNextFilemark(-2),
                $"expected at least {i} checkpoint filemarks on the aborted trail");
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
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

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
        var (driveA, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));
        ITapeCalibration? clean = new TapeCalibrator(driveA) { Options = FastOptions() }.Run();
        Assert.NotNull(clean);

        // Interrupted-then-resumed run on an equivalent cartridge.
        var (driveB, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));
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
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

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
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        // No run has been performed: there is no header on the medium, so nothing to resume.
        ITapeCalibration? resumed = new TapeCalibrator(drive) { Options = FastOptions() }.Resume();
        Assert.Null(resumed);
    }

    [Fact]
    public void Resume_RestoresPriorReserveAndCalibrations()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

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

    [Fact]
    public void Resume_RecoversFromTornLastCheckpoint()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        // Abort mid-body so SEVERAL body checkpoints are on tape (16 checkpoints, aborted at ~50% ⇒ ~8).
        var run = new TapeCalibrator(drive) { Options = FastOptions() };
        Assert.Null(run.Run(new AbortAfterBytes(run, Capacity / 2)));

        // Tear the LAST checkpoint(s). The resume walk must reject it (CRC/signature fail) and step back one
        //  more checkpoint — the n≥2 (-2) path. The fact that this test COMPLETES also proves termination.
        CorruptLastCheckpointBlocks(drive, count: 2); // corrupt 2 last of ~8 checkpoints

        var resumer = new TapeCalibrator(drive) { Options = FastOptions() };
        ITapeCalibration? resumed = resumer.Resume();

        Assert.NotNull(resumed);
        Assert.InRange(resumed!.CapacityActual, (long)(Capacity * 0.98), Capacity);
        Assert.NotNull(resumed.EarlyWarning);
        AssertCurveWellFormed(resumed);
    }

    [Fact]
    public void Resume_OnForeignCartridgeWithRegularData_ReturnsNull()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        // The user's mix-up: a cartridge carrying ordinary filemark-delimited data (backup-like sets) but
        //  NO calibration header at BOM. Resume must reject it cleanly — caught by the header-at-BOM check
        //  in O(1), before any backward walk — returning null rather than misreading foreign blocks.
        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());
        Assert.True(drive.SetBlockSize(drive.MaximumBlockSize));

        int blk = (int)drive.BlockSize;
        var data = new byte[blk];
        new Random(99).NextBytes(data);

        for (int seg = 0; seg < 5; seg++)
        {
            Assert.Equal(blk, drive.WriteDirect(data, 0, blk));
            Assert.True(drive.WriteFilemark(1));
        }

        ITapeCalibration? resumed = new TapeCalibrator(drive) { Options = FastOptions() }.Resume();
        Assert.Null(resumed);
    }

    #endregion

    #region *** Recalibrate (end-to-end) ***

    [Fact]
    public void Recalibrate_AfterCompleteRun_ReassessesTail_WithSmallDelta()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

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
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        // No trail on the medium ⇒ nothing to re-measure from.
        var existing = TapeCalibration.Apriori(drive.DriveProfileKey, Capacity);
        (ITapeCalibration? reassessed, _) = new TapeCalibrator(drive) { Options = FastOptions() }
            .Recalibrate(existing);

        Assert.Null(reassessed);
    }

    [Fact]
    public void Recalibrate_AfterDriveBehaviorChange_ReportsLargeEwShift()
    {
        // Original drive behavior: a wide 8% early-warning zone.
        var (drive, backend) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity, ewZonePercent: 8.0));

        ITapeCalibration? original = new TapeCalibrator(drive) { Options = FastOptions() }.Run();
        Assert.NotNull(original);
        Assert.True(original!.EwToEomDistance > 0);

        // Emulate a firmware update that SHRINKS the EW zone to 2%. ApplyEwProfileToMedia only reassigns
        //  the profile (it does not wipe content), so the resumable trail survives and the tail
        //  re-measurement now sees the new, later early warning. Shrinking (not growing) the zone keeps
        //  the new EW point AHEAD of the resume position, so it is measured cleanly rather than truncated.
        backend.EmulatedEarlyWarning = VirtualTapeEwProfile.EmulatedOverreport(Capacity, ewZonePercent: 2.0);

        (ITapeCalibration? reassessed, TapeRecalibrationDelta delta) =
            new TapeCalibrator(drive) { Options = FastOptions() }.Recalibrate(original!);

        Assert.NotNull(reassessed);
        AssertCurveWellFormed(reassessed!);

        // The EW landmark moved substantially closer to EOM — the calibrator surfaces the behavior change
        //  as a large, verdict-free delta (the service layer, not the calibrator, judges it).
        Assert.True(delta.NewEwToEomDistance < delta.OldEwToEomDistance,
            $"EW→EOM should shrink after the zone shrank: {delta.OldEwToEomDistance} → {delta.NewEwToEomDistance}");
        Assert.True(Math.Abs(delta.EwShiftFraction) > 0.10,
            $"EW shift {delta.EwShiftFraction:P1} should be large after a drive-behavior change");
    }

    #endregion

    #region *** Media inspection (read-only) ***

    [Fact]
    public void InspectMedia_AfterCompleteRun_ReportsResumableAndComplete()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        ITapeCalibration? cal = new TapeCalibrator(drive) { Options = FastOptions() }.Run();
        Assert.NotNull(cal);

        TapeCalibrationMediaInfo? info = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();

        Assert.NotNull(info);
        Assert.True(info!.IsResumable, "a completed run leaves a resumable trail");
        Assert.True(info.AppearsComplete, "a run that reached the tail should read as complete");
        Assert.Equal(drive.DriveProfileKey, info.ProfileKey);
        Assert.NotEqual(Guid.Empty, info.RunId);
        Assert.True(info.CheckpointedBytes > 0);
        Assert.True(info.CheckpointIndex >= 0);
        // Checkpoints are BODY-ONLY (they stop just before the tail), so a complete run reads ≈ 0.95.
        Assert.InRange(info.ProgressFraction, 0.5, 1.0);
    }

    [Fact]
    public void InspectMedia_AfterAbortedRun_ReportsResumableButNotComplete()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        var run = new TapeCalibrator(drive) { Options = FastOptions() };
        Assert.Null(run.Run(new AbortAfterBytes(run, Capacity / 2)));   // interrupted ~halfway

        TapeCalibrationMediaInfo? info = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();

        Assert.NotNull(info);
        Assert.True(info!.IsResumable, "an aborted run past its first checkpoint is resumable");
        Assert.False(info.AppearsComplete, "a mid-body interruption should not read as complete");
        Assert.Equal(drive.DriveProfileKey, info.ProfileKey);
        Assert.InRange(info.ProgressFraction, 0.10, 0.90);   // stopped mid-body, well short of the tail
    }

    [Fact]
    public void InspectMedia_OnBlankCartridge_ReturnsNull()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        // No run performed ⇒ no header at BOM ⇒ nothing to inspect.
        Assert.Null(new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia());
    }

    [Fact]
    public void InspectMedia_OnForeignCartridgeWithRegularData_ReturnsNull()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        // Ordinary filemark-delimited data, but NO calibration header at BOM — a mixed-up cartridge.
        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());
        Assert.True(drive.SetBlockSize(drive.MaximumBlockSize));

        int blk = (int)drive.BlockSize;
        var data = new byte[blk];
        new Random(123).NextBytes(data);
        for (int seg = 0; seg < 4; seg++)
        {
            Assert.Equal(blk, drive.WriteDirect(data, 0, blk));
            Assert.True(drive.WriteFilemark(1));
        }

        Assert.Null(new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia());
    }

    [Fact]
    public void InspectMedia_IsNonDestructive_ResumeStillSucceeds()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        var run = new TapeCalibrator(drive) { Options = FastOptions() };
        Assert.Null(run.Run(new AbortAfterBytes(run, Capacity / 2)));

        // Inspect TWICE — the read must be idempotent and must not consume the trail.
        var inspector = new TapeCalibrator(drive) { Options = FastOptions() };
        TapeCalibrationMediaInfo? info1 = inspector.InspectMedia();
        TapeCalibrationMediaInfo? info2 = inspector.InspectMedia();
        Assert.NotNull(info1);
        Assert.NotNull(info2);
        Assert.Equal(info1!.RunId, info2!.RunId);            // same run identified both times
        Assert.Equal(info1.CheckpointedBytes, info2.CheckpointedBytes);

        // The crucial contract: inspection wrote nothing, so a real Resume still completes.
        ITapeCalibration? resumed = new TapeCalibrator(drive) { Options = FastOptions() }.Resume();
        Assert.NotNull(resumed);
        Assert.InRange(resumed!.CapacityActual, (long)(Capacity * 0.98), Capacity);
        AssertCurveWellFormed(resumed);

        // After a completed resume the SAME run is still identifiable and now reads as complete.
        TapeCalibrationMediaInfo? after = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();
        Assert.NotNull(after);
        Assert.Equal(info1.RunId, after!.RunId);             // RunId preserved across resume
        Assert.True(after.AppearsComplete);
    }

    [Fact]
    public void InspectMedia_DoesNotDisturbLoadedCalibrationsOrReserve()
    {
        var (drive, _) = CreateDrive(VirtualTapeEwProfile.EmulatedOverreport(Capacity));

        // Leave a resumable trail so InspectMedia has a header to read.
        Assert.NotNull(new TapeCalibrator(drive) { Options = FastOptions() }.Run());

        // A pre-existing reserve + loaded calibration the read-only inspect must NOT touch (it uses no
        //  RunGuard, unlike Run/Resume/Recalibrate).
        var preloaded = TapeCalibration.Apriori(drive.DriveProfileKey, Capacity);
        Assert.True(drive.AddCalibration(preloaded));
        const long reserve = 2L * 1024 * 1024;
        Assert.True(drive.SetEarlyWarning(reserve));

        Assert.NotNull(new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia());

        Assert.Equal(reserve, drive.EarlyWarning);
        Assert.Contains(preloaded, drive.Calibrations);
    }

    #endregion
}
