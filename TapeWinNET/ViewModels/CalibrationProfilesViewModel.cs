using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeLibNET;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the "Calibration Profiles..." browser window (Media menu). Lists every
/// calibration profile previously persisted to <see cref="AppSettings.Calibrations"/>, lets the
/// user inspect one, apply it to the currently loaded media, or remove it from the store.
/// </summary>
public sealed class CalibrationProfilesViewModel : ViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Func<bool> _isBusy;
    private ITapeCalibration? _selectedProfile;
    private string _statusMessage = string.Empty;

    public CalibrationProfilesViewModel(TapeService tapeService, Func<bool> isBusy)
    {
        _tapeService = tapeService;
        _isBusy = isBusy;

        ApplyCommand = new RelayCommand(_ => Apply(), _ => CanApply);
        RemoveCommand = new RelayCommand(_ => Remove(), _ => SelectedProfile is not null);

        Reload();
    }

    #region Profiles

    public ObservableCollection<ITapeCalibration> Profiles { get; } = [];

    public ITapeCalibration? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
                return;

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ReportedCapacityAtBomDisplay));
            OnPropertyChanged(nameof(PhantomFreeAtEomDisplay));
            OnPropertyChanged(nameof(CapacityActualDisplay));
            OnPropertyChanged(nameof(EarlyWarningDisplay));
            OnPropertyChanged(nameof(EwToEomDistanceDisplay));
            OnPropertyChanged(nameof(CurvePointCountDisplay));
            StatusMessage = string.Empty;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => SelectedProfile is not null;

    public string ReportedCapacityAtBomDisplay =>
        SelectedProfile is not null ? Helpers.BytesToStringLong(SelectedProfile.ReportedCapacityAtBom) : "—";

    public string PhantomFreeAtEomDisplay =>
        SelectedProfile is not null ? Helpers.BytesToStringLong(SelectedProfile.PhantomFreeAtEom) : "—";

    public string CapacityActualDisplay =>
        SelectedProfile is not null ? Helpers.BytesToStringLong(SelectedProfile.CapacityActual) : "—";

    public string EarlyWarningDisplay =>
        SelectedProfile?.EarlyWarning is { } ew
            ? $"{Helpers.BytesToStringLong(ew.ActualRemaining)} remaining (reported {Helpers.BytesToStringLong(ew.ReportedRemaining)})"
            : "Not observed";

    public string EwToEomDistanceDisplay =>
        SelectedProfile is not null && SelectedProfile.EwToEomDistance > 0
            ? Helpers.BytesToStringLong(SelectedProfile.EwToEomDistance)
            : "—";

    public string CurvePointCountDisplay =>
        SelectedProfile is not null ? SelectedProfile.Curve.Count.ToString("N0") : "0";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Commands

    public ICommand ApplyCommand { get; }
    public ICommand RemoveCommand { get; }

    private bool CanApply =>
        SelectedProfile is not null && !_isBusy() && _tapeService.IsMediaLoaded;

    #endregion

    #region Operations

    private void Reload()
    {
        var selectedKey = SelectedProfile?.ProfileKey;

        Profiles.Clear();
        foreach (var profile in App.Settings.Calibrations.LoadAll())
            Profiles.Add(profile);

        SelectedProfile = selectedKey is null
            ? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault(p => p.ProfileKey == selectedKey) ?? Profiles.FirstOrDefault();
    }

    private void Apply()
    {
        if (SelectedProfile is null)
            return;

        bool matched = _tapeService.AddCalibration(SelectedProfile);
        StatusMessage = matched
            ? "Calibration profile applied to the current media."
            : "Calibration profile loaded, but it does not match the current media.";
    }

    private void Remove()
    {
        if (SelectedProfile is null)
            return;

        var result = SimpleBox.Show(
            $"Remove the calibration profile '{SelectedProfile.ProfileKey}'?\n\nThis cannot be undone.",
            "Remove Calibration Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        if (!App.Settings.Calibrations.Delete(SelectedProfile.ProfileKey))
        {
            SimpleBox.Show(
                $"Failed to remove the calibration profile.\n\n{App.Settings.Calibrations.LastErrorMessage}",
                "Remove Calibration Profile",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        SelectedProfile = null;
        Reload();
        StatusMessage = "Calibration profile removed.";
    }

    #endregion
}
