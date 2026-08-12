using System;

namespace TapeLibNET;

/// <summary>
/// Caller intent for a calibration run. The calibrator resolves this against a specific
/// <see cref="TapeDrive"/> into a concrete <see cref="TapeCalibrationPlan"/>. Defaults target a
/// correct, deterministic measurement: the drive's maximum block size, hardware compression off,
/// and ~<see cref="SampleCount"/> curve points spread across the medium, with a reserved fraction
/// of that budget spent on a FINE-GRAINED tail (the EW → EOM region, where accuracy matters most).
/// </summary>
public readonly record struct TapeCalibrationOptions
{
    /// <summary>Approximate number of <c>ReportedRemaining → ActualRemaining</c> curve points to record.
    /// Default 1,000 proved good resolution for LTO drives — but its EW→EOM tail needs more (see below).</summary>
    public int SampleCount { get; init; }

    /// <summary>Payload size per <c>WriteDirect</c> call, in blocks (BODY phase). ≤ 0 falls back to <see cref="DefaultBlocksPerChunk"/>.</summary>
    public int BlocksPerChunk { get; init; }

    /// <summary>
    /// Fraction of <see cref="SampleCount"/> reserved for the fine-grained TAIL phase (the EW → EOM
    /// region). Real LTO runs showed the default 100/1,000 uniform points far too coarse for that
    /// last stretch — LTO-3 in particular collapses its reported figure right at EW — so we spend a
    /// dedicated slice of the budget there, at a proportionally finer chunk. Default 0.20 (20%).
    /// </summary>
    public double TailSampleFraction { get; init; }

    /// <summary>
    /// The tail begins at whichever comes FIRST while writing toward EOM: the drive's physical early
    /// warning, OR the last <see cref="TailCapacityFraction"/> of capacity. The capacity trigger
    /// guarantees a fine tail even when EW fires extremely late (LTO-3: ~0.1% before EOM). Default 0.05 (5%).
    /// </summary>
    public double TailCapacityFraction { get; init; }

    /// <summary>Default value for <see cref="BlocksPerChunk"/>.</summary>
    public const int DefaultBlocksPerChunk = 8;

    /// <summary>Default value for <see cref="TailSampleFraction"/> — 20% of the sample budget goes to the tail.</summary>
    public const double DefaultTailSampleFraction = 0.20;

    /// <summary>Default value for <see cref="TailCapacityFraction"/> — the tail is the last 5% of capacity (or EW, whichever first).</summary>
    public const double DefaultTailCapacityFraction = 0.05;

    public TapeCalibrationOptions()
    {
        SampleCount = 1_000;                                // 1,000 proved good resolution for LTO drives
        BlocksPerChunk = DefaultBlocksPerChunk;
        TailSampleFraction = DefaultTailSampleFraction;     // reserve 20% of the budget for the EW→EOM tail
        TailCapacityFraction = DefaultTailCapacityFraction; // tail = last 5% of capacity (or EW, whichever first)
    }

    /// <summary>Turn caller intent into a concrete, always-valid plan for this drive.</summary>
    public TapeCalibrationPlan ResolveFor(TapeDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);

        long capacity = Math.Max(1L, drive.ContentCapacity);

        int sampleCount = Math.Max(1, SampleCount);
        double tailSampleFraction = Math.Clamp(TailSampleFraction, 0.0, 0.9);
        double tailCapacityFraction = Math.Clamp(TailCapacityFraction, 0.0, 0.9);

        // Split the sample budget: a reserved slice for the fine tail, the rest for the body.
        int tailSampleCount = Math.Max(1, (int)(sampleCount * tailSampleFraction));
        int bodySampleCount = Math.Max(1, sampleCount - tailSampleCount);

        int blocksPerChunk = BlocksPerChunk > 0 ? BlocksPerChunk : DefaultBlocksPerChunk;
        uint blockSize = drive.MaximumBlockSize;

        // --- BODY chunk sizing: keep the chunk fine enough to reach bodySampleCount across the body zone
        //      (the first (1 - tailCapacityFraction) of capacity). ---
        long bodyZone = Math.Max(1L, (long)(capacity * (1.0 - tailCapacityFraction)));
        long bodyStep = Math.Max(1L, bodyZone / bodySampleCount);

        if ((long)blocksPerChunk * blockSize > bodyStep)
        {
            // Too coarse to reach bodySampleCount: first try to reduce BlocksPerChunk
            blocksPerChunk = (int)(bodyStep / blockSize);

            if (blocksPerChunk <= 0)
            {
                // if still too coarse, reduce BlockSize to the drive's default (we won't reduce any further)
                blockSize = drive.DefaultBlockSize > 0 ? drive.DefaultBlockSize : blockSize;
                blocksPerChunk = Math.Max(1, (int)(bodyStep / blockSize));
            }
        }
        blocksPerChunk = Math.Max(1, blocksPerChunk);

        // --- TAIL chunk sizing: finer, so tailSampleCount samples span the last tailCapacityFraction.
        //      For small (virtual) media the tail step floors to a single block, as for body. ---
        long tailZone = Math.Max(1L, (long)(capacity * tailCapacityFraction));
        long tailStep = Math.Max(1L, tailZone / tailSampleCount);

        int tailBlocksPerChunk = Math.Max(1, (int)(tailStep / blockSize));
        // The tail must be at least as fine as the body — never coarser.
        tailBlocksPerChunk = Math.Min(tailBlocksPerChunk, blocksPerChunk);

        return TapeCalibrationPlan.Create(
            sampleCount, bodySampleCount, tailSampleCount,
            blockSize, blocksPerChunk, tailBlocksPerChunk, tailCapacityFraction);
    }
}

