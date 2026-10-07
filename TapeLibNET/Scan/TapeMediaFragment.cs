using System.Text.Json.Serialization;

using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Scan;


/// <summary>What a scanned fragment turned out to be. An OBSERVATION, never a verdict (SM-3).</summary>
/// <remarks>
/// Words like <i>complete</i>, <i>lost</i> or <i>expected</i> are deliberately absent: those need a TOC to
///  compare against, and belong to the later comparison phase. Keeping this enum verdict-free is what lets
///  one scan be compared against several candidate TOCs.
/// </remarks>
public enum FragmentKind
{
    /// <summary>Could not be identified — random data, a foreign structure, or a torn record.</summary>
    Unknown = 0,

    /// <summary>A <see cref="TapeMediaHeader"/> — normally at block 0 of the content partition.</summary>
    MediaHeader,

    /// <summary>A <see cref="TapeSetHeader"/> at the front of a backup set.</summary>
    SetHeader,

    /// <summary>A <see cref="TapeCalibrationHeader"/> — this is a calibration cartridge (SM-7).</summary>
    CalibrationHeader,

    /// <summary>A table-of-contents copy. Signature-identified; deserialized only on request.</summary>
    TOC,

    /// <summary>
    /// Marks directly adjacent to the mark that closed the previous fragment, with no data between them —
    ///  an erased region, or a double-filemark end of data.
    /// </summary>
    MarkRun,

    /// <summary>
    /// The TOC mark of the sequential-filemark layout: a gap block followed by a run of filemarks, just
    ///  before the first TOC copy. Identified by its shape (see <c>TapeScanner.FoldMarkRun</c>).
    /// </summary>
    TocMark,

}

/// <summary>
/// One identifiable object found on the medium: what it is, and where it begins. The atom of a
///  <see cref="MediaScanMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>States what EXISTS, never what SHOULD.</b> A fragment is true regardless of which TOC surfaces
///  later, which is what makes a scan reusable: serialize it, compare it against one TOC, then another,
///  attach it to a support report — all without touching the cartridge again.
/// </para>
/// <para>
/// A fragment's CONTENTS are opaque (SM-11). The scan proves an object begins at a block and that a
///  separator closes it. It never reads files, never checks CRCs, and never measures a set's payload.
/// </para>
/// </remarks>
public sealed record TapeMediaFragment
{
    /// <summary>0-based position in the walk. Media order, always contiguous.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Logical block where this fragment begins, as reported by the drive.</summary>
    public required long StartBlock { get; init; }

    /// <summary>What this fragment turned out to be.</summary>
    public required FragmentKind Kind { get; init; }

    /// <summary>
    /// Blocks from here to the next fragment's start; -1 for the last fragment.
    /// </summary>
    /// <remarks>
    /// <b>An upper bound INCLUDING marks — never a payload size (SM-11).</b> Nothing in the scan measures
    ///  a set's real footprint, so every consumer must treat this as indicative. Reporting it as a size
    ///  would put a plausible, wrong number in front of a user making a capacity decision.
    /// </remarks>
    public long BlockSpan { get; init; } = -1L;

    /// <summary>
    /// Whether a separator mark followed this fragment.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> on a set header means EOD arrived instead — i.e. the set was never closed,
    ///  which is the dominant real-world fault and is detectable here with NO TOC whatsoever. See
    ///  <see cref="MediaScanMap.LastSetUnclosed"/>.
    /// </remarks>
    public bool ClosedBySeparator { get; init; }

    // ── Identity — populated for MediaHeader / SetHeader / CalibrationHeader ──────────────────────────

    /// <summary>The header's identity: a media/series id, or a calibration run id.</summary>
    /// <remarks>
    /// One slot for all three kinds, mirroring <see cref="TapeHeader.Id"/>, which each concrete header
    ///  re-exposes under its own domain name. Splitting it here would only re-introduce the aliasing the
    ///  header hierarchy already resolved.
    /// </remarks>
    public Guid? Id { get; init; }

    /// <summary>Volume number recorded in the header.</summary>
    public int? Volume { get; init; }

    /// <summary>Set index within its volume, from a set header.</summary>
    public int? VolumeSetIndex { get; init; }

    /// <summary>Set index across the whole series, from a set header.</summary>
    public int? GlobalSetIndex { get; init; }

    /// <summary>Media label, set description, or calibration profile key — whichever the kind carries.</summary>
    public string? Description { get; init; }

