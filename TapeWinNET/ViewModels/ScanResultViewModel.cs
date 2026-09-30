using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

using TapeLibNET;
using TapeLibNET.Scan;
using TapeLibNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// One read-only row of the Scan Media result list — a display rendering of a <see cref="TapeMediaFragment"/>.
/// </summary>
public sealed class ScanFragmentRow(TapeMediaFragment fragment)
{
    public TapeMediaFragment Fragment { get; } = fragment;

    public int Ordinal => Fragment.Ordinal;
    public string Block => Fragment.StartBlock.ToString("N0");
    public bool IsToc => Fragment.Kind == FragmentKind.TOC;
    public bool HasHarvestedToc => Fragment.HarvestedToc is not null;

    /// <summary>Warning (amber) rows: unclosed set, unrecovered TOC copy, unidentified data.</summary>
    public bool IsWarning => Fragment.Kind switch
    {
        FragmentKind.SetHeader => !Fragment.ClosedBySeparator,
        FragmentKind.TOC => !HasHarvestedToc && !Fragment.Diagnosis.Success,
        FragmentKind.Unknown => Fragment.Diagnosis.Success,
        _ => false,
    };

    /// <summary>Error (red) rows: damaged blocks.</summary>
    public bool IsError => Fragment.Kind == FragmentKind.Unknown && !Fragment.Diagnosis.Success;

    public string KindText => Fragment.Kind switch
    {
        FragmentKind.MediaHeader => "Media header",
        FragmentKind.SetHeader => "Backup set",
        FragmentKind.TOC => "Table of contents",
        FragmentKind.TocMark => "TOC mark",
        FragmentKind.MarkRun => "Mark run",
        FragmentKind.CalibrationHeader => "Calibration run",
        _ => Fragment.Diagnosis.Success ? "Unidentified" : "Damaged",
    };

    public string Identity => Fragment.Kind switch
    {
        FragmentKind.MediaHeader => $"{(Fragment.Id is { } g ? g.ToString("N")[..8] : "?")} · vol {Fragment.Volume}",
        FragmentKind.SetHeader => $"#{Fragment.VolumeSetIndex + 1}",
        FragmentKind.TOC => Fragment.TocVersion is { } v ? $"v{v:X4}" : "—",
        FragmentKind.CalibrationHeader => Fragment.Description ?? "—",
        _ => "—",
    };

    public string Detail => Fragment.Kind switch
    {
        FragmentKind.MediaHeader => Fragment.Description ?? string.Empty,
        FragmentKind.SetHeader =>
            $"{Fragment.Description}{(Fragment.CreatedUtc is { } t ? $" · {t.ToLocalTime():g}" : "")}"
            + (Fragment.ClosedBySeparator ? string.Empty : " — never completed"),
        FragmentKind.TOC => Fragment.HarvestedToc is { } toc ? $"recovered — {toc.Count} set(s)"
            : !Fragment.Diagnosis.Success ? $"not recovered: {Fragment.Diagnosis.ErrorMessage}"
            : "found",
        FragmentKind.TocMark => "gap + filemarks",
        FragmentKind.MarkRun => $"{Fragment.MarkCount} consecutive marks",
        FragmentKind.Unknown => !Fragment.Diagnosis.Success
            ? Fragment.Diagnosis.ErrorMessage ?? string.Empty
            : Fragment.Fingerprint ?? string.Empty,
        _ => string.Empty,
    };
}

/// <summary>
/// ViewModel for the Scan Media result window:
///  advice buttons and map saving. Never writes to tape.
/// </summary>
public sealed class ScanResultViewModel : ViewModelBase
{
    private readonly TapeWinNET.Services.TapeService _service;
    private readonly ScanMediaResult _result;
    private readonly MediaScanMap _map;
    private readonly Action _onTocAdopted;

    private string _statusMessage = string.Empty;
    private string? _mapPath;
    private ScanFragmentRow? _selectedRow;
    private bool _isWorking;

    public ScanResultViewModel(TapeWinNET.Services.TapeService service, ScanMediaResult result,
        Action onTocAdopted)
    {
        _service = service;
        _result = result;
        _map = result.Map ?? throw new ArgumentException("The scan result carries no map.", nameof(result));
        _onTocAdopted = onTocAdopted;
        _mapPath = result.MapExportPath;

        Rows = new ObservableCollection<ScanFragmentRow>(_map.Fragments.Select(f => new ScanFragmentRow(f)));
        Details = [.. TapeServiceBase.VerbalizeScan(_map).Details];
        // Advice is shown as static recommendation text, not as buttons: the TOC actions already
        //  sit on the selected TOC row, and the other advice has no in-dialog action to trigger.
        Advice = [.. result.Advice.Select(TapeServiceBase.ScanAdviceText)];

        UseTocCommand = new AsyncRelayCommand(_ => UseTocAsync(), _ => CanActOnToc);
        SaveTocCommand = new AsyncRelayCommand(_ => SaveTocAsync(), _ => CanActOnToc);
        SaveMapCommand = new RelayCommand(_ => SaveMap(), _ => !HasMapPath);
    }

