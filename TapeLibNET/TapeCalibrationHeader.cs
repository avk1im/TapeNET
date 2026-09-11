using System;

namespace TapeLibNET;

/// <summary>
/// Calibration run header written once at BOM of a scratch cartridge — the calibration kind of the
///  unified <see cref="TapeHeader"/> hierarchy (renamed from the former <c>TapeCalibrationRunHeader</c>).
/// </summary>
/// <remarks>
/// <para>
/// Self-identifies the run and cartridge so <see cref="TapeCalibrator.Resume"/> can verify "same run"
///  (<see cref="RunId"/> consistency) before trusting any checkpoint, and so a returned cartridge is
///  inspectable ("what run / drive / when does this hold?"). Profile MATCHING against the current
///  drive is deliberately NOT done here — that is the caller's / service layer's responsibility.
/// </para>
/// <para>
/// Sharing the <see cref="TapeHeader"/> base means one BOM read now classifies a cartridge as media,
///  set, or calibration: a backup load that meets a calibration cartridge learns so from the kind
///  byte instead of fruitlessly seeking a TOC. Only the record grammar is shared — the calibration
///  header still rides in the run's own block via the calibrator's <c>RecordBlockWriter</c>, NOT the
///  fixed 16 KiB header block, so <see cref="TapeHeader.BlockSize"/> carries the run block size.
/// </para>
/// </remarks>
public sealed record TapeCalibrationHeader : TapeHeader
{
    /// <inheritdoc/>
    public override TapeHeaderKind Kind => TapeHeaderKind.Calibration;

    /// <summary>The run's unique id — the base <see cref="TapeHeader.Id"/> under its calibration name.</summary>
    public Guid RunId => Id;

    /// <summary>The run's effective block size (NOT the fixed header block).</summary>
    public uint RunBlockSize => BlockSize;

    /// <summary>When the run started (UTC) — the base <see cref="TapeHeader.CreatedUtc"/> under its calibration name.</summary>
    public DateTime StartedUtc => CreatedUtc;

    /// <summary>The drive+media profile key the run was recorded against.</summary>
    public string ProfileKey { get; init; } = string.Empty;

    /// <summary>Driver-reported remaining sampled at BOM at the start of the run.</summary>
    public long CapacityReportedAtBom { get; init; }

    /// <summary>The resolved run plan — enough to resume with an identical cadence/chunking, without re-resolving.</summary>
    public TapeCalibrationPlan Plan { get; init; }

    /// <summary>
    /// Builds the run header, mirroring <see cref="TapeTOC.CreateHeader"/> so a header is always
    ///  assembled through one factory and cannot silently diverge from the record's field layout.
    /// </summary>
    /// <param name="runId">The run's unique id.</param>
    /// <param name="profileKey">The drive+media profile key (usually <see cref="TapeDrive.DriveProfileKey"/>).</param>
    /// <param name="capacityReportedAtBom">Driver-reported remaining at BOM.</param>
    /// <param name="blockSize">The run's effective block size (NOT the fixed header block).</param>
    /// <param name="startedUtc">Run start time, UTC.</param>
    /// <param name="plan">The resolved calibration plan.</param>
    public static TapeCalibrationHeader CreateHeader(
        Guid runId, string profileKey, long capacityReportedAtBom, uint blockSize,
        DateTime startedUtc, TapeCalibrationPlan plan) =>
        new()
        {
            Id                    = runId,          // protected base member — settable from within the hierarchy
            CreatedUtc            = startedUtc,
            BlockSize             = blockSize,
            ProfileKey            = profileKey,
            CapacityReportedAtBom = capacityReportedAtBom,
            Plan                  = plan,
        };

    /// <inheritdoc/>
    public override void SerializeTo(TapeSerializer s)
    {
        SerializePreamble(s);   // signature + Kind + RunId (16 bytes) + StartedUtc + BlockSize

        s.Serialize(ProfileKey);            // length-prefixed UTF-8
        s.Serialize(CapacityReportedAtBom);

        // Plan — enough to resume with an IDENTICAL cadence/chunking, without re-resolving.
        s.Serialize(Plan.SampleCount);
        s.Serialize(Plan.BodySampleCount);
        s.Serialize(Plan.TailSampleCount);
        s.Serialize(Plan.BlockSize);
        s.Serialize(Plan.BlocksPerChunk);
        s.Serialize(Plan.ChunkSize);
        s.Serialize(Plan.TailBlocksPerChunk);
        s.Serialize(Plan.TailChunkSize);
        s.Serialize(Plan.TailCapacityFraction);
        s.Serialize(Plan.NumCheckpoints);
    }

    /// <summary>
    /// Reads the calibration-specific fields after the shared preamble has been decoded. Called only
    ///  by <see cref="TapeHeader.ConstructFrom"/> once the kind byte selected
    ///  <see cref="TapeHeaderKind.Calibration"/>.
    /// </summary>
    internal static TapeCalibrationHeader ConstructBody(TapeDeserializer d, in TapeHeaderPreamble p)
    {
        string profileKey = d.DeserializeString();
        long capacity     = d.DeserializeInt64();

        var plan = new TapeCalibrationPlan(
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

        return new TapeCalibrationHeader
        {
            Id                    = p.Id,
            CreatedUtc            = p.CreatedUtc,
            BlockSize             = p.BlockSize,
            ProfileKey            = profileKey,
            CapacityReportedAtBom = capacity,
            Plan                  = plan,
        };
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Calibration cartridge — run {RunId:N}, started {StartedUtc:u}, profile \"{ProfileKey}\"";
}
