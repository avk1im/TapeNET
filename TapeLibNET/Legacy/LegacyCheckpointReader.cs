#define LEGACY_TapeCalibrationRunHeader // FIXME: temporary to keep compatibility with legacy calibration cartridges

namespace TapeLibNET.Legacy;

/// <summary>
/// Frozen, read-only reader of the pre-2.1 calibration records: the checkpoint body and the (older)
///  stand-alone run header.
/// </summary>
internal static class LegacyCheckpointReader
{
    /// <summary>Reads a cumulative checkpoint; null when the signature or version does not match.</summary>
    public static TapeCalibrationCheckpoint? ReadCheckpoint(LegacyDeserializer d)
    {
        if (!d.ValidateSignature())
            return null;

        var runId = new Guid(d.DeserializeBytes(16) ?? throw new FormatException("RunId"));
        int index = d.DeserializeInt32();
        long bytesWritten = d.DeserializeInt64();

        (long, long)? ew = null;
        if (d.DeserializeBoolean())
            ew = (d.DeserializeInt64(), d.DeserializeInt64());

        int count = d.DeserializeInt32();
        var samples = new List<(long ActualWritten, long ReportedRemaining)>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
            samples.Add((d.DeserializeInt64(), d.DeserializeInt64()));

        return new TapeCalibrationCheckpoint(runId, index, bytesWritten, ew, samples);
    }

#if LEGACY_TapeCalibrationRunHeader
    /// <summary>Reads the legacy stand-alone run header; null when the signature or version does not match.</summary>
    public static TapeCalibrationRunHeader? ReadRunHeader(LegacyDeserializer d)
    {
        if (!d.ValidateSignature())
            return null;                             // wrong signature/version => not our record

        var runId = new Guid(d.DeserializeBytes(16) ?? throw new FormatException("RunId"));
        string profileKey = d.DeserializeString();
        long capacity = d.DeserializeInt64();
        uint blockSize = d.DeserializeUInt32();
        DateTime started = LegacyTime.FromUtc(d.DeserializeDateTime());   // written from DateTime.UtcNow

        var plan = ReadPlan(d);

        return new TapeCalibrationRunHeader(runId, profileKey, capacity, blockSize, started, plan);
    }
#endif

    /// <summary>Reads the ten-field calibration plan shared by the run header and the unified calibration header.</summary>
    public static TapeCalibrationPlan ReadPlan(LegacyDeserializer d) => new(
        d.DeserializeInt32(),                    // SampleCount
        d.DeserializeInt32(),                    // BodySampleCount
        d.DeserializeInt32(),                    // TailSampleCount
        d.DeserializeUInt32(),                   // BlockSize
        d.DeserializeInt32(),                    // BlocksPerChunk
        d.DeserializeInt32(),                    // ChunkSize
        d.DeserializeInt32(),                    // TailBlocksPerChunk
        d.DeserializeInt32(),                    // TailChunkSize
        d.DeserializeDouble(),                   // TailCapacityFraction
        d.DeserializeInt32());                   // NumCheckpoints
}
