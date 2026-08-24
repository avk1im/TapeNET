using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TapeLibNET;

/// <summary>
/// A single calibrated mapping point on the <c>ReportedRemaining → ActualRemaining</c> curve,
/// both in bytes. The curve is the artifact the runtime actually consumes, so we store it
/// already transformed (not the raw <c>ActualWritten → ReportedRemaining</c> we measure during a run).
/// </summary>
public readonly record struct CalibrationPoint(long ReportedRemaining, long ActualRemaining);

/// <summary>
/// Opaque, persistable calibration for a drive+media profile. Translates the (often optimistic)
/// driver-<c>ReportedRemaining</c> into a truer <c>ActualRemaining</c>.
/// <para>
/// The application persists this verbatim via <see cref="SaveTo"/> and restores it via
/// <see cref="TapeCalibration.LoadFrom"/>, WITHOUT interpreting the contents. The concrete
/// representation lives in <see cref="TapeCalibration"/> and may evolve; <see cref="FormatId"/>
/// guards backward compatibility.
/// </para>
/// </summary>
public interface ITapeCalibration
{
    /// <summary>Backend/format identifier + version so a loader can reject a blob it does not understand.</summary>
    string FormatId { get; }

    /// <summary>
    /// Stable key identifying the drive+media profile this calibration applies to
    /// (<c>vendor|product|revision|NNNGB</c>). Compared against <see cref="TapeDrive.DriveProfileKey"/>.
    /// </summary>
    string ProfileKey { get; }

    /// <summary>
    /// Quantity (4) — the driver-reported remaining sampled at BOM (beginning of media), i.e. the
    /// drive's own idea of the cartridge size. May exceed <see cref="CapacityActual"/> when the drive
    /// inflates its capacity from the very first byte; observed ≈ equal on LTO-4.
    /// </summary>
    long ReportedCapacityAtBom { get; }

    /// <summary>
    /// Quantity (5) — the driver-reported remaining still claimed at the instant hard EOM fires:
    /// phantom free space that does not physically exist (LTO-4: ~28 GB). The headline measure of how
    /// much the drive over-reports.
    /// </summary>
    long PhantomFreeAtEom { get; }

    /// <summary>True raw capacity measured as bytes written at hard EOM (bytes) — the ground truth.</summary>
    long CapacityActual { get; }

    /// <summary>
    /// The total capacity implied by the driver's reporting: everything it ever claimed was writable,
    /// including the phantom tail. Derived: <see cref="CapacityActual"/> + <see cref="PhantomFreeAtEom"/>.
    /// </summary>
    long ReportedCapacityTotal => CapacityActual + PhantomFreeAtEom;

    /// <summary>The calibrated curve, sorted ascending by <see cref="CalibrationPoint.ReportedRemaining"/>.</summary>
    IReadOnlyList<CalibrationPoint> Curve { get; }

    /// <summary>
    /// The early-warning landmark, as a <c>(ReportedRemaining, ActualRemaining)</c> point, or
    /// <see langword="null"/> if the drive never reported EW during the run.
    /// </summary>
    CalibrationPoint? EarlyWarning { get; }

    /// <summary>
    /// Bytes still actually writable at the moment EW fires — i.e. <see cref="EarlyWarning"/>'s
    /// <see cref="CalibrationPoint.ActualRemaining"/> (0 if no EW landmark). This is the stable
    /// per-profile physical constant the runtime uses to byte-count precisely after EW.
    /// </summary>
    long EwToEomDistance => EarlyWarning?.ActualRemaining ?? 0L;

    /// <summary>
    /// Pure, curve-only translation <c>ReportedRemaining → ActualRemaining</c> (bytes), with
    /// clamping at the curve ends. This is the "EW-not-fired / no-EW-support" branch; the precise
    /// after-EW branch is applied by <see cref="TapeDrive"/> using live session state.
    /// </summary>
    long TranslateReportedToActual(long reportedRemaining);

