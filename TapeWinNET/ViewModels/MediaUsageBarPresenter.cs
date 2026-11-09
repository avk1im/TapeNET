using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET.Services;
using TapeWinNET.Controls;
using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;


/// <summary>
/// Owns the segment list and capacity for a <see cref="MediaUsageBarControl"/> and lives for the entire lifetime of the
///  host view model.
/// <para>
/// <b>One source of figures.</b> Every number the bar shows comes from <see cref="TapeServiceBase.DescribeUsage"/> — the
///  same figures the property pane shows (<c>TapeServiceBase.AddCapacity</c>): each set's size on tape, the TOC reserve
///  (or partition) with the TOC's size, and the writable space. The bar never reads the TOC or recomputes capacity
///  itself.
/// </para>
/// <para>
/// <b>Layout:</b> [TOC partition] · sets of this volume · [TOC reserve] · Writable. The axis is the sum of the segments,
///  so the bar is always full and every segment is proportional to the figure it is labelled with.
/// </para>
/// <para>
/// Derived classes override <see cref="AddContentSegments"/> to inject what-if segments the service knows nothing about
///  — the pending new set (BackupWindow), the deletion preview (DeleteBackupSetsWindow). The Writable segment then shows
///  the space left after that what-if (<see cref="BuildSegments"/>).
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

    /// <summary>Denominator for bar proportions: the sum of all segments.</summary>
    public long TotalCapacity
    {
        get => _totalCapacity;
        private set => SetProperty(ref _totalCapacity, value);
    }

    /// <summary>Command bound to <c>MediaUsageBarControl.SegmentClickCommand</c>.</summary>
    public ICommand SegmentClickCommand { get; }

    // ─────────────────────────────────────────────────────────────── Public API ────────

    /// <summary>Rebuilds the segments from the service's usage snapshot. Safe to call repeatedly.</summary>
    public void Rebuild()
    {
        if (_tapeService.DescribeUsage() is not { } usage)
        {
            Clear();
            return;
        }
        var segments = new List<UsageSegment>();
        BuildSegments(segments, usage);
        TotalCapacity = Math.Max(1L, segments.Sum(s => s.Size));
        Segments = segments;
        ReapplyHighlight();
    }

    /// <summary>Empties the bar (e.g. when navigating away from a media view).</summary>
    public void Clear()
    {
        Segments = [];
        TotalCapacity = 0;
    }

    /// <summary>Marks the segment of <paramref name="setIndex"/> as highlighted; <c>null</c> clears.</summary>
    public virtual void UpdateHighlight(int? setIndex) => UpdateHighlight(setIndex is int i ? [i] : []);

    /// <summary>
    /// Marks every segment whose set is in <paramref name="setIndexes"/> as highlighted — the multi-set selection of the
    ///  backup sets list. An empty sequence clears the highlight.
    /// </summary>
    public void UpdateHighlight(IEnumerable<int> setIndexes)
    {
        _highlightedSetIndexes = [.. setIndexes];
        foreach (var seg in _segments)
            seg.IsHighlighted = seg.Kind == UsageSegmentKind.BackupSet
                             && seg.SetIndex is int idx && _highlightedSetIndexes.Contains(idx);
    }

    private HashSet<int> _highlightedSetIndexes = [];

    /// <summary>Reapplies the last highlight selection to the freshly built segments.</summary>
    protected void ReapplyHighlight() => UpdateHighlight([.. _highlightedSetIndexes]);

    // ─────────────────────────────────────────────────────────── Template method ────────

    /// <summary>
    /// [TOC partition] · content (override hook) · [TOC reserve] · Writable.
    /// <para>
    /// The Writable segment shows the service's writable space, corrected by what the hook changed: content the hook
    ///  removed (sets an overwrite drops) returns to it, content it added (the pending set) is taken from it. A segment
    ///  the hook only re-labels or merges (the deletion preview) leaves it unchanged.
    /// </para>
    /// </summary>
    protected virtual void BuildSegments(List<UsageSegment> segments, MediaUsage usage)
    {
        if (usage.TocInPartition)
            segments.Add(TocSegment(usage));

        AddContentSegments(segments, usage);

        if (!usage.TocInPartition)
            segments.Add(TocSegment(usage));

        long contentBefore = usage.Sets.Sum(s => Visible(s.SizeOnTape));
        long contentNow = segments
            .Where(s => s.Kind is UsageSegmentKind.BackupSet or UsageSegmentKind.PendingBackupSet)
            .Sum(s => s.Size);
        long writable = Math.Max(0L, usage.Writable + contentBefore - contentNow);
        bool whatIf = contentNow != contentBefore;

        segments.Add(new UsageSegment(
            label:   "Writable",
            size:    Visible(writable),
            color:   WritableColor(usage, writable),
            tooltip: whatIf
                ? $"Writable after this operation: {Helpers.BytesToString(writable)}\n"
                  + $"(now {Helpers.BytesToString(usage.Writable)})"
                : $"Writable: {Helpers.BytesToString(writable)}",
            kind:    UsageSegmentKind.Free));
    }

    /// <summary>
    /// The TOC segment: the reserve the service keeps for the TOC on this volume (TOC in set), or the TOC partition —
    ///  with the TOC's own size in brackets, as the property pane shows it.
    /// </summary>
    protected virtual UsageSegment TocSegment(MediaUsage usage)
    {
        string what = usage.TocInPartition ? "TOC partition" : "TOC reserve";
        return new UsageSegment(
            label:   "TOC",
            size:    Visible(usage.TocSpace),
            color:   default, // Kind-based color applied by the control
            tooltip: $"{what}: {Helpers.BytesToString(usage.TocSpace)} (TOC ~{Helpers.BytesToString(usage.TocSize)})",
            kind:    UsageSegmentKind.TOC);
    }

    /// <summary>Adds one segment per set of this volume, in physical order — sizes from the service.</summary>
    protected virtual void AddContentSegments(List<UsageSegment> segments, MediaUsage usage)
    {
        foreach (var set in usage.Sets)
        {
            segments.Add(new UsageSegment(
                label:    $"#{set.SetIndex}|{set.AltIndex}",
                size:     Visible(set.SizeOnTape),
                // Round-robin by 0-based standard index: set 1 always gets palette[0], across volumes
                color:    MediaUsageBarControl.GetSetColor(set.SetIndex - 1),
                tooltip:  $"Set #{set.SetIndex}|{set.AltIndex}: {set.DisplayName}\n"
                          + $"{Helpers.BytesToString(set.SizeOnTape)}, {set.DisplayCount} file(s)",
                kind:     UsageSegmentKind.BackupSet,
                setIndex: set.SetIndex));
        }
    }

    // ───────────────────────────────────────────────────────────── Click dispatch ────────

    /// <summary>
    /// Invoked when the user clicks any segment. Default: the constructor-supplied callback for set segments only.
    /// </summary>
    protected virtual void OnSegmentClicked(UsageSegment seg)
    {
        if (seg.Kind == UsageSegmentKind.BackupSet && seg.SetIndex is int idx)
            _onBackupSetClicked?.Invoke(idx);
    }

    // ──────────────────────────────────────────────────────────────────── Helpers ────────

    /// <summary>At least 1 byte, so a tiny or empty segment stays visible.</summary>
    protected static long Visible(long size) => Math.Max(size, 1L);

    /// <summary>
    /// The Writable segment's shade — the same traffic light the property pane gives the "Writable" row
    ///  (<see cref="WarningLevelHelper.Translate"/> over writable ÷ estimated capacity); the control's default when no
    ///  level applies.
    /// </summary>
    private static Color WritableColor(MediaUsage usage, long writable)
    {
        if (usage.EstimatedCapacity <= 0)
            return default;
        var level = WarningLevelHelper.Translate((double)writable / usage.EstimatedCapacity);
        return level == ServiceReportLevel.None ? default : GetWarningBgColor(level.ToString());
    }

    /// <summary>
    /// Resolves a <c>WarningBg.&lt;name&gt;</c> brush from the application resources and returns its color. Falls back
    ///  to the App.xaml values when resources are unavailable (designer mode).
    /// </summary>
    protected static Color GetWarningBgColor(string name)
    {
        if (Application.Current?.TryFindResource("WarningBg." + name) is SolidColorBrush b)
            return b.Color;
        return name switch
        {
            "Completed" => Color.FromRgb(0xCC, 0xFF, 0xCC),
            "Info"      => Color.FromRgb(0xCC, 0xE5, 0xFF),
            "Warning"   => Color.FromRgb(0xFF, 0xF0, 0xCC),
            "Failed"    => Color.FromRgb(0xFF, 0xDD, 0xD0),
            "Error"     => Color.FromRgb(0xFF, 0xCC, 0xCC),
            _           => Colors.LightGray,
        };
    }
}
