using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Calibration;
using TapeLibNET.Format;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Xunit.Abstractions;

namespace TapeLibNET.Tests;

/// <summary>
/// Phase 6 (Design-Format-v2 §5.4, §5.5, §6.5): the calibration run header and checkpoint in format 2.1, reading their
///  legacy forms, identification, the checkpoint size gain, and resuming a run whose trail mixes both formats.
/// </summary>
public class CalibrationFormatTests(ITestOutputHelper output)
{
    private const long Capacity = 64L * 1024 * 1024;
    private static readonly DateTime T0 = new(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc);

    #region *** Helpers ***

    private static TapeCalibrationPlan Plan() => new(
        SampleCount: 1000, BodySampleCount: 600, TailSampleCount: 400,
        BlockSize: 1u << 20, BlocksPerChunk: 8, ChunkSize: 8 << 20,
        TailBlocksPerChunk: 1, TailChunkSize: 1 << 20,
        TailCapacityFraction: 0.05, NumCheckpoints: 128);

    private static TapeCalibrationHeader Header() => TapeCalibrationHeader.CreateHeader(
        Guid.NewGuid(), "VENDOR|PRODUCT|REV|64MB", 12345L, 1u << 20, T0, Plan());

    // A realistic sample trail: ascending bytes written at a body cadence, reported remaining falling
    private static List<(long ActualWritten, long ReportedRemaining)> Samples(int count, long step)
    {
        var list = new List<(long, long)>(count);
        long capacity = step * (count + 10);
        for (int i = 0; i < count; i++)
            list.Add((i * step, capacity - i * step - i * 37L));
        return list;
    }

    private static LegacyCalibrationPlan LegacyPlan(TapeCalibrationPlan p) => new(
        p.SampleCount, p.BodySampleCount, p.TailSampleCount, p.BlockSize, p.BlocksPerChunk, p.ChunkSize,
        p.TailBlocksPerChunk, p.TailChunkSize, p.TailCapacityFraction, p.NumCheckpoints);

    private static byte[] LegacyCheckpointFrame(TapeCalibrationCheckpoint cp)
        => LegacyFormatWriter.Frame(LegacyFormatWriter.CheckpointPayload(
            cp.RunId, cp.Index, cp.BytesWritten, cp.EarlyWarning, cp.Samples));

