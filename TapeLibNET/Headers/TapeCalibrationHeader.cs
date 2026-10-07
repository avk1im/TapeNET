using TapeLibNET.Toc;
using TapeLibNET.Calibration;
using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET.Headers;

/// <summary>
/// Schema target for the run plan, nested as a group in the calibration header (Design-Format-v2 §5.4).
/// </summary>
/// <remarks>
/// Wraps the immutable <see cref="TapeCalibrationPlan"/> and updates it field by field with <c>with</c>, so each schema
///  line binds to the plan property's OWN type — no copy of the plan's field types to keep in step.
/// </remarks>
internal sealed class TapeCalibrationPlanWire
{
    public TapeCalibrationPlan Plan;

    /// <summary>Group schema (no record kind). All fields required: a resume must reproduce the cadence exactly.</summary>
    public static readonly TapeSchema<TapeCalibrationPlanWire> Schema = new()
    {
        { 1,  w => w.Plan.SampleCount,          (w, v) => w.Plan = w.Plan with { SampleCount = v },          FieldFlags.Required },
        { 2,  w => w.Plan.BodySampleCount,      (w, v) => w.Plan = w.Plan with { BodySampleCount = v },      FieldFlags.Required },
        { 3,  w => w.Plan.TailSampleCount,      (w, v) => w.Plan = w.Plan with { TailSampleCount = v },      FieldFlags.Required },
        { 4,  w => w.Plan.BlockSize,            (w, v) => w.Plan = w.Plan with { BlockSize = v },            FieldFlags.Required },
        { 5,  w => w.Plan.BlocksPerChunk,       (w, v) => w.Plan = w.Plan with { BlocksPerChunk = v },       FieldFlags.Required },
        { 6,  w => w.Plan.ChunkSize,            (w, v) => w.Plan = w.Plan with { ChunkSize = v },            FieldFlags.Required },
        { 7,  w => w.Plan.TailBlocksPerChunk,   (w, v) => w.Plan = w.Plan with { TailBlocksPerChunk = v },   FieldFlags.Required },
        { 8,  w => w.Plan.TailChunkSize,        (w, v) => w.Plan = w.Plan with { TailChunkSize = v },        FieldFlags.Required },
        { 9,  w => w.Plan.TailCapacityFraction, (w, v) => w.Plan = w.Plan with { TailCapacityFraction = v }, FieldFlags.Required },
        { 10, w => w.Plan.NumCheckpoints,       (w, v) => w.Plan = w.Plan with { NumCheckpoints = v },       FieldFlags.Required },
    };
}

/// <summary>Wire form of the 2.1 calibration run header (record kind <c>CalibrationRunHeader</c>, §5.4).</summary>
internal sealed class TapeCalibrationHeaderWire : TapeHeaderWire
{
    public long CapacityReportedAtBom;
    public string ProfileKey = "";
    public TapeCalibrationPlanWire? Plan = new();

    public static readonly TapeSchema<TapeCalibrationHeaderWire> Schema = new(TapeRecordKind.CalibrationRunHeader, inherits: Shared)
    {
        // ── scalars 1–31 (1–3 inherited: RunId, StartedUtc, RunBlockSize) ──
        { 4,  w => w.CapacityReportedAtBom, (w, v) => w.CapacityReportedAtBom = v, FieldFlags.Required },
        // ── strings 32–47 ──
        { 32, w => w.ProfileKey,            (w, v) => w.ProfileKey = v },
        // ── groups 48–63 ──
        { 48, w => w.Plan,                  (w, v) => w.Plan = v,                  TapeCalibrationPlanWire.Schema, FieldFlags.Required },
    };
}

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
/// <b>Format 2.1</b> block frame (Design-Format-v2 §5.4): the shared tags 1–3 carry <see cref="RunId"/>,
///  <see cref="StartedUtc"/> and <see cref="RunBlockSize"/>; the plan travels as a nested group. Legacy run headers on
///  existing cartridges keep reading (<see cref="ConstructBody"/>), so Resume and Recalibrate of a legacy run still work.
/// </para>
/// <para>
/// Written either as one standard header block (<see cref="TapeHeaderBlock.Write"/>) or — on drives whose maximum block
///  is smaller — in the run's own block via the calibrator's <c>RecordBlockWriter</c>. <see cref="TapeHeader.BlockSize"/>
///  carries the run block size in both cases.
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

    #region *** Format 2.1 ***

    /// <inheritdoc/>
    public override void WriteBody(TapeFieldWriter fields) => TapeCalibrationHeaderWire.Schema.Write(fields, new TapeCalibrationHeaderWire
    {
        Id = RunId,
        CreatedUtc = StartedUtc,
        BlockSize = RunBlockSize,
        CapacityReportedAtBom = CapacityReportedAtBom,
        ProfileKey = ProfileKey ?? "",
        Plan = new TapeCalibrationPlanWire { Plan = Plan },
    });

    /// <summary>Reads a 2.1 calibration header body. Called by <see cref="TapeHeader.ReadBody"/>.</summary>
    internal static TapeCalibrationHeader ReadWire(TapeFieldReader fields)
    {
        TapeCalibrationHeaderWire w = TapeCalibrationHeaderWire.Schema.Read(fields, new TapeCalibrationHeaderWire());
        return new TapeCalibrationHeader
        {
            Id = w.Id,
            CreatedUtc = w.CreatedUtc,
            BlockSize = w.BlockSize,
            ProfileKey = w.ProfileKey,
            CapacityReportedAtBom = w.CapacityReportedAtBom,
            Plan = w.Plan!.Plan,      // '!' safe: field 48 is Required, so a successful read always set it
        };
    }

    #endregion

    #region *** Legacy read ***

    /// <summary>
    /// Reads the calibration-specific fields of a LEGACY header after the shared preamble has been decoded. Called only
    ///  by <see cref="LegacyHeaderReader.Read"/> once the kind byte selected <see cref="TapeHeaderKind.Calibration"/>.
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
