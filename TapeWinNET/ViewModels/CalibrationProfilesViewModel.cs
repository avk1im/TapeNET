using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

using TapeLibNET;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the "Calibration Profiles..." browser window (Media menu). Lists every
/// calibration profile previously persisted to <see cref="AppSettings.Calibrations"/>, lets the
/// user inspect one, apply it to the currently loaded media, or remove it from the store.
/// </summary>
public sealed class CalibrationProfilesViewModel : CalibrationResultViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Func<bool> _isBusy;
    private ITapeCalibration? _selectedProfile;

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

    /// <inheritdoc/>
    public override ITapeCalibration? Calibration => SelectedProfile;

    public ITapeCalibration? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
                return;

            OnPropertyChanged(nameof(HasSelection));
            RaiseResultPropertiesChanged();
            StatusMessage = string.Empty;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => SelectedProfile is not null;

    #endregion

    #region Commands

    public ICommand ApplyCommand { get; }
    public ICommand RemoveCommand { get; }

    private bool CanApply =>
        SelectedProfile is not null && !_isBusy() && _tapeService.IsMediaLoaded && !_tapeService.HasInitiatorPartition;

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
                SimpleBox.ImageFailed);
            return;
        }

        SelectedProfile = null;
        Reload();
        StatusMessage = "Calibration profile removed.";
    }

    #endregion
}
