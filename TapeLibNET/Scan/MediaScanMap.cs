// Save as: TapeLibNET/Scan/MediaScanMap.cs
using System.Text.Json;
using System.Text.Json.Serialization;

using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Media;

namespace TapeLibNET.Scan;


/// <summary>What kind of cartridge the scan concluded this is.</summary>
public enum ScannedMediaKind
{
    /// <summary>Nothing readable in ANY partition. A clean, successful finding, not a failure (SM-6).</summary>
    Blank = 0,
    /// <summary>
    /// Carries backup content — in any partition: a media header, set headers, header-less 2.1 sets, or a
    ///  table-of-contents copy of ours.
    /// </summary>
    Backup,
    /// <summary>Carries a calibration trail. Identified, then deliberately NOT walked (SM-7).</summary>
    CalibrationCartridge,
    /// <summary>Readable, but nothing on it is ours.</summary>
    Foreign,
}

/// <summary>How the walk of one partition ended.</summary>
/// <param name="Partition">Which partition.</param>
/// <param name="FragmentCount">Fragments found in it.</param>
/// <param name="Truncated">
/// The walk of this partition ended early — abort, transport fault, fragment guard — or it was never walked because the
///  scan stopped before reaching it.
/// </param>
/// <param name="TerminatorWin32">Why the walk ended; <c>ERROR_NO_DATA_DETECTED</c> on a clean end.</param>
/// <remarks>
/// The partition-level counterpart of <see cref="MediaScanMap.Truncated"/>: "the TOC partition is EMPTY" and "the TOC
///  partition was NOT SCANNED" must never read the same.
/// </remarks>
public sealed record ScannedPartition(
    MediaPartition Partition,
    int FragmentCount,
    bool Truncated,
    uint TerminatorWin32)
{
    /// <summary>Walked to its end and nothing was there.</summary>
    [JsonIgnore]
    public bool Blank => FragmentCount == 0 && !Truncated;
}

/// <summary>
/// The result of a Scan Media pass: everything identifiable on a cartridge, in tape order, partition by partition, derived
///  from the medium ALONE (SM-2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scan once, compare many.</b> The map holds no verdicts (SM-3), so it stays true regardless of which table of
///  contents turns up later.
/// </para>
/// <para>
/// <b>Order:</b> the content partition's fragments first, then the initiator partition's; ordinals run contiguously
///  across both. <see cref="TapeMediaFragment.StartBlock"/> is relative to the fragment's own
///  <see cref="TapeMediaFragment.Partition"/>.
/// </para>
/// <para>
/// Round-trips to JSON: the comparison phase need not re-scan, a user can attach a map to a support request, and a scan
///  taken before a repair becomes the before-picture of one taken after.
/// </para>
/// </remarks>
public sealed record MediaScanMap
{
    /// <summary>The layout this drive+media implies — known BEFORE the tape moved (§2).</summary>
    public required TapeMediaLayout Layout { get; init; }

    /// <summary>What kind of cartridge this turned out to be.</summary>
    public required ScannedMediaKind Kind { get; init; }

    /// <summary>Every identified fragment, in tape order: content partition first, then the initiator partition.</summary>
    public required IReadOnlyList<TapeMediaFragment> Fragments { get; init; }

    /// <summary>One entry per partition the medium has, in the order walked.</summary>
    public IReadOnlyList<ScannedPartition> Partitions { get; init; } = [];

    /// <summary>When the scan ran (UTC).</summary>
    public required DateTime ScannedUtc { get; init; }

    /// <summary>
    /// The map is INCOMPLETE: some partition's walk was aborted, hit a transport fault or
    ///  <see cref="ScanMediaOptions.MaxFragments"/>, or a partition was never reached.
    /// </summary>
    /// <remarks>
    /// <b>Load-bearing (SM-5).</b> A partial map presented as complete is worse than none: the comparison phase would read
    ///  the silence past the cut as ABSENCE. Deliberately FALSE for a calibration cartridge (SM-7).
    /// </remarks>
    public bool Truncated { get; init; }

    /// <summary>
    /// Why the scan ended: the first truncating partition's reason, else <c>ERROR_NO_DATA_DETECTED</c> — the normal end.
    /// </summary>
    public uint TerminatorWin32 { get; init; }

    /// <summary>
    /// Checkpoint-derived state of a calibration trail, when <see cref="ScanMediaOptions.InspectCalibrationTrail"/> was
    ///  set and a trail was found. Excluded from JSON.
    /// </summary>
    [JsonIgnore]
    public TapeCalibrationMediaInfo? CalibrationInfo { get; init; }

    // ── Derived views ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fragments of one partition, in tape order.</summary>
    public IEnumerable<TapeMediaFragment> FragmentsIn(MediaPartition partition)
        => Fragments.Where(f => f.Partition == partition);

    /// <summary>
    /// The media header that speaks for the cartridge: the first one in the content partition, else the first one
    ///  anywhere (partitions mixed up). Null when none was found.
    /// </summary>
    [JsonIgnore]
    public TapeMediaFragment? PrimaryMediaHeader
        => Fragments.FirstOrDefault(f => f.Kind == FragmentKind.MediaHeader && f.Partition == MediaPartition.Content)
        ?? Fragments.FirstOrDefault(f => f.Kind == FragmentKind.MediaHeader);

    /// <summary>The cartridge's series identity, from its primary media header; null when none was found.</summary>
    [JsonIgnore]
    public Guid? MediaId => PrimaryMediaHeader?.Id;

    /// <summary>The volume number, from the primary media header.</summary>
    [JsonIgnore]
    public int? Volume => PrimaryMediaHeader?.Volume;