    /// <summary>
    /// Inverse, curve-only translation <c>ActualRemaining → ReportedRemaining</c> (bytes), with
    /// clamping at the curve ends. Answers "what would the driver report if the true remaining were
    /// <paramref name="actualRemaining"/>?" — used to REPRODUCE a drive's optimistic remaining figure
    /// (e.g. by the virtual backend's emulation), the mirror image of <see cref="TranslateReportedToActual"/>.
    /// </summary>
    long TranslateActualToReported(long actualRemaining);

    /// <summary>Writes the opaque representation to <paramref name="stream"/>. The app saves this verbatim.</summary>
    void SaveTo(Stream stream);
}

/// <summary>
/// Concrete, JSON-serialized <see cref="ITapeCalibration"/>. Construct via <see cref="FromMeasurements"/>
/// (a calibration run), <see cref="Apriori"/> (a blind-guess baseline usable before any run), or
/// <see cref="LoadFrom"/> (a previously saved blob).
/// </summary>
public sealed class TapeCalibration : ITapeCalibration
{
    #region *** Constants ***

    /// <summary>Current on-disk format identifier.</summary>
    public const string CurrentFormatId = "tapelibnet-cal/2";

    private const long c_bytesPerGB = 1024L * 1024 * 1024;

    #endregion

    #region *** Properties ***

    public string FormatId { get; }
    public string ProfileKey { get; }
    public long ReportedCapacityAtBom { get; }
    public long PhantomFreeAtEom { get; }
    public long CapacityActual { get; }
    public IReadOnlyList<CalibrationPoint> Curve { get; }
    public CalibrationPoint? EarlyWarning { get; }

    /// <summary>
    /// EXPERIMENTAL parallel series: the drive's OWN remaining figure (SCSI LOG SENSE, Tape Capacity
    /// page 0x31) transformed to the same <c>reported → actual</c> shape as <see cref="Curve"/>. Null on
    /// older blobs and on non-LTO runs. Kept ALONGSIDE (never replacing) <see cref="Curve"/> so the
    /// runtime is unaffected while we compare the two offline — in particular to see whether the native
    /// figure dodges the driver's tail quirks (e.g. the LTO-3 collapse, LTO-4 phantom).
    /// </summary>
    public IReadOnlyList<CalibrationPoint>? LtoRemainingCurve { get; }

    #endregion

    #region *** Construction ***

    private TapeCalibration(
        string formatId, string profileKey, long reportedCapacityAtBom, long phantomFreeAtEom,
        long capacityActual, IReadOnlyList<CalibrationPoint> curve, CalibrationPoint? earlyWarning,
        IReadOnlyList<CalibrationPoint>? ltoRemainingCurve = null)
    {
        FormatId = formatId;
        ProfileKey = profileKey;
        ReportedCapacityAtBom = reportedCapacityAtBom;
        PhantomFreeAtEom = phantomFreeAtEom;
        CapacityActual = capacityActual;
        Curve = curve;
        EarlyWarning = earlyWarning;
        LtoRemainingCurve = ltoRemainingCurve;
    }

    /// <summary>
    /// Builds an IDENTITY baseline: actual remaining == reported remaining everywhere, with NO margin and NO
    /// EW landmark. For a backend that reports EXACT capacity (a virtual drive with no EW emulation), so the
    /// estimator neither compensates for over-report nor holds any pessimistic buffer — the logical-EW reserve
    /// then fires precisely when reported drops to the requested TOC size, and the "space remaining" figure is
    /// the honest truth. NOT for real hardware, which always over- or under-reports (use <see cref="Apriori"/>).
    /// </summary>
    /// <remarks>Synthesized per session, never persisted — its <c>FormatId</c> is internal-only.</remarks>
    public static ITapeCalibration Ideal(string profileKey, long capacity)
    {
        if (capacity < 0) capacity = 0;

        // Identity curve: actual == reported at both anchors ⇒ TranslateReportedToActual is the identity.
        var curve = new List<CalibrationPoint>
        {
            new(0L, 0L),
            new(capacity, capacity),
        };

        // No EW landmark: an honest drive has no phantom/collapse tail to byte-count against.
        return new TapeCalibration(
            "tapelibnet-cal-ideal/2", profileKey, capacity, /*phantom*/ 0L, /*capacityActual*/ capacity, curve, null);
    }

