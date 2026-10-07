using System.Windows;
using System.Windows.Input;

using TapeLibNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Partial class containing Scan Media functionality for <see cref="MainViewModel"/>.
/// A scan is READ-ONLY: it writes nothing and never touches the TOC, so — unlike calibration — nothing
///  here ever reloads the tree afterwards (only an explicit "Use this TOC" from the result window does).
/// </summary>
public partial class MainViewModel
{
    #region Scan Fields

    private double _scanProgressPercent;
    private string _scanProgressText = string.Empty;
    private string _currentScanPhase = string.Empty;
    private bool _isScanInProgress;
    private bool _isAbortScanEnabled = true;
    private CancellationTokenSource? _scanCts;

    #endregion

    #region Scan Properties

    public bool IsScanInProgress
    {
        get => _isScanInProgress;
        set
        {
            if (SetProperty(ref _isScanInProgress, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                OnPropertyChanged(nameof(IsOperationInProgress));
                OnPropertyChanged(nameof(IsMediaBrowsingEnabled));
                NotifyOperationPropertiesChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public double ScanProgressPercent
    {
        get => _scanProgressPercent;
        set
        {
            if (SetProperty(ref _scanProgressPercent, value))
                OnPropertyChanged(nameof(OperationProgressPercent));
        }
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        set
        {
            if (SetProperty(ref _scanProgressText, value))
                OnPropertyChanged(nameof(OperationProgressText));
        }
    }

    public string CurrentScanPhase
    {
        get => _currentScanPhase;
        set
        {
            if (SetProperty(ref _currentScanPhase, value))
                OnPropertyChanged(nameof(CurrentOperationFile));
        }
    }

    public bool IsAbortScanEnabled
    {
        get => _isAbortScanEnabled;
        set
        {
            if (SetProperty(ref _isAbortScanEnabled, value))
                OnPropertyChanged(nameof(IsAbortOperationEnabled));
        }
    }

    #endregion

    #region Scan Commands

    public ICommand ScanMediaCommand { get; private set; } = null!;
    public ICommand AbortScanCommand { get; private set; } = null!;

    private void InitializeScanCommands()
    {
        ScanMediaCommand = new RelayCommand(ShowScanWindow, _ => !IsBusy && _tapeService.IsMediaLoaded);
        AbortScanCommand = new RelayCommand(AbortScan, _ => IsScanInProgress);
    }

    #endregion

    #region Private Methods - Scan Operations

    /// <summary>Shows the (one-checkbox) Scan Media setup dialog; its Scan button starts the operation.</summary>
    private void ShowScanWindow(object? parameter)
    {
        var viewModel = new ScanMediaViewModel(
            OnStartScan,
            () => Application.Current.Windows.OfType<ScanMediaWindow>().FirstOrDefault()?.Close());

        var window = new ScanMediaWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void OnStartScan(ScanMediaViewModel viewModel)
    {
        Application.Current.Windows.OfType<ScanMediaWindow>().FirstOrDefault()?.Close();
        _ = ExecuteScanAsync(viewModel.BuildRequest());
    }

    /// <summary>
    /// Runs the scan in the shared operation overlay, then shows the result window.
    /// </summary>
    /// <remarks>
    /// The busy/in-progress state is reset BEFORE the result window opens (not just in <c>finally</c>): the
    ///  window is modal, and its follow-up actions (Use this TOC, Inspect Media...) need the service and the
    ///  commands' <c>!IsBusy</c> guards free. The tree is deliberately NOT reloaded after the scan.
    /// </remarks>
    private async Task ExecuteScanAsync(ScanMediaRequest request)
    {
        IsBusy = true;
        IsScanInProgress = true;
        IsAbortScanEnabled = true;
        BusyMessage = "Scanning media...";
        ScanProgressPercent = 0;
        ScanProgressText = "Starting...";
        CurrentScanPhase = string.Empty;

        // The abort is cooperative via the request token; linked to the service's own token inside.
        using var cts = new CancellationTokenSource();
        _scanCts = cts;

        ScanMediaResult? result = null;
        try
        {
            result = await _tapeService.ScanMediaAsync(request with { Cancellation = cts.Token });
        }
        catch (Exception ex)
        {
            LogErr($"Scan failed: {ex.Message}");
            SimpleBox.Show($"Scan failed.\n\n{ex.Message}", "Scan Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ResetScanState();
        }

        if (result is null)
            return;

        // No map at all (e.g. no media, drive error): nothing to show beyond the reason.
        if (result.Map is null)
        {
            SimpleBox.Show($"Scan failed.\n\n{result.Message}", "Scan Failed",
                MessageBoxButton.OK, SimpleBox.ImageFailed);
            return;
        }

        try
        {
            var resultViewModel = new ScanResultViewModel(
                _tapeService,
                result,
                onTocAdopted: () =>
                {
                    UpdateTreeFromTOC(_tapeService.DriveNumber);
                    SelectMostRecentSet();
                });

            var resultWindow = new ScanResultWindow(resultViewModel)
            {
                Owner = Application.Current.MainWindow
            };
            resultWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            LogErr($"Couldn't show scan result: {ex.Message}");
        }
    }

    private void ResetScanState()
    {
        _scanCts = null;
        IsScanInProgress = false;
        IsAbortScanEnabled = true;
        IsBusy = false;
        BusyMessage = string.Empty;
        ScanProgressText = string.Empty;
        CurrentScanPhase = string.Empty;
    }

    /// <summary>Requests a cooperative abort. No confirmation: a scan writes nothing, so nothing can be lost.</summary>
    private void AbortScan(object? parameter)
    {
        if (_scanCts is not { } cts)
            return;

        IsAbortScanEnabled = false;
        BusyMessage = "Aborting scan...";
        cts.Cancel();
    }

    #endregion
}
