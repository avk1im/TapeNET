using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET.Services;
using TapeWinNET.Services;
using TapeWinNET.Controls;

namespace TapeWinNET.ViewModels;


/// <summary>
/// Specialized <see cref="MediaUsageBarPresenter"/> for DeleteBackupSetsWindow.
/// <para>
/// Collapses the sets selected for deletion into one <see cref="UsageSegmentKind.PendingBackupSet"/> segment in the
///  pale-red error shade, so the user sees exactly which part of the tape goes. The Writable segment keeps the service's
///  figure — the sets still occupy the tape until the delete runs; the window's statistics show the space after it.
/// </para>
/// <para><b>Click semantics:</b></para>
/// <list type="bullet">
///   <item>Preserved-set segment → that set becomes the first to delete (the red zone grows left).</item>
///   <item>Red or Writable segment → the deletion shrinks by one set (one more preserved).</item>
/// </list>
/// </summary>
public class DeleteBackupSetsMediaUsageBarPresenter(TapeService tapeService, DeleteBackupSetsViewModel vm)
    : MediaUsageBarPresenter(tapeService)
{
    private readonly DeleteBackupSetsViewModel _vm = vm;

    // ─────────────────────────────────────────── Content with deletion preview ────────

    /// <summary>All sets of the volume, the ones to delete merged into one red segment.</summary>
    protected override void AddContentSegments(List<UsageSegment> segments, MediaUsage usage)
    {
        base.AddContentSegments(segments, usage);

        if (_vm.SelectedOption is not { } first || usage.Sets.Count == 0)
            return;

        int firstDeleteIdx = first.SetIndex;
        var lastSet = usage.Sets[^1];

        long deletedBytes = 0;
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            var s = segments[i];
            if (s.Kind == UsageSegmentKind.BackupSet && s.SetIndex is int idx && idx >= firstDeleteIdx)
            {
                deletedBytes += s.Size;
                segments.RemoveAt(i);
            }
        }
        if (deletedBytes == 0)
            return; // nothing to collapse (shouldn't happen with a valid option)

        string rangeLabel = firstDeleteIdx == lastSet.SetIndex
            ? $"#{firstDeleteIdx}|{first.AltIndex}"
            : $"#{firstDeleteIdx}|{first.AltIndex} to #{lastSet.SetIndex}|{lastSet.AltIndex}";

        int locked = _vm.DeleteLockedSetsCount;
        string files = $"{_vm.DeleteFileCount:N0} file(s)" + (locked > 0 ? $" + {locked} locked set(s)" : string.Empty);

        segments.Add(new UsageSegment(
            label:    rangeLabel,
            size:     deletedBytes,
            color:    GetWarningBgColor("Error"),
            tooltip:  $"Set(s) to delete: {rangeLabel}\n{Helpers.BytesToString(deletedBytes)}, {files}",
            kind:     UsageSegmentKind.PendingBackupSet,
            setIndex: firstDeleteIdx)); // first deleted set, used by the click handler
    }

    // ─────────────────────────────────────────────────────────────── Click handling ────────

    /// <inheritdoc/>
    protected override void OnSegmentClicked(UsageSegment seg)
    {
        switch (seg.Kind)
        {
            case UsageSegmentKind.BackupSet when seg.SetIndex is int idx:
                SelectDeleteFromSet(idx);
                break;
            case UsageSegmentKind.PendingBackupSet:
            case UsageSegmentKind.Free:
                ShrinkDeletionByOne();
                break;
        }
    }

    /// <summary>Makes the clicked set the first to delete.</summary>
    private void SelectDeleteFromSet(int setIndex)
    {
        var pick = _vm.DeleteOptions.FirstOrDefault(o => o.SetIndex == setIndex);
        if (pick is not null)
            _vm.SelectedOption = pick;
    }

    /// <summary>Preserves one more set; never fewer than one set to delete.</summary>
    private void ShrinkDeletionByOne()
    {
        if (_vm.SelectedOption is null || _vm.DeleteOptions.Count == 0)
            return;

        int newestIdx = _vm.DeleteOptions[0].SetIndex; // DeleteOptions: newest → oldest
        int current = _vm.SelectedOption.SetIndex;
        if (current >= newestIdx)
            return;

        var pick = _vm.DeleteOptions.FirstOrDefault(o => o.SetIndex == current + 1);
        if (pick is not null)
            _vm.SelectedOption = pick;
    }
}
