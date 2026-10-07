using System.Windows.Input;
using TapeLibNET.Scan;
using TapeLibNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the Scan Media setup dialog (<see cref="TapeWinNET.ScanMediaWindow"/>): two options and
///  Scan / Cancel. The scan itself is run by <see cref="MainViewModel"/> in the shared operation overlay.
/// </summary>
public sealed class ScanMediaViewModel : ViewModelBase
{
    private readonly Action<ScanMediaViewModel> _onStart;

    private bool _recoverTocCopies = true;
    private string _mapExportFolder = string.Empty;

    public ScanMediaViewModel(Action<ScanMediaViewModel> onStart, Action onCancel)
    {
        _onStart = onStart;

        StartCommand = new RelayCommand(_ => _onStart(this));
        CancelCommand = new RelayCommand(_ => onCancel());
        BrowseMapFolderCommand = new RelayCommand(_ => BrowseMapFolder());
        ClearMapFolderCommand = new RelayCommand(_ => MapExportFolder = string.Empty, _ => HasMapExportFolder);
    }

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseMapFolderCommand { get; }
    public ICommand ClearMapFolderCommand { get; }

    /// <summary>Drives the shared <c>WarningPanelStyle</c> info banner ("nothing is written").</summary>
    public WarningLevel WarningLevel => WarningLevel.Info;

    /// <summary>Maps to <see cref="ScanMediaRequest.RecoverTocCopies"/>.</summary>
    public bool RecoverTocCopies
    {
        get => _recoverTocCopies;
        set => SetProperty(ref _recoverTocCopies, value);
    }

    /// <summary>Folder to save the scan map to; empty means "don't save".</summary>
    public string MapExportFolder
    {
        get => _mapExportFolder;
        set
        {
            if (SetProperty(ref _mapExportFolder, value))
            {
                OnPropertyChanged(nameof(HasMapExportFolder));
                OnPropertyChanged(nameof(MapExportFolderDisplay));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasMapExportFolder => !string.IsNullOrWhiteSpace(_mapExportFolder);

    public string MapExportFolderDisplay => HasMapExportFolder ? _mapExportFolder : "(map is not saved)";

    /// <summary>Builds the service request from the dialog's choices.</summary>
    public ScanMediaRequest BuildRequest() => new()
    {
        RecoverTocCopies = RecoverTocCopies,
        MapExportFolder = HasMapExportFolder ? MapExportFolder : null,
    };

    private void BrowseMapFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Folder for the scan map ({MediaScanMap.MapFileExtension} file)",
            InitialDirectory = HasMapExportFolder
                ? MapExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog() == true)
            MapExportFolder = dialog.FolderName;
    }
}
