using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

using TapeWinNET.Controls;
using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Owns the segment list and capacity for a <see cref="MediaUsageBarControl"/>
///  and lives for the entire lifetime of the host view model.
/// <para>
/// Encapsulates the logic that builds <see cref="UsageSegment"/>s from the
///  current <see cref="TapeService.TOC"/>, manages the highlight, and dispatches
///  segment clicks. The base implementation reproduces what
///  <c>MainViewModel</c> used to do directly; derived classes override
///  <see cref="AddContentSegments"/> (and optionally
///  <see cref="OnSegmentClicked"/>) to inject extra segments such as a pending
///  new backup set in BackupWindow or a deletion preview in
///  DeleteBackupSetsWindow.
/// </para>
/// <para>
/// <b>Free-space convention:</b> the trailing free segment's size is computed
///  as <c>capacity − sum(all other segments)</c>, never less than 1 byte so
///  the bar always has a visible free remnant. This keeps the math consistent
///  for derived classes that drop or inject content segments.
/// </para>
/// </summary>
public class MediaUsageBarPresenter : ViewModelBase
{
    protected readonly TapeService _tapeService;
    private readonly Action<int>? _onBackupSetClicked;

    private IReadOnlyList<UsageSegment> _segments = [];
    private long _totalCapacity;

    public MediaUsageBarPresenter(TapeService tapeService, Action<int>? onBackupSetClicked = null)
    {
        _tapeService = tapeService;
        _onBackupSetClicked = onBackupSetClicked;
        SegmentClickCommand = new RelayCommand(p =>
        {
            if (p is UsageSegment seg)
                OnSegmentClicked(seg);
        });
    }

    /// <summary>Ordered list of segments rendered by the bar.</summary>
    public IReadOnlyList<UsageSegment> Segments
    {
        get => _segments;
        private set => SetProperty(ref _segments, value);
    }

    /// <summary>Total media capacity in bytes; denominator for bar proportions.</summary>
    public long TotalCapacity
    {
        get => _totalCapacity;
        private set => SetProperty(ref _totalCapacity, value);
    }

    /// <summary>Command bound to <c>MediaUsageBarControl.SegmentClickCommand</c>.</summary>
    public ICommand SegmentClickCommand { get; }

    // ─────────────────────────────────────────────────────────────── Public API ────────

    /// <summary>
    /// Rebuilds <see cref="Segments"/> and <see cref="TotalCapacity"/> from the
    ///  current <see cref="TapeService"/> state. Safe to call repeatedly.
    /// </summary>
    public void Rebuild()
    {
        var toc = _tapeService.TOC;
        long capacity = _tapeService.Capacity;
        if (capacity <= 0)
        {
            Clear();
            return;
        }

        var segments = new List<UsageSegment>();
        BuildSegments(segments, toc, capacity);

        TotalCapacity = capacity;
        Segments = segments;

        // Reapply highlight after a rebuild — derived classes may track their own state.
        ReapplyHighlight();
    }

    /// <summary>Empties the bar (e.g. when navigating away from a media view).</summary>
    public void Clear()
    {
        Segments = [];
        TotalCapacity = 0;
    }

    /// <summary>
    /// Marks the segment with the given <paramref name="setIndex"/> as highlighted.
    ///  Pass <c>null</c> to clear the highlight.
    /// </summary>
    public virtual void UpdateHighlight(int? setIndex)
    {
        _highlightedSetIndex = setIndex;
        foreach (var seg in _segments)
            seg.IsHighlighted = seg.Kind == UsageSegmentKind.BackupSet
                             && seg.SetIndex == setIndex;
    }

    private int? _highlightedSetIndex;

    /// <summary>Reapplies the last highlight selection to the freshly built segments.</summary>
    protected void ReapplyHighlight() => UpdateHighlight(_highlightedSetIndex);

    // ─────────────────────────────────────────────────────────── Template method ────────

