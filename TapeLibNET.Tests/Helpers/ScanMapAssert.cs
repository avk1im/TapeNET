using System.Text;
using TapeLibNET.Scan;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Reading and asserting on a <see cref="MediaScanMap"/> — the shape a scan test wants, rather than the
///  flat fragment list the scanner produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a helper at all.</b> A map is a list of heterogeneous fragments whose INDICES shift with the
///  layout: a media header may or may not be present, TOC copies trail the content on three of the four
///  profiles, and mark runs appear only where a TOC mark does. Indexing into it by hand would make every
///  assertion layout-specific and every failure message a bare index mismatch.
/// </para>
/// <para>
/// <b>Every failure prints the whole map.</b> A scan test that fails tells you almost nothing from the
///  assertion alone — "expected 4 set headers, found 5" leaves the interesting question (WHICH fifth?)
///  unanswered. <see cref="Describe"/> is attached to every assertion here, mirroring what
///  <c>VirtualTapeMedia.FormatBlockLayout</c> does for the physical layer.
/// </para>
/// </remarks>
public static class ScanMapAssert
{
    #region *** Rendering ***

    /// <summary>Renders a map as one line per fragment — attached to every failure message here.</summary>
    public static string Describe(MediaScanMap? map)
    {
        if (map is null)
            return "(no map — the scan returned null)";

        var sb = new StringBuilder();

        sb.AppendLine($"{map.Kind} · {map.Layout.NavigatorKind} · {map.Fragments.Count} fragment(s)" +
                      (map.Truncated ? $" · TRUNCATED ({(Windows.Win32.Foundation.WIN32_ERROR)map.TerminatorWin32})" : "") +
                      $" · terminator {(Windows.Win32.Foundation.WIN32_ERROR)map.TerminatorWin32}");

        foreach (TapeMediaFragment f in map.Fragments)
            sb.AppendLine($"  {f}");

        return sb.ToString();
    }

    #endregion

    #region *** Selecting ***

    public static IReadOnlyList<TapeMediaFragment> OfKind(MediaScanMap map, FragmentKind kind)
        => [.. map.Fragments.Where(f => f.Kind == kind)];

    public static IReadOnlyList<TapeMediaFragment> SetHeaders(MediaScanMap map)
        => OfKind(map, FragmentKind.SetHeader);

    /// <summary>The media header, or null on legacy media — never throws, since "absent" is a finding.</summary>
    public static TapeMediaFragment? MediaHeader(MediaScanMap map)
        => map.Fragments.FirstOrDefault(f => f.Kind == FragmentKind.MediaHeader);

    /// <summary>The fragment at a given block, or null. Blocks, not ordinals — the tape's own coordinate.</summary>
    public static TapeMediaFragment? AtBlock(MediaScanMap map, long block)
        => map.Fragments.FirstOrDefault(f => f.StartBlock == block);

    #endregion

    #region *** Structural assertions ***

    /// <summary>
    /// Asserts the map holds exactly <paramref name="expected"/> set headers.
    /// </summary>
    /// <remarks>
    /// <b>The exact count is the point.</b> On the filemark layouts the set separator and the TOC delimiter
    ///  are the same mark, so a walk that over-runs the content reads a TOC copy and reports it as a
    ///  phantom set. "At least N" would pass on exactly the cartridge the scanner must get right.
    /// </remarks>
    public static void SetCount(MediaScanMap map, int expected)
        => Assert.True(map.SetCount == expected,
            $"Expected exactly {expected} set header(s), found {map.SetCount}.\n{Describe(map)}");

    /// <summary>Asserts the map's fragment kinds, in tape order, are exactly these.</summary>
    /// <remarks>
    /// The strictest available statement about a scan, and the right one for a healthy cartridge whose
    ///  layout is fully known. Prefer the looser assertions for damaged media, where the tail is the
    ///  variable under test.
    /// </remarks>
    public static void KindSequence(MediaScanMap map, params FragmentKind[] expected)
    {
        var actual = map.Fragments.Select(f => f.Kind).ToArray();

        Assert.True(expected.SequenceEqual(actual),
            $"Fragment kinds differ.\n  expected: {string.Join(", ", expected)}\n" +
            $"  actual:   {string.Join(", ", actual)}\n{Describe(map)}");
    }

