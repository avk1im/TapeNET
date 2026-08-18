using System.Windows.Input;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeLibNET;
using TapeLibNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Shared display surface for a calibration result: the measured figures (profile key, reported vs.
/// actual capacity, phantom, EW landmark, curve point count) and — for <see cref="CalibrationMode.Recalibrate"/>
/// — the verdict banner and before/after delta rows. Owns nothing operation-specific (no Save/Apply/Run),
/// so both <see cref="CalibrationResultViewModel"/> (a fresh run's result) and
/// <see cref="CalibrationProfilesViewModel"/> (a browsed, stored profile) derive from it and share the
/// <c>CalibrationResultView</c> user control.
/// </summary>
public abstract class CalibrationResultViewModelBase : ViewModelBase
{
    private string _statusMessage = string.Empty;

    /// <summary>The calibration currently on display, or <see langword="null"/> when none is selected.</summary>
    public abstract ITapeCalibration? Calibration { get; }

    #region Measured result display

    public string ProfileKeyDisplay =>
        Calibration is not null ? Calibration.ProfileKey : "(unknown)";

    /// <summary>What the driver claimed was free on the virgin cartridge (quantity (4)).</summary>
    public string ReportedCapacityAtBomDisplay =>
        Calibration is not null ? Helpers.BytesToStringLong(Calibration.ReportedCapacityAtBom) : "—";

    /// <summary>The headline result: phantom free space still claimed at hard EOM (quantity (5)).</summary>
    public string PhantomFreeAtEomDisplay =>
        Calibration is not null ? Helpers.BytesToStringLong(Calibration.PhantomFreeAtEom) : "—";

    public string CapacityActualDisplay =>
        Calibration is not null ? Helpers.BytesToStringLong(Calibration.CapacityActual) : "—";

    public string EarlyWarningDisplay =>
        Calibration?.EarlyWarning is { } ew
            ? $"{Helpers.BytesToStringLong(ew.ActualRemaining)} remaining (reported {Helpers.BytesToStringLong(ew.ReportedRemaining)})"
            : "Not observed";

    public string EwToEomDistanceDisplay =>
        Calibration is not null && Calibration.EwToEomDistance > 0
            ? Helpers.BytesToStringLong(Calibration.EwToEomDistance)
            : "—";

    public string CurvePointCountDisplay =>
        Calibration is not null ? Calibration.Curve.Count.ToString("N0") : "0";

    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Verdict banner (Recalibrate only — defaults to "no verdict")

    /// <summary>True when a recalibration verdict is available to display.</summary>
    public virtual bool HasVerdict => false;

    public virtual WarningLevel VerdictLevel => WarningLevel.Info;

    /// <summary>Alias for <see cref="VerdictLevel"/> so the shared <c>WarningPanelStyle</c> border
    ///  (bound to <c>WarningLevel</c>) can drive the verdict banner's visibility/colors.</summary>
    public WarningLevel WarningLevel => VerdictLevel;

    public virtual string VerdictMessage => string.Empty;

    /// <summary>True when before/after delta rows should be shown alongside the measured result.</summary>
    public virtual bool HasRecalibrationDelta => false;

    public virtual string EwToEomDeltaDisplay => string.Empty;

    public virtual string CapacityActualDeltaDisplay => string.Empty;

    public virtual string PhantomFreeAtEomDeltaDisplay => string.Empty;

    /// <summary>Command to request a full recalibration after an advisory verdict, or <see langword="null"/>
    ///  when this view has no such action (e.g. the profiles browser).</summary>
    public virtual ICommand? RunFullCalibrationCommand => null;

    #endregion

    /// <summary>Raises property-changed for every display property derived from <see cref="Calibration"/>.
    ///  Call after the underlying calibration/result changes.</summary>
    protected void RaiseResultPropertiesChanged()
    {
        OnPropertyChanged(nameof(Calibration));
        OnPropertyChanged(nameof(ProfileKeyDisplay));
        OnPropertyChanged(nameof(ReportedCapacityAtBomDisplay));
        OnPropertyChanged(nameof(PhantomFreeAtEomDisplay));
        OnPropertyChanged(nameof(CapacityActualDisplay));
        OnPropertyChanged(nameof(EarlyWarningDisplay));
        OnPropertyChanged(nameof(EwToEomDistanceDisplay));
        OnPropertyChanged(nameof(CurvePointCountDisplay));
        OnPropertyChanged(nameof(HasVerdict));
        OnPropertyChanged(nameof(VerdictLevel));
        OnPropertyChanged(nameof(WarningLevel));
        OnPropertyChanged(nameof(VerdictMessage));
        OnPropertyChanged(nameof(HasRecalibrationDelta));
        OnPropertyChanged(nameof(EwToEomDeltaDisplay));
        OnPropertyChanged(nameof(CapacityActualDeltaDisplay));
        OnPropertyChanged(nameof(PhantomFreeAtEomDeltaDisplay));
    }
}
