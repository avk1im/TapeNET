using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using TapeLibNET.Toc;
using TapeWinNET.Models;

namespace TapeWinNET.ViewModels;

/// <summary>
/// The TOC barrier — "don't open the mixer while it runs".
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> The view model reads the TOC only while no service operation holds it. Every mutation of the TOC
///  happens inside a service operation, under its lock; <see cref="TapeLibNET.Services.ServiceStateChange.TocChanged"/>
///  is raised from inside it. So a TOC change never triggers TOC reads: it only marks the views stale
///  (<see cref="_tocStale"/>). They are rebuilt once the operation is over (<see cref="RefreshAfterTocChange"/>).
/// </para>
/// <para>
/// <b>When.</b> The primary trigger is <see cref="IsBusy"/> turning false: the view model raises it around every
///  service call it awaits, and the await returns only after the service released its lock. A TOC change outside a busy
///  scope (e.g. "Use this TOC" from the scan result window) schedules the refresh directly; should the operation still
///  hold the lock then, its <c>OperationEnded</c> re-schedules it.
/// </para>
/// <para>
/// <b>Who refreshes explicitly.</b> Operations that rebuild the tree themselves (backup, restore, delete via
///  <c>ReloadMediaAsync</c>, media identification, format) clear the flag through <see cref="ResetTree"/>, so nothing
///  is rebuilt twice.
/// </para>
/// <para>
/// <b>Not needed during an operation.</b> Progress, log, overlay message, prompts and the abort button get their data
///  from the service's callbacks, never from the TOC.
/// </para>
/// </remarks>
public partial class MainViewModel
{
    // The TOC changed since the tree, lists and TOCView were built from it. Set by TocChanged; cleared by ResetTree.
    private bool _tocStale;

    /// <summary>The views describe the TOC, and no service operation holds it: the TOC may be read.</summary>
    private bool IsTocQuiet => !_tocStale && !_tapeService.IsOperationRunning;

    /// <summary>Debug check at every TOC-reading entry point: no service operation may hold the TOC.</summary>
    [Conditional("DEBUG")]
    private void AssertTocQuiet([CallerMemberName] string caller = "")
        => Debug.Assert(!_tapeService.IsOperationRunning,
            $"{caller} reads the TOC while a service operation holds it — see MainViewModel.TocBarrier.cs");

    /// <summary>The service reported a TOC change (from inside an operation): mark stale, refresh when it is over.</summary>
    private void OnTocChangedNotification()
    {
        _tocStale = true;
        if (!IsBusy)
            ScheduleTocRefresh();
    }

    /// <summary>Queues <see cref="RefreshAfterTocChange"/> behind the current UI work.</summary>
    private void ScheduleTocRefresh()
        => Application.Current.Dispatcher.InvokeAsync(RefreshAfterTocChange, DispatcherPriority.Background);

    /// <summary>Starts a tree rebuild. <paramref name="keepTocView"/>: rebuilding from a TOC — its view (checks, filters)
    ///  carries over through <see cref="TOCView.Refresh"/>; false when no TOC remains to show.</summary>
    private void ResetTree(bool keepTocView)
    {
        TreeItems.Clear();
        _currentSetView = null;
        if (!keepTocView)
            _tocView = null;
        else
            AssertTocQuiet(); // since we'll continue using our TOCView, no service operation may be accessing / modifying it!
        _tocStale = false;
    }

    /// <summary>
    /// Rebuilds the tree and panes from the changed TOC, keeping the selected backup set where it still exists (by set
    ///  object — an index may shift), else the media node. A no-op unless stale, idle and the lock is free.
    /// </summary>
    private void RefreshAfterTocChange()
    {
        if (!_tocStale || IsBusy || _tapeService.IsOperationRunning)
            return;

        if (!_tapeService.IsDriveOpen)
        {
            _tocStale = false;
            return;
        }

        var selectedKind = _selectedTreeItem?.ItemType;
        var selectedSet = selectedKind == TreeItemType.BackupSet ? _selectedTreeItem!.SetTOC : null;
        int driveNumber = _tapeService.DriveNumber;

        if (_tapeService.TOC is null)
        {
            if (_tapeService.CalibrationHeader is not null)
                UpdateTreeForCalibrationMedia(driveNumber);
            else if (IsRemoteConnected && _remoteHostSettings is not null)
                UpdateTreeForRemoteDriveOnly(driveNumber, _remoteHostSettings);
            else
                UpdateTreeForDriveOnly(driveNumber);
            return;
        }

        UpdateTreeFromTOC(driveNumber);     // remote-aware

        if (selectedSet is not null && FindSetNode(selectedSet) is { } node)
        {
            node.IsSelected = true;
            OnTreeItemSelected(node);
        }
        else if (selectedKind == TreeItemType.Drive && TreeItems.Count > 0)
        {
            TreeItems[0].IsSelected = true;
            OnTreeItemSelected(TreeItems[0]);
        }
        else
        {
            SelectMostRecentSet();          // the media node
        }
    }

    /// <summary>The tree node showing <paramref name="set"/>, wherever its index is now; null when it is gone.</summary>
    private TapeTreeItemViewModel? FindSetNode(TapeSetTOC set)
        => TreeItems.SelectMany(d => d.Children).SelectMany(t => t.Children)
            .FirstOrDefault(n => n.ItemType == TreeItemType.BackupSet && ReferenceEquals(n.SetTOC, set));
}