    /// <summary>Asserts ordinals are 0-based and contiguous, and that blocks never move backwards.</summary>
    /// <remarks>
    /// A structural invariant of every map, healthy or not: the walk only ever moves forward, and a
    ///  backwards block would mean the scanner re-read a region — the signature of a resync bug, which
    ///  §3.2 forbids outright.
    /// </remarks>
    public static void WellFormed(MediaScanMap map)
    {
        for (int i = 0; i < map.Fragments.Count; i++)
            Assert.True(map.Fragments[i].Ordinal == i,
                $"Fragment {i} carries ordinal {map.Fragments[i].Ordinal}.\n{Describe(map)}");

        long previous = -1L;

        foreach (TapeMediaFragment f in map.Fragments)
        {
            if (f.StartBlock < 0)
                continue;       // TrailingRegion carries -1 by design

            Assert.True(f.StartBlock >= previous,
                $"Fragment #{f.Ordinal} starts at block {f.StartBlock}, behind {previous} — the walk went " +
                $"backwards.\n{Describe(map)}");

            previous = f.StartBlock;
        }
    }

    /// <summary>Asserts the scan completed: a full map, ending at end-of-data.</summary>
    public static void Complete(MediaScanMap map)
    {
        Assert.False(map.Truncated, $"The map is truncated.\n{Describe(map)}");

        Assert.True(
            map.TerminatorWin32 == (uint)Windows.Win32.Foundation.WIN32_ERROR.ERROR_NO_DATA_DETECTED
            || map.TerminatorWin32 == (uint)Windows.Win32.Foundation.WIN32_ERROR.NO_ERROR,
            $"Expected a clean end-of-data terminator.\n{Describe(map)}");
    }

    /// <summary>Asserts the map is incomplete — and that it still returned what it gathered (SM-5).</summary>
    public static void Truncated(MediaScanMap map)
        => Assert.True(map.Truncated, $"Expected a truncated map.\n{Describe(map)}");

    #endregion

    #region *** Content assertions ***

    /// <summary>
    /// Asserts the Nth set header (0-based, tape order) carries this identity — with NO TOC consulted.
    /// </summary>
    /// <remarks>
    /// The payoff of a TOC-less scan stated as an assertion: a user who lost their index can still see
    ///  what is on the tape, because each set names and dates itself.
    /// </remarks>
    public static void SetHeaderAt(
        MediaScanMap map, int index,
        string? description = null, int? volumeSetIndex = null, Guid? mediaId = null)
    {
        var sets = SetHeaders(map);

        Assert.True(index < sets.Count,
            $"No set header at position {index} — only {sets.Count} found.\n{Describe(map)}");

        TapeMediaFragment set = sets[index];

        if (description is not null)
            Assert.True(set.Description == description,
                $"Set #{index} describes itself as '{set.Description}', expected '{description}'.\n{Describe(map)}");

        if (volumeSetIndex is { } vsi)
            Assert.True(set.VolumeSetIndex == vsi,
                $"Set #{index} reports on-volume index {set.VolumeSetIndex}, expected {vsi}.\n{Describe(map)}");

        if (mediaId is { } id)
            Assert.True(set.Id == id,
                $"Set #{index} carries media id {set.Id}, expected {id}.\n{Describe(map)}");
    }

    /// <summary>Asserts every set header is closed by a separator — i.e. no backup died mid-set.</summary>
    public static void AllSetsClosed(MediaScanMap map)
    {
        Assert.False(map.LastSetUnclosed, $"The last set is unclosed.\n{Describe(map)}");

        foreach (TapeMediaFragment set in SetHeaders(map))
            Assert.True(set.ClosedBySeparator,
                $"Set at block {set.StartBlock} is not closed by a separator.\n{Describe(map)}");
    }