/// <summary>
/// Fully resolved run parameters — everything the calibrator needs to configure the drive, nothing
/// more. Every field is concrete; <see cref="ChunkSize"/> and <see cref="TailChunkSize"/> are always
/// valid (no divide-by-zero path). The run has two phases: a coarse BODY (chunk <see cref="ChunkSize"/>,
/// interval <see cref="BodySampleInterval"/>) and a fine TAIL (chunk <see cref="TailChunkSize"/>,
/// interval <see cref="TailSampleInterval"/>) that begins at EW or the last <see cref="TailCapacityFraction"/>.
/// </summary>
public readonly record struct TapeCalibrationPlan(
    int SampleCount,
    int BodySampleCount,
    int TailSampleCount,
    uint BlockSize,
    int BlocksPerChunk,
    int ChunkSize,
    int TailBlocksPerChunk,
    int TailChunkSize,
    double TailCapacityFraction)
{
    /// <summary>Build a plan, deriving the two chunk sizes and clamping the counts to ≥ 1.</summary>
    internal static TapeCalibrationPlan Create(
        int sampleCount, int bodySampleCount, int tailSampleCount,
        uint blockSize, int blocksPerChunk, int tailBlocksPerChunk, double tailCapacityFraction)
    {
        int chunkSize = checked((int)(blocksPerChunk * (long)blockSize));
        int tailChunkSize = checked((int)(tailBlocksPerChunk * (long)blockSize));

        return new TapeCalibrationPlan(
            Math.Max(1, sampleCount),
            Math.Max(1, bodySampleCount),
            Math.Max(1, tailSampleCount),
            blockSize,
            Math.Max(1, blocksPerChunk), chunkSize,
            Math.Max(1, tailBlocksPerChunk), tailChunkSize,
            tailCapacityFraction);
    }

    /// <summary>
    /// Re-derives the two chunk sizes for a block size the drive actually accepted (it may round the
    /// requested max to its own granularity), keeping the blocks-per-chunk and sample counts intact.
    /// </summary>
    internal TapeCalibrationPlan WithBlockSize(uint newBlockSize)
    {
        int chunkSize = checked((int)(BlocksPerChunk * (long)newBlockSize));
        int tailChunkSize = checked((int)(TailBlocksPerChunk * (long)newBlockSize));

        return this with { BlockSize = newBlockSize, ChunkSize = chunkSize, TailChunkSize = tailChunkSize };
    }

    /// <summary>Bytes-written mark at which the tail phase begins (the last <see cref="TailCapacityFraction"/>
    ///  of <paramref name="capacity"/>). The calibrator ALSO enters the tail early if physical EW fires first.</summary>
    public long TailStartBytes(long capacity)
        => (long)(capacity * (1.0 - TailCapacityFraction));

    /// <summary>Sample cadence for the body phase: never finer than one body chunk, ~<see cref="BodySampleCount"/>
    ///  points across the body zone.</summary>
    public long BodySampleInterval(long capacity)
        => Math.Max(ChunkSize, (long)(capacity * (1.0 - TailCapacityFraction)) / Math.Max(1, BodySampleCount));

    /// <summary>Sample cadence for the tail phase: never finer than one tail chunk, ~<see cref="TailSampleCount"/>
    ///  points across the last <see cref="TailCapacityFraction"/> of the medium.</summary>
    public long TailSampleInterval(long capacity)
        => Math.Max(TailChunkSize, (long)(capacity * TailCapacityFraction) / Math.Max(1, TailSampleCount));
}
