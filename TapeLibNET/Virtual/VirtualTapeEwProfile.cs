namespace TapeLibNET.Virtual;

/// <summary>
/// The two INDEPENDENT anchors of an emulated driver's reported-remaining line. See
/// <c>docs/Design-RemainingAndEw.md</c> §5.1 for the normative vocabulary.
/// <list type="bullet">
///   <item><see cref="ReportedCapacityBoost"/> — (a) inflated capacity at BOM: the driver claims
///   <c>TrueCapacity + boost</c> free on a virgin cartridge, then counts down. Overshoot is a CONSTANT
///   from the very first byte. Defaults to 0, matching faithful LTO behavior.</item>
///   <item><see cref="PhantomFreeAtEom"/> — (b) phantom free space at hard EOM: the driver decrements
///   too slowly, so overshoot GROWS from the boost at BOM to <c>boost + phantom</c> at hard EOM, where it
///   still claims <see cref="PhantomFreeAtEom"/> bytes that do not exist. This is the LTO-4's ~28 GB lie.</item>
/// </list>
/// The reported line interpolates linearly (monotonic non-increasing) between the two anchors:
/// <code>
/// reported(0)            = TrueCapacity + ReportedCapacityBoost
/// reported(TrueCapacity) = PhantomFreeAtEom
/// </code>
/// </summary>
public readonly record struct ReportedRemainingAnchors(long ReportedCapacityBoost, long PhantomFreeAtEom)
{
    /// <summary>The truthful line: no boost at BOM, no phantom free space at EOM.</summary>
    public static ReportedRemainingAnchors Truthful => new(0L, 0L);

    /// <summary>Whether these anchors describe any divergence from the truth at all.</summary>
    public bool IsTruthful => ReportedCapacityBoost == 0 && PhantomFreeAtEom == 0;

    /// <summary>Linearly interpolates the reported-remaining figure for a true position, floored at zero.</summary>
    public long ReportedRemaining(long actualWritten, long capacity)
    {
        if (capacity <= 0)
            return 0L;

        double atBom = capacity + ReportedCapacityBoost;
        double slope = (atBom - PhantomFreeAtEom) / capacity;
        return System.Math.Max(0L, (long)System.Math.Round(atBom - actualWritten * slope));
    }
}

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
    /// The BOM/EOM anchors of the emulated reported-remaining line. This is the preferred, inspectable and
    /// serializable way to express over-reporting; <see cref="ReportedRemainingModel"/> exists only for
    /// shapes that a two-point line cannot express (e.g. a real calibration curve).
    /// </summary>
    public ReportedRemainingAnchors Anchors { get; init; } = ReportedRemainingAnchors.Truthful;

    /// <summary>
    /// Maps <c>(actualWritten, capacity)</c> to the driver-<c>Remaining</c> figure the medium reports.
    /// Should be monotonic non-increasing in <c>actualWritten</c>. When <see langword="null"/>, the medium
    /// falls back to <see cref="Anchors"/> (which, when truthful, yields the exact
    /// <c>capacity − actualWritten</c> legacy behavior).
    /// </summary>
    public System.Func<long, long, long>? ReportedRemainingModel { get; init; }

    /// <summary>The reported remaining figure for a given true position, floored at zero.</summary>
    public long ReportedRemaining(long actualWritten, long capacity)
    {
        long reported = ReportedRemainingModel?.Invoke(actualWritten, capacity)
            ?? Anchors.ReportedRemaining(actualWritten, capacity);
        return System.Math.Max(0L, reported);
    }

    /// <summary>Whether the true position <paramref name="actualWritten"/> lies within the EW zone.</summary>
    public bool IsInEarlyWarningZone(long actualWritten, long capacity)
        => EarlyWarningZone > 0 && actualWritten >= capacity - EarlyWarningZone;

    #region *** Factories ***

    /// <summary>
    /// A realistic LTO-4-like preset: an EW zone of <paramref name="ewZonePercent"/> of capacity, and a
    /// reported-remaining line pinned by the two independent over-report anchors
    /// (<see cref="ReportedRemainingAnchors"/>).
    /// <para>
    /// The three percentages describe INDEPENDENT axes and do NOT overlap:
    /// <list type="bullet">
    ///   <item><paramref name="ewZonePercent"/> is a PHYSICAL distance before hard EOM at which early warning
    ///   begins to assert (<see cref="EarlyWarningZone"/> = <c>capacity * ewZonePercent/100</c>). It is the
    ///   last stretch of REAL, writable medium.</item>
    ///   <item><paramref name="phantomFreePercent"/> is a REPORTED-REMAINING figure: the phantom free space
    ///   the driver still claims once hard EOM is reached
    ///   (<c>reported(capacity) = capacity * phantomFreePercent/100</c>). This space is not physically
    ///   writable. This is the faithful LTO-4 shape and the reason the estimator exists.</item>
    ///   <item><paramref name="reportedBoostPercent"/> is an INFLATED CAPACITY at BOM: the driver claims
    ///   <c>capacity * (1 + reportedBoostPercent/100)</c> free on a virgin cartridge. Real LTO drives do not
    ///   do this, hence the default of 0 — but the knob exists so the effect can be emulated and the
    ///   estimator proven against it.</item>
    /// </list>
    /// Because both over-report knobs describe PHANTOM (over-reported) capacity rather than physical medium,
    /// the EW zone is orthogonal to them.
    /// </para>
    /// </summary>
    public static VirtualTapeEwProfile Lto4Like(
        long capacity, double ewZonePercent = 4.0, double phantomFreePercent = 4.0,
        double reportedBoostPercent = 0.0)
    {
        if (capacity < 0) capacity = 0;

        return new VirtualTapeEwProfile
        {
            EarlyWarningZone = (long)(capacity * ewZonePercent / 100.0),
            Anchors = new ReportedRemainingAnchors(
                ReportedCapacityBoost: (long)(capacity * reportedBoostPercent / 100.0),
                PhantomFreeAtEom: (long)(capacity * phantomFreePercent / 100.0)),
        };
    }

    /// <summary>
    /// Minimum size of the EMULATED early-warning zone produced by <see cref="FromCalibration"/>, no matter
    /// how tiny the real tail scales to. Sized to one writer buffer (16 GB) so a full write operation lands
    /// INSIDE the zone and the emulated EW / collapse / phantom behavior actually surfaces during a run.
    /// On media smaller than this the zone is clamped to capacity — the whole cartridge becomes early warning,
    /// which is quirky but exception-free (the user's problem to live with when emulating EW on a toy drive).
    /// </summary>
    public const long MinEmulatedEarlyWarningZone = 16L * 1024 * 1024 * 1024;

    /// <summary>
    /// Builds an emulation profile from a real (or a-priori) <see cref="ITapeCalibration"/>, rescaling the
    /// profile's (typically large) capacity onto the virtual medium's <paramref name="targetCapacity"/> so a
    /// hundreds-of-GB LTO profile can drive a small test cartridge. The reported-remaining model is derived
    /// from <see cref="ITapeCalibration.TranslateActualToReported"/>; the EW zone from
    /// <see cref="ITapeCalibration.EwToEomDistance"/>.
    /// <para>
    /// NOTE the DUALITY: a calibration is normally an ESTIMATION artifact, translating reported → actual
    /// (<see cref="ITapeCalibration.TranslateRemaining"/>). Here it is used in the opposite direction, as an
    /// EMULATION source, translating actual → reported. Both directions ride the same curve, so scaling must
    /// stay on the <b>actual</b> axis — hence <see cref="ITapeCalibration.CapacityActual"/> is the scale
    /// reference, and the fallback is the curve's own top actual anchor, never a reported figure.
    /// </para>
    /// <para>
    /// The EW/EOM tail is the ONLY interesting region, yet it is a physical CONSTANT (LTO-3: ~0.45 GB,
    /// LTO-4: ~32 GB), independent of cartridge size. Scaled naively onto a small test cartridge it shrinks
    /// to a few KB — well below one writer buffer — so the emulated behavior would never surface. We therefore
    /// GUARANTEE the emulated EW zone spans at least <see cref="MinEmulatedEarlyWarningZone"/>, then map the
    /// source's BODY and TAIL onto their two virtual segments INDEPENDENTLY (a piecewise-linear rescale — the
    /// same "magnified tail" idea the calibration graph uses). Total capacity stays exact, the EW landmark and
    /// the reported-curve shape stay consistent, and the tail is blown up enough to observe. Both actual- and
    /// reported-remaining ride the SAME map, which is what preserves the (reported − actual) over-report.
    /// </para>
    /// </summary>
    public static VirtualTapeEwProfile FromCalibration(ITapeCalibration calibration, long targetCapacity)
    {
        System.ArgumentNullException.ThrowIfNull(calibration);
        if (targetCapacity < 0) targetCapacity = 0;

        // Stay on the ACTUAL axis: mixing in a reported figure here would silently mis-scale the model.
        long sourceCapacity = calibration.CapacityActual > 0
            ? calibration.CapacityActual
            : calibration.Curve.Count > 0 ? calibration.Curve[^1].ActualRemaining : 0L;

        // The source's EW/EOM tail, clamped into [0, sourceCapacity].
        long sourceEwZone = System.Math.Clamp(calibration.EwToEomDistance, 0L, sourceCapacity);

        double capacityScale = sourceCapacity > 0 ? (double)targetCapacity / sourceCapacity : 1.0;

        // Degenerate inputs (no source tail, or an empty medium) can't support a magnified tail — fall back to
        //  the original single-scale linear model (which reduces to the legacy passthrough when the curve is flat).
        if (sourceEwZone <= 0 || sourceCapacity <= 0 || targetCapacity <= 0)
        {
            long LinearModel(long actualWritten, long cap)
            {
                long targetActualRemaining = System.Math.Max(0L, cap - actualWritten);
                long sourceActualRemaining = (long)System.Math.Round(targetActualRemaining / (capacityScale == 0 ? 1.0 : capacityScale));
                long sourceReported = calibration.TranslateActualToReported(sourceActualRemaining);

                return System.Math.Max(0L, (long)System.Math.Round(sourceReported * capacityScale));
            }

            return new VirtualTapeEwProfile
            {
                EarlyWarningZone = (long)System.Math.Round(sourceEwZone * capacityScale),
                ReportedRemainingModel = LinearModel,
            };
        }

        // Floor the emulated EW zone to one writer buffer so it is observable, but never past capacity (a
        //  sub-buffer cartridge simply becomes all-EW). All four segment lengths below are >= 1, so the
        //  piecewise maps can never divide by zero.
        long ewZone = System.Math.Clamp(
            System.Math.Max(
                (long)System.Math.Round(sourceEwZone * capacityScale),
                System.Math.Min(MinEmulatedEarlyWarningZone, targetCapacity)),
            0L, targetCapacity);

        long targetBody = System.Math.Max(1L, targetCapacity - ewZone);
        long sourceBody = System.Math.Max(1L, sourceCapacity - sourceEwZone);

        // Piecewise-linear remaining maps. Tail segment [0, ewZone] ↔ source [0, sourceEwZone] (magnified);
        //  body segment the rest. Reported and actual are both byte counts on the SAME remaining axis, so BOTH
        //  ride these maps — that is what keeps the (reported − actual) over-report faithful after rescaling.
        long ToSource(long virtualRemaining)
            => virtualRemaining <= ewZone
                ? (long)System.Math.Round((double)virtualRemaining / ewZone * sourceEwZone)
                : sourceEwZone + (long)System.Math.Round((double)(virtualRemaining - ewZone) / targetBody * sourceBody);

        long ToVirtual(long sourceRemaining)
            => sourceRemaining <= sourceEwZone
                ? (long)System.Math.Round((double)sourceRemaining / sourceEwZone * ewZone)
                : ewZone + (long)System.Math.Round((double)(sourceRemaining - sourceEwZone) / sourceBody * targetBody);

        long Model(long actualWritten, long cap)
        {
            long targetActualRemaining = System.Math.Max(0L, cap - actualWritten);

            // Virtual → source (magnified tail), translate on the source curve, then source → virtual.
            long sourceActualRemaining = ToSource(targetActualRemaining);
            long sourceReported = calibration.TranslateActualToReported(sourceActualRemaining);

            return System.Math.Max(0L, ToVirtual(sourceReported));
        }

        return new VirtualTapeEwProfile
        {
            EarlyWarningZone = ewZone,
            ReportedRemainingModel = Model,
        };
    }

    #endregion
}
