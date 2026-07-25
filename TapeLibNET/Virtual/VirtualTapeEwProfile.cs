namespace TapeLibNET.Virtual;

/// <summary>
/// Opt-in emulation profile that makes a <see cref="VirtualTapeMedia"/> reproduce the two real-world
/// LTO behaviors the remaining-capacity estimator exists to tame:
/// <list type="bullet">
///   <item>a built-in <b>early-warning (EW) zone</b> — a stretch of medium near the physical end where the
///   drive starts asserting EW while still accepting data;</item>
///   <item>an <b>optimistic reported-remaining model</b> — the "quirky" figure the driver reports, which
///   overshoots the true remaining and floors near the tail (the LTO-4 reports ~28-32 GB free at hard EOM).</item>
/// </list>
/// Both are <b>physical properties of the cartridge</b>, hence carried on the media (not the drive).
/// A null/absent profile preserves the exact legacy behavior (<c>reported == capacity − bytesWritten</c>,
/// no EW).
/// </summary>
public sealed record VirtualTapeEwProfile
{
    /// <summary>
    /// Bytes before physical EOM at which the built-in early warning starts firing. Once the true bytes
    /// written enter this zone, EW keeps asserting on every write up to hard EOM. Zero disables EW emulation.
    /// </summary>
    public long EarlyWarningZone { get; init; }

    /// <summary>
    /// Maps <c>(actualWritten, capacity)</c> to the driver-<c>Remaining</c> figure the medium reports.
    /// Should be monotonic non-increasing in <c>actualWritten</c>. When <see langword="null"/>, the medium
    /// reports the exact <c>capacity − actualWritten</c> (legacy behavior).
    /// </summary>
    public System.Func<long, long, long>? ReportedRemainingModel { get; init; }

    /// <summary>The reported remaining figure for a given true position, floored at zero.</summary>
    public long ReportedRemaining(long actualWritten, long capacity)
    {
        long reported = ReportedRemainingModel?.Invoke(actualWritten, capacity)
            ?? System.Math.Max(0L, capacity - actualWritten);
        return System.Math.Max(0L, reported);
    }

    /// <summary>Whether the true position <paramref name="actualWritten"/> lies within the EW zone.</summary>
    public bool IsInEarlyWarningZone(long actualWritten, long capacity)
        => EarlyWarningZone > 0 && actualWritten >= capacity - EarlyWarningZone;

    #region *** Factories ***

    /// <summary>
    /// A realistic LTO-4-like preset: an EW zone of <paramref name="ewZonePercent"/> of capacity, and a
    /// linear reported-remaining model that overshoots toward the tail and floors at
    /// <paramref name="floorPercent"/> of capacity at hard EOM (mirrors the documented ~3.6% overshoot and
    /// ~4% floor). Independent of the medium's absolute capacity, so it applies to small test cartridges too.
    /// </summary>
    public static VirtualTapeEwProfile Lto4Like(long capacity, double ewZonePercent = 4.0, double floorPercent = 4.0)
    {
        if (capacity < 0) capacity = 0;
        long ewZone = (long)(capacity * ewZonePercent / 100.0);
        double floorFraction = floorPercent / 100.0;

        // reported(0) == capacity ; reported(capacity) == floor == capacity*floorFraction.
        // Linear, monotonic decreasing; overshoot (reported − true) grows to floor at EOM.
        long Model(long actualWritten, long cap)
        {
            if (cap <= 0) return 0;
            double slope = 1.0 - floorFraction;
            double reported = cap - actualWritten * slope;
            return (long)System.Math.Round(reported);
        }

        return new VirtualTapeEwProfile
        {
            EarlyWarningZone = ewZone,
            ReportedRemainingModel = Model,
        };
    }

    /// <summary>
    /// Builds an emulation profile from a real (or a-priori) <see cref="ITapeCalibration"/>, rescaling the
    /// profile's (typically large) capacity onto the virtual medium's <paramref name="targetCapacity"/> so a
    /// hundreds-of-GB LTO profile can drive a small test cartridge. The reported-remaining model is derived
    /// from <see cref="ITapeCalibration.TranslateActualToReported"/>; the EW zone from
    /// <see cref="ITapeCalibration.EwToEomDistance"/>. Both are scaled by
    /// <c>targetCapacity / calibration.CapacityActual</c>.
    /// </summary>
    public static VirtualTapeEwProfile FromCalibration(ITapeCalibration calibration, long targetCapacity)
    {
        System.ArgumentNullException.ThrowIfNull(calibration);
        if (targetCapacity < 0) targetCapacity = 0;

        long sourceCapacity = calibration.CapacityActual > 0
            ? calibration.CapacityActual
            : calibration.CapacityReported;

        double scale = sourceCapacity > 0 ? (double)targetCapacity / sourceCapacity : 1.0;

        long ewZone = (long)System.Math.Round(calibration.EwToEomDistance * scale);

        long Model(long actualWritten, long cap)
        {
            // Map the virtual position back onto the source profile's scale, translate, then scale back.
            long targetActualRemaining = System.Math.Max(0L, cap - actualWritten);
            long sourceActualRemaining = (long)System.Math.Round(targetActualRemaining / (scale == 0 ? 1.0 : scale));
            long sourceReported = calibration.TranslateActualToReported(sourceActualRemaining);
            return (long)System.Math.Round(sourceReported * scale);
        }

        return new VirtualTapeEwProfile
        {
            EarlyWarningZone = ewZone,
            ReportedRemainingModel = Model,
        };
    }

    #endregion
}
