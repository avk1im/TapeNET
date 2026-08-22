using System.Windows.Input;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeLibNET;
using TapeLibNET.Services;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the calibration setup/confirmation dialog (<see cref="TapeWinNET.CalibrateWindow"/>).
/// Owns mode selection (New / Resume / Recalibrate), the optional Inspect Media convenience probe, and
/// the destructive calibration run itself. Result display/Save/Apply now live in
/// <see cref="CalibrationResultViewModel"/>, shown by a separate result window once the run completes.
/// </summary>
public sealed class CalibrationRunViewModel : ViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Action<CalibrationRunViewModel> _onStart;
    private readonly Action _onCancel;
    private readonly CancellationTokenSource _abortCts = new();

    private bool _isConfirmChecked;
    private bool _ejectWhenDone;
    private CalibrationMode _selectedMode = CalibrationMode.New;
    private bool _isInspecting;
    private string _inspectionSummary = string.Empty;
    private WarningLevel _inspectionLevel = WarningLevel.Info;
    private bool _hasInspectionResult;

    public CalibrationRunViewModel(
        TapeService tapeService,
        Action<CalibrationRunViewModel> onStart,
        Action onCancel)
    {
        _tapeService = tapeService;
        _onStart = onStart;
        _onCancel = onCancel;

        StartCommand = new RelayCommand(_ => _onStart(this), _ => IsConfirmChecked);
        CancelCommand = new RelayCommand(_ => _onCancel());
        InspectMediaCommand = new RelayCommand(async _ => await InspectMediaAsync(), _ => !_isInspecting);
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

    /// <summary>Whether to eject the media once the calibration run completes.</summary>
    public bool EjectWhenDone
    {
        get => _ejectWhenDone;
        set => SetProperty(ref _ejectWhenDone, value);
    }

    public string Vendor => string.IsNullOrWhiteSpace(_tapeService.DeviceVendor) ? "Unknown" : _tapeService.DeviceVendor;
    public string Product => string.IsNullOrWhiteSpace(_tapeService.DeviceProduct) ? "Unknown" : _tapeService.DeviceProduct;
    public string Revision => string.IsNullOrWhiteSpace(_tapeService.DeviceRevision) ? "Unknown" : _tapeService.DeviceRevision;
    public string ProfileKey => string.IsNullOrWhiteSpace(_tapeService.DriveProfileKey) ? "(unknown)" : _tapeService.DriveProfileKey;
    public string CapacityDisplay => Helpers.BytesToStringLong(_tapeService.Capacity);
    public string CapacityBucketDisplay => $"{TapeCalibration.CapacityBucket(_tapeService.Capacity)} bucket";
    public static WarningLevel WarningLevel => WarningLevel.Warning;
    public static string WarningMessage =>
        "Calibration writes the scratch cartridge to end-of-media and destroys any existing content.\r\n" +
        "Use only expendable media dedicated to calibration.";

    #endregion

    #region Mode selection

    /// <summary>Which calibration operation to perform. All three remain available at all times —
    ///  Inspect Media is an optional convenience, never a gate.</summary>
    public CalibrationMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value))
                return;

            OnPropertyChanged(nameof(IsNewRunMode));
            OnPropertyChanged(nameof(IsResumeMode));
            OnPropertyChanged(nameof(IsRecalibrateMode));
            OnPropertyChanged(nameof(IsInspectAvailable));
        }
    }

    public bool IsNewRunMode
    {
        get => SelectedMode == CalibrationMode.New;
        set { if (value) SelectedMode = CalibrationMode.New; }
    }

    public bool IsResumeMode
    {
        get => SelectedMode == CalibrationMode.Resume;
        set { if (value) SelectedMode = CalibrationMode.Resume; }
    }

    public bool IsRecalibrateMode
    {
        get => SelectedMode == CalibrationMode.Recalibrate;
        set { if (value) SelectedMode = CalibrationMode.Recalibrate; }
    }

    /// <summary>True for Resume/Recalibrate — the modes where inspecting the cartridge first is a
    ///  useful (but never required) convenience.</summary>
    public bool IsInspectAvailable => !IsNewRunMode;

    #endregion

    #region Inspection (optional convenience — never gates a mode)

    public string InspectionSummary
    {
        get => _inspectionSummary;
        private set => SetProperty(ref _inspectionSummary, value);
    }

    public WarningLevel InspectionLevel
    {
        get => _inspectionLevel;
        private set => SetProperty(ref _inspectionLevel, value);
    }

    public bool HasInspectionResult
    {
        get => _hasInspectionResult;
        private set => SetProperty(ref _hasInspectionResult, value);
    }

    public ICommand InspectMediaCommand { get; }

    private async Task InspectMediaAsync()
    {
        _isInspecting = true;
        CommandManager.InvalidateRequerySuggested();

        InspectionSummary = "Inspecting media, please wait...";
        InspectionLevel = WarningLevel.Info;
        HasInspectionResult = true;

        try
        {
            InspectCalibrationMediaResult result = await _tapeService.ExecuteInspectCalibrationMediaAsync();

            InspectionSummary = result.Success
                ? result.Summary
                : $"Inspection failed: {result.Message}";

            // Severity mirrors the cartridge state the service resolved, not merely "has a header":
            //  - failure                         → Failed
            //  - no trail (New required)         → Info   (normal — a scratch cartridge)
            //  - header present but no checkpoint→ Warning (a trail exists but is unusable)
            //  - trail from a different drive    → Warning (usable, but worth flagging)
            //  - resumable/complete on this drive→ Info
            InspectionLevel = !result.Success
                ? WarningLevel.Failed
                : !result.HasRunHeader
                    ? WarningLevel.Info
                    : (!result.HasCheckpoint || !result.MatchesCurrentDrive)
                        ? WarningLevel.Warning
                        : WarningLevel.Info;

            HasInspectionResult = true;

            // Default the mode selector to the recommendation — but only on a successful read, so a failed
            //  inspection never silently flips the user's chosen mode (e.g. back to New).
            if (result.Success && result.RecommendedMode is { } recommended)
                SelectedMode = recommended;
        }
        finally
        {
            _isInspecting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    #endregion

    #region Commands

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }

    #endregion

    #region Operations

    public async Task<CalibrateResult> RunAsync()
    {
        var result = await _tapeService.ExecuteCalibrateAsync(
            new CalibrateRequest(
                EjectWhenDone: EjectWhenDone,
                Options: new TapeCalibrationOptions(),
                Mode: SelectedMode)
            {
                Cancellation = _abortCts.Token,
                OperationLabel = "Calibration",
            });

        return result;
    }

    public void RequestAbort() => _abortCts.Cancel();

    #endregion
}
