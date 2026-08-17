using System.Linq;
using System.Windows;
using System.Windows.Input;

using TapeLibNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Partial class containing calibration-related functionality for <see cref="MainViewModel"/>.
/// </summary>
public partial class MainViewModel
{
    #region Calibration Fields

    private double _calibrationProgressPercent;
    private string _calibrationProgressText = string.Empty;
    private string _currentCalibrationPhase = string.Empty;
    private bool _isCalibrateInProgress;
    private bool _isAbortCalibrationEnabled = true;
    private CalibrationViewModel? _activeCalibrationViewModel;

    #endregion

    #region Calibration Properties

    public bool IsCalibrateInProgress
    {
        get => _isCalibrateInProgress;
        set
        {
            if (SetProperty(ref _isCalibrateInProgress, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                OnPropertyChanged(nameof(IsOperationInProgress));
                OnPropertyChanged(nameof(IsMediaBrowsingEnabled));
                NotifyOperationPropertiesChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public double CalibrationProgressPercent
    {
        get => _calibrationProgressPercent;
        set
        {
            if (SetProperty(ref _calibrationProgressPercent, value))
                OnPropertyChanged(nameof(OperationProgressPercent));
        }
    }

    public string CalibrationProgressText
    {
        get => _calibrationProgressText;
        set
        {
            if (SetProperty(ref _calibrationProgressText, value))
                OnPropertyChanged(nameof(OperationProgressText));
        }
    }

    public string CurrentCalibrationPhase
    {
        get => _currentCalibrationPhase;
        set
        {
            if (SetProperty(ref _currentCalibrationPhase, value))
                OnPropertyChanged(nameof(CurrentOperationFile));
        }
    }

    public bool IsAbortCalibrationEnabled
    {
        get => _isAbortCalibrationEnabled;
        set
        {
            if (SetProperty(ref _isAbortCalibrationEnabled, value))
                OnPropertyChanged(nameof(IsAbortOperationEnabled));
        }
    }

    #endregion

    #region Calibration Commands

    public ICommand CalibrateMediaCommand { get; private set; } = null!;
    public ICommand AbortCalibrationCommand { get; private set; } = null!;
    public ICommand ShowCalibrationProfilesCommand { get; private set; } = null!;

    private void InitializeCalibrationCommands()
    {
        CalibrateMediaCommand = new RelayCommand(ShowCalibrationWindow, _ => !IsBusy && _tapeService.IsMediaLoaded);
        AbortCalibrationCommand = new RelayCommand(AbortCalibration, _ => IsCalibrateInProgress);
        ShowCalibrationProfilesCommand = new RelayCommand(ShowCalibrationProfilesWindow);
    }

    #endregion

    #region Private Methods - Calibration Operations

    private void ShowCalibrationWindow(object? parameter)
    {
        var viewModel = new CalibrationViewModel(
            _tapeService,
            OnStartCalibration,
            () => Application.Current.Windows.OfType<CalibrateWindow>().FirstOrDefault()?.Close(),
            onApplied: RefreshCurrentView);

        var window = new CalibrateWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void ShowCalibrationProfilesWindow(object? parameter)
    {
        var viewModel = new CalibrationProfilesViewModel(_tapeService, () => IsBusy);

        var window = new CalibrationProfilesWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void OnStartCalibration(CalibrationViewModel viewModel)
    {
        Application.Current.Windows.OfType<CalibrateWindow>().FirstOrDefault()?.Close();
        _ = ExecuteCalibrationAsync(viewModel);
    }

    private async Task ExecuteCalibrationAsync(CalibrationViewModel viewModel)
    {
        IsBusy = true;
        IsCalibrateInProgress = true;
        IsAbortCalibrationEnabled = true;
        BusyMessage = "Preparing calibration...";
        CalibrationProgressPercent = 0;
        CalibrationProgressText = "Starting...";
        CurrentCalibrationPhase = string.Empty;
        _activeCalibrationViewModel = viewModel;

        try
        {
            var operationResult = await viewModel.RunAsync();

            // Calibration overwrites the media from the moment PrepareMedia succeeds, regardless
            //  of the eventual outcome, so the TOC the service (and this tree/view) previously held
            //  is now stale. Drop back to the "media loaded, no TOC" (or, if EjectWhenDone ejected
            //  the media, "no media") state to match reality.
            if (_tapeService.TOC == null)
                UpdateTreeForDriveOnly(_tapeService.DriveNumber);

            if (operationResult is { HasFailed: true })
            {
                SimpleBox.Show("Calibration failed. See log for details.", "Calibration Failed",
                    MessageBoxButton.OK, SimpleBox.ImageFailed);
                return;
            }

            if (operationResult is { WasAborted: true })
            {
                SimpleBox.Show("Calibration was aborted.", "Calibration Aborted",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _activeCalibrationViewModel = null;
            IsCalibrateInProgress = false;
            IsAbortCalibrationEnabled = true;
            IsBusy = false;
            BusyMessage = string.Empty;
            CalibrationProgressText = string.Empty;
            CurrentCalibrationPhase = string.Empty;

            var resultWindow = new CalibrationWindow(viewModel)
            {
                Owner = Application.Current.MainWindow
            };
            resultWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            LogErr($"Calibration failed: {ex.Message}");
            SimpleBox.Show($"Calibration failed.\n\n{ex.Message}", "Calibration Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _activeCalibrationViewModel = null;
            IsCalibrateInProgress = false;
            IsAbortCalibrationEnabled = true;
            IsBusy = false;
            BusyMessage = string.Empty;
            CalibrationProgressText = string.Empty;
            CurrentCalibrationPhase = string.Empty;
        }
    }

    private void AbortCalibration(object? parameter)
    {
        if (_activeCalibrationViewModel is null)
            return;

        var result = SimpleBox.Show(
            "Are you sure you want to abort the calibration?\n\nThe scratch media may already be partially written.",
            "Abort Calibration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        _activeCalibrationViewModel.RequestAbort();
        IsAbortCalibrationEnabled = false;
        BusyMessage = "Aborting calibration...";
    }

    #endregion
}
