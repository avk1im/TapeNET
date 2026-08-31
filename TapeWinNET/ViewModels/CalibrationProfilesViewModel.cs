using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

using Microsoft.Win32;

using TapeLibNET;
using TapeWinNET.Controls;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the "Calibration Profiles..." browser window (Media menu). Lists every
/// calibration profile version previously persisted to <see cref="AppSettings.Calibrations"/>, lets the
/// user inspect one, apply it to the currently loaded media, remove a specific version, or exchange
/// profiles as files (import/export bundles).
/// </summary>
public sealed class CalibrationProfilesViewModel : CalibrationResultViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Func<bool> _isBusy;
    private ITapeCalibration? _selectedProfile;
    private bool _onlyForThisDrive = true;

    public CalibrationProfilesViewModel(TapeService tapeService, Func<bool> isBusy)
    {
        _tapeService = tapeService;
        _isBusy = isBusy;

        ApplyCommand = new RelayCommand(_ => Apply(), _ => CanApply);
        RemoveCommand = new RelayCommand(_ => Remove(), _ => SelectedProfile is not null);
        ImportCommand = new RelayCommand(_ => Import());
        ExportCurrentCommand = new RelayCommand(_ => ExportCurrent(), _ => SelectedProfile is not null);
        ExportDriveProfilesCommand = new RelayCommand(_ => ExportDriveProfiles(), _ => CanFilterByDrive);
        ExportAllProfilesCommand = new RelayCommand(_ => ExportAllProfiles());

        ExportMenuItems =
        [
            new() { Header = "Current profile", Command = ExportCurrentCommand },
            new() { Header = "All for this drive", Command = ExportDriveProfilesCommand },
            new() { Header = "All on this system", Command = ExportAllProfilesCommand },
        ];

        Reload();
    }

    #region Profiles

    /// <summary>Every stored profile version matching the current filter (see <see cref="OnlyForThisDrive"/>).</summary>
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
            OnPropertyChanged(nameof(MatchesLoadedMediaForSelection));
            OnPropertyChanged(nameof(IsAlreadyActiveForSelection));
            RaiseResultPropertiesChanged();
            StatusMessage = string.Empty;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => SelectedProfile is not null;

    /// <summary>
    /// When checked (default), the profile combobox is filtered to the loaded drive by the
    /// <c>vendor|product|revision</c> prefix of <see cref="ITapeCalibration.ProfileKey"/> — same
    /// physical drive, any media capacity. Disabled (all profiles shown) when no drive/media is loaded.
    /// </summary>
    public bool OnlyForThisDrive
    {
        get => _onlyForThisDrive;
        set
        {
            if (!SetProperty(ref _onlyForThisDrive, value))
                return;
            Reload();
        }
    }

    /// <summary>True when a drive+media is loaded, so the "only for this drive" filter has meaning.</summary>
    public bool CanFilterByDrive => _tapeService.IsMediaLoaded && !string.IsNullOrEmpty(_tapeService.DriveProfileKey);

    #endregion
    #region Commands

    public ICommand ApplyCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCurrentCommand { get; }
    public ICommand ExportDriveProfilesCommand { get; }
    public ICommand ExportAllProfilesCommand { get; }

    /// <summary>Entries for the "Export…" split button's dropdown menu.</summary>
    public ObservableCollection<SplitButtonMenuItem> ExportMenuItems { get; }

    private bool CanApply =>
        SelectedProfile is not null && !_isBusy()
        && MatchesLoadedMediaForSelection && !IsAlreadyActiveForSelection;

    /// <summary>True when the selected profile's full key matches the loaded media.</summary>
    public bool MatchesLoadedMediaForSelection =>
        SelectedProfile is { } p && _tapeService.IsMediaLoaded && !_tapeService.HasInitiatorPartition
        && string.Equals(p.ProfileKey, _tapeService.DriveProfileKey, StringComparison.Ordinal);

    /// <summary>True when the selected profile is already the drive's active calibration.</summary>
    public bool IsAlreadyActiveForSelection =>
        SelectedProfile is { } p && _tapeService.Calibration is { } active
        && string.Equals(active.ProfileKey, p.ProfileKey, StringComparison.Ordinal)
        && active.MeasuredUtc == p.MeasuredUtc;

    #endregion

    #region Operations

    private void Reload()
    {
        var selectedKey = SelectedProfile?.ProfileKey;
        var selectedUtc = SelectedProfile?.MeasuredUtc;

        var all = App.Settings.Calibrations.LoadAll();

        IEnumerable<ITapeCalibration> filtered = all;
        if (OnlyForThisDrive && CanFilterByDrive)
        {
            string drivePrefix = DrivePrefix(_tapeService.DriveProfileKey);
            filtered = all.Where(p => DrivePrefix(p.ProfileKey) == drivePrefix);
        }

        Profiles.Clear();
        foreach (var profile in filtered.OrderByDescending(p => p.MeasuredUtc ?? DateTime.MinValue))
            Profiles.Add(profile);

        OnPropertyChanged(nameof(CanFilterByDrive));

        // Auto-select: the active calibration if listed, else the newest for the loaded media, else the first.
        ITapeCalibration? toSelect = null;
        if (_tapeService.Calibration is { } active)
            toSelect = Profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileKey, active.ProfileKey, StringComparison.Ordinal) && p.MeasuredUtc == active.MeasuredUtc);

        toSelect ??= selectedKey is not null
            ? Profiles.FirstOrDefault(p => p.ProfileKey == selectedKey && p.MeasuredUtc == selectedUtc)
            : null;

        toSelect ??= _tapeService.IsMediaLoaded
            ? Profiles.Where(p => string.Equals(p.ProfileKey, _tapeService.DriveProfileKey, StringComparison.Ordinal))
                      .OrderByDescending(p => p.MeasuredUtc ?? DateTime.MinValue)
                      .FirstOrDefault()
            : null;

        toSelect ??= Profiles.FirstOrDefault();

        SelectedProfile = toSelect;
    }

    // Vendor|product|revision prefix of a "vendor|product|revision|NNNGB" profile key.
    private static string DrivePrefix(string profileKey)
    {
        int lastPipe = profileKey.LastIndexOf('|');
        return lastPipe < 0 ? profileKey : profileKey[..lastPipe];
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
            $"Remove the calibration profile '{SelectedProfile.ProfileKey}'" +
            (SelectedProfile.MeasuredUtc is { } utc ? $" (measured {utc.ToLocalTime():g})" : "") +
            "?\n\nThis cannot be undone.",
            "Remove Calibration Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        if (!App.Settings.Calibrations.Delete(SelectedProfile.ProfileKey, SelectedProfile.MeasuredUtc))
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

    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Calibration Profile(s)",
            Filter = "Calibration files (*.tapecal.json;*.tapecals.json)|*.tapecal.json;*.tapecals.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            if (!App.Settings.Calibrations.Import(stream, out int imported, out int skipped))
            {
                SimpleBox.Show(
                    "The selected file is not a recognized calibration profile or bundle.",
                    "Import Calibration Profile(s)", MessageBoxButton.OK, SimpleBox.ImageFailed);
                return;
            }

            Reload();
            StatusMessage = $"Imported {imported}, skipped {skipped} (already present).";
        }
        catch (IOException ex)
        {
            SimpleBox.Show($"Failed to import calibration profile(s).\n\n{ex.Message}",
                "Import Calibration Profile(s)", MessageBoxButton.OK, SimpleBox.ImageFailed);
        }
    }

    private void ExportCurrent()
    {
        if (SelectedProfile is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Calibration Profile",
            Filter = "Calibration file (*.tapecal.json)|*.tapecal.json|All files (*.*)|*.*",
            FileName = SelectedProfile.ProfileKey.Replace('|', '_') + TapeCalibrationStore.SingleFileExtension,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using var stream = File.Create(dialog.FileName);
            SelectedProfile.SaveTo(stream);
            StatusMessage = $"Exported to:\n{dialog.FileName}";
        }
        catch (IOException ex)
        {
            SimpleBox.Show($"Failed to export calibration profile.\n\n{ex.Message}",
                "Export Calibration Profile", MessageBoxButton.OK, SimpleBox.ImageFailed);
        }
    }

    private void ExportDriveProfiles()
    {
        if (!CanFilterByDrive)
            return;

        string drivePrefix = DrivePrefix(_tapeService.DriveProfileKey);
        var keys = App.Settings.Calibrations.LoadAll()
            .Where(p => DrivePrefix(p.ProfileKey) == drivePrefix)
            .Select(p => p.ProfileKey)
            .Distinct()
            .ToList();

        ExportBundle(keys, "Export Calibration Profiles for This Drive");
    }

    private void ExportAllProfiles()
    {
        var keys = App.Settings.Calibrations.LoadAll()
            .Select(p => p.ProfileKey)
            .Distinct()
            .ToList();

        ExportBundle(keys, "Export All Calibration Profiles");
    }

    private void ExportBundle(List<string> profileKeys, string title)
    {
        if (profileKeys.Count == 0)
        {
            SimpleBox.Show("There are no calibration profiles to export.",
                title, MessageBoxButton.OK, SimpleBox.ImageFailed);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "Calibration bundle (*.tapecals.json)|*.tapecals.json|All files (*.*)|*.*",
            FileName = "calibrations" + TapeCalibrationStore.BundleFileExtension,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using var stream = File.Create(dialog.FileName);
            App.Settings.Calibrations.Export(stream, profileKeys);
            StatusMessage = $"Exported {profileKeys.Count} profile(s) to:\n{dialog.FileName}";
        }
        catch (IOException ex)
        {
            SimpleBox.Show($"Failed to export calibration profiles.\n\n{ex.Message}",
                title, MessageBoxButton.OK, SimpleBox.ImageFailed);
        }
    }

    #endregion
}
