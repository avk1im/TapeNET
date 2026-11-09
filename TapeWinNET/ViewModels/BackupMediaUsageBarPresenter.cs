using System.Windows.Media;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET.Services;
using TapeWinNET.Services;
using TapeWinNET.Controls;

namespace TapeWinNET.ViewModels;


/// <summary>
/// Specialized <see cref="MediaUsageBarPresenter"/> for BackupWindow.
/// <para>
/// Drops the sets after the chosen "Append after set" target (all of them on Overwrite) and adds a
///  <see cref="UsageSegmentKind.PendingBackupSet"/> segment for the backup being composed. The room it may take is the
///  service's writable space plus the sets the overwrite drops — the base then shows what stays writable. Color: green
///  when it fits, red when it overflows the volume (multi-volume continues), amber for an incremental (an upper bound).
/// </para>
/// <para>
/// On media without a TOC, the base's TOC segment is the TOC still to be written: labelled "TOC (pending)".
/// </para>
/// <para><b>Click semantics:</b></para>
/// <list type="bullet">
///   <item>Existing-set segment → that set becomes the "Append after" target (Overwrite when the oldest set is clicked
///         while already the target).</item>
///   <item>Pending-set or Writable segment → the new set shifts one step to the right (preserves one more set).</item>
/// </list>
/// </summary>
public class BackupMediaUsageBarPresenter(TapeService tapeService, BackupViewModel vm)
    : MediaUsageBarPresenter(tapeService)
{
    private readonly BackupViewModel _vm = vm;

    // ───────────────────────────────────────────────── Content with pending set ────────

    /// <inheritdoc/>
    protected override UsageSegment TocSegment(MediaUsage usage)
    {
        var toc = base.TocSegment(usage);
        if (!_vm.IsNoToc)
            return toc;
        return new UsageSegment(
            label:   "TOC (pending)",
            size:    toc.Size,
            color:   default,
            tooltip: $"Pending {toc.Tooltip}",
            kind:    UsageSegmentKind.PendingTOC);
    }

    /// <summary>
    /// The existing sets up to the "Append after" target, then the pending set — sized to the room it can take.
    /// </summary>
    protected override void AddContentSegments(List<UsageSegment> segments, MediaUsage usage)
    {
        // 1. Existing sets, without those the new set replaces.
        int appendAfter = _vm.OverwriteMedia ? -1 : (_vm.SelectedAppendOption?.SetIndex ?? -1);
        var kept = new List<UsageSegment>();
        base.AddContentSegments(kept, usage);
        kept.RemoveAll(s => s.SetIndex is int idx && idx > appendAfter);
        segments.AddRange(kept);

        // 2. Nothing pending until the user has checked something.
        long pendingSize = _vm.CheckedFileSizeBytes;
        int pendingCount = _vm.CheckedFileCount;
        if (pendingSize <= 0 || pendingCount <= 0)
            return;

        // 3. Room: writable now plus what the overwrite frees (the TOC reserve's own change is ignored — small).
        long dropped = usage.Sets.Where(s => s.SetIndex > appendAfter).Sum(s => Visible(s.SizeOnTape));
        long available = Math.Max(1L, usage.Writable + dropped);
        bool fits = pendingSize <= available;
        long visualSize = Math.Min(pendingSize, available);

        // 4. Index, color, texts.
        int newSetIndex = Math.Max(1, appendAfter + 1);
        const int newAlt = 0;
        bool incremental = _vm.IncrementalBackup;
        Color color = incremental && !fits ? GetWarningBgColor("Warning")
            : fits ? GetWarningBgColor("Completed")
            : GetWarningBgColor("Error");

        string description = string.IsNullOrWhiteSpace(_vm.Description) ? "(unnamed)" : _vm.Description;
        string sizeStr = Helpers.BytesToString(pendingSize);

        segments.Add(new UsageSegment(
            label:    $"#{newSetIndex}|{newAlt}: {description}\n{sizeStr}, {pendingCount:N0} file(s)",
            size:     visualSize,
            color:    color,
            tooltip:  BuildTooltip(newSetIndex, newAlt, description, sizeStr, pendingCount, incremental, fits, available),
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
             + $"{sizeStr}, {fileCount:N0} file(s) —\nexceeds the {Helpers.BytesToString(available)} "
             + $"writable on this volume;\ncan continue backup on the next volume.";
    }

    // ─────────────────────────────────────────────────────────────── Click handling ────────

    /// <inheritdoc/>
    protected override void OnSegmentClicked(UsageSegment seg)
    {
        switch (seg.Kind)
        {
            case UsageSegmentKind.BackupSet when seg.SetIndex is int idx:
                SelectAsAppendAfter(idx);
                break;
            case UsageSegmentKind.PendingBackupSet:
            case UsageSegmentKind.Free:
                ShiftRightPreserveOneMore();
                break;
        }
    }

    /// <summary>
    /// Makes set <paramref name="setIndex"/> the "Append after" target. Clicking the oldest set while it already is the
    ///  target switches to Overwrite.
    /// </summary>
    private void SelectAsAppendAfter(int setIndex)
    {
        var options = _vm.AppendOptions;
        if (options.Count == 0)
            return;

        int oldestIdx = options[^1].SetIndex; // AppendOptions: newest → oldest
        if (setIndex == oldestIdx && !_vm.OverwriteMedia && _vm.SelectedAppendOption?.SetIndex == oldestIdx)
        {
            _vm.OverwriteMedia = true;
            return;
        }

        var pick = options.FirstOrDefault(o => o.SetIndex == setIndex);
        if (pick is null)
            return;
        _vm.OverwriteMedia = false;
        _vm.SelectedAppendOption = pick;
    }

    /// <summary>Preserves one more existing set: from Overwrite to the oldest set, else the next newer one.</summary>
    private void ShiftRightPreserveOneMore()
    {
        var options = _vm.AppendOptions;
        if (options.Count == 0)
            return;

        int oldestIdx = options[^1].SetIndex;
        int newestIdx = options[0].SetIndex;
        int current = _vm.OverwriteMedia ? oldestIdx - 1 : (_vm.SelectedAppendOption?.SetIndex ?? oldestIdx - 1);
        int target = Math.Min(current + 1, newestIdx);

        var pick = options.FirstOrDefault(o => o.SetIndex == target);
        if (pick is null)
            return;
        _vm.OverwriteMedia = false;
        _vm.SelectedAppendOption = pick;
    }
}
