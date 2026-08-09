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
    long TranslateRemaining(long reportedRemaining);

    /// <summary>
    /// Inverse, curve-only translation <c>ActualRemaining → ReportedRemaining</c> (bytes), with
    /// clamping at the curve ends. Answers "what would the driver report if the true remaining were
    /// <paramref name="actualRemaining"/>?" — used to REPRODUCE a drive's optimistic remaining figure
    /// (e.g. by the virtual backend's emulation), the mirror image of <see cref="TranslateRemaining"/>.
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

    #endregion

    #region *** Construction ***

    private TapeCalibration(
        string formatId, string profileKey, long reportedCapacityAtBom, long phantomFreeAtEom,
        long capacityActual, IReadOnlyList<CalibrationPoint> curve, CalibrationPoint? earlyWarning)
    {
        FormatId = formatId;
        ProfileKey = profileKey;
        ReportedCapacityAtBom = reportedCapacityAtBom;
        PhantomFreeAtEom = phantomFreeAtEom;
        CapacityActual = capacityActual;
        Curve = curve;
        EarlyWarning = earlyWarning;
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
        (long ActualWritten, long ReportedRemaining)? earlyWarning)
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

        // De-duplicate identical ReportedRemaining values, keeping the first (conservative) one.
        var curve = new List<CalibrationPoint>(pts.Count);
        foreach (var p in pts)
            if (curve.Count == 0 || curve[^1].ReportedRemaining != p.ReportedRemaining)
                curve.Add(p);

        CalibrationPoint? ewPoint = earlyWarning is { } ew
            ? new CalibrationPoint(ew.ReportedRemaining, Math.Max(0L, capacityActual - ew.ActualWritten))
            : null;

        return new TapeCalibration(CurrentFormatId, profileKey, Math.Max(0L, reportedCapacityAtBom),
            phantomFreeAtEom, capacityActual, curve, ewPoint);
    }

    /// <summary>
    /// Builds a blind-guess baseline calibration (no run required): a simple linear curve that
    /// treats <paramref name="marginPercent"/> of capacity as an unusable reserve, and synthesizes an
    /// EW landmark at <paramref name="remainingAtEwPercent"/> of reported capacity. Lets the runtime
    /// estimate improve on raw reported remaining until a real calibration replaces it.
    /// </summary>
    public static ITapeCalibration Apriori(
        string profileKey, long capacity, double marginPercent = 5.0, double remainingAtEwPercent = 7.0)
    {
        if (capacity < 0) capacity = 0;
        long margin = (long)(capacity * marginPercent / 100.0);
        long ewReported = (long)(capacity * remainingAtEwPercent / 100.0);
        long capacityActual = Math.Max(0L, capacity - margin);

        // A-priori calibration curve: ReportedRemaining -> ActualRemaining
        // (blind linear model; example numbers for an ~780 GB LTO-4 at margin=5%, ewAt=7%)
        //
        //   ActualRemaining
        //     ^
        //  741┤ capacityActual                                              ● BOM
        //  (GB)│  = capacity - margin                                   ╱     (reported=780, actual=741)
        //     │                                                     ╱
        //     │                                                 ╱
        //     │                                             ╱   slope ≈ 1
        //     │                                         ╱       (actual ≈ reported - margin)
        //     │                                     ╱
        //     │                                 ╱
        //     │                             ╱
        //   16┤ - - - - - - - - - - - - -◆   EW landmark (fake / synthesized)
        //     │                       ╱ :    reported = ewReported (7%)  = 54.6 GB
        //     │                   ╱     :    actual   = ewReported-margin = 15.6 GB
        //     │               ╱         :    → EwToEomDistance
        //     │           ╱             :
        //    0┤───────●─────────────────┼───────────────────────────────────→ ReportedRemaining
        //     0     margin              54.6                                780   (GB)
        //     │    (39 GB)            (ewReported)                       (capacity)
        //     │       ↑
        //     │  blind stop point: driver still reports `margin` free,
        //     │  but real writable space is already 0 (curve clamps below here)
        //
        //   Anchors stored in curve[]:  (margin, 0)  and  (capacity, capacityActual)
        //   EW point (nullable):        (ewReported, ewReported - margin)
        //   Model:  ActualRemaining ≈ ReportedRemaining - margin,  floored at 0
        
        // Curve (ascending by ReportedRemaining):
        //  at reported == margin       → actual == 0        (blind stop point)
        //  at reported == capacity     → actual == capacity − margin (BOM)
        var curve = new List<CalibrationPoint>
        {
            new(margin, 0L),
            new(capacity, capacityActual),
        };

        CalibrationPoint? ew = new CalibrationPoint(ewReported, Math.Max(0L, ewReported - margin));

        // A-priori assumes NO capacity boost at BOM (quantity (4) == the nominal capacity) and treats the
        //  whole margin as phantom free space still claimed at hard EOM (quantity (5)).
        return new TapeCalibration("tapelibnet-cal-apriori/2", profileKey, capacity, margin, capacityActual, curve, ew);
    }

    #endregion

    #region *** Translation ***

    public long EwToEomDistance => EarlyWarning?.ActualRemaining ?? 0L;

    /// <summary>
    /// Translates a driver-reported remaining byte count into a more accurate actual remaining count
    /// estimation, based on the calibration curve.
    /// </summary>
    /// <param name="reportedRemaining">The remaining byte count reported by the driver.</param>
    /// <returns>The estimated actual remaining byte count.</returns>
    public long TranslateRemaining(long reportedRemaining)
    {
        var c = Curve;
        if (c.Count == 0)
            return reportedRemaining;             // no data → passthrough

        if (reportedRemaining <= c[0].ReportedRemaining)
            return c[0].ActualRemaining;          // clamp low (near EOM → conservative)
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
            return a.ActualRemaining;

        double t = (double)(reportedRemaining - a.ReportedRemaining) / dr;
        return a.ActualRemaining + (long)Math.Round(t * (b.ActualRemaining - a.ActualRemaining));
    }

    /// <summary>
    /// Inverse of <see cref="TranslateRemaining"/>: given a true <paramref name="actualRemaining"/>,
    /// returns the (typically optimistic) figure the driver would report, by interpolating the curve
    /// on its <see cref="CalibrationPoint.ActualRemaining"/> axis (monotonic non-decreasing).
    /// </summary>
    public long TranslateActualToReported(long actualRemaining)
    {
        var c = Curve;
        if (c.Count == 0)
            return actualRemaining;               // no data → passthrough

        if (actualRemaining <= c[0].ActualRemaining)
            return c[0].ReportedRemaining;        // clamp low (near EOM)
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
            return a.ReportedRemaining;

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
        CalibrationPoint? EarlyWarning);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
    };

    public void SaveTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var dto = new Dto(FormatId, ProfileKey, ReportedCapacityAtBom, PhantomFreeAtEom, CapacityActual,
            [.. Curve], EarlyWarning);
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
                dto.ReportedCapacityAtBom, dto.PhantomFreeAtEom, dto.CapacityActual, curve, dto.EarlyWarning);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #endregion
}
