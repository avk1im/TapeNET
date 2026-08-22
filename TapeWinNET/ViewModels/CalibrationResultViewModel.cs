using System.Windows;
using System.Windows.Input;

using TapeLibNET;
using TapeLibNET.Services;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the calibration result dialog (<see cref="TapeWinNET.CalibrationWindow"/>), backed by a
/// completed <see cref="CalibrateResult"/>. Owns Save/Apply, the recalibration verdict banner, and the
/// user-driven "run a full calibration after all" follow-up (via <see cref="CloseRequested"/>).
/// </summary>
public sealed class CalibrationResultViewModel : CalibrationResultViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly CalibrateResult _result;
    private readonly Action? _onApplied;
    private bool _isSaved;
    private bool _isApplied;

    public CalibrationResultViewModel(TapeService tapeService, CalibrateResult result, Action? onApplied = null)
    {
        _tapeService = tapeService;
        _result = result;
        _onApplied = onApplied;

        SaveProfileCommand = new RelayCommand(_ => SaveProfile(), _ => Calibration is not null && !IsSaved);
        ApplyProfileCommand = new RelayCommand(_ => ApplyProfile(), _ => Calibration is not null && !IsApplied && !_tapeService.HasInitiatorPartition);

        if (_result is { RecalibrationVerdict: RecalibrationVerdict.FullRecalibrationAdvised })
            RunFullCalibrationCommand = new RelayCommand(_ => RequestFullCalibration());
    }

    /// <inheritdoc/>
    public override ITapeCalibration? Calibration => _result.Calibration;

    #region Result

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

    #endregion

    #region Verdict banner (Recalibrate only)

    /// <inheritdoc/>
    public override bool HasVerdict => _result.RecalibrationVerdict is not null;

    /// <inheritdoc/>
    public override WarningLevel VerdictLevel =>
        _result.RecalibrationVerdict == RecalibrationVerdict.FullRecalibrationAdvised
            ? WarningLevel.Warning
            : WarningLevel.Info;

    /// <inheritdoc/>
    public override string VerdictMessage =>
        _result.RecalibrationVerdict switch
        {
            RecalibrationVerdict.Holds => "The existing calibration still holds — no full recalibration needed.",
            RecalibrationVerdict.FullRecalibrationAdvised =>
                "The drive's remaining-space behavior has shifted beyond tolerance — a full recalibration is advised.",
            _ => string.Empty,
        };

    /// <inheritdoc/>
    public override bool HasRecalibrationDelta => _result.RecalibrationDelta is not null;

    /// <inheritdoc/>
    public override string EwToEomDeltaDisplay =>
        _result.RecalibrationDelta is { } d
            ? $"{Windows.Win32.System.SystemServices.Helpers.BytesToStringLong(d.OldEwToEomDistance)} → " +
              $"{Windows.Win32.System.SystemServices.Helpers.BytesToStringLong(d.NewEwToEomDistance)} ({d.EwShiftFraction:+0.0%;-0.0%})"
            : string.Empty;

    /// <inheritdoc/>
    public override string CapacityActualDeltaDisplay =>
        _result.RecalibrationDelta is { } d
            ? $"{Windows.Win32.System.SystemServices.Helpers.BytesToStringLong(d.OldCapacityActual)} → " +
              $"{Windows.Win32.System.SystemServices.Helpers.BytesToStringLong(d.NewCapacityActual)} ({d.CapacityShiftFraction:+0.0%;-0.0%})"
            : string.Empty;

    /// <inheritdoc/>
    public override string PhantomFreeAtEomDeltaDisplay =>
        _result.RecalibrationDelta is { } d
            ? $"{Windows.Win32.System.SystemServices.Helpers.BytesToStringLong(d.OldPhantomFreeAtEom)} → " +
              $"{Windows.Win32.System.SystemServices.Helpers.BytesToStringLong(d.NewPhantomFreeAtEom)} ({d.PhantomShiftFraction:+0.0%;-0.0%})"
            : string.Empty;

    /// <inheritdoc/>
    public override ICommand? RunFullCalibrationCommand { get; }

    /// <summary>True when the user requested a follow-up full calibration via <see cref="RunFullCalibrationCommand"/>.</summary>
    public bool FullCalibrationRequested { get; private set; }

    /// <summary>Raised when the result window should close — either normally (Close) or to launch a
    ///  requested follow-up full calibration (<see cref="FullCalibrationRequested"/>).</summary>
    public event EventHandler? CloseRequested;

    private void RequestFullCalibration()
    {
        FullCalibrationRequested = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Commands

    public ICommand SaveProfileCommand { get; }
    public ICommand ApplyProfileCommand { get; }

    #endregion

    #region Operations

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
                SimpleBox.ImageFailed);
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