    /// <summary>
    /// Builds a calibration from a completed run. Raw samples are <c>(ActualWritten, ReportedRemaining)</c>
    /// captured while writing; they are transformed here into the <c>ReportedRemaining → ActualRemaining</c>
    /// curve using <paramref name="capacityActual"/> (bytes at hard EOM): <c>ActualRemaining = CapacityActual − ActualWritten</c>.
    /// </summary>
    /// <param name="profileKey">Usually <see cref="TapeDrive.DriveProfileKey"/> so a fresh run always matches.</param>
    /// <param name="reportedCapacityAtBom">Driver-reported remaining at BOM — quantity (4).</param>
    /// <param name="capacityActual">Bytes written at hard EOM (ground truth) — quantity (1).</param>
    /// <param name="rawSamples">The <c>(ActualWritten, ReportedRemaining)</c> pairs, including the EOM point.</param>
    /// <param name="earlyWarning">The <c>(ActualWritten, ReportedRemaining)</c> at first EW, or null if none.</param>
    public static TapeCalibration FromMeasurements(
        string profileKey, long reportedCapacityAtBom, long capacityActual,
        IEnumerable<(long ActualWritten, long ReportedRemaining)> rawSamples,
        (long ActualWritten, long ReportedRemaining)? earlyWarning,
        IEnumerable<(long ActualWritten, long LtoRemaining)>? ltoSamples = null)
    {
        var pts = new List<CalibrationPoint>();

        // The phantom free space at EOM is the reported figure at the DEEPEST sample — the last thing
        //  the driver claimed while the medium was already physically full. It is an independent
        //  measurement, not derivable from the BOM anchor.
        long deepestWritten = -1L;
        long phantomFreeAtEom = 0L;

        foreach (var (aw, rr) in rawSamples)
        {
            pts.Add(new CalibrationPoint(rr, Math.Max(0L, capacityActual - aw)));
            if (aw > deepestWritten)
            {
                deepestWritten = aw;
                phantomFreeAtEom = Math.Max(0L, rr);
            }
        }

        // Sort ascending by ReportedRemaining; on ties keep the CONSERVATIVE (smallest) ActualRemaining.
        pts.Sort(static (a, b) =>
            a.ReportedRemaining != b.ReportedRemaining
                ? a.ReportedRemaining.CompareTo(b.ReportedRemaining)
                : a.ActualRemaining.CompareTo(b.ActualRemaining));

        // Keep one point per distinct ReportedRemaining (conservative: smallest ActualRemaining on ties) —
        //  EXCEPT the collapse tail, where ReportedRemaining pins to 0 across a real span of ActualRemaining
        //  (LTO-3). Those points are a valid Actual→Reported function and plot directly on the flipped graph,
        //  so we retain them all. The Reported→Actual lookup guards the resulting duplicate keys (see below).
        var curve = new List<CalibrationPoint>(pts.Count);
        foreach (var p in pts)
            if (curve.Count == 0
                || curve[^1].ReportedRemaining != p.ReportedRemaining
                || p.ReportedRemaining == 0)      // do NOT dedup the reported==0 collapse run
                curve.Add(p);   // De-duplicate identical ReportedRemaining values, keeping the first (conservative) one.

        CalibrationPoint? ewPoint = earlyWarning is { } ew
            ? new CalibrationPoint(ew.ReportedRemaining, Math.Max(0L, capacityActual - ew.ActualWritten))
            : null;

        // Build the optional LTO (LOG SENSE) parallel series with the same reported→actual transform and
        //  conservative-tie dedup as the main curve, so the two are directly comparable point-for-point.
        List<CalibrationPoint>? ltoCurve = null;
        if (ltoSamples is not null)
        {
            var lp = new List<CalibrationPoint>();
            foreach (var (aw, lto) in ltoSamples)
                if (lto >= 0)
                    lp.Add(new CalibrationPoint(lto, Math.Max(0L, capacityActual - aw)));

            lp.Sort(static (a, b) =>
                a.ReportedRemaining != b.ReportedRemaining
                    ? a.ReportedRemaining.CompareTo(b.ReportedRemaining)
                    : a.ActualRemaining.CompareTo(b.ActualRemaining));

            ltoCurve = new List<CalibrationPoint>(lp.Count);
            foreach (var p in lp)
                if (ltoCurve.Count == 0 || ltoCurve[^1].ReportedRemaining != p.ReportedRemaining)
                    ltoCurve.Add(p);
        }

        return new TapeCalibration(CurrentFormatId, profileKey, Math.Max(0L, reportedCapacityAtBom),
            phantomFreeAtEom, capacityActual, curve, ewPoint, ltoCurve);
    }