    /// <summary>
    /// Orchestrates segment construction: leading TOC, content (override hook),
    ///  trailing TOC, then free-space.
    /// </summary>
    protected virtual void BuildSegments(List<UsageSegment> segments, TapeTOC? toc, long capacity)
    {
        long tocSize = 0;
        bool tocInPartition = false;

        if (toc is not null)
        {
            tocSize = _tapeService.DefaultTOCCapacity;
            tocInPartition = _tapeService.HasInitiatorPartition;

            // TOC-in-partition: leftmost segment
            if (tocInPartition)
                segments.Add(new UsageSegment(
                    label: "TOC",
                    size: tocSize,
                    color: default, // Kind-based color applied by the control
                    tooltip: $"TOC partition: {Helpers.BytesToString(tocSize)}",
                    kind: UsageSegmentKind.TOC));

            // Content (backup-set) segments — override hook
            AddContentSegments(segments, toc);

            // TOC-in-set: rightmost data segment (immediately before free)
            if (!tocInPartition)
                segments.Add(new UsageSegment(
                    label: "TOC",
                    size: tocSize,
                    color: default,
                    tooltip: $"TOC (in set): {Helpers.BytesToString(tocSize)}",
                    kind: UsageSegmentKind.TOC));
        }
        else // toc is null
            AddContentSegments(segments, null); // derived classes may inject segments even if toc is null

        // Free space — computed as remainder so derived classes that add/remove
        //  content segments get a consistent picture without extra plumbing.
        long usedSoFar = segments.Sum(s => s.Size);
        long free = capacity - usedSoFar;
        // any segments pending?
        bool pending = segments.Any(s => s.Kind == UsageSegmentKind.PendingBackupSet
            || s.Kind == UsageSegmentKind.PendingTOC);

        if (pending)
        {
            // Don't use _tapeService.AdjustRemainingContentCapacity(free) as it can't know about
            //  to-be-added backup set / TOC and will retun the (adjusted) drive's reported free space
            free = Math.Max(free, 0); // avoid negative free space
        }
        else
        {
            free += tocSize; // AdjustRemainingContentCapacity() will account for TOC size
            // Adjust free space to account for the TOC's reserved capacity.
            free = _tapeService.AdjustRemainingContentCapacity(free);
        }

        segments.Add(new UsageSegment(
            label:    "Free",
            size:     Math.Max(free, 1), // 1-byte minimum so the visual remnant survives
            color:    default,
            tooltip:  $"Free: {Helpers.BytesToString(free)}",
            kind:     UsageSegmentKind.Free));
    }

    /// <summary>
    /// Adds backup-set content segments to <paramref name="segments"/>.
    ///  Default implementation lists every set on the current volume in physical order.
    /// </summary>
    protected virtual void AddContentSegments(List<UsageSegment> segments, TapeTOC? toc)
    {
        // Guard against empty media: FirstSetOnVolume / LastSetOnVolume must not be
        //  called when Count == 0 as they can throw an out-of-range exception.
        if (!(toc is { Count: > 0 }))
            return;

        uint blockSize = _tapeService.DefaultBlockSize;
        int firstSet = toc.FirstSetOnVolume;
        int lastSet  = toc.LastSetOnVolume;
        for (int setIndex = firstSet; setIndex <= lastSet; setIndex++)
        {
            var setTOC    = toc[setIndex];
            long setSize  = setTOC.ComputeTotalFileSizeOnTape(blockSize);
            int  altIndex = toc.SetIndexToAlt(setIndex);
            // Color assigned round-robin by 0-based standard index so that colors are
            //  stable across volumes (set 1 always gets palette[0], etc.).
            var color = MediaUsageBarControl.GetSetColor(setIndex - 1);

            segments.Add(new UsageSegment(
                label:    $"#{setIndex}|{altIndex}",
                size:     Math.Max(setSize, 1), // always at least 1 byte so tiny sets are visible
                color:    color,
                tooltip:  $"Set #{setIndex}|{altIndex}: {setTOC.Description ?? "(unnamed)"}\n"
                        + $"{Helpers.BytesToString(setSize)}, {setTOC.Count} file(s)",
                kind:     UsageSegmentKind.BackupSet,
                setIndex: setIndex));
        }
    }

    // ───────────────────────────────────────────────────────────── Click dispatch ────────

    /// <summary>
    /// Invoked when the user clicks any segment. Default implementation calls
    ///  the constructor-supplied <c>onBackupSetClicked</c> callback for
    ///  <see cref="UsageSegmentKind.BackupSet"/> segments only.
    /// </summary>
    protected virtual void OnSegmentClicked(UsageSegment seg)
    {
        if (seg.Kind == UsageSegmentKind.BackupSet && seg.SetIndex is int idx)
            _onBackupSetClicked?.Invoke(idx);
    }
}
