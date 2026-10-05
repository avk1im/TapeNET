using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET;

/// <summary>
/// Calibration run header written once at BOM of a scratch cartridge — the calibration kind of the unified
///  <see cref="TapeHeader"/> hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// Self-identifies the run and cartridge so <see cref="TapeCalibrator.Resume"/> can verify "same run"
///  (<see cref="RunId"/> consistency) before trusting any checkpoint, and so a returned cartridge is inspectable.
///  Profile MATCHING against the current drive is deliberately NOT done here — that is the caller's responsibility.
/// </para>
/// <para>
/// <b>Transitional (Phase 5):</b> still written in the LEGACY frame through <see cref="ITapeSerializable"/> and
///  <see cref="TapeFramer.Pack(ITapeSerializable)"/>, like the calibration checkpoint; both move to format 2.1 together
///  in Phase 6 (Design-Format-v2 §5.4, §5.5). <see cref="WriteBody"/> therefore refuses.
/// </para>
/// <para>
/// The calibration header rides in the run's own block via the calibrator's <c>RecordBlockWriter</c>, NOT the fixed
///  16 KiB header block, so <see cref="TapeHeader.BlockSize"/> carries the run block size.
/// </para>
/// </remarks>
public sealed record TapeCalibrationHeader : TapeHeader, ITapeSerializable
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
    /// Builds the run header, mirroring <see cref="TapeTOC.CreateHeader"/> so a header is always assembled through one
    ///  factory and cannot silently diverge from the record's field layout.
    /// </summary>
    public static TapeCalibrationHeader CreateHeader(
        Guid runId, string profileKey, long capacityReportedAtBom, uint blockSize,
        DateTime startedUtc, TapeCalibrationPlan plan) =>
        new()
        {
            Id = runId,          // protected base member — settable from within the hierarchy
            CreatedUtc = startedUtc,
            BlockSize = blockSize,
            ProfileKey = profileKey,
            CapacityReportedAtBom = capacityReportedAtBom,
            Plan = plan,
        };

    /// <summary>Format 2.1 write — not before Phase 6. Callers pack through <see cref="TapeFramer.PackHeader"/>.</summary>
    public override void WriteBody(TapeFieldWriter fields)
        => throw new InvalidOperationException(
            "The calibration header is written in the legacy frame until Phase 6 (TapeFramer.Pack)");

    #region *** Legacy (ITapeSerializable) — removed in Phase 6 ***

    /// <inheritdoc/>
    /// <remarks>The calibration golden file pins these bytes: change nothing here until Phase 6 replaces it.</remarks>
    public void SerializeTo(TapeSerializer s)
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
    /// <see cref="ITapeSerializable"/> factory for the legacy framer (<see cref="TapeFramer.Unpack{T}"/>): reads a legacy
    ///  header of ANY kind through <see cref="LegacyHeaderReader.Read"/> and keeps it only if it is a calibration header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until Phase 5 this was inherited from <see cref="TapeHeader"/>; the base no longer implements
    ///  <see cref="ITapeSerializable"/> (media and set headers write format 2.1), so the calibration kind declares it itself.
    /// </para>
    /// <para>
    /// Explicit on purpose: it stays off the public surface, so no caller mistakes it for the dual-format header read
    ///  (<see cref="TapeFramer.UnpackHeader{T}"/>). The framer reaches it through its type parameter.
    /// </para>
    /// <para>
    /// A media or set header yields <see langword="null"/>, which the framer reports as
    ///  <see cref="TapeFramer.FrameStatus.Unparseable"/> — the narrow "not my kind" contract the calibrator relies on.
    /// </para>
    /// </remarks>
    static ITapeSerializable? ITapeSerializable.ConstructFrom(LegacyDeserializer d)
        => LegacyHeaderReader.Read(d) as TapeCalibrationHeader;

    /// <summary>
    /// Reads the calibration-specific fields after the shared preamble has been decoded. Called only by
    ///  <see cref="LegacyHeaderReader.Read"/> once the kind byte selected <see cref="TapeHeaderKind.Calibration"/>.
    /// </summary>
    internal static TapeCalibrationHeader ConstructBody(LegacyDeserializer d, in TapeHeaderPreamble p)
    {
        string profileKey = d.DeserializeString();
        long capacity = d.DeserializeInt64();
        var plan = LegacyCheckpointReader.ReadPlan(d);
        return new TapeCalibrationHeader
        {
            Id = p.Id,
            CreatedUtc = p.CreatedUtc,
            BlockSize = p.BlockSize,
            ProfileKey = profileKey,
            CapacityReportedAtBom = capacity,
            Plan = plan,
        };
    }

    #endregion

    /// <inheritdoc/>
    public override string ToString() =>
        $"Calibration cartridge — run {RunId:N}, started {StartedUtc:u}, profile \"{ProfileKey}\"";
}
