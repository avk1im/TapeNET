using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeLibNET;
using TapeLibNET.Services;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the calibration confirmation and result dialogs.
/// Owns the destructive calibration run and the subsequent Save/Apply actions.
/// </summary>
public sealed class CalibrationViewModel : ViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Action<CalibrationViewModel> _onStart;
    private readonly Action _onCancel;
    private readonly Action? _onApplied;
    private readonly CancellationTokenSource _abortCts = new();

    private bool _isConfirmChecked;
    private bool _isSaved;
    private bool _isApplied;
    private string _statusMessage = string.Empty;
    private CalibrateResult? _result;

    public CalibrationViewModel(
        TapeService tapeService,
        Action<CalibrationViewModel> onStart,
        Action onCancel,
        Action? onApplied = null)
    {
        _tapeService = tapeService;
        _onStart = onStart;
        _onCancel = onCancel;
        _onApplied = onApplied;

        StartCommand = new RelayCommand(_ => _onStart(this), _ => IsConfirmChecked);
        CancelCommand = new RelayCommand(_ => _onCancel());
        SaveProfileCommand = new RelayCommand(_ => SaveProfile(), _ => Result?.Calibration is not null && !IsSaved);
        ApplyProfileCommand = new RelayCommand(_ => ApplyProfile(), _ => Result?.Calibration is not null && !IsApplied);
    }

    #region Confirmation

    public bool IsConfirmChecked
    {
        get => _isConfirmChecked;
        set
        {
            if (SetProperty(ref _isConfirmChecked, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Vendor => string.IsNullOrWhiteSpace(_tapeService.DeviceVendor) ? "Unknown" : _tapeService.DeviceVendor;
    public string Product => string.IsNullOrWhiteSpace(_tapeService.DeviceProduct) ? "Unknown" : _tapeService.DeviceProduct;
    public string Revision => string.IsNullOrWhiteSpace(_tapeService.DeviceRevision) ? "Unknown" : _tapeService.DeviceRevision;
    public string ProfileKey => string.IsNullOrWhiteSpace(_tapeService.DriveProfileKey) ? "(unknown)" : _tapeService.DriveProfileKey;
    public string CapacityDisplay => Helpers.BytesToStringLong(_tapeService.Capacity);
    public string CapacityBucketDisplay => $"{TapeCalibration.CapacityBucket(_tapeService.Capacity)} bucket";
    public static WarningLevel WarningLevel => WarningLevel.Error;
    public static string WarningMessage =>
        "Calibration writes the scratch cartridge to end-of-media and destroys any existing content.\r\n" +
        "Use only expendable media dedicated to calibration.";

    #endregion

    #region Result

    public CalibrateResult? Result
    {
        get => _result;
        private set
        {
            if (!SetProperty(ref _result, value))
                return;

            OnPropertyChanged(nameof(Calibration));
            OnPropertyChanged(nameof(ReportedCapacityAtBomDisplay));
            OnPropertyChanged(nameof(PhantomFreeAtEomDisplay));
            OnPropertyChanged(nameof(CapacityActualDisplay));
            OnPropertyChanged(nameof(EarlyWarningDisplay));
            OnPropertyChanged(nameof(EwToEomDistanceDisplay));
            OnPropertyChanged(nameof(CurvePointCountDisplay));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ITapeCalibration? Calibration => Result?.Calibration;

    /// <summary>What the driver claimed was free on the virgin cartridge (quantity (4)).</summary>
    public string ReportedCapacityAtBomDisplay =>
        Result is not null ? Helpers.BytesToStringLong(Result.ReportedCapacityAtBom) : "—";

    /// <summary>The headline result: phantom free space still claimed at hard EOM (quantity (5)).</summary>
    public string PhantomFreeAtEomDisplay =>
        Result is not null ? Helpers.BytesToStringLong(Result.PhantomFreeAtEom) : "—";

    public string CapacityActualDisplay =>
        Result is not null ? Helpers.BytesToStringLong(Result.CapacityActual) : "—";

    public string EarlyWarningDisplay =>
        Result?.EarlyWarning is { } ew
            ? $"{Helpers.BytesToStringLong(ew.ActualRemaining)} remaining (reported {Helpers.BytesToStringLong(ew.ReportedRemaining)})"
            : "Not observed";

    public string EwToEomDistanceDisplay =>
        Result is not null && Result.EwToEomDistance > 0
            ? Helpers.BytesToStringLong(Result.EwToEomDistance)
            : "—";

    public string CurvePointCountDisplay =>
        Result is not null ? Result.CurvePointCount.ToString("N0") : "0";

    public bool IsSaved
    {
        get => _isSaved;
        private set
        {
            if (SetProperty(ref _isSaved, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsApplied
    {
        get => _isApplied;
        private set
        {
            if (SetProperty(ref _isApplied, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Commands

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand ApplyProfileCommand { get; }

    #endregion

    #region Operations

    public async Task<CalibrateResult> RunAsync()
    {
        Result = await _tapeService.ExecuteCalibrateAsync(
            new CalibrateRequest(
                EjectWhenDone: false,
                Options: new TapeCalibrationOptions())
            {
                Cancellation = _abortCts.Token,
                OperationLabel = "Calibration",
            });

        return Result;
    }

    public void RequestAbort() => _abortCts.Cancel();

    private void SaveProfile()
    {
        if (Calibration is null)
            return;

        if (!App.Settings.Calibrations.Save(Calibration))
        {
            SimpleBox.Show(
                $"Failed to save the calibration profile.\n\n{App.Settings.Calibrations.LastErrorMessage}",
                "Save Calibration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        IsSaved = true;
        StatusMessage = "Calibration profile saved.";
    }

    private void ApplyProfile()
    {
        if (Calibration is null)
            return;

        bool matched = _tapeService.AddCalibration(Calibration);
        IsApplied = true;
        StatusMessage = matched
            ? "Calibration profile applied to the current media."
            : "Calibration profile loaded, but it does not match the current media.";
        _onApplied?.Invoke();
    }

    #endregion
}