    /// <summary>
    /// Backup sets counted FROM THE TAPE, in every partition: by a set header, or — for sets written without one — by
    ///  their first file's 2.1 header frame. Legacy sets without headers are not counted; a recovered TOC lists them.
    /// </summary>
    [JsonIgnore]
    public int SetCount => Fragments.Count(f => f.Kind is FragmentKind.SetHeader or FragmentKind.SetContent);

    /// <summary>TOC copies found in any partition, whether or not they were harvested.</summary>
    [JsonIgnore]
    public int TocCopyCount => Fragments.Count(f => f.Kind == FragmentKind.TOC);

    /// <summary>Fragments that could not be identified at all.</summary>
    [JsonIgnore]
    public int UnknownCount => Fragments.Count(f => f.Kind == FragmentKind.Unknown);

    /// <summary>
    /// In some partition, the last backup set was never closed by a separator — a backup that died mid-set.
    /// </summary>
    /// <remarks>
    /// Per partition: a set header in the initiator partition (partitions mixed up) must not hide an unclosed last set in
    ///  the content partition by coming later in the map.
    /// </remarks>
    [JsonIgnore]
    public bool LastSetUnclosed
        => Fragments.GroupBy(f => f.Partition)
                    .Any(g => g.LastOrDefault(f => f.Kind == FragmentKind.SetHeader) is { ClosedBySeparator: false });

    /// <summary>The last set header of the partition whose last set is unclosed — for naming it. Null otherwise.</summary>
    [JsonIgnore]
    public TapeMediaFragment? UnclosedLastSet
        => Fragments.GroupBy(f => f.Partition)
                    .Select(g => g.LastOrDefault(f => f.Kind == FragmentKind.SetHeader))
                    .FirstOrDefault(f => f is { ClosedBySeparator: false });

    /// <summary>
    /// Headers whose identity disagrees with the primary media header — another series has physically overwritten part
    ///  of this cartridge. Set headers and further media headers, in any partition.
    /// </summary>
    /// <remarks>
    /// An OBSERVATION, never a verdict (SM-3). Empty when no media header was found. A <see cref="FragmentKind.SetContent"/>
    ///  carries a SetId, not a MediaId, and is never compared.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<TapeMediaFragment> MixedIdentityFragments
    {
        get
        {
            TapeMediaFragment? primary = PrimaryMediaHeader;
            if (primary?.Id is not { } id)
                return [];
            return [.. Fragments.Where(f => f.Kind is FragmentKind.SetHeader or FragmentKind.MediaHeader
                                            && !ReferenceEquals(f, primary)
                                            && f.Id is { } fid && fid != id)];
        }
    }

    /// <summary>
    /// Fragments standing in a partition this layout does not put them in: in the initiator partition, anything that
    ///  belongs to content (headers, sets); in the content partition of a partitioned layout, a TOC copy. Evidence that the
    ///  partitions were mixed up, or that something wrote into the wrong one.
    /// </summary>
    /// <remarks>
    /// Relative to <see cref="Layout"/>, like <see cref="MixedIdentityFragments"/> is relative to the media header — an
    ///  observation the map can make on its own, not a comparison with a TOC. Unidentified data is counted elsewhere.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<TapeMediaFragment> OutOfPlaceFragments
        => [.. Fragments.Where(f => f.Partition == MediaPartition.Initiator
            ? f.Kind is FragmentKind.MediaHeader or FragmentKind.SetHeader or FragmentKind.SetContent
                     or FragmentKind.CalibrationHeader
            : Layout.TocInPartition && f.Kind == FragmentKind.TOC)];

    /// <summary>
    /// Gaps in the on-volume set numbering (e.g. 1, 2, 4) — a set is missing from the middle. Per partition, from the set
    ///  headers alone.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<int> SetIndexGaps
    {
        get
        {
            List<int> gaps = [];
            foreach (var partition in Fragments.GroupBy(f => f.Partition))
            {
                int? previous = null;
                foreach (var f in partition.Where(f => f.Kind == FragmentKind.SetHeader))
                {
                    if (f.VolumeSetIndex is not { } index)
                        continue;
                    // Only a FORWARD jump is a gap. A repeat or a step backwards is stranger than a missing set, and
                    //  belongs to the comparison phase.
                    if (previous is { } prev && index > prev + 1)
                        for (int missing = prev + 1; missing < index; missing++)
                            gaps.Add(missing);
                    previous = index;
                }
            }
            return gaps;
        }
    }

    // ── Serialization ────────────────────────────────────────────────────────────────────────────────

    /// <summary>File extension for an exported scan map.</summary>
    public const string MapFileExtension = ".tapescan";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes the map to indented JSON. Harvested TOCs are saved separately (§4.3).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    /// <summary>Reads a map back from JSON. Returns null when the text is not a valid map.</summary>
    public static MediaScanMap? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MediaScanMap>(json, s_json);
        }
        catch (JsonException)
        {
            // A diagnostic artifact that will not parse is simply not a map.
            return null;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Kind}: {Fragments.Count} fragment(s), {SetCount} set(s), {TocCopyCount} TOC copy/copies" +
           (Partitions.Count > 1
               ? " — " + string.Join(", ", Partitions.Select(p =>
                   $"{p.Partition}: {(p.Blank ? "blank" : $"{p.FragmentCount}")}{(p.Truncated ? " TRUNCATED" : "")}"))
               : "") +
           (LastSetUnclosed ? ", last set unclosed" : "") +
           (OutOfPlaceFragments.Count > 0 ? $", {OutOfPlaceFragments.Count} out of place" : "") +
           (Truncated ? " — TRUNCATED" : "");
}
