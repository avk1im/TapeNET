using System.Windows;
using System.Windows.Media;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Specialized <see cref="MediaUsageBarPresenter"/> for DeleteBackupSetsWindow.
/// <para>
/// Collapses the sets selected for deletion into a single
///  <see cref="UsageSegmentKind.PendingBackupSet"/> segment drawn with the
///  "Error" (pale-red) warning color so the user sees exactly which portion
///  of the tape will be erased.
/// </para>
/// <para>
/// <b>Click semantics:</b>
/// </para>
/// <list type="bullet">
///   <item>Preserved-set segment → that set becomes the first to delete
///         (the red zone expands left to include it).</item>
///   <item>Pending (red) or Free segment → the deletion window shrinks by
///         one: the first-to-delete index advances by one, preserving one
///         more existing set.</item>
/// </list>
/// </summary>
public class DeleteBackupSetsMediaUsageBarPresenter(TapeService tapeService, DeleteBackupSetsViewModel vm)
    : MediaUsageBarPresenter(tapeService)
{
    private readonly DeleteBackupSetsViewModel _vm = vm;

    // ─────────────────────────────────────────── Content with deletion preview ────────

    /// <summary>
    /// Adds all existing set segments via the base, then collapses the
    ///  "to-be-deleted" range into a single red pending segment.
    /// </summary>
    protected override void AddContentSegments(List<UsageSegment> segments, TapeTOC? toc)
    {
        // 1. Base adds all existing sets (oldest → newest).
        base.AddContentSegments(segments, toc);

        if (_vm.SelectedOption == null || !(toc is { Count: > 0 }))
            return;

        int firstDeleteIdx = _vm.SelectedOption.SetIndex;
        int lastDeleteIdx  = toc.LastSetOnVolume;

        // 2. Collect the sizes of the doomed segments and remove them.
        long deletedBytes = 0;
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            var s = segments[i];
            if (s.Kind == UsageSegmentKind.BackupSet
                && s.SetIndex is int idx
                && idx >= firstDeleteIdx && idx <= lastDeleteIdx)
            {
                deletedBytes += s.Size;
                segments.RemoveAt(i);
            }
        }

        if (deletedBytes == 0)
            return; // nothing to collapse (shouldn't happen with a valid option)

        // 3. Build label and tooltip for the consolidated deletion segment.
        int firstAlt = _vm.SelectedOption.AltIndex;
        int lastAlt  = toc.SetIndexToAlt(lastDeleteIdx);
        int fileCount = _vm.DeleteFileCount;

        string rangeLabel = firstDeleteIdx == lastDeleteIdx
            ? $"#{firstDeleteIdx}|{firstAlt}"
            : $"#{firstDeleteIdx}|{firstAlt} to #{lastDeleteIdx}|{lastAlt}";

        string label   = rangeLabel;
        string sizeStr = Helpers.BytesToString(deletedBytes);
        string tooltip = $"Set(s) to delete: {rangeLabel}\n"
                       + $"{sizeStr}, {fileCount:N0} file(s)";

        // 4. Error color: the same diluted pale-red used by warning panels.
        Color color = GetWarningBgColor("Error");

        segments.Add(new UsageSegment(
            label:    label,
            size:     deletedBytes,
            color:    color,
            tooltip:  tooltip,
            kind:     UsageSegmentKind.PendingBackupSet,
            setIndex: firstDeleteIdx)); // setIndex = first deleted set, used by click handler
    }

    // ─────────────────────────────────────────────────────────────── Click handling ────────

    /// <inheritdoc/>
    protected override void OnSegmentClicked(UsageSegment seg)
    {
        switch (seg.Kind)
        {
            case UsageSegmentKind.BackupSet when seg.SetIndex is int idx:
                // Clicked a preserved set ⇒ make IT the first to delete
                //  (red zone expands left to cover it).
                SelectDeleteFromSet(idx);
                break;

            case UsageSegmentKind.PendingBackupSet:
            case UsageSegmentKind.Free:
                // Clicked the deletion zone or free space ⇒ shrink the deletion
                //  window by one (first-to-delete index advances by one).
                ShrinkDeletionByOne();
                break;
        }
    }

    /// <summary>
    /// Sets <see cref="DeleteBackupSetsViewModel.SelectedOption"/> to the option
    ///  whose <c>SetIndex</c> equals <paramref name="setIndex"/> (the clicked set
    ///  becomes the first to delete).
    /// </summary>
    private void SelectDeleteFromSet(int setIndex)
    {
        var pick = _vm.DeleteOptions.FirstOrDefault(o => o.SetIndex == setIndex);
        if (pick is null) return;
        _vm.SelectedOption = pick;
    }

    /// <summary>
    /// Advances the first-to-delete index by one, preserving one more set.
    ///  Clamped at the newest (last) set — cannot delete fewer than one set.
    /// </summary>
    private void ShrinkDeletionByOne()
    {
        if (_vm.SelectedOption is null) return;

        var options = _vm.DeleteOptions;
        if (options.Count == 0) return;

        // DeleteOptions are ordered newest → oldest; [0] is the latest (smallest range).
        int newestIdx = options[0].SetIndex; // LastSetOnVolume — minimum "from" index
        int current   = _vm.SelectedOption.SetIndex;

        // Already at the minimum (delete only the last set) — nowhere to shrink.
        if (current >= newestIdx) return;

        var pick = options.FirstOrDefault(o => o.SetIndex == current + 1);
        if (pick is null) return;
        _vm.SelectedOption = pick;
    }

    // ──────────────────────────────────────────────────── App.xaml warning brushes ────────

    /// <summary>
    /// Resolves a <c>WarningBg.&lt;name&gt;</c> brush from the application
    ///  resources and returns its <see cref="Color"/>. Falls back gracefully
    ///  when resources are unavailable (e.g. designer mode).
    /// </summary>
    private static Color GetWarningBgColor(string name)
    {
        if (Application.Current?.TryFindResource("WarningBg." + name) is SolidColorBrush b)
            return b.Color;
        return name switch
        {
            "Error"   => Color.FromRgb(0xFF, 0xCC, 0xCC),
            "Warning" => Color.FromRgb(0xFF, 0xF0, 0xCC),
            _         => Colors.LightGray,
        };
    }
}
