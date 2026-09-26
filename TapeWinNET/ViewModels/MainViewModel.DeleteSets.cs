using System.Linq;
using System.Windows;
using System.Windows.Input;

using TapeWinNET.Services;

using TapeLibNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Partial class containing delete-backup-sets functionality for MainViewModel.
/// </summary>
public partial class MainViewModel
{
    private void ShowDeleteBackupSetsWindow(object? parameter)
    {
        var viewModel = new DeleteBackupSetsViewModel(
            _tapeService,
            OnStartDeleteBackupSets,
            () => Application.Current.Windows.OfType<DeleteBackupSetsWindow>().FirstOrDefault()?.Close());

        var window = new DeleteBackupSetsWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void OnStartDeleteBackupSets(DeleteBackupSetsViewModel deleteViewModel)
    {
        Application.Current.Windows.OfType<DeleteBackupSetsWindow>().FirstOrDefault()?.Close();
        _ = ExecuteDeleteBackupSetsAsync(deleteViewModel.DeleteFromSetIndex);
    }

    private async Task ExecuteDeleteBackupSetsAsync(int deleteFromSetIndex)
    {
        IsBusy = true;
        BusyMessage = "Deleting backup sets...";

        try
        {
            DeleteSetsResult? operationResult = await _tapeService.DeleteBackupSetsExAsync(deleteFromSetIndex);

            // Refused before anything was written (SH-13) — the tape and TOC are provably
            //  untouched, so no refresh is performed and no ambiguity is left for the user.
            if (operationResult is { Sets.SetWriteBlocked: true })
            {
                SimpleBox.Show(
                    "The backup set found on tape is not the one expected, so nothing was deleted. " +
                    "The tape is unchanged.\n\nSee log for details.",
                    "Delete Refused", MessageBoxButton.OK, SimpleBox.ImageFailed);
                return;
            }

            // Every other branch may have altered the TOC, even partially — refresh regardless of outcome.
            try { await ReloadMediaAsync(); } catch { /* ignore */ }

            if (operationResult is { WasAborted: true })
            {
                SimpleBox.Show(
                    "Deletion was aborted." + (operationResult.TapeUnchanged ? " The tape is unchanged." : ""),
                    "Delete Aborted", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (operationResult is { HasFailed: true } or { Success: false })
            {
                SimpleBox.Show($"Failed to delete backup sets.\n\n{operationResult.Message}",
                    "Delete Failed", MessageBoxButton.OK, SimpleBox.ImageFailed);
            }
            else
            {
                string message = $"{operationResult.SetsDeleted} backup set(s) deleted.";
                if (operationResult.Sets.HasAnomalies)
                    message += "\n\nBackup set positions had to be corrected during the operation — " +
                               "see log for details and advice.";

                SimpleBox.Show(message, "Delete Complete", MessageBoxButton.OK, SimpleBox.ImageComplete);
            }
        }
        catch (Exception ex)
        {
            LogErr($"Delete backup sets failed: {ex.Message}");
            SimpleBox.Show($"Delete failed.\n\n{ex.Message}", "Delete Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }
}