    /// <summary>
    /// Builds a conservative blind-guess baseline calibration (no run required), DIFFERENTIATED by LTO
    /// generation. The runtime stops content when <c>reported ≤ margin + reserve</c>, so <c>margin</c> is a
    /// capacity-fraction upper bound on the driver's tail over-report — guaranteeing actual remaining ≥ the
    /// TOC reserve. The EW landmark is a real (deliberately under-estimated) runway on LTO-4+, and a tiny
    /// emergency backstop on the older collapse-prone drives (where the physical EW fires uselessly late).
    /// Lets the runtime estimate improve on raw reported remaining until a measured calibration replaces it.
    /// </summary>
    /// <remarks>
    /// The three behavioral envelopes below were derived from real AIT/DAT/DLT/LTO-3/4/6 calibration runs:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Generation 0</b> (pre-LTO forced-LTO: AIT/DAT/DLT/SDLT). Reported COLLAPSES near EOM and the
    ///     physical EW fires uselessly late (&lt; ~1.5 MB … 0.5 GB before EOM); one member (DLT-V4) even
    ///     OVER-reports in the tail with a small phantom. So the reserve MUST come from the curve, well
    ///     before the collapse — a 2%-of-capacity margin covers the worst observed case. BOM error swings
    ///     both ways (AIT/DLT over-report ~1.5–2.2%, DAT is nearly truthful).
    ///   </item>
    ///   <item>
    ///     <b>Generations 1–3</b> (LTO-1..3). Reported collapses to 0 at EW (LTO-3: an abrupt cliff at
    ///     ~0.9% of capacity); BOM error up to ~4% in EITHER direction (LTO-3 UNDER-reports 3.8%); the
    ///     physical EW fires ~0.1% before EOM — a backstop only. A 2% margin clears the cliff with ~2× safety.
    ///   </item>
    ///   <item>
    ///     <b>Generations 4+</b> (LTO-4+). Smooth, reliable physical EW ~4% before EOM with a large
    ///     phantom-free runway; small BOM error (LTO-4 −0.76%, LTO-6 +0.19%). The physical-EW byte-count is
    ///     the primary mechanism, so we store a REAL runway (under-estimated to ~3% for safety) and only a
    ///     ~1% margin.
    ///   </item>
    /// </list>
    /// The stored EW landmark cooperates with the runtime's "tighten-only" rule
    /// (<c>estimate = min(curveEstimate, EwToEomDistance − bytesAfterPhysicalEw)</c>): on LTO-4+ the real
    /// runway sharpens the estimate once the hardware EW fires, while on the collapse drives the tiny
    /// backstop can only ever STOP the caller, never inflate remaining — so a late/stale hardware EW can
    /// never cause an overrun.
    /// </remarks>
    /// <param name="profileKey">The drive+media profile key this baseline is for.</param>
    /// <param name="capacity">Nominal content capacity in bytes.</param>
    /// <param name="ltoGeneration">The (possibly forced) LTO generation: 0 = pre-LTO SCSI-addressabele forced-LTO,
    ///  1..3 = LTO-1..3, ≥ 4 = LTO-4+. Negative is treated as 0 (unknown/pre-LTO, most pessimistic).</param>
    public static ITapeCalibration Apriori(string profileKey, long capacity, int ltoGeneration = -1)
    {
        if (capacity < 0) capacity = 0;

        // Resolve the generation into a safety envelope:
        //  marginPct : conservative over-report/collapse envelope as a fraction of capacity (drives the stop).
        //  ewActual  : a-priori EwToEomDistance — a real (under-estimated) runway on LTO-4+, ~0 backstop below.
        //  floor     : absolute lower bound on the margin so tiny cartridges still get a sane buffer.
        (double marginPct, long ewActual, long floor) = ltoGeneration switch
        {
            // LTO-4+ : ~1% envelope (LTO-4 −0.76%, LTO-6 +0.19% observed); 3% runway UNDER-estimates the ~4% real.
            >= 4 => (0.010, (long)(capacity * 0.030), 64L * 1024 * 1024),
            // LTO-1..3 : 2% envelope clears LTO-3's 0.9% cliff with ~2× safety; EW ≈ EOM ⇒ 1 MB backstop only.
            >= 1 => (0.020, 1L * 1024 * 1024, 16L * 1024 * 1024),
            // Generation 0 / unknown : 2% envelope covers DLT-V4's phantom + tail over-report; 1 MB backstop only.
            _ => (0.020, 1L * 1024 * 1024, 8L * 1024 * 1024),
        };

        long margin = Math.Max(floor, (long)(capacity * marginPct));
        long capacityActual = Math.Max(0L, capacity - margin);
        ewActual = Math.Min(ewActual, capacityActual);

        // Conservative linear curve: actual ≈ reported − margin, clamped. Because margin ≥ the worst tail
        //  over-report, TranslateReportedToActual never overestimates actual (see design doc §5.1).
        var curve = new List<CalibrationPoint>
        {
            new(margin, 0L),
            new(capacity, capacityActual),
        };

        // EW landmark: reported = ewActual + margin (matches the over-report), actual = ewActual.
        CalibrationPoint? ew = new CalibrationPoint(ewActual + margin, ewActual);

        return new TapeCalibration(
            "tapelibnet-cal-apriori/2", profileKey, capacity, margin, capacityActual, curve, ew);
    }
    
