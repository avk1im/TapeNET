using TapeLibNET.Drive;
namespace TapeLibNET.Media;

/// <summary>
/// The on-tape layout a drive+media combination implies, derived from drive capabilities and media
///  organization ALONE — no tape motion, no TOC, no probing.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="TapeNavigator.ProduceNavigator"/>, which made the same decision but made it
///  WHILE CONSTRUCTING a navigator over a TOC. Scan Media has no TOC, and needs the answer before the
///  tape moves — so the decision becomes a value and the factory is reimplemented on top of it. The two
///  can no longer disagree.
/// </para>
/// <para>
/// Worth reporting even when the scan then finds nothing readable: on a cartridge that yields no
///  fragments, knowing WHAT SHOULD HAVE BEEN THERE is the whole diagnosis.
/// </para>
/// </remarks>
/// <param name="NavigatorKind">Name of the navigator this layout selects — for display and the scan map.</param>
/// <param name="UseSmks">Whether to use setmarks to delimit content sets.</param>
/// <param name="TocInPartition">TOC lives in the initiator partition rather than after the content.</param>
/// <param name="HasTocMark">A gap file plus three filemarks separates content from TOC.</param>
/// <param name="MediaLoaded">
/// Whether media was loaded when the prediction was made. FALSE means partitioning could not be observed,
///  so <see cref="TocInPartition"/> is a floor, not a fact — see <see cref="Predict"/>.
/// </param>
public readonly record struct TapeMediaLayout(
    string            NavigatorKind,
    bool              UseSmks,
    bool              TocInPartition,
    bool              HasTocMark,
    bool              MediaLoaded)
{
    /// <summary>
    /// Derives the layout for <paramref name="drive"/>. Pure: queries capabilities only, moves nothing.
    /// </summary>
    /// <param name="useTOCMark">
    /// Mirrors <see cref="TapeNavigator.ProduceNavigator"/>'s parameter: when false, a sequential-filemark
    ///  drive uses the plain filemark layout instead of the dedicated TOC marker sequence.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Works with NO media loaded</b>, which is the point: a UI can state the expected layout before the
    ///  user inserts a cartridge, and a scan that fails at the first read still reports it.
    /// </para>
    /// <para>
    /// <b>The one thing media-absence costs.</b> <see cref="TapeDrive.HasInitiatorPartition"/> reads media
    ///  parameters, so with no cartridge it answers false and the prediction falls through to a
    ///  single-partition layout. That is the right floor — an unpartitioned guess never sends a reader into
    ///  the wrong partition — and <see cref="MediaLoaded"/> says so plainly rather than letting the caller
    ///  mistake the floor for a finding.
    /// </para>
    /// <para>
    /// Ordering follows the factory exactly — partitioning, then setmarks, then the TOC mark — because the
    ///  tests assert the two agree profile by profile.
    /// </para>
    /// </remarks>
    public static TapeMediaLayout Predict(TapeDrive drive, bool useTOCMark = true)
    {
        ArgumentNullException.ThrowIfNull(drive);

        bool mediaLoaded = drive.IsMediaLoaded;

        // Initiator partition: the TOC lives in its own partition, and the content partition is delimited
        //  by real setmarks when the drive has them (the factory sets UseSmks = SupportsSetmarks here).
        if (mediaLoaded && drive.HasInitiatorPartition)
            return new TapeMediaLayout(
                nameof(TapeNavigatorTOCInPartition),
                UseSmks:        drive.SupportsSetmarks,
                TocInPartition: true,
                HasTocMark:     false,
                MediaLoaded:    mediaLoaded);

        // Real setmarks: sets are setmark-delimited, the TOC filemark-delimited — the one layout where
        //  mark TYPE alone distinguishes content from TOC.
        if (drive.SupportsSetmarks)
            return new TapeMediaLayout(
                nameof(TapeNavigatorTOCInSetWithSmks),
                UseSmks:        drive.SupportsSetmarks,
                TocInPartition: false,
                HasTocMark:     false,
                MediaLoaded:    mediaLoaded);

        // Sequential filemarks + TOC mark: [FM][gap][FM][FM][FM][toc1]…
        if (drive.SupportsSeqFilemarks && useTOCMark)
            return new TapeMediaLayout(
                nameof(TapeNavigatorTOCInSetWithFmksAndTOCMark),
                UseSmks:        false,
                TocInPartition: false,
                HasTocMark:     true,
                MediaLoaded:    mediaLoaded);

        // Plain filemarks: separators and TOC delimiters are the SAME mark, so only classifying the block
        //  after each mark can tell content from TOC. That is why identification, not bounding, is the
        //  scanner's core.
        return new TapeMediaLayout(
            nameof(TapeNavigatorTOCInSetWithFmks),
            UseSmks:        false,
            TocInPartition: false,
            HasTocMark:     false,
            MediaLoaded:    mediaLoaded);
    }

    /// <summary>Whether the TOC is delimited by the same mark type that separates content sets.</summary>
    /// <remarks>
    /// True only on the filemark layouts. The single fact that makes a mark-hopping walk insufficient on
    ///  its own — and therefore the reason the scanner classifies every block it lands on.
    /// </remarks>
    public bool SeparatorAmbiguousWithToc
        => !TocInPartition && !UseSmks;

    /// <inheritdoc/>
    public override string ToString()
        => $"{NavigatorKind} — {(UseSmks ? "setmark" : "filemark")} separators" +
           (TocInPartition ? ", TOC in partition" : HasTocMark ? ", TOC mark" : ", TOC in set") +
           (MediaLoaded ? "" : " (predicted without media)");
}