    /// <summary>Creation time recorded in the header (UTC).</summary>
    public DateTime? CreatedUtc { get; init; }

    /// <summary>Block size recorded in the header.</summary>
    public uint? BlockSize { get; init; }

    // ── TOC ──────────────────────────────────────────────────────────────────────────────

    /// <summary>TOC format version, when the copy was deserialized far enough to read it.</summary>
    public ushort? TocVersion { get; init; }

    /// <summary>
    /// The harvested table of contents — the most valuable thing recoverable from a damaged cartridge.
    ///  Null unless the harvest ran AND succeeded.
    /// </summary>
    /// <remarks>
    /// <b>Excluded from JSON</b> (and from equality, being a mutable reference): a harvested TOC is saved
    ///  beside the map as its own <c>.tapetoc</c>, which feeds the existing import path with no new
    ///  plumbing. Serializing it inline would duplicate a large structure into a diagnostic artifact.
    /// </remarks>
    [JsonIgnore]
    public TapeTOC? HarvestedToc { get; init; }

    // ── MarkRun / Unknown / TrailingRegion ───────────────────────────────────────────────────────────

    /// <summary>
    /// Marks directly adjacent to this fragment's closing mark, i.e. BEYOND it — for
    ///  <see cref="FragmentKind.MarkRun"/> and <see cref="FragmentKind.TocMark"/>.
    /// </summary>
    /// <remarks>
    /// Informational: drives are not relied on to report exactly one mark per read, and nothing in the scan
    ///  depends on the exact figure. Hence no dependency on drive's reporting seq. tapemarks exactly.
    /// </remarks>
    public int MarkCount { get; init; }


    /// <summary>
    /// First bytes of an unidentified block, hex-encoded — so a support report can tell "random data"
    ///  from "a structure we do not parse yet".
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// Why this fragment could not be identified, or why the walk stopped here.
    ///  <see cref="TapeResult.OK"/> for anything cleanly identified.
    /// <para>
    /// For <see cref="Kind"/> of <see cref="FragmentKind.TOC"/>, a TOC copy that was identified but
    ///  couldn't be recovered reports its reason here.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Initialized to <see cref="TapeResult.OK"/> explicitly: <c>default(TapeResult)</c> means FAILURE,
    ///  so an un-set field would report every healthy fragment as broken.
    /// </remarks>
    public TapeResult Diagnosis { get; init; } = TapeResult.OK;

    /// <summary>Bytes captured by <see cref="Fingerprint"/>.</summary>
    public const int FingerprintBytes = 32;

    /// <summary>Hex-encodes the first <see cref="FingerprintBytes"/> bytes of an unidentified block.</summary>
    public static string MakeFingerprint(byte[] block, int length)
        => block is null || length <= 0
            ? string.Empty
            : Convert.ToHexString(block.AsSpan(0, Math.Min(FingerprintBytes, Math.Min(length, block.Length))));

    /// <summary>True for the three kinds that carry a parsed <see cref="TapeHeader"/>.</summary>
    public bool IsHeader
        => Kind is FragmentKind.MediaHeader or FragmentKind.SetHeader or FragmentKind.CalibrationHeader;

    /// <summary>A never-empty, human-readable name for this fragment.</summary>
    public string DisplayName => Kind switch
    {
        FragmentKind.MediaHeader       => Description ?? $"Media {Id:N} · vol {Volume}",
        FragmentKind.SetHeader         => Description ?? $"Set #{VolumeSetIndex}",
        FragmentKind.CalibrationHeader => $"Calibration run {Id:N}",
        FragmentKind.TOC   => "Table of contents",
        FragmentKind.MarkRun           => $"{MarkCount} consecutive mark(s)",
        FragmentKind.TocMark => $"TOC mark (gap + {MarkCount + 1} filemarks)",
        _ => "Unidentified",
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Carries the diagnosis and a short fingerprint when present: a scan-test failure prints the whole map,
    ///  and "Unknown — Unidentified" alone cannot tell a failed read from bytes we do not recognize.
    /// </remarks>
    public override string ToString()
        => $"#{Ordinal} @ block {StartBlock}: {Kind} — {DisplayName}" +
           (ClosedBySeparator ? "" : " (not closed by a separator)") +
           (Diagnosis.Success ? "" : $" [{Diagnosis}]") +
           (Fingerprint is { Length: > 0 } fp ? $" <{fp[..Math.Min(16, fp.Length)]}…>" : "");
}