    #endregion

    #region *** Translation ***

    public long EwToEomDistance => EarlyWarning?.ActualRemaining ?? 0L;

    /// <summary>
    /// Translates a driver-reported remaining byte count into a more accurate actual remaining count
    /// estimation, based on the calibration curve.
    /// <para>
    /// Robust against the "collapse tail" some drives exhibit (LTO-3): a run of curve points that all
    /// share <c>ReportedRemaining == 0</c> while <see cref="CalibrationPoint.ActualRemaining"/> still
    /// spans a real range. Because the curve is sorted ascending by reported (ties broken by ascending
    /// actual), that run sits at the head as <c>(0, 0) … (0, EwToEomDistance)</c>. A reported figure of 0
    /// therefore clamps to the conservative (smallest) actual, and any positive reported brackets past
    /// the whole run (lo on the last zero, hi on the first positive), so the interpolation below never
    /// divides by a zero-width reported span.
    /// </para>
    /// </summary>
    /// <param name="reportedRemaining">The remaining byte count reported by the driver.</param>
    /// <returns>The estimated actual remaining byte count.</returns>
    public long TranslateReportedToActual(long reportedRemaining)
    {
        var c = Curve;

        if (c.Count == 0)
            return reportedRemaining;             // no data → passthrough

        if (reportedRemaining <= c[0].ReportedRemaining)
            return c[0].ActualRemaining;          // clamp low (at/near EOM, incl. a reported==0 collapse → conservative)

        if (reportedRemaining >= c[^1].ReportedRemaining)
            return c[^1].ActualRemaining;         // clamp high (near BOM)

        // Binary-search the bracketing pair, then linearly interpolate.
        int lo = 0, hi = c.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (c[mid].ReportedRemaining <= reportedRemaining) lo = mid; else hi = mid;
        }

