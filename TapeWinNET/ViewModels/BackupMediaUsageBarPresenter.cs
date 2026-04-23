using System.Windows;
using System.Windows.Media;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Specialized <see cref="MediaUsageBarPresenter"/> for BackupWindow.
/// <para>
/// Adds a <see cref="UsageSegmentKind.PendingBackupSet"/> segment representing
///  the backup the user is composing, dropping any existing set segments after
///  the chosen "Append after set" target. Color encodes whether the new set
///  fits the available room: green (Completed) when it fits, red (Error) when
///  it overflows the volume, amber (Warning) when the user has selected
///  Incremental — in which case the size estimate is an upper bound only.
/// </para>
/// <para>
/// <b>Click semantics:</b>
/// </para>
/// <list type="bullet">
///   <item>Existing-set segment → that set becomes the first to be replaced
///         (AppendAfter shifts to its index − 1, switching to Overwrite if
///         the user clicked the oldest set).</item>
///   <item>Pending-set or Free segment → the new set shifts one step to the
///         right (AppendAfter += 1), preserving one more existing set.</item>
/// </list>
/// </summary>
public class BackupMediaUsageBarPresenter(TapeService tapeService, BackupViewModel vm)
    : MediaUsageBarPresenter(tapeService)
{
    private readonly BackupViewModel _vm = vm;

    // ───────────────────────────────────────────────── Content with pending set ────────

    /// <summary>
    /// Adds existing set segments via the base, then drops sets after the
    ///  selected AppendAfter target and appends the pending new-set segment
    ///  capped to the available room.
    /// </summary>
    protected override void AddContentSegments(List<UsageSegment> segments, TapeTOC toc)
    {
        // 1. Base populates all existing set segments (oldest → newest).
        base.AddContentSegments(segments, toc);

        // 2. No pending segment until the user has checked something.
        long pendingSize = _vm.CheckedFileSizeBytes;
        int  pendingCount = _vm.CheckedFileCount;
        if (pendingSize <= 0 || pendingCount <= 0)
            return;

        // 3. Determine the AppendAfter target. Overwrite ⇒ −1 (drop everything).
        bool overwrite = _vm.OverwriteMedia;
        int appendAfter = overwrite ? -1 : (_vm.SelectedAppendOption?.SetIndex ?? -1);

        // 4. Drop set segments with SetIndex > appendAfter.
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            var s = segments[i];
            if (s.Kind == UsageSegmentKind.BackupSet && s.SetIndex is int idx && idx > appendAfter)
                segments.RemoveAt(i);
        }

        // 5. Compute the room actually available for the pending segment.
        //  Reserve the trailing TOC (added later by base.BuildSegments) and
        //  one byte for the visible Free remnant.
        long capacity = _tapeService.Capacity;
        long usedAfterDrop = segments.Sum(s => s.Size);
        long trailingTocReserve = _tapeService.HasInitiatorPartition
            ? 0
            : TapeNavigator.DefaultTOCCapacity;
        long available = capacity - usedAfterDrop - trailingTocReserve - 1;
        if (available < 1) available = 1;

        bool fits = pendingSize <= available;
        long visualSize = Math.Min(pendingSize, available);

        // 6. Compute the new set's index/alt and color.
        int newSetIndex = appendAfter + 1; // 1-based; first set on a fresh tape is #1
        if (newSetIndex < 1) newSetIndex = 1;
        int newAlt = 0; // newest after append

        bool incremental = _vm.IncrementalBackup;
        Color color = incremental && !fits
            ? GetWarningBgColor("Warning")
            : fits
                ? GetWarningBgColor("Completed")
                : GetWarningBgColor("Error");

        string description = string.IsNullOrWhiteSpace(_vm.Description)
            ? "(unnamed)"
            : _vm.Description;
        string sizeStr = Helpers.BytesToString(pendingSize);
        string label   = $"#{newSetIndex}|{newAlt}: {description}\n{sizeStr}, {pendingCount:N0} file(s)";

        string tooltip = BuildTooltip(
            newSetIndex, newAlt, description, sizeStr, pendingCount,
            incremental, fits, available);

        segments.Add(new UsageSegment(
            label:    label,
            size:     visualSize,
            color:    color,
            tooltip:  tooltip,
            kind:     UsageSegmentKind.PendingBackupSet,
            setIndex: newSetIndex));
    }

    private static string BuildTooltip(
        int setIndex, int alt, string description, string sizeStr, int fileCount,
        bool incremental, bool fits, long available)
    {
        string head = $"Pending #{setIndex}|{alt}: {description}";
        if (incremental)
            return $"{head}\n"
                 + $"Up to {sizeStr}, up to {fileCount:N0} file(s) — incremental:\n"
                 + $"actual size depends on which files have changed.";
        if (fits)
            return $"{head}\n{sizeStr}, {fileCount:N0} file(s).";
        // Doesn't fit — multi-volume continuation, neutrally phrased.
        return $"{head}\n"
             + $"{sizeStr}, {fileCount:N0} file(s) —\nexceeds the {Helpers.BytesToString(Math.Max(available, 0))} "
             + $"available on this volume;\ncan continue backup on the next volume.";
    }

    // ─────────────────────────────────────────────────────────────── Click handling ────────

    /// <summary>
    /// Maps user clicks on the bar to changes in <see cref="BackupViewModel.SelectedAppendOption"/>
    ///  / <see cref="BackupViewModel.OverwriteMedia"/>.
    /// </summary>
    protected override void OnSegmentClicked(UsageSegment seg)
    {
        switch (seg.Kind)
        {
            case UsageSegmentKind.BackupSet when seg.SetIndex is int idx:
                // Clicked existing set ⇒ select IT as the "Append after" set so that
                //  it (and all older sets) are preserved and the new set follows.
                //  Exception: clicking the first (oldest) set when it is already
                //  selected as AppendAfter → the user wants to drop everything,
                //  so we switch to Overwrite.
                SelectAsAppendAfter(idx);
                break;

            case UsageSegmentKind.PendingBackupSet:
            case UsageSegmentKind.Free:
                // Clicked pending or free ⇒ shift AppendAfter one step to the right
                //  (preserve one more existing set, i.e. AppendAfter index + 1).
                ShiftRightPreserveOneMore();
                break;
        }
    }

    /// <summary>
    /// Selects the set with index <paramref name="setIndex"/> as the "Append after"
    ///  target, making the new set land immediately after it.
    /// <para>
    /// Special case: clicking the oldest set on the volume when it is <em>already</em>
    ///  selected as AppendAfter means the user wants to drop all existing sets ⇒
    ///  switch to Overwrite.
    /// </para>
    /// </summary>
    private void SelectAsAppendAfter(int setIndex)
    {
        var options = _vm.AppendOptions;
        if (options.Count == 0)
            return;

        int oldestIdx = options[^1].SetIndex; // AppendOptions: newest → oldest

        // Toggle to Overwrite when clicking the oldest set while it's already selected.
        if (setIndex == oldestIdx
            && !_vm.OverwriteMedia
            && _vm.SelectedAppendOption?.SetIndex == oldestIdx)
        {
            _vm.OverwriteMedia = true;
            return;
        }

        var pick = options.FirstOrDefault(o => o.SetIndex == setIndex);
        if (pick is null) return;
        _vm.OverwriteMedia = false;
        _vm.SelectedAppendOption = pick;
    }

    /// <summary>
    /// One-step right-shift: preserve one additional existing set. From
    ///  Overwrite this lands on the oldest set; otherwise it advances the
    ///  AppendAfter selection by one (clamped at the newest set).
    /// </summary>
    private void ShiftRightPreserveOneMore()
    {
        var options = _vm.AppendOptions;
        if (options.Count == 0)
            return;

        int oldestIdx = options[^1].SetIndex;
        int newestIdx = options[0].SetIndex;

        // In Overwrite mode, conceptual current is "below oldest"; +1 lands on oldest.
        int current = _vm.OverwriteMedia
            ? oldestIdx - 1
            : (_vm.SelectedAppendOption?.SetIndex ?? oldestIdx - 1);
        int target = Math.Min(current + 1, newestIdx);

        var pick = options.FirstOrDefault(o => o.SetIndex == target);
        if (pick is null) return;
        _vm.OverwriteMedia = false;
        _vm.SelectedAppendOption = pick;
    }

    // ──────────────────────────────────────────────────── App.xaml warning brushes ────────

    /// <summary>
    /// Resolves a <c>WarningBg.&lt;name&gt;</c> brush from the application
    ///  resources and returns its <see cref="Color"/>. Falls back to a sensible
    ///  default if the resource is unavailable (e.g. designer mode).
    /// </summary>
    private static Color GetWarningBgColor(string name)
    {
        if (Application.Current?.TryFindResource("WarningBg." + name) is SolidColorBrush b)
            return b.Color;
        // Fallbacks roughly matching App.xaml diluted palette
        return name switch
        {
            "Completed" => Color.FromRgb(0xCC, 0xFF, 0xCC),
            "Warning"   => Color.FromRgb(0xFF, 0xF0, 0xCC),
            "Error"     => Color.FromRgb(0xFF, 0xCC, 0xCC),
            _           => Colors.LightGray,
        };
    }
}
