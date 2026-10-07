using System.Text.Json;
using System.Text.Json.Serialization;

using TapeLibNET.Media;
using TapeLibNET.Calibration;

namespace TapeLibNET.Scan;


/// <summary>What kind of cartridge the scan concluded this is.</summary>
public enum ScannedMediaKind
{
    /// <summary>Nothing readable at all — EOD at block 0. A clean, successful finding, not a failure (SM-6).</summary>
    Blank = 0,

    /// <summary>Carries backup content (a media header, set headers, or both).</summary>
    Backup,

    /// <summary>Carries a calibration trail. Identified, then deliberately NOT walked (SM-7).</summary>
    CalibrationCartridge,

    /// <summary>Readable, but nothing on it is ours.</summary>
    Foreign,
}

/// <summary>
/// The result of a Scan Media pass: everything identifiable on a cartridge, in tape order, derived from
///  the medium ALONE (SM-2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scan once, compare many.</b> The map holds no verdicts (SM-3), so it stays true regardless of which
///  table of contents turns up later. A caller may diff it against the loaded TOC, then against an
///  imported one, then against a copy harvested off this very cartridge — without touching the drive again.
/// </para>
/// <para>
/// Round-trips to JSON, which buys three things for almost nothing: the comparison phase need not re-scan,
///  a user can attach a map to a support request, and a scan taken before a repair becomes the
///  before-picture of one taken after.
/// </para>
/// </remarks>
public sealed record MediaScanMap
{
    /// <summary>The layout this drive+media implies — known BEFORE the tape moved (§2).</summary>
    /// <remarks>
    /// Reported even when the scan then finds nothing: on a cartridge that yields no fragments, knowing
    ///  what should have been there is the whole diagnosis.
    /// </remarks>
    public required TapeMediaLayout Layout { get; init; }

    /// <summary>What kind of cartridge this turned out to be.</summary>
    public required ScannedMediaKind Kind { get; init; }

    /// <summary>Every identified fragment, in tape order.</summary>
    public required IReadOnlyList<TapeMediaFragment> Fragments { get; init; }

    /// <summary>When the scan ran (UTC).</summary>
    public required DateTime ScannedUtc { get; init; }

    /// <summary>
    /// The map is INCOMPLETE: the walk was aborted, hit a transport fault, or ran into
    ///  <see cref="ScanMediaOptions.MaxFragments"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Load-bearing (SM-5).</b> A partial map presented as complete is worse than no map at all,
    ///  because the comparison phase would read the silence past the cut as ABSENCE — and conclude sets
    ///  are missing that are merely unscanned.
    /// </para>
    /// <para>
    /// Deliberately FALSE for a calibration cartridge (SM-7): stopping at the calibration header is a
    ///  complete and correct conclusion, not a truncation.
    /// </para>
    /// </remarks>
    public bool Truncated { get; init; }

    /// <summary>
    /// Why the walk ended. <c>ERROR_NO_DATA_DETECTED</c> on a clean end — the normal, expected terminator.
    /// </summary>
    public uint TerminatorWin32 { get; init; }

    /// <summary>
    /// Checkpoint-derived state of a calibration trail (resumable / complete / progress), when
    ///  <see cref="ScanMediaOptions.InspectCalibrationTrail"/> was set and a trail was found.
    /// </summary>
    /// <remarks>
    /// Obtained by CALLING <see cref="TapeCalibrator.InspectMedia"/> — the calibrator already owns the
    ///  legacy run-block probe and the backward checkpoint walk, so the scanner reuses it rather than
    ///  duplicating a code path for a cartridge kind it does not otherwise care about. Excluded from JSON,
    ///  being outside this feature's serialization contract.
    /// </remarks>
    [JsonIgnore]
    public TapeCalibrationMediaInfo? CalibrationInfo { get; init; }

    // ── Derived views ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cartridge's series identity, from its media header; null when none was found.</summary>
    [JsonIgnore]
    public Guid? MediaId => Fragments.FirstOrDefault(f => f.Kind == FragmentKind.MediaHeader)?.Id;

    /// <summary>The volume number, from the media header.</summary>
    [JsonIgnore]
    public int? Volume => Fragments.FirstOrDefault(f => f.Kind == FragmentKind.MediaHeader)?.Volume;

    /// <summary>Backup sets counted FROM THE TAPE — not from an index that may be wrong.</summary>
    [JsonIgnore]
    public int SetCount => Fragments.Count(f => f.Kind == FragmentKind.SetHeader);

    /// <summary>TOC copies found, whether or not they were harvested.</summary>
    [JsonIgnore]
    public int TocCopyCount => Fragments.Count(f => f.Kind == FragmentKind.TOC);

    /// <summary>Fragments that could not be identified at all.</summary>
    [JsonIgnore]
    public int UnknownCount => Fragments.Count(f => f.Kind == FragmentKind.Unknown);

    /// <summary>
    /// The last backup set on the tape was never closed by a separator — a backup that died mid-set.
    /// </summary>
    /// <remarks>
    /// The dominant real-world fault, and detectable with NO TOC whatsoever. Note a half-written set still
    ///  carries a PERFECTLY HEALTHY set header — it is written at the set's start, before the first file —
    ///  so the missing closing mark is the only thing that proves it never completed.
    /// </remarks>
    [JsonIgnore]
    public bool LastSetUnclosed
        => Fragments.LastOrDefault(f => f.Kind == FragmentKind.SetHeader) is { ClosedBySeparator: false };

    /// <summary>
    /// Set headers whose identity disagrees with the media header — another series has physically
    ///  overwritten part of this cartridge.
    /// </summary>
    /// <remarks>
    /// Reported as an OBSERVATION, never a verdict (SM-3). Empty when no media header was found, since
    ///  there is then nothing to disagree with.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<TapeMediaFragment> MixedIdentityFragments
    {
        get
        {
            if (MediaId is not { } id)
                return [];

            return [.. Fragments.Where(f => f.Kind == FragmentKind.SetHeader
                                            && f.Id is { } fid && fid != id)];
        }
    }

    /// <summary>
    /// Gaps in the on-volume set numbering (e.g. 1, 2, 4) — a set is missing from the middle.
    ///  Self-evident from the set headers alone.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<int> SetIndexGaps
    {
        get
        {
            List<int> gaps = [];
            int? previous = null;

            foreach (var f in Fragments.Where(f => f.Kind == FragmentKind.SetHeader))
            {
                if (f.VolumeSetIndex is not { } index)
                    continue;

                // Only a FORWARD jump is a gap. A repeat or a step backwards means something stranger than
                //  a missing set, and belongs to the comparison phase to interpret.
                if (previous is { } prev && index > prev + 1)
                    for (int missing = prev + 1; missing < index; missing++)
                        gaps.Add(missing);

                previous = index;
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
            // A diagnostic artifact that will not parse is not an exceptional condition for the caller —
            //  it is simply not a map.
            return null;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Kind}: {Fragments.Count} fragment(s), {SetCount} set(s), {TocCopyCount} TOC copy/copies" +
           (LastSetUnclosed ? ", last set unclosed" : "") +
           (Truncated ? " — TRUNCATED" : "");
}