        CalibrationPoint a = c[lo], b = c[hi];

        long dr = b.ReportedRemaining - a.ReportedRemaining;
        if (dr <= 0)
            return a.ActualRemaining;             // equal-reported bracket (defensive): conservative (smaller) actual

        double t = (double)(reportedRemaining - a.ReportedRemaining) / dr;
        return a.ActualRemaining + (long)Math.Round(t * (b.ActualRemaining - a.ActualRemaining));
    }

    /// <summary>
    /// Inverse of <see cref="TranslateReportedToActual"/>: given a true <paramref name="actualRemaining"/>,
    /// returns the (typically optimistic) figure the driver would report, by interpolating the curve
    /// on its <see cref="CalibrationPoint.ActualRemaining"/> axis (monotonic non-decreasing).
    /// <para>
    /// Reproduces the "collapse tail" (LTO-3) faithfully: across the run of points that share
    /// <c>ReportedRemaining == 0</c> but distinct actuals, both bracketing endpoints carry reported 0,
    /// so this returns 0 for every actual inside the collapse zone — exactly what the drive reports
    /// there. (Before those points were retained, this method wrongly ramped reported from 0 up to the
    /// first post-collapse anchor.) ActualRemaining stays unique and ascending across the whole curve,
    /// so the actual-axis span is strictly positive and never divides by zero.
    /// </para>
    /// </summary>
    public long TranslateActualToReported(long actualRemaining)
    {
        var c = Curve;

        if (c.Count == 0)
            return actualRemaining;               // no data → passthrough

        if (actualRemaining <= c[0].ActualRemaining)
            return c[0].ReportedRemaining;        // clamp low (at/near EOM)

        if (actualRemaining >= c[^1].ActualRemaining)
            return c[^1].ReportedRemaining;       // clamp high (near BOM)

        // Binary-search the bracketing pair on the ActualRemaining axis, then linearly interpolate.
        int lo = 0, hi = c.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (c[mid].ActualRemaining <= actualRemaining) lo = mid; else hi = mid;
        }

        CalibrationPoint a = c[lo], b = c[hi];

        long da = b.ActualRemaining - a.ActualRemaining;
        if (da <= 0)
            return a.ReportedRemaining;           // equal-actual bracket (defensive)

        double t = (double)(actualRemaining - a.ActualRemaining) / da;
        return a.ReportedRemaining + (long)Math.Round(t * (b.ReportedRemaining - a.ReportedRemaining));
    }

    #endregion

    #region *** Profile Key ***

    /// <summary>
    /// Produces a profile key identical in form to <see cref="TapeDriveBackend.ProfileKey"/>:
    /// <c>vendor|product|revision|NNNGB</c>. Provided as a convenience; matching relies on
    /// exact string equality against <see cref="TapeDrive.DriveProfileKey"/>.
    /// </summary>
    public static string MakeProfileKey(string vendor, string product, string revision, long capacityBytes)
        => $"{vendor}|{product}|{revision}|{CapacityBucket(capacityBytes)}";

    /// <summary>
    /// Coarse capacity bucket (2 significant figures) shared with the backend, so a key made here lines
    /// up with the backend-generated one. Absorbs cartridge-to-cartridge jitter while keeping distinct
    /// media generations apart. Media below 2 GB are bucketed in MB (<c>500MB</c>) rather than collapsing
    /// to <c>0GB</c>, so small virtual test cartridges of different sizes stay distinguishable.
    /// </summary>
    public static string CapacityBucket(long capacityBytes)
    {
        if (capacityBytes <= 0)
            return "0";

        const long bytesPerMB = 1024L * 1024;
        bool useMB = capacityBytes < 2L * c_bytesPerGB;
        double value = capacityBytes / (double)(useMB ? bytesPerMB : c_bytesPerGB);

        // Keep 2 significant figures: round to the nearest 10^(floor(log10)-1), never below 1 unit.
        double mag = Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        if (mag < 1) mag = 1;

        return $"{(long)(Math.Round(value / mag) * mag)}{(useMB ? "MB" : "GB")}";
    }

