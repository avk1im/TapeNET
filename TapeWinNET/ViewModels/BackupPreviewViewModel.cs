using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TapeLibNET;
using Windows.Win32.System.SystemServices;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Represents a file item in the backup preview list.
/// </summary>
public class BackupPreviewFileItem : INotifyPropertyChanged
{
    private static BitmapSource? _fileIcon;
    private static bool _iconLoaded;

    private bool _isSelected = true;

    static BackupPreviewFileItem()
    {
        try
        {
            _fileIcon = TapeIcons.GetTapeFileIcon(large: false);
            _fileIcon?.Freeze();
        }
        catch { }
        _iconLoaded = true;
    }

    public BackupPreviewFileItem(string fullPath)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Exists)
            {
                Size = info.Length;
                SizeDisplay = Helpers.BytesToString(info.Length);
                LastModified = info.LastWriteTime;
                LastModifiedDisplay = info.LastWriteTime.ToString("G");
            }
            else
            {
                SizeDisplay = "?";
                LastModifiedDisplay = "?";
            }
        }
        catch
        {
            SizeDisplay = "?";
            LastModifiedDisplay = "?";
        }
    }

    public BitmapSource? Icon => _fileIcon;
    public string FullPath { get; }
    public string FileName { get; }
    public string Directory { get; }
    public long Size { get; }
    public string SizeDisplay { get; }
    public DateTime LastModified { get; }
    public string LastModifiedDisplay { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// ViewModel for the Backup Preview window.
/// </summary>
public class BackupPreviewViewModel : ViewModelBase
{
    private readonly Action<List<string>> _onStartBackup;
    private readonly Action _onBack;
    //private readonly Action _onCancel;
    private bool _selectAll = true;

    public BackupPreviewViewModel(
        List<string> fileList,
        Action<List<string>> onStartBackup,
        Action onBack
        /*, Action onCancel*/)
    {
        _onStartBackup = onStartBackup;
        _onBack = onBack;
        //_onCancel = onCancel;

        // Populate file list
        foreach (var file in fileList)
        {
            var item = new BackupPreviewFileItem(file);
            item.PropertyChanged += FileItem_PropertyChanged;
            Files.Add(item);
        }

        UpdateTotals();

        // Initialize commands
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => HasSelectedFiles);
        SelectAllCommand = new RelayCommand(ToggleSelectAll);
        StartBackupCommand = new RelayCommand(_ => StartBackup(), param => SelectedFileCount > 0);
        BackCommand = new RelayCommand(_ => _onBack());
        //CancelCommand = new RelayCommand(_ => _onCancel());
    }

    #region Properties

    public ObservableCollection<BackupPreviewFileItem> Files { get; } = [];

    public bool SelectAll
    {
        get => _selectAll;
        set
        {
            if (SetProperty(ref _selectAll, value))
            {
                foreach (var file in Files)
                {
                    file.IsSelected = value;
                }
                UpdateTotals();
            }
        }
    }

    public int TotalFileCount => Files.Count;
    public int SelectedFileCount { get; private set; }
    public long SelectedTotalSize { get; private set; }

    public string TotalFileCountDisplay => $"{TotalFileCount:N0} files";
    public string SelectedFileCountDisplay => $"{SelectedFileCount:N0} files selected";
    public string SelectedTotalSizeDisplay => Helpers.BytesToString(SelectedTotalSize);

    public bool HasSelectedFiles => Files.Any(f => f.IsSelected);

    #endregion

    #region Commands

    public ICommand RemoveSelectedCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand StartBackupCommand { get; }
    public ICommand BackCommand { get; }
    //public ICommand CancelCommand { get; }

    #endregion

    #region Private Methods

    private void FileItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupPreviewFileItem.IsSelected))
        {
            UpdateTotals();
        }
    }

    private void UpdateTotals()
    {
        SelectedFileCount = Files.Count(f => f.IsSelected);
        SelectedTotalSize = Files.Where(f => f.IsSelected).Sum(f => f.Size);

        OnPropertyChanged(nameof(TotalFileCount));
        OnPropertyChanged(nameof(TotalFileCountDisplay));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedFileCountDisplay));
        OnPropertyChanged(nameof(SelectedTotalSize));
        OnPropertyChanged(nameof(SelectedTotalSizeDisplay));
        OnPropertyChanged(nameof(HasSelectedFiles));

        CommandManager.InvalidateRequerySuggested();
    }

    private void RemoveSelected()
    {
        var toRemove = Files.Where(f => f.IsSelected).ToList();
        foreach (var file in toRemove)
        {
            file.PropertyChanged -= FileItem_PropertyChanged;
            Files.Remove(file);
        }
        UpdateTotals();
    }

    private void ToggleSelectAll()
    {
        SelectAll = !SelectAll;
    }

    private void StartBackup()
    {
        var selectedFiles = Files.Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
        _onStartBackup(selectedFiles);
    }

    #endregion
}