    /// <summary>
    /// Asserts the LAST set was never closed — the dominant real-world fault, detected with no TOC.
    /// </summary>
    /// <remarks>
    /// Also asserts every EARLIER set IS closed: a damaged tail must not be confused with a cartridge
    ///  whose separators are broken throughout, which would indict the scanner rather than the medium.
    /// </remarks>
    public static void OnlyLastSetUnclosed(MediaScanMap map)
    {
        var sets = SetHeaders(map);

        Assert.True(sets.Count > 0, $"No set headers at all.\n{Describe(map)}");
        Assert.True(map.LastSetUnclosed, $"Expected the last set to be unclosed.\n{Describe(map)}");

        for (int i = 0; i < sets.Count - 1; i++)
            Assert.True(sets[i].ClosedBySeparator,
                $"Set #{i} is also unclosed — the damage is not confined to the tail.\n{Describe(map)}");
    }

    /// <summary>Asserts no fragment was left unidentified.</summary>
    public static void NothingUnknown(MediaScanMap map)
        => Assert.True(map.UnknownCount == 0,
            $"Expected every fragment to be identified, {map.UnknownCount} were not.\n{Describe(map)}");

    /// <summary>Asserts exactly this many fragments could not be identified.</summary>
    public static void UnknownCount(MediaScanMap map, int expected)
        => Assert.True(map.UnknownCount == expected,
            $"Expected {expected} unidentified fragment(s), found {map.UnknownCount}.\n{Describe(map)}");

    /// <summary>Asserts at least one table-of-contents copy was found.</summary>
    /// <remarks>
    /// The reframing at the heart of the TOC-less design: the trailing TOC area is IDENTIFIED, not avoided
    ///  — and on a damaged cartridge a surviving copy is the most valuable thing on the tape.
    /// </remarks>
    public static void FoundTocCopies(MediaScanMap map, int atLeast = 1)
        => Assert.True(map.TocCopyCount >= atLeast,
            $"Expected at least {atLeast} TOC copy/copies, found {map.TocCopyCount}.\n{Describe(map)}");

    #endregion

    #region *** Read-only verification ***

    /// <summary>
    /// A byte-level snapshot of the content medium, so a test can prove the scan wrote nothing (SM-1).
    /// </summary>
    /// <remarks>
    /// Compares the BACKING STREAM, not the library's own view: a scan that somehow wrote would be caught
    ///  here even if every invariant the library checks still held.
    /// </remarks>
    public sealed class MediaSnapshot
    {
        private readonly byte[] m_content;
        private readonly byte[]? m_metadata;

        public MediaSnapshot(VirtualTapeFixture fixture)
        {
            var snapshot = fixture.Backend.CaptureMemorySnapshot();

            Assert.NotNull(snapshot);
            m_content = snapshot!.ContentData;
            m_metadata = snapshot.ContentMetadata;
        }

        /// <summary>Asserts the medium is byte-identical to when this snapshot was taken.</summary>
        public void AssertUnchanged(VirtualTapeFixture fixture, string because = "the scan must write nothing")
        {
            var now = fixture.Backend.CaptureMemorySnapshot();

            Assert.NotNull(now);
            Assert.True(m_content.AsSpan().SequenceEqual(now!.ContentData),
                $"Content bytes changed — {because}. " +
                $"({m_content.Length} B before, {now.ContentData.Length} B after)");

            // The metadata stream carries the virtual block list: a mark written without data would show
            //  up ONLY here, so comparing content alone would miss it.
            if (m_metadata is not null && now.ContentMetadata is not null)
                Assert.True(m_metadata.AsSpan().SequenceEqual(now.ContentMetadata),
                    $"Media structure changed — {because}.");
        }
    }

    /// <summary>Takes a snapshot of the fixture's medium for a later <c>AssertUnchanged</c>.</summary>
    public static MediaSnapshot Snapshot(VirtualTapeFixture fixture) => new(fixture);

    #endregion
}