#if ALTERNATIVE_VERSION_WITH_ROUNDING
    public static string CapacityBucket(long capacityBytes)
    {
        if (capacityBytes <= 0)
            return "0";

        const long bytesPerMB = 1024L * 1024;
        bool useMB = capacityBytes < 2L * c_bytesPerGB;
        double value = capacityBytes / (double)(useMB ? bytesPerMB : c_bytesPerGB);

        // Base granularity: nearest 10^(floor(log10)-1) keeps 2 significant figures.
        double step = Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        if (step < 1) step = 1;

        double fine   = Math.Round(value / step) * step;               // 2 sig figs (default)
        double coarse = Math.Round(value / (step * 10)) * (step * 10);  // trailing sig fig -> 0

        // Snap to the rounder label only when it stays within relative tolerance.
        double chosen = (coarse > 0 && Math.Abs(coarse - value) <= c_bucketSnapTolerance * value)
            ? coarse
            : fine;

        return $"{(long)chosen}{(useMB ? "MB" : "GB")}";
    }

    /// <summary>
    /// Relative jitter a capacity may show before it counts as a distinct bucket.
    /// ~2%: snaps 79.2 GB to 80GB, keeps 76 GB at 76GB, keeps 780 GB at 780GB.
    /// </summary>
    private const double c_bucketSnapTolerance = 0.02;
#endif

    #endregion

    #region *** Persistence (JSON) ***

    // Serialization DTO: keeps the wire format stable and independent of the class shape.
    private sealed record Dto(
        string FormatId,
        string ProfileKey,
        long ReportedCapacityAtBom,
        long PhantomFreeAtEom,
        long CapacityActual,
        List<CalibrationPoint> Curve,
        CalibrationPoint? EarlyWarning,
        List<CalibrationPoint>? LtoRemainingCurve = null  // appended → older blobs read back as null
    );

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
    };

    public void SaveTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var dto = new Dto(FormatId, ProfileKey, ReportedCapacityAtBom, PhantomFreeAtEom, CapacityActual,
            [.. Curve], EarlyWarning, LtoRemainingCurve is null ? null : [.. LtoRemainingCurve]);
        JsonSerializer.Serialize(stream, dto, s_json);
    }

    /// <summary>
    /// Reconstructs a calibration from a stream previously written by <see cref="SaveTo"/>.
    /// Returns <see langword="null"/> if the stream is empty, malformed, or carries an unrecognized
    /// <see cref="FormatId"/>.
    /// </summary>
    public static TapeCalibration? LoadFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            Dto? dto = JsonSerializer.Deserialize<Dto>(stream, s_json);
            if (dto is null)
                return null;

            // Accept known format ids (run + apriori). Reject anything else.
            if (dto.FormatId != CurrentFormatId && dto.FormatId != "tapelibnet-cal-apriori/2")
                return null;

            var curve = dto.Curve ?? [];
            return new TapeCalibration(dto.FormatId, dto.ProfileKey,
                dto.ReportedCapacityAtBom, dto.PhantomFreeAtEom, dto.CapacityActual, curve, dto.EarlyWarning,
                dto.LtoRemainingCurve);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #endregion
}
