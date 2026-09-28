namespace TapeLibNET.Scan;

/// <summary>
/// Tuning for a Scan Media pass. All defaults are safe on an unknown, possibly damaged cartridge.
/// </summary>
/// <param name="HarvestTocCopies">
/// Deserialize TOC copies, not merely detect them. OFF by default: the signature probe is a ~16-byte
///  comparison, while a harvest reads a multi-block stream at the TOC's own block size mid-walk and can
///  fail on a damaged copy AFTER the signature matched. The service turns it on, because the payoff — a
///  table of contents recovered from the tape itself — is large.
/// </param>
/// <param name="InspectCalibrationTrail">
/// On a calibration cartridge, enrich the map by calling <see cref="TapeCalibrator.InspectMedia"/> for the
///  checkpoint-derived run state. Cheap, read-only, and the only thing that makes such a cartridge's entry
///  useful rather than merely correct.
/// </param>
/// <param name="MaxFragments">
/// Runaway guard. A pathological medium can yield marks indefinitely; hitting this stops the walk and sets
///  <see cref="MediaScanMap.Truncated"/>, so the map is never presented as complete (SM-5).
/// </param>
public sealed record ScanMediaOptions
{
    /// <summary>Guard against a runaway walk when the caller left <see cref="MaxFragments"/> unset.</summary>
    public const int DefaultMaxFragments = 1_000;

    /// <summary>Deserialize TOC copies, not merely detect them. See §4.3.</summary>
    public bool HarvestTocCopies { get; init; } = false;

    /// <summary>Enrich a calibration cartridge's entry with its checkpoint-derived run state (§4.4).</summary>
    public bool InspectCalibrationTrail { get; init; } = false;

    /// <summary>Runaway guard; hitting it truncates the map (SM-5).</summary>
    public int MaxFragments { get; init; } = DefaultMaxFragments;

    /// <summary>Detect TOC copies without deserializing them; inspect calibration trails.</summary>
    public static ScanMediaOptions Default { get; } = new();
}


/// <summary>
/// A progress sample emitted during a scan, suitable for <see cref="IProgress{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="IProgress{T}"/>, deliberately — not <c>ITapeFileNotifiable</c>.</b> The scan uses none of
///  that interface's members, and no existing implementer would ever use a fragment member added to it.
///  More decisively: its callbacks return <see langword="void"/>, so the ONLY way an implementer can stop
///  an operation is by throwing — which is why <see cref="TapeAgentBase"/> is thick with
///  <see cref="TapeAbortRequestedException"/> handling. The scanner owns a cooperative abort flag and
///  polls it between fragments, so adopting the notifiable would import an exception-based control-flow
///  protocol to solve a problem that no longer exists.
/// </para>
/// <para>
/// Mirrors <see cref="TapeCalibrationProgress"/>, the other read/write verb that drives only the public
///  <see cref="TapeDrive"/> surface and reports through <see cref="IProgress{T}"/>.
/// </para>
/// </remarks>
/// <param name="FragmentOrdinal">Fragments recorded so far — the natural progress counter.</param>
/// <param name="CurrentBlock">Where the head sits, for a position readout.</param>
/// <param name="Kind">What the fragment just recorded turned out to be.</param>
/// <param name="Phase">Coarse stage, for status text. See the <c>Phase*</c> constants.</param>
/// <param name="Fragment">The fragment just recorded; null on a phase-only tick.</param>
public readonly record struct TapeScanProgress(
    int                FragmentOrdinal,
    long               CurrentBlock,
    FragmentKind       Kind,
    string             Phase,
    TapeMediaFragment? Fragment)
{
    /// <summary>Walking the medium, identifying fragments — the bulk of any scan.</summary>
    public const string PhaseScanning = "scanning";

    /// <summary>Deserializing a table-of-contents copy.</summary>
    public const string PhaseHarvestingToc = "harvesting-toc";

    /// <summary>Reading a calibration trail's checkpoints.</summary>
    public const string PhaseInspectingCalibration = "inspecting-calibration";

    /// <summary>The walk has ended; the map is being finalized.</summary>
    public const string PhaseCompleting = "completing";

    /// <summary>Builds a fragment-bearing sample.</summary>
    public static TapeScanProgress ForFragment(TapeMediaFragment fragment, long currentBlock)
        => new(fragment.Ordinal, currentBlock, fragment.Kind, PhaseScanning, fragment);

    /// <summary>Builds a phase-only sample, carrying no fragment.</summary>
    public static TapeScanProgress ForPhase(string phase, int ordinal, long currentBlock)
        => new(ordinal, currentBlock, FragmentKind.Unknown, phase, null);
}
