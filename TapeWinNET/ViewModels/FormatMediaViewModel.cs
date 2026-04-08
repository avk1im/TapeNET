using System.Windows.Input;

using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

public class FormatMediaViewModel : ViewModelBase
{
    private bool _createInitiatorPartition;
    private string _mediaName;

    public FormatMediaViewModel(TapeService tapeService, Action<FormatMediaViewModel> onFormat, Action onCancel)
    {
        SupportsInitiatorPartition = tapeService.SupportsInitiatorPartition;
        _createInitiatorPartition = SupportsInitiatorPartition;
        _mediaName = $"Media created {DateTime.Now:g}";

        FormatCommand = new RelayCommand(_ => onFormat(this));
        CancelCommand = new RelayCommand(_ => onCancel());
    }

    public bool SupportsInitiatorPartition { get; }

    public bool CreateInitiatorPartition
    {
        get => _createInitiatorPartition;
        set => SetProperty(ref _createInitiatorPartition, value);
    }

    public string MediaName
    {
        get => _mediaName;
        set => SetProperty(ref _mediaName, value);
    }

    public string InitiatorPartitionHint => SupportsInitiatorPartition
        ? "Storing the TOC in a dedicated partition speeds up media access but may slightly increase tape wear."
        : "This drive does not support multiple partitions.";

    public WarningLevel WarningLevel => WarningLevel.Error;

    public string WarningMessage => "WARNING: Formatting will ERASE ALL DATA on the media!";

    public ICommand FormatCommand { get; }
    public ICommand CancelCommand { get; }
}
