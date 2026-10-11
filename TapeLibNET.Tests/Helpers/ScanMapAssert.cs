using System.Text;

using Windows.Win32.Foundation;

using TapeLibNET.Drive;
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
///  profiles and stand in the TOC partition on the fourth, and mark runs appear only where a TOC mark does.
///  Indexing into it by hand would make every assertion layout-specific and every failure message a bare
///  index mismatch.
/// </para>
/// <para>
/// <b>Every failure prints the whole map.</b> A scan test that fails tells you almost nothing from the
///  assertion alone — "expected 4 set headers, found 5" leaves the interesting question (WHICH fifth?)
///  unanswered. <see cref="Describe"/> is attached to every assertion here, mirroring what
///  <c>VirtualTapeMedia.FormatBlockLayout</c> does for the physical layer.
/// </para>
/// <para>
/// <b>Partitions (SM-14).</b> A block number means nothing without its partition: it restarts at 0 in the initiator
///  partition. Every positional helper here therefore takes — or prints — the partition, and the structural checks
///  run per partition.
/// </para>
/// </remarks>
public static class ScanMapAssert
{
    #region *** Rendering ***

    /// <summary>
    /// Renders a map as one line per fragment, prefixed by the partition outcomes — attached to every failure message.
    ///  A fragment in the TOC partition prints <c>I:</c> before its block (<see cref="TapeMediaFragment.ToString"/>).
    /// </summary>
    public static string Describe(MediaScanMap? map)
    {
        if (map is null)
            return "(no map — the scan returned null)";

        var sb = new StringBuilder();
        sb.AppendLine($"{map.Kind} · {map.Layout.NavigatorKind} · {map.Fragments.Count} fragment(s)" +
                      (map.Truncated ? $" · TRUNCATED ({(WIN32_ERROR)map.TerminatorWin32})" : "") +
                      $" · terminator {(WIN32_ERROR)map.TerminatorWin32}");
        foreach (ScannedPartition p in map.Partitions)
            sb.AppendLine($"  [{p.Partition}] {(p.Blank ? "blank" : $"{p.FragmentCount} fragment(s)")}" +
                          (p.Truncated ? $" · TRUNCATED ({(WIN32_ERROR)p.TerminatorWin32})" : ""));
        foreach (TapeMediaFragment f in map.Fragments)
            sb.AppendLine($"  {f}");
        if (map.OutOfPlaceFragments.Count > 0)
            sb.AppendLine($"  out of place: {string.Join(", ", map.OutOfPlaceFragments.Select(f => $"#{f.Ordinal}"))}");
        return sb.ToString();
    }

    #endregion

    #region *** Selecting ***

    /// <summary>All fragments of a kind, in map order (content partition first).</summary>
    public static IReadOnlyList<TapeMediaFragment> OfKind(MediaScanMap map, FragmentKind kind)
        => [.. map.Fragments.Where(f => f.Kind == kind)];

    /// <summary>All fragments of a kind standing in one partition, in tape order.</summary>
    public static IReadOnlyList<TapeMediaFragment> OfKind(MediaScanMap map, FragmentKind kind, MediaPartition partition)
        => [.. map.FragmentsIn(partition).Where(f => f.Kind == kind)];

    public static IReadOnlyList<TapeMediaFragment> SetHeaders(MediaScanMap map)
        => OfKind(map, FragmentKind.SetHeader);

    /// <summary>The media header that speaks for the cartridge, or null — never throws, since "absent" is a finding.</summary>
    public static TapeMediaFragment? MediaHeader(MediaScanMap map) => map.PrimaryMediaHeader;

    /// <summary>
    /// The fragment at a given block of a given partition, or null. Blocks, not ordinals — the tape's own coordinate,
    ///  which restarts at 0 in every partition.
    /// </summary>
    public static TapeMediaFragment? AtBlock(MediaScanMap map, long block,
        MediaPartition partition = MediaPartition.Content)
        => map.Fragments.FirstOrDefault(f => f.Partition == partition && f.StartBlock == block);

    /// <summary>The fragment with a given ordinal — the map's own, partition-independent coordinate.</summary>
    public static TapeMediaFragment ByOrdinal(MediaScanMap map, int ordinal)
    {
        Assert.True(ordinal >= 0 && ordinal < map.Fragments.Count,
            $"No fragment #{ordinal} — the map holds {map.Fragments.Count}.\n{Describe(map)}");
        return map.Fragments[ordinal];
    }

    /// <summary>The walk result of one partition. Asserts the map reports it.</summary>
    public static ScannedPartition Partition(MediaScanMap map, MediaPartition partition)
    {
        ScannedPartition? p = map.Partitions.FirstOrDefault(x => x.Partition == partition);
        Assert.True(p is not null, $"The map reports no {partition} partition.\n{Describe(map)}");
        return p!;
    }

    #endregion

    #region *** Structural assertions ***

    /// <summary>
    /// Asserts the map holds exactly <paramref name="expected"/> identified sets — set headers and header-less 2.1 sets.
    /// </summary>
    /// <remarks>
    /// <b>The exact count is the point.</b> On the filemark layouts the set separator and the TOC delimiter
    ///  are the same mark, so a walk that over-runs the content reads a TOC copy and reports it as a
    ///  phantom set. "At least N" would pass on exactly the cartridge the scanner must get right.
    /// </remarks>
    public static void SetCount(MediaScanMap map, int expected)
        => Assert.True(map.SetCount == expected,
            $"Expected exactly {expected} set(s), found {map.SetCount}.\n{Describe(map)}");

    /// <summary>Asserts the map's fragment kinds, in map order (content, then initiator), are exactly these.</summary>
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

    /// <summary>As <see cref="KindSequence(MediaScanMap, FragmentKind[])"/>, for one partition only.</summary>
    public static void KindSequence(MediaScanMap map, MediaPartition partition, params FragmentKind[] expected)
    {
        var actual = map.FragmentsIn(partition).Select(f => f.Kind).ToArray();
        Assert.True(expected.SequenceEqual(actual),
            $"Fragment kinds in the {partition} partition differ.\n  expected: {string.Join(", ", expected)}\n" +
            $"  actual:   {string.Join(", ", actual)}\n{Describe(map)}");
    }

    /// <summary>Asserts the map contains exactly this many fragments of the given kind; returns the count.</summary>
    public static int KindCount(MediaScanMap map, FragmentKind kind, int expected)
    {
        int actual = map.Fragments.Count(f => f.Kind == kind);
        Assert.True(actual == expected,
            $"Expected exactly {expected} fragment(s) of kind {kind}, found {actual}.\n{Describe(map)}");
        return actual;
    }

    /// <summary>
    /// Asserts the map's structural invariants: ordinals 0-based and contiguous; content fragments before initiator
    ///  fragments; blocks never moving backwards WITHIN a partition; one partition entry per partition walked, with
    ///  matching fragment counts.
    /// </summary>
    /// <remarks>
    /// The walk only ever moves forward inside a partition. A backwards block there would mean the scanner re-read a
    ///  region — the signature of a resync bug, which §3 forbids. Across partitions the block number restarts at 0,
    ///  which is why the check is per partition.
    /// </remarks>
    public static void WellFormed(MediaScanMap map)
    {
        for (int i = 0; i < map.Fragments.Count; i++)
            Assert.True(map.Fragments[i].Ordinal == i,
                $"Fragment {i} carries ordinal {map.Fragments[i].Ordinal}.\n{Describe(map)}");

        bool initiatorSeen = false;
        foreach (TapeMediaFragment f in map.Fragments)
        {
            initiatorSeen |= f.Partition == MediaPartition.Initiator;
            Assert.False(initiatorSeen && f.Partition == MediaPartition.Content,
                $"Content fragment #{f.Ordinal} after an initiator fragment.\n{Describe(map)}");
        }

        foreach (var partition in map.Fragments.GroupBy(f => f.Partition))
        {
            long previous = -1L;
            foreach (TapeMediaFragment f in partition)
            {
                Assert.True(f.StartBlock >= previous,
                    $"Fragment #{f.Ordinal} starts at {f.Where}, behind block {previous} — the walk went backwards." +
                    $"\n{Describe(map)}");
                previous = f.StartBlock;
            }
        }

        Assert.True(map.Partitions.Select(p => p.Partition).Distinct().Count() == map.Partitions.Count,
            $"A partition is reported twice.\n{Describe(map)}");
        foreach (ScannedPartition p in map.Partitions)
            Assert.True(p.FragmentCount == map.FragmentsIn(p.Partition).Count(),
                $"The {p.Partition} partition reports {p.FragmentCount} fragment(s), the map holds " +
                $"{map.FragmentsIn(p.Partition).Count()}.\n{Describe(map)}");
        Assert.True(map.Fragments.All(f => map.Partitions.Any(p => p.Partition == f.Partition)),
            $"A fragment stands in a partition the map does not report.\n{Describe(map)}");
    }

    /// <summary>Asserts the scan completed: a full map, every partition walked to its end of data.</summary>
    public static void Complete(MediaScanMap map)
    {
        Assert.False(map.Truncated, $"The map is truncated.\n{Describe(map)}");
        Assert.True(
            map.TerminatorWin32 == (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED
            || map.TerminatorWin32 == (uint)WIN32_ERROR.NO_ERROR,
            $"Expected a clean end-of-data terminator.\n{Describe(map)}");
        Assert.All(map.Partitions, p => Assert.False(p.Truncated,
            $"The {p.Partition} partition is truncated.\n{Describe(map)}"));
    }

    /// <summary>Asserts the map is incomplete — and that it still returned what it gathered (SM-5).</summary>
    public static void Truncated(MediaScanMap map)
        => Assert.True(map.Truncated, $"Expected a truncated map.\n{Describe(map)}");

    /// <summary>Asserts a partition was walked to its end and held nothing — a finding, not a gap.</summary>
    public static void PartitionBlank(MediaScanMap map, MediaPartition partition)
        => Assert.True(Partition(map, partition).Blank,
            $"Expected the {partition} partition to be blank (walked, empty).\n{Describe(map)}");

    /// <summary>
    /// Asserts a partition was NOT scanned — truncated with no fragments. Must never read the same as
    ///  <see cref="PartitionBlank"/> (SM-14).
    /// </summary>
    public static void PartitionNotScanned(MediaScanMap map, MediaPartition partition)
    {
        ScannedPartition p = Partition(map, partition);
        Assert.True(p.Truncated && p.FragmentCount == 0 && !p.Blank,
            $"Expected the {partition} partition to be reported as not scanned.\n{Describe(map)}");
    }

    #endregion

    #region *** Content assertions ***

    /// <summary>
    /// Asserts the Nth set header (0-based, map order) carries this identity — with NO TOC consulted.
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
                $"Set at {set.Where} is not closed by a separator.\n{Describe(map)}");
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

    /// <summary>Asserts no fragment is DAMAGED — Unknown with a failed diagnosis. Unidentified data is allowed.</summary>
    public static void NothingDamaged(MediaScanMap map)
    {
        var damaged = map.Fragments.Where(f => f.Kind == FragmentKind.Unknown && !f.Diagnosis.Success).ToList();
        Assert.True(damaged.Count == 0,
            $"Expected no damaged fragment, found {damaged.Count} " +
            $"({string.Join(", ", damaged.Select(f => f.Where))}).\n{Describe(map)}");
    }

    /// <summary>Asserts at least this many table-of-contents copies were found, in any partition.</summary>
    /// <remarks>
    /// The reframing at the heart of the TOC-less design: the TOC area is IDENTIFIED, not avoided — and on a damaged
    ///  cartridge a surviving copy is the most valuable thing on the tape. Holds on every layout since SM-14.
    /// </remarks>
    public static void FoundTocCopies(MediaScanMap map, int atLeast = 1)
        => Assert.True(map.TocCopyCount >= atLeast,
            $"Expected at least {atLeast} TOC copy/copies, found {map.TocCopyCount}.\n{Describe(map)}");

    /// <summary>Asserts the TOC copies stand where the layout writes them, and nowhere else.</summary>
    public static void TocCopiesWhereTheLayoutPutsThem(MediaScanMap map)
    {
        MediaPartition expected = map.Layout.TocInPartition ? MediaPartition.Initiator : MediaPartition.Content;
        Assert.All(OfKind(map, FragmentKind.TOC), c => Assert.True(c.Partition == expected,
            $"TOC copy at {c.Where}, expected the {expected} partition.\n{Describe(map)}"));
    }

    /// <summary>Asserts every TOC copy was recovered.</summary>
    public static void AllTocCopiesRecovered(MediaScanMap map)
        => Assert.All(OfKind(map, FragmentKind.TOC), c => Assert.True(c.HarvestedToc is not null,
            $"TOC copy at {c.Where} was not recovered: {c.Diagnosis}.\n{Describe(map)}"));

    /// <summary>Asserts nothing stands in a partition the layout does not put it in.</summary>
    public static void NothingOutOfPlace(MediaScanMap map)
        => Assert.True(map.OutOfPlaceFragments.Count == 0,
            $"Expected nothing out of place, found {map.OutOfPlaceFragments.Count}.\n{Describe(map)}");

    #endregion

    #region *** Read-only verification ***

    /// <summary>
    /// A byte-level snapshot of the medium — EVERY partition — so a test can prove the scan wrote nothing (SM-1).
    /// </summary>
    /// <remarks>
    /// Compares the BACKING STREAMS, not the library's own view: a scan that somehow wrote would be caught here even if
    ///  every invariant the library checks still held. The initiator partition is included since SM-14: the scan walks
    ///  it, so it must be proven untouched too.
    /// </remarks>
    public sealed class MediaSnapshot
    {
        private readonly byte[] m_content;
        private readonly byte[]? m_contentMetadata;
        private readonly byte[]? m_initiator;
        private readonly byte[]? m_initiatorMetadata;

        public MediaSnapshot(VirtualTapeFixture fixture)
        {
            var snapshot = fixture.Backend.CaptureMemorySnapshot();
            Assert.NotNull(snapshot);
            m_content = snapshot!.ContentData;
            m_contentMetadata = snapshot.ContentMetadata;
            m_initiator = snapshot.InitiatorData;
            m_initiatorMetadata = snapshot.InitiatorMetadata;
        }

        /// <summary>Asserts every partition is byte-identical to when this snapshot was taken.</summary>
        public void AssertUnchanged(VirtualTapeFixture fixture, string because = "the scan must write nothing")
        {
            var now = fixture.Backend.CaptureMemorySnapshot();
            Assert.NotNull(now);

            AssertSame(m_content, now!.ContentData, $"Content bytes changed — {because}");
            // The metadata stream carries the virtual block list: a mark written without data would show
            //  up ONLY here, so comparing content alone would miss it.
            AssertSame(m_contentMetadata, now.ContentMetadata, $"Content structure changed — {because}");
            AssertSame(m_initiator, now.InitiatorData, $"TOC partition bytes changed — {because}");
            AssertSame(m_initiatorMetadata, now.InitiatorMetadata, $"TOC partition structure changed — {because}");
        }

        private static void AssertSame(byte[]? before, byte[]? after, string message)
        {
            if (before is null && after is null)
                return;
            Assert.True(before is not null && after is not null,
                $"{message} (a stream {(before is null ? "appeared" : "disappeared")})");
            Assert.True(before.AsSpan().SequenceEqual(after),
                $"{message} ({before!.Length} B before, {after!.Length} B after)");
        }
    }

    /// <summary>Takes a snapshot of the fixture's medium — every partition — for a later <c>AssertUnchanged</c>.</summary>
    public static MediaSnapshot Snapshot(VirtualTapeFixture fixture) => new(fixture);

    #endregion
}
