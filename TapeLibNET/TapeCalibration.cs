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
    /// (<c>vendor|product|revision|cap=NNNGB</c>). Compared against <see cref="TapeDrive.DriveProfileKey"/>.
    /// </summary>
    string ProfileKey { get; }

    /// <summary>Driver-reported capacity at BOT (bytes).</summary>
    long CapacityReported { get; }

    /// <summary>True raw capacity measured as bytes written at hard EOM (bytes) — the ground truth.</summary>
    long CapacityActual { get; }

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
    public const string CurrentFormatId = "tapelibnet-cal/1";

    private const long c_bytesPerGB = 1024L * 1024 * 1024;

    #endregion

    #region *** Properties ***

    public string FormatId { get; }
    public string ProfileKey { get; }
    public long CapacityReported { get; }
    public long CapacityActual { get; }
    public IReadOnlyList<CalibrationPoint> Curve { get; }
    public CalibrationPoint? EarlyWarning { get; }

    #endregion

    #region *** Construction ***

    private TapeCalibration(
        string formatId, string profileKey, long capacityReported, long capacityActual,
        IReadOnlyList<CalibrationPoint> curve, CalibrationPoint? earlyWarning)
    {
        FormatId = formatId;
        ProfileKey = profileKey;
        CapacityReported = capacityReported;
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
    /// <param name="capacityReported">Driver capacity at BOT.</param>
    /// <param name="capacityActual">Bytes written at hard EOM (ground truth).</param>
    /// <param name="rawSamples">The <c>(ActualWritten, ReportedRemaining)</c> pairs, including the EOM point.</param>
    /// <param name="earlyWarning">The <c>(ActualWritten, ReportedRemaining)</c> at first EW, or null if none.</param>
    public static TapeCalibration FromMeasurements(
        string profileKey, long capacityReported, long capacityActual,
        IEnumerable<(long ActualWritten, long ReportedRemaining)> rawSamples,
        (long ActualWritten, long ReportedRemaining)? earlyWarning)
    {
        var pts = new List<CalibrationPoint>();
        foreach (var (aw, rr) in rawSamples)
            pts.Add(new CalibrationPoint(rr, Math.Max(0L, capacityActual - aw)));

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

        return new TapeCalibration(CurrentFormatId, profileKey, capacityReported, capacityActual, curve, ewPoint);
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
        //  741┤ capacityActual                                              ● BOT
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
        //  at reported == capacity     → actual == capacity − margin (BOT)
        var curve = new List<CalibrationPoint>
        {
            new(margin, 0L),
            new(capacity, capacityActual),
        };

        CalibrationPoint? ew = new CalibrationPoint(ewReported, Math.Max(0L, ewReported - margin));

        return new TapeCalibration("tapelibnet-cal-apriori/1", profileKey, capacity, capacityActual, curve, ew);
    }

    #endregion

    #region *** Translation ***

    public long EwToEomDistance => EarlyWarning?.ActualRemaining ?? 0L;

    public long TranslateRemaining(long reportedRemaining)
    {
        var c = Curve;
        if (c.Count == 0)
            return reportedRemaining;             // no data → passthrough

        if (reportedRemaining <= c[0].ReportedRemaining)
            return c[0].ActualRemaining;          // clamp low (near EOM → conservative)
        if (reportedRemaining >= c[^1].ReportedRemaining)
            return c[^1].ActualRemaining;         // clamp high (near BOT)

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

    #endregion

    #region *** Profile Key ***

    /// <summary>
    /// Produces a profile key identical in form to <see cref="TapeDriveBackend.ProfileKey"/>:
    /// <c>vendor|product|revision|cap=NNNGB</c>. Provided as a convenience; matching relies on
    /// exact string equality against <see cref="TapeDrive.DriveProfileKey"/>.
    /// </summary>
    public static string MakeProfileKey(string vendor, string product, string revision, long capacityBytes)
        => $"{vendor}|{product}|{revision}|cap={CapacityBucketGB(capacityBytes)}GB";

    /// <summary>
    /// Coarse GB bucket (2 significant figures) matching the backend's bucketing, so a key made here
    /// lines up with the backend-generated one. Absorbs cartridge-to-cartridge jitter while keeping
    /// distinct media generations apart.
    /// </summary>
    public static long CapacityBucketGB(long capacityBytes)
    {
        if (capacityBytes <= 0)
            return 0;

        double gb = capacityBytes / (double)c_bytesPerGB;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(gb)) - 1);
        if (mag < 1) mag = 1;

        return (long)(Math.Round(gb / mag) * mag);
    }

    #endregion

    #region *** Persistence (JSON) ***

    // Serialization DTO: keeps the wire format stable and independent of the class shape.
    private sealed record Dto(
        string FormatId,
        string ProfileKey,
        long CapacityReported,
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
        var dto = new Dto(FormatId, ProfileKey, CapacityReported, CapacityActual,
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
            if (dto.FormatId != CurrentFormatId && dto.FormatId != "tapelibnet-cal-apriori/1")
                return null;

            var curve = dto.Curve ?? [];
            return new TapeCalibration(dto.FormatId, dto.ProfileKey,
                dto.CapacityReported, dto.CapacityActual, curve, dto.EarlyWarning);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #endregion
}
