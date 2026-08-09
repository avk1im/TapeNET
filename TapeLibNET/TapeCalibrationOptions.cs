namespace TapeLibNET;

/// <summary>
/// Caller intent for a calibration run. The calibrator resolves this against a specific
/// <see cref="TapeDrive"/> into a concrete <see cref="TapeCalibrationPlan"/>. Defaults target a
/// correct, deterministic measurement: the drive's maximum block size, hardware compression off,
/// and ~<see cref="SampleCount"/> curve points spread across the medium.
/// </summary>
public readonly record struct TapeCalibrationOptions
{
    /// <summary>Approximate number of <c>ReportedRemaining → ActualRemaining</c> curve points to record. Default 100.</summary>
    public int SampleCount { get; init; }

    /// <summary>Payload size per <c>WriteDirect</c> call, in blocks. ≤ 0 falls back to <see cref="DefaultBlocksPerChunk"/>.</summary>
    public int BlocksPerChunk { get; init; }

    /// <summary>Default value for <see cref="BlocksPerChunk"/>.</summary>
    public const int DefaultBlocksPerChunk = 8;

    public TapeCalibrationOptions()
    {
        SampleCount = 100;
        BlocksPerChunk = DefaultBlocksPerChunk;
    }

    /// <summary>Turn caller intent into a concrete, always-valid plan for this drive.</summary>
    public TapeCalibrationPlan ResolveFor(TapeDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        int blocksPerChunk = BlocksPerChunk > 0 ? BlocksPerChunk : DefaultBlocksPerChunk;
        uint blockSize = drive.MaximumBlockSize;
        var plan = TapeCalibrationPlan.Create(SampleCount, blockSize, blocksPerChunk);

        // Check if the ChunkSize isn't too coarse to reach SampleCount
        long sampleStep = drive.ContentCapacity / plan.SampleCount;
        if (plan.ChunkSize <= sampleStep)
            return plan; // if yes, we're good to go

        // If not, first try to reduce BlocksPerChunk
        blocksPerChunk = (int)(sampleStep / blockSize);
        if (blocksPerChunk <= 0)
        {
            // if still too coarse, reduce BlockSize to the drive's default
            blockSize = drive.DefaultBlockSize;
            blocksPerChunk = Math.Max(1, (int)(sampleStep / blockSize)); // we won't reduce any further
        }

        plan = TapeCalibrationPlan.Create(SampleCount, blockSize, blocksPerChunk);

        return plan;
    }
}

/// <summary>
/// Fully resolved run parameters — everything the calibrator needs to configure the drive, nothing
/// more. Every field is concrete; <see cref="ChunkSize"/> is always valid (no divide-by-zero path).
/// </summary>
public readonly record struct TapeCalibrationPlan(
    int SampleCount,
    uint BlockSize,
    int BlocksPerChunk,
    int ChunkSize)
{
    /// <summary>Build a plan, deriving <see cref="ChunkSize"/> and clamping <see cref="SampleCount"/> to ≥ 1.</summary>
    internal static TapeCalibrationPlan Create(int sampleCount, uint blockSize, int blocksPerChunk)
    {
        int chunkSize = checked((int)(blocksPerChunk * (long)blockSize));
        return new TapeCalibrationPlan(Math.Max(1, sampleCount), blockSize, blocksPerChunk, chunkSize);
    }
}