    private static (TapeDrive Drive, VirtualTapeDriveBackend Backend) CreateDrive()
    {
        var backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            TestLoggerFactory.Default,
            VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
            contentCapacity: Capacity,
            initiatorPartitionCapacity: 0);
        backend.IoRate = VirtualTapeDriveIoRate.Unlimited;
        backend.EmulatedEarlyWarning = VirtualTapeEwProfile.EmulatedOverreport(Capacity);
        var drive = new TapeDrive(TestLoggerFactory.Default, backend);
        Assert.True(drive.ReopenDrive(0), "Failed to open virtual drive");
        Assert.True(drive.ReloadMedia(), "Failed to load virtual media");
        Assert.True(drive.PrepareMedia(), "Failed to prepare virtual media");
        return (drive, backend);
    }

    private static TapeCalibrationOptions FastOptions() => new() { SampleCount = 40, NumCheckpoints = 16 };

    private sealed class AbortAfterBytes(TapeCalibrator calibrator, long thresholdBytes) : IProgress<TapeCalibrationProgress>
    {
        public void Report(TapeCalibrationProgress p)
        {
            if (p.BytesWritten >= thresholdBytes)
                calibrator.IsAbortRequested = true;
        }
    }

    #endregion

    #region *** Run header ***

    [Fact]
    public void Header_IsWrittenInFormat21_AndRoundTrips()
    {
        TapeCalibrationHeader original = Header();
        byte[] frame = TapeCalibrationFramer.Pack(original);

        Assert.True(TapeFormat.IsV2(frame));
        Assert.Equal((ushort)TapeRecordKind.CalibrationRunHeader, BitConverter.ToUInt16(frame, 4));

        TapeCalibrationHeader? back = TapeCalibrationFramer.UnpackHeader(frame, frame.Length);
        Assert.NotNull(back);
        Assert.Equal(original, back);
        Assert.Equal(DateTimeKind.Utc, back!.StartedUtc.Kind);
    }

    [Fact]
    public void Header_Legacy_StillReads_WithUtcTime()
    {
        TapeCalibrationHeader original = Header();
        byte[] frame = LegacyFormatWriter.Frame(LegacyFormatWriter.CalibrationHeaderPayload(
            original.RunId, original.StartedUtc, original.RunBlockSize, original.ProfileKey,
            original.CapacityReportedAtBom, LegacyPlan(original.Plan)));

        TapeCalibrationHeader? back = TapeCalibrationFramer.UnpackHeader(frame, frame.Length);
        Assert.NotNull(back);
        Assert.Equal(original, back);                    // calibration ticks were UTC: no conversion, exact match
    }

    [Fact]
    public void Header_InRunBlock_WithRandomPadding_Reads()
    {
        // The run-block shape (drives without a standard header block): random padding behind the frame
        byte[] frame = TapeCalibrationFramer.Pack(Header());
        var block = new byte[1 << 20];
        new Random(5).NextBytes(block);
        frame.CopyTo(block, 0);
        Assert.NotNull(TapeCalibrationFramer.UnpackHeader(block, block.Length));
    }

    #endregion

    #region *** Checkpoint ***

    [Fact]
    public void Checkpoint_IsWrittenInFormat21_AndRoundTrips()
    {
        var cp = new TapeCalibrationCheckpoint(Guid.NewGuid(), 7, 123_456_789L, (100_000_000L, 5_000_000L),
            Samples(200, 512 * 1024));
        byte[] frame = TapeCalibrationFramer.Pack(cp);

        Assert.True(TapeFormat.IsV2(frame));
        Assert.Equal((ushort)TapeRecordKind.CalibrationCheckpoint, BitConverter.ToUInt16(frame, 4));

        TapeCalibrationCheckpoint? back = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(frame, frame.Length);
        Assert.NotNull(back);
        Assert.Equal(cp.RunId, back!.RunId);
        Assert.Equal(cp.Index, back.Index);
        Assert.Equal(cp.BytesWritten, back.BytesWritten);
        Assert.Equal(cp.EarlyWarning, back.EarlyWarning);
        Assert.Equal(cp.Samples, back.Samples);
    }

    /// <summary>LTO-3 collapses reported remaining to 0 exactly at EW: that EW point must survive default elision.</summary>
    [Fact]
    public void Checkpoint_EarlyWarningWithZeroReported_RoundTrips()
    {
        var cp = new TapeCalibrationCheckpoint(Guid.NewGuid(), 1, 1000L, (900L, 0L), Samples(3, 300));
        byte[] frame = TapeCalibrationFramer.Pack(cp);
        TapeCalibrationCheckpoint? back = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(frame, frame.Length);
        Assert.NotNull(back);
        Assert.Equal((900L, 0L), back!.EarlyWarning);
    }

    [Fact]
    public void Checkpoint_WithoutEarlyWarning_RoundTripsAsNull()
    {
        var cp = new TapeCalibrationCheckpoint(Guid.NewGuid(), 0, 0L, null, Samples(1, 1));
        byte[] frame = TapeCalibrationFramer.Pack(cp);
        TapeCalibrationCheckpoint? back = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(frame, frame.Length);
        Assert.NotNull(back);
        Assert.Null(back!.EarlyWarning);
    }

    [Fact]
    public void Checkpoint_Legacy_StillReads()
    {
        var cp = new TapeCalibrationCheckpoint(Guid.NewGuid(), 4, 4_000_000L, (3_000_000L, 77L), Samples(50, 65536));
        byte[] frame = LegacyCheckpointFrame(cp);
        Assert.False(TapeFormat.IsV2(frame));

        TapeCalibrationCheckpoint? back = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(frame, frame.Length);
        Assert.NotNull(back);
        Assert.Equal(cp.RunId, back!.RunId);
        Assert.Equal(cp.EarlyWarning, back.EarlyWarning);
        Assert.Equal(cp.Samples, back.Samples);
    }

    [Fact]
    public void Checkpoint_Torn_IsNull()
    {
        byte[] frame = TapeCalibrationFramer.Pack(
            new TapeCalibrationCheckpoint(Guid.NewGuid(), 2, 2000L, null, Samples(20, 100)));
        frame[frame.Length / 2] ^= 0xFF;
        Assert.Null(TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(frame, frame.Length));
    }

    [Fact]
    public void Checkpoint_IsNotAHeader_AndAHeaderIsNotACheckpoint()
    {
        byte[] cpFrame = TapeCalibrationFramer.Pack(new TapeCalibrationCheckpoint(Guid.NewGuid(), 0, 0L, null, Samples(1, 1)));
        byte[] hdFrame = TapeCalibrationFramer.Pack(Header());
        Assert.Null(TapeCalibrationFramer.UnpackHeader(cpFrame, cpFrame.Length));
        Assert.Null(TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(hdFrame, hdFrame.Length));
    }

    /// <summary>
    /// Delta coding keeps a checkpoint smaller than its legacy form, so it fits its run block for longer — beyond that
    ///  size <c>RecordBlockWriter</c> stops checkpointing and the run is no longer resumable. Measured, not assumed: the
    ///  gain depends on the sample cadence (large deltas on large media take more varint bytes).
    /// </summary>
    [Theory]
    [InlineData(1_000, 1L << 20)]              // virtual-media cadence (~1 MiB)
    [InlineData(1_000, 30L << 30)]             // LTO-9 body cadence (~30 GiB)
    public void Checkpoint_SmallerThanLegacy(int count, long step)
    {
        var cp = new TapeCalibrationCheckpoint(Guid.NewGuid(), 99, step * count, (step * (count - 5), 12345L),
            Samples(count, step));

        int v21 = TapeCalibrationFramer.Pack(cp).Length;
        int legacy = LegacyCheckpointFrame(cp).Length;

        output.WriteLine($"{count} samples at {step:N0} B: legacy {legacy:N0} B, 2.1 {v21:N0} B ({(double)legacy / v21:F2}× smaller)");
        Assert.True(v21 < legacy, $"2.1 checkpoint ({v21:N0} B) not smaller than legacy ({legacy:N0} B)");
    }

    #endregion

    #region *** Identification ***

    [Fact]
    public void Identify_21Checkpoint_IsCalibrationCheckpoint()
    {
        byte[] frame = TapeCalibrationFramer.Pack(new TapeCalibrationCheckpoint(Guid.NewGuid(), 0, 0L, null, Samples(5, 10)));
        var block = new byte[1 << 16];
        frame.CopyTo(block, 0);
        Assert.Equal(HeaderBlockIdentity.CalibrationCheckpoint, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);

        block[12] ^= 0x5A;                         // damaged behind the magic
        IdentifiedBlock damaged = TapeHeaderBlock.IdentifyBlock(block, block.Length);
        Assert.Equal(HeaderBlockIdentity.DamagedRecord, damaged.Kind);
    }

    [Fact]
    public void Identify_21CalibrationHeader_IsHeader()
    {
        byte[] block = TapeHeaderBlock.Frame(Header())!;
        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);
        Assert.Equal(HeaderBlockIdentity.Header, id.Kind);
        Assert.IsType<TapeCalibrationHeader>(id.Header);
    }

    #endregion

    #region *** Mixed trail (Format_Calibration_ResumeLegacyRun) ***

    /// <summary>
    /// A run interrupted mid-body whose LAST checkpoint is a legacy frame — the state a legacy run leaves behind. Resume
    ///  must read it, rewrite the boundary checkpoint in 2.1, and finish; the trail then mixes both formats.
    /// </summary>
    [Fact]
    public void Resume_FromLegacyCheckpoint_CompletesWithMixedTrail()
    {
        var (drive, _) = CreateDrive();

        var run = new TapeCalibrator(drive) { Options = FastOptions() };
        Assert.Null(run.Run(new AbortAfterBytes(run, Capacity / 2)));

        // What the trail holds now: the last 2.1 checkpoint
        TapeCalibrationMediaInfo? before = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();
        Assert.NotNull(before);
        TapeCalibrationCheckpoint last = Assert.IsType<TapeCalibrationCheckpoint>(before!.LastCheckpoint);

        // Replace that block by the same checkpoint in its LEGACY frame (the write truncates the trailing payload)
        Assert.True(drive.SetBlockSize(before.Header.RunBlockSize));
        int blk = (int)drive.BlockSize;
        Assert.True(drive.FastforwardToEnd(MediaPartition.Content));
        Assert.True(drive.MoveToNextFilemark(-1));
        Assert.True(drive.MoveToNextFilemark(1));
        byte[] legacyBlock = LegacyFormatWriter.PadToBlock(LegacyCheckpointFrame(last), blk);
        Assert.Equal(blk, drive.WriteDirect(legacyBlock, 0, blk));

        // The legacy checkpoint is what Inspect finds now
        TapeCalibrationMediaInfo? legacy = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();
        Assert.NotNull(legacy);
        Assert.Equal(last.Index, legacy!.CheckpointIndex);
        Assert.Equal(last.Samples, legacy.LastCheckpoint!.Samples);

        // Resume continues from it, writing 2.1 from here on
        ITapeCalibration? resumed = new TapeCalibrator(drive) { Options = FastOptions() }.Resume();
        Assert.NotNull(resumed);
        Assert.InRange(resumed!.CapacityActual, (long)(Capacity * 0.98), Capacity);
        Assert.NotNull(resumed.EarlyWarning);

        TapeCalibrationMediaInfo? after = new TapeCalibrator(drive) { Options = FastOptions() }.InspectMedia();
        Assert.NotNull(after);
        Assert.Equal(before.RunId, after!.RunId);
        Assert.True(after.AppearsComplete);
    }

    #endregion
}