    public Action? CloseRequested { get; set; }

    public ObservableCollection<ScanFragmentRow> Rows { get; }
    public IReadOnlyList<string> Details { get; }

    public ICommand UseTocCommand { get; }
    public ICommand SaveTocCommand { get; }
    public ICommand SaveMapCommand { get; }

    /// <summary>Drives the shared <c>WarningPanelStyle</c> banner from the scan's outcome.</summary>
    /// <remarks>
    /// A clean scan (Completed) is shown with the neutral Info palette: the strong "success green" is
    ///  reserved for operations that changed something, and a scan only reads.
    /// </remarks>
    public ServiceReportLevel WarningLevel => _result.Outcome == ServiceReportLevel.Completed
        ? ServiceReportLevel.Info : _result.Outcome;

    /// <summary>Banner text; flags an aborted or otherwise incomplete map explicitly (SM-5).</summary>
    public string Banner => _result.WasAborted ? $"{_result.Summary} — scan aborted; the map is incomplete."
        : _map.Truncated && _result.Success ? $"{_result.Summary} — the map is incomplete."
        : !_result.Success ? $"{_result.Summary} — scan failed; the map is incomplete."
        : _result.Summary;

    /// <summary>A calibration cartridge shows only banner + advice, not the list (SM-7).</summary>
    public bool ShowList => _result.MediaKind != ScannedMediaKind.CalibrationCartridge;

    /// <summary>Recommendations from the scan, most useful first, as display text.</summary>
    public IReadOnlyList<string> Advice { get; }

    public bool HasMapPath => !string.IsNullOrEmpty(_mapPath);
    public string MapPathText => HasMapPath ? $"Map saved: {_mapPath}" : string.Empty;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ScanFragmentRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(CanActOnToc));
                OnPropertyChanged(nameof(UseTocText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Label of the first TOC action: "Use this TOC" when already recovered, else "Try to recover".</summary>
    public string UseTocText => SelectedRow?.HasHarvestedToc == true ? "Use this TOC" : "Try to recover";

    public bool CanActOnToc => !_isWorking && SelectedRow is { IsToc: true };

    // ── TOC actions

    private Task UseTocAsync() => RecoverAsync(SelectedRow, adopt: true, savePath: null);

    private async Task SaveTocAsync()
    {
        if (SelectedRow is not { IsToc: true } row)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save recovered TOC",
            Filter = $"Media TOC files (*{TapeAgentBase.TOCFileExtension})|*{TapeAgentBase.TOCFileExtension}",
            DefaultExt = TapeAgentBase.TOCFileExtension,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
            return;

        await RecoverAsync(row, adopt: false, savePath: dialog.FileName);
    }

    /// <summary>
    /// Runs <see cref="TapeServiceBase.RecoverTocAsync"/> for a TOC row. The service reads from tape (with its
    ///  cartridge-swap guards) only when the row has no harvested TOC; on adoption the window closes and the
    ///  main tree is rebuilt via the callback.
    /// </summary>
    private async Task RecoverAsync(ScanFragmentRow? row, bool adopt, string? savePath)
    {
        if (row is not { IsToc: true })
            return;

        _isWorking = true;
        OnPropertyChanged(nameof(CanActOnToc));
        StatusMessage = "Recovering table of contents...";
        try
        {
            var result = await _service.RecoverTocAsync(new RecoverTocRequest(_map, row.Ordinal)
            {
                Adopt = adopt,
                SaveToFilePath = savePath,
            });

            if (!result.Success)
            {
                StatusMessage = $"Recovery failed: {result.Message}";
                SimpleBox.Show($"Couldn't recover the table of contents.\n\n{result.Message}",
                    "Recover TOC", MessageBoxButton.OK, SimpleBox.ImageFailed);
                return;
            }

            if (result.Adopted)
            {
                _onTocAdopted();
                CloseRequested?.Invoke();
                return;
            }

            StatusMessage = result.SavedPath is { } p ? $"TOC saved: {p}" : "Table of contents recovered.";
            if (adopt)
                StatusMessage = "Recovered, but not adopted. " + result.Message;
        }
        finally
        {
            _isWorking = false;
            OnPropertyChanged(nameof(CanActOnToc));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ── Map saving

    /// <summary>Saves <c>map.ToJson()</c> to a user-chosen <c>.tapescan</c> file (offered only if not yet exported).</summary>
    private void SaveMap()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save scan map",
            Filter = $"Scan maps (*{MediaScanMap.MapFileExtension})|*{MediaScanMap.MapFileExtension}",
            DefaultExt = MediaScanMap.MapFileExtension,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, _map.ToJson());
            _mapPath = dialog.FileName;
            OnPropertyChanged(nameof(HasMapPath));
            OnPropertyChanged(nameof(MapPathText));
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            SimpleBox.Show($"Couldn't save the map.\n\n{ex.Message}", "Save Map",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
