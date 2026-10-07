using TapeLibNET.Calibration;

namespace TapeLibNET.Legacy;


/// <summary>
/// Frozen, read-only reader of the pre-2.1 calibration records: the checkpoint body and the run plan shared with the
///  legacy calibration header. The oldest stand-alone run header (<c>TapeCalibrationRunHeader</c>) is no longer read —
///  no such cartridges remain in the field (Design-Format-v2 §6.5).
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
        if (count < 0)
            throw new FormatException($"Invalid sample count {count}");
        var samples = new List<(long ActualWritten, long ReportedRemaining)>(Math.Min(count, 64 * 1024));  // never trust the count
        for (int i = 0; i < count; i++)
            samples.Add((d.DeserializeInt64(), d.DeserializeInt64()));
        return new TapeCalibrationCheckpoint(runId, index, bytesWritten, ew, samples);
    }

    /// <summary>Reads the ten-field calibration plan of the legacy calibration header.</summary>
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
