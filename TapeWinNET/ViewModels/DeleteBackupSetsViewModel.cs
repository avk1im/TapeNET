using System.Collections.ObjectModel;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;

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
        _mediaRemaining = tapeService.Remaining;

        PopulateDeleteOptions();

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
            }
        }
    }

    /// <summary>
    /// Standard set index of the first set to delete. Used by the service layer.
    /// </summary>
    public int DeleteFromSetIndex => SelectedOption?.SetIndex ?? -1;

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

    /// <summary>Warning level for styling — always destructive.</summary>
    public WarningLevel WarningLevel
    {
        get
        {
            var toc = _tapeService.TOC;
            if (toc == null || SelectedOption == null)
                return WarningLevel.Warning;

            if (IsDeleteBlocked)
                return WarningLevel.Info; // just explain why "Delete" is disabled

            // Deleting all sets is more severe
            if (SelectedOption.SetIndex == toc.FirstSetOnVolume)
                return WarningLevel.Error;

            return WarningLevel.Warning;
        }
    }

    /// <summary>Warning message describing what will be deleted.</summary>
    public string WarningMessage
    {
        get
        {
            var toc = _tapeService.TOC;
            if (toc == null || SelectedOption == null)
                return string.Empty;

            int count = SetsToDeleteCount;
            int firstStd = SelectedOption.SetIndex;
            int firstAlt = SelectedOption.AltIndex;
            int lastStd = toc.LastSetOnVolume;
            int lastAlt = toc.SetIndexToAlt(lastStd);

            string message = (count == 1)
                ? $"1 backup set will be deleted: #{firstStd} | {firstAlt}"
                : $"{count} backup set(s) will be deleted: from #{firstStd} | {firstAlt} to #{lastStd} | {lastAlt}";

            if (toc.ContinuedOnNextVolume)
                message += "\r\nNote: Deleting from this media may invalidate a multi-volume backup";
            
            if (firstStd == toc.FirstSetOnVolume)
            {
                if (_tapeService.HasInitiatorPartition)
                    message = "Cannot delete all backup sets on media. Format media instead.";
                else
                    message += "\r\nThis will remove ALL backup sets from the media!";
            }

            return message;
        }
    }

    // ═════════════════════════════════════════════════
    //  Statistics
    // ═════════════════════════════════════════════════

    /// <summary>Total media capacity, formatted.</summary>
    public string MediaCapacityDisplay => Helpers.BytesToStringLong(_mediaCapacity);

    /// <summary>Current remaining capacity, formatted.</summary>
    public string MediaRemainingDisplay => Helpers.BytesToStringLong(_mediaRemaining);

    /// <summary>Estimated size of sets to delete, formatted.</summary>
    public string DeleteSizeDisplay
    {
        get
        {
            long size = ComputeDeleteSize();
            return size > 0 ? $"~{Helpers.BytesToStringLong(size)}" : "\u2014";
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
