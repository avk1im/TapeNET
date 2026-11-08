using System.Collections.ObjectModel;
using System.Windows.Input;
using TapeWinNET.Models;
using TapeWinNET.Services;
using Windows.Win32.System.SystemServices; // for Helpers

namespace TapeWinNET.ViewModels;


/// <summary>
/// ViewModel for the DeleteBackupSetsWindow.
/// Shows a "Delete from set" combobox, warning pane, and capacity statistics.
/// </summary>
public class DeleteBackupSetsViewModel : ViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Action<DeleteBackupSetsViewModel> _onDelete;
    private readonly Action _onCancel;
    private DeleteFromSetOption? _selectedOption;

    // Capacity values (bytes)
    private readonly long _mediaCapacity;
    private readonly long _mediaRemaining;

    public DeleteBackupSetsViewModel(
        TapeService tapeService,
        Action<DeleteBackupSetsViewModel> onDelete,
        Action onCancel)
    {
        _tapeService = tapeService;
        _onDelete = onDelete;
        _onCancel = onCancel;

        _mediaCapacity = tapeService.Capacity;
        _mediaRemaining = tapeService.WritableRemaining;

        PopulateDeleteOptions();

        // Construct the usage bar after options are populated so the initial
        //  Rebuild sees the correct SelectedOption.
        UsageBar = new DeleteBackupSetsMediaUsageBarPresenter(_tapeService, this);
        UsageBar.Rebuild();

        DeleteCommand = new RelayCommand(_ => _onDelete(this), _ => SelectedOption != null && !IsDeleteBlocked);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    // ═════════════════════════════════════════════════
    //  Collections
    // ═════════════════════════════════════════════════

    /// <summary>Options for the "Delete from set" combobox.</summary>
    public ObservableCollection<DeleteFromSetOption> DeleteOptions { get; } = [];

    // ═════════════════════════════════════════════════
    //  Selection
    // ═════════════════════════════════════════════════

    /// <summary>Currently selected "delete from" option.</summary>
    public DeleteFromSetOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value))
            {
                OnPropertyChanged(nameof(SetsToDeleteCount));
                OnPropertyChanged(nameof(IsDeleteBlocked));
                OnPropertyChanged(nameof(WarningLevel));
                OnPropertyChanged(nameof(WarningMessage));
                OnPropertyChanged(nameof(DeleteSizeDisplay));
                OnPropertyChanged(nameof(RemainingAfterDeleteDisplay));
                UsageBar?.Rebuild();
            }
        }
    }

    /// <summary>
    /// Standard set index of the first set to delete. Used by the service layer.
    /// </summary>
    public int DeleteFromSetIndex => SelectedOption?.SetIndex ?? -1;

    /// <summary>
    /// Owns the segment list, capacity, and click command for the
    ///  <c>MediaUsageBarControl</c> in DeleteBackupSetsWindow.
    /// </summary>
    public DeleteBackupSetsMediaUsageBarPresenter UsageBar { get; }

    // ═════════════════════════════════════════════════
    //  Computed: set counts
    // ═════════════════════════════════════════════════

    /// <summary>Number of sets that will be deleted.</summary>
    public int SetsToDeleteCount
    {
        get
        {
            var toc = _tapeService.TOC;
            if (toc == null || SelectedOption == null)
                return 0;
            return toc.LastSetOnVolume - SelectedOption.SetIndex + 1;
        }
    }

    // ═════════════════════════════════════════════════
    //  Warning logic
    // ═════════════════════════════════════════════════

    /// <summary>
    /// True when the selected option would delete all sets on a partitioned media,
    /// which is not supported — the user should format instead.
    /// </summary>
    public bool IsDeleteBlocked
    {
        get
        {
            var toc = _tapeService.TOC;
            if (toc == null || SelectedOption == null)
                return false;
            return SelectedOption.SetIndex == toc.FirstSetOnVolume && _tapeService.HasInitiatorPartition;
        }
    }

    /// <summary>
    /// The warning pane's lines (W6), each only while its condition holds. Reads only stub members — a locked set never
    ///  throws here (Design-Encryption §11.1.8).
    /// </summary>
    private IEnumerable<(WarningLevel Level, string Text)> WarningLines()
    {
        var toc = _tapeService.TOC;
        if (toc == null || SelectedOption == null)
            yield break;

        if (IsDeleteBlocked)
        {
            yield return (WarningLevel.Info, "Cannot delete all backup sets on media. Format media instead.");
            yield break;
        }

        int count = SetsToDeleteCount;
        int firstStd = SelectedOption.SetIndex;
        int lastStd = toc.LastSetOnVolume;
        bool all = firstStd == toc.FirstSetOnVolume;
        yield return (all ? WarningLevel.Error : WarningLevel.Warning, count == 1
            ? $"1 backup set will be deleted: #{firstStd} | {SelectedOption.AltIndex}"
            : $"{count} backup set(s) will be deleted: from #{firstStd} | {SelectedOption.AltIndex} " +
              $"to #{lastStd} | {toc.SetIndexToAlt(lastStd)}");
        if (all)
            yield return (WarningLevel.Error, "This will remove ALL backup sets from the media!");
        if (toc.ContinuedOnNextVolume)
            yield return (WarningLevel.Warning, "Deleting from this media may invalidate a multi-volume backup.");

        int encrypted = 0;
        for (int i = firstStd; i <= lastStd; i++)
            if (toc[i].IsEncrypted)
                encrypted++;
        if (encrypted > 0)
            yield return (WarningLevel.Info, encrypted == 1
                ? "1 of them is encrypted — deleting needs no password."
                : $"{encrypted} of them are encrypted — deleting needs no password.");
    }

    /// <summary>Warning level for styling: the most severe line — always destructive unless blocked.</summary>
    public WarningLevel WarningLevel => WarningLines()
        .Select(l => l.Level)
        .DefaultIfEmpty(WarningLevel.Warning)
        .Max();     // ServiceReportLevel is ordered by severity: None < Info < … < Error

    /// <summary>Warning message describing what will be deleted.</summary>
    public string WarningMessage => string.Join("\r\n", WarningLines().Select(l => l.Text));

    // ═════════════════════════════════════════════════
    //  Statistics
    // ═════════════════════════════════════════════════

    /// <summary>Total media capacity, formatted.</summary>
    public string MediaCapacityDisplay => Helpers.BytesToStringLong(_mediaCapacity);

    /// <summary>Current remaining capacity, formatted.</summary>
    public string MediaRemainingDisplay => Helpers.BytesToStringLong(_mediaRemaining);

    /// <summary>Estimated on-tape size of sets to delete, in bytes (raw — used by the usage bar).</summary>
    public long DeleteSizeBytes => ComputeDeleteSize();

    /// <summary>Total file count across all sets to delete (raw — used by the usage bar).</summary>
    public int DeleteFileCount
    {
        get
        {
            var toc = _tapeService.TOC;
            if (toc == null || SelectedOption == null)
                return 0;
            int total = 0;
            for (int i = SelectedOption.SetIndex; i <= toc.LastSetOnVolume; i++)
                if (!toc[i].IsLocked)
                    total += toc[i].Count;
            return total;
        }
    }

    /// <summary>Locked sets among those to delete — their file counts are hidden.</summary>
    public int DeleteLockedSetsCount
    {
        get
        {
            var toc = _tapeService.TOC;
            if (toc == null || SelectedOption == null)
                return 0;
            int locked = 0;
            for (int i = SelectedOption.SetIndex; i <= toc.LastSetOnVolume; i++)
                if (toc[i].IsLocked)
                    locked++;
            return locked;
        }
    }

    /// <summary>Estimated size and file count of sets to delete, formatted.</summary>
    public string DeleteSizeDisplay
    {
        get
        {
            long size = ComputeDeleteSize();
            if (size <= 0)
                return "\u2014";

            int files = DeleteFileCount;
            int locked = DeleteLockedSetsCount;
            string filesPart = files > 0 ? $", {files:N0} file(s)" : string.Empty;
            string lockedPart = locked > 0 ? $" + {locked} locked set(s)" : string.Empty;
            return $"~{Helpers.BytesToStringLong(size)}{filesPart}{lockedPart}";
        }
    }

    /// <summary>Estimated remaining capacity after deletion, formatted.</summary>
    public string RemainingAfterDeleteDisplay
    {
        get
        {
            long deleteSize = ComputeDeleteSize();
            if (deleteSize <= 0)
                return "\u2014";
            return $"~{Helpers.BytesToStringLong(_mediaRemaining + deleteSize)}";
        }
    }

    // ═════════════════════════════════════════════════
    //  Commands
    // ═════════════════════════════════════════════════

    public ICommand DeleteCommand { get; }
    public ICommand CancelCommand { get; }

    // ═════════════════════════════════════════════════
    //  Private helpers
    // ═════════════════════════════════════════════════

    private void PopulateDeleteOptions()
    {
        DeleteOptions.Clear();

        var toc = _tapeService.TOC;
        if (toc == null || toc.Count == 0)
            return;

        // Show sets from latest to oldest — user picks the first set to delete
        for (int setIndex = toc.LastSetOnVolume; setIndex >= toc.FirstSetOnVolume; setIndex--)
        {
            int alt = toc.SetIndexToAlt(setIndex);
            var setTOC = toc[setIndex];
            DeleteOptions.Add(new DeleteFromSetOption(setTOC, setIndex, alt));
        }

        // Default: delete only the last set
        if (DeleteOptions.Count > 0)
            SelectedOption = DeleteOptions[0];
    }

    /// <summary>
    /// Estimates the on-tape size of the sets to be deleted by summing each set's
    /// block-aligned file sizes. This is an approximation (excludes inter-file overhead).
    /// </summary>
    private long ComputeDeleteSize()
    {
        var toc = _tapeService.TOC;
        if (toc == null || SelectedOption == null)
            return 0;

        long total = 0;
        for (int i = SelectedOption.SetIndex; i <= toc.LastSetOnVolume; i++)
        {
            var setTOC = toc[i];
            total += setTOC.ComputeTotalFileSizeOnTape();
        }
        return total;
    }
}
