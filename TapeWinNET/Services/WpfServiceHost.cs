using System.Windows;
using System.Windows.Threading;

using Windows.Win32.System.SystemServices; // Helpers.BytesToString

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
using TapeWinNET.Models;
using TapeWinNET.ViewModels;

// ReSharper disable once RedundantUsingDirective — WpfServiceHost itself lives in TapeWinNET.Services
// but its prompt methods reference dialogs from the TapeWinNET root namespace.
using TapeWinNET;  // MediaChangeDialog, FileErrorDialog, OpenVirtualDriveWindow

namespace TapeWinNET.Services;

/// <summary>
/// <see cref="ITapeServiceHost"/> adapter for WPF. Routes log entries to a caller-supplied
///  log sink and marshals all UI interactions to the UI thread via the supplied
///  <see cref="Dispatcher"/>.
/// <para>
/// Two construction modes:
/// <list type="bullet">
///  <item><b>Full mode</b> – pass a <see cref="MainViewModel"/>; <see cref="Report"/>
///        enqueues into its thread-safe log buffer.</item>
///  <item><b>Callback mode</b> – pass an <see cref="Action{LogEntry}"/> delegate; used
///        by Phase-B progress handlers that live inside TapeService partials and only
///        have access to the log callback. Migrated away in Phase C.</item>
/// </list>
/// </para>
/// <para>
/// Threading contract: <see cref="Report"/> is safe to call from any thread.
///  Prompt methods block the caller (always a background worker thread) via
///  <see cref="Dispatcher.Invoke"/>; no deadlock risk as the UI thread never
///  holds the service lock.
/// </para>
/// </summary>
/// <remarks>
/// Callback mode: log entries are forwarded to <paramref name="logCallback"/>.
/// Used by Phase-B progress handler subclasses inside TapeService partials.
/// </remarks>
public sealed class WpfServiceHost(Dispatcher dispatcher, Action<LogEntry> logCallback) : ITapeServiceHost
{
    private readonly Dispatcher _dispatcher = dispatcher;
    private readonly Action<LogEntry> _logSink = logCallback;

    // Stored in full-mode construction so OnServiceStateChanged can reach the ViewModel.
    private readonly MainViewModel? _viewModel;

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>
    /// Full mode: log entries are enqueued into <paramref name="viewModel"/>'s
    ///  thread-safe log buffer and state changes are forwarded to it.
    /// </summary>
    public WpfServiceHost(Dispatcher dispatcher, MainViewModel viewModel)
        : this(dispatcher, viewModel.AddLog)
    {
        _viewModel = viewModel;
    }

    // ── ITapeServiceHost — Logging ────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Thread-safe — no dispatcher marshalling needed here.</remarks>
    public void Report(ServiceReportLevel level, string message, bool isSubEntry = false)
    {
        // Map ServiceReportLevel → WarningLevel (enums share identical ordinal layout)
        var warnLevel = (WarningLevel)(int)level;
        _logSink(new LogEntry(warnLevel, message, isSubEntry, DateTime.Now));
    }

    // ── ITapeServiceHost — Prompts ────────────────────────────────────────────

    /// <inheritdoc/>
    public bool Confirm(string question, bool defaultAnswer = false)
    {
        bool result = defaultAnswer;
        _dispatcher.Invoke(() =>
        {
            var answer = MessageBox.Show(
                question,
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                defaultAnswer ? MessageBoxResult.Yes : MessageBoxResult.No);
            result = answer == MessageBoxResult.Yes;
        });
        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Shows a <see cref="MessageBox"/> for two-choice prompts. Multi-choice prompts
    ///  fall back to the default — WPF progress handlers that need the full
    ///  <see cref="FileErrorDialog"/> should override <c>OnFileFailed</c> directly
    ///  (as done by <c>GuiBackupProgressHandler</c> / <c>GuiRestoreProgressHandler</c>).
    ///  Phase C will replace this with a proper selection dialog.
    /// </remarks>
    public int Select(string question, IReadOnlyList<string> choices, int defaultIndex = 0)
    {
        if (choices.Count == 0) return defaultIndex;

        int result = defaultIndex;
        _dispatcher.Invoke(() =>
        {
            if (choices.Count == 2)
            {
                var answer = MessageBox.Show(
                    $"{question}\n\n[Yes] {choices[0]}   [No] {choices[1]}",
                    "Select",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                result = answer == MessageBoxResult.Yes ? 0 : 1;
            }
            else
            {
                // Multi-choice fallback — progress handlers override OnFileFailed for FileErrorDialog
                MessageBox.Show(
                    $"{question}\n\n(Using default: {choices[defaultIndex]})",
                    "Select", MessageBoxButton.OK, MessageBoxImage.Information);
                result = defaultIndex;
            }
        });
        return result;
    }

    /// <inheritdoc/>
    public string? Ask(string question, string? defaultValue = null)
    {
        // Phase B: no WPF InputBox — falls through to default.
        // Phase C will replace with a proper text-input dialog.
        string? result = defaultValue;
        _dispatcher.Invoke(() =>
        {
            var answer = MessageBox.Show(
                $"{question}\n\n(Default: {defaultValue ?? "(none)"})\n\nClick OK to accept, Cancel to abort.",
                "Input Required",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.OK);
            if (answer != MessageBoxResult.OK)
                result = null;
        });
        return result;
    }

    // ── ITapeServiceHost — State notification ─────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Forwards to <see cref="MainViewModel.OnServiceStateChanged"/> (full mode);
    ///  no-op in callback mode (progress handlers that only need logging).
    /// </remarks>
    public void OnServiceStateChanged(ServiceStateChange change)
        => _viewModel?.OnServiceStateChanged(change);

    // ── ITapeServiceHost — Structured operation prompts ───────────────────────

    /// <summary>
    /// Injected by <see cref="TapeService"/> after construction so that
    ///  <see cref="OnInsertMediaConfirm"/> can call <c>InsertVirtualMedia</c>
    ///  and query drive-capability properties without a circular type dependency.
    /// Set to <see langword="null"/> in callback-mode construction (progress
    ///  handlers) where media-change prompts are not needed.
    /// </summary>
    public TapeServiceBase? ServiceRef { get; set; }

    /// <inheritdoc/>
    public bool OnVolumeContinueConfirm(int volumeNeeded, RestoreMode mode)
    {
        bool result = false;
        _dispatcher.Invoke(() =>
        {
            var dialog = new MediaChangeDialog(
                $"{mode.ToDisplayName()}: Volume Change Required",
                $"{mode.ToDisplayName()} requires Volume #{volumeNeeded} to continue.",
                $"Click Continue to eject the current media and insert Volume #{volumeNeeded}.",
                $"Continue {mode.ToDisplayName()}")
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true)
                result = dialog.ContinueBackup;
        });
        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// For virtual drives, opens <see cref="OpenVirtualDriveWindow"/> so the user
    ///  can select the media file for the required volume; then calls
    ///  <see cref="TapeServiceBase.InsertVirtualMedia"/> via <see cref="ServiceRef"/>.
    /// For physical drives, shows the generic media-insertion dialog.
    /// </remarks>
    public bool OnInsertMediaConfirm(int volumeNeeded, RestoreMode mode)
    {
        bool mediaReady = false;
        var svc = ServiceRef;

        _dispatcher.Invoke(() =>
        {
            if (svc?.IsVirtualDrive == true)
            {
                // Virtual drive: show OpenVirtualDriveWindow to pick the media file
                var currentCaps = new VirtualTapeDriveCapabilities
                {
                    MinBlockSize               = svc.MinimumBlockSize,
                    MaxBlockSize               = svc.MaximumBlockSize,
                    DefaultBlockSize           = svc.DefaultBlockSize,
                    SupportsSetmarks           = svc.SupportsSetmarks,
                    SupportsSeqFilemarks       = svc.SupportsSeqFilemarks,
                    SupportsInitiatorPartition = svc.SupportsInitiatorPartition,
                    SupportsCompression        = false,
                };

                var lastVmd = svc.LastVMD;
                VirtualMediaDescriptor? prePopulate = null;
                if (lastVmd != null)
                {
                    prePopulate = lastVmd with
                    {
                        ContentPath = OpenVirtualDriveViewModel.BuildVolumeFilePath(
                            lastVmd.ContentPath, volumeNeeded)
                    };
                }

                var vm = new OpenVirtualDriveViewModel(
                    request =>
                    {
                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                        // InsertVirtualMedia runs on the worker thread that holds the
                        //  semaphore; this Invoke is on the UI thread, so no deadlock.
                        mediaReady = svc.InsertVirtualMedia(request.Media, System.IO.FileMode.Open);
                    },
                    () =>
                    {
                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                        mediaReady = false;
                    },
                    prePopulate: prePopulate,
                    mediaMode: System.IO.FileMode.Open,
                    currentCapabilities: currentCaps);

                var window = new OpenVirtualDriveWindow(vm) { Owner = Application.Current.MainWindow };
                window.ShowDialog();
            }
            else
            {
                // Physical drive: simple prompt to insert the required volume
                var dialog = new MediaChangeDialog(
                    "Insert Media",
                    "The current volume has been ejected.",
                    $"Please insert the media containing Volume #{volumeNeeded}.\n\n" +
                    $"Click Continue when the media is in the drive.",
                    $"Continue {mode.ToDisplayName()}",
                    showWarning: true)
                {
                    Owner = Application.Current.MainWindow
                };
                if (dialog.ShowDialog() == true)
                    mediaReady = dialog.ContinueBackup;
            }
        });
        return mediaReady;
    }

    /// <inheritdoc/>
    public bool OnMediaLoadRetryConfirm(string errorMessage, bool isRetry)
    {
        bool retry = false;
        _dispatcher.Invoke(() =>
        {
            string info = !isRetry
                ? $"The drive could not load the media.\n\nError: {errorMessage}\n\n" +
                  "Make sure the media is properly inserted. Retry?"
                : $"Loading media failed again.\n\nError: {errorMessage}\n\n" +
                  "Try re-seating the media. Retry?";

            var answer = MessageBox.Show(
                info,
                "Media Load Failed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            retry = answer == MessageBoxResult.Yes;
        });
        return retry;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Shows the WPF <see cref="FileErrorDialog"/> and maps
    ///  <see cref="FileErrorDialog.SkipAllErrors"/> to
    ///  <see cref="FileFailedAction.SkipAll"/>.
    /// </remarks>
    public FileFailedAction OnFileErrorSelect(string filePath, string errorMessage, string operationName)
    {
        FileFailedAction action = FileFailedAction.Skip;
        _dispatcher.Invoke(() =>
        {
            var dialog = new FileErrorDialog(filePath, errorMessage, operationName)
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true)
            {
                // Map SkipAllErrors bool → SkipAll enum value so the progress handler
                //  can set its internal flag without any WPF dependency.
                action = dialog.SkipAllErrors ? FileFailedAction.SkipAll : dialog.Result;
            }
        });
        return action;
    }

    // ── Restore progress ──────────────────────────────────────────────────────

    /// <summary>
    /// Updates the restore progress indicators on the bound <see cref="MainViewModel"/>.
    /// Safe to call from any thread — marshals to the UI dispatcher internally.
    /// No-op in callback-mode construction (no ViewModel reference).
    /// </summary>
    /// <param name="processed">Files processed so far.</param>
    /// <param name="total">Total files expected.</param>
    /// <param name="bytes">Bytes processed so far.</param>
    /// <param name="currentFile">Current file name, or <see langword="null"/> to leave unchanged.</param>
    public void UpdateRestoreProgress(int processed, int total, long bytes, string? currentFile)
    {
        var vm = _viewModel;
        if (vm is null) return;

        _dispatcher.Invoke(() =>
        {
            if (currentFile is not null)
                vm.CurrentRestoreFile = System.IO.Path.GetFileName(currentFile);
            vm.RestoreProgressPercent = total > 0 ? (int)(100.0 * processed / total) : 0;
            vm.RestoreProgressText    = $"{processed:N0} / {total:N0} files ({Helpers.BytesToString(bytes)})";
        });
    }

    // ── Backup progress ───────────────────────────────────────────────────────

    /// <summary>
    /// Updates the backup progress indicators on the bound <see cref="MainViewModel"/>.
    /// Safe to call from any thread — marshals to the UI dispatcher internally.
    /// No-op in callback-mode construction (no ViewModel reference).
    /// </summary>
    /// <param name="processed">Files processed so far.</param>
    /// <param name="total">Total files expected.</param>
    /// <param name="bytes">Bytes processed so far.</param>
    /// <param name="currentFile">Current file name, or <see langword="null"/> to leave unchanged.</param>
    public void UpdateBackupProgress(int processed, int total, long bytes, string? currentFile)
    {
        var vm = _viewModel;
        if (vm is null) return;

        _dispatcher.Invoke(() =>
        {
            if (currentFile is not null)
                vm.CurrentBackupFile = System.IO.Path.GetFileName(currentFile);
            vm.BackupProgressPercent = total > 0 ? (int)(100.0 * processed / total) : 0;
            vm.BackupProgressText    = $"{processed:N0} / {total:N0} files ({Helpers.BytesToString(bytes)})";
        });
    }

    // ── ITapeServiceHost — Backup prompts ─────────────────────────────────────

    /// <inheritdoc/>
    public bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup)
    {
        bool result = false;
        _dispatcher.Invoke(() =>
        {
            var dialog = new MediaChangeDialog(
                "Volume Full",
                $"Volume #{currentVolume} is full.\n" +
                $"Backed up {filesProcessed:N0} of {totalFiles:N0} files " +
                $"({Helpers.BytesToString(bytesBackedup)}) so far.",
                "Click Continue to eject this volume and continue the backup on a new media volume.",
                "Continue to Next Volume")
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true)
                result = dialog.ContinueBackup;
        });
        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// For virtual drives, opens <see cref="OpenVirtualDriveWindow"/> in file-creation
    ///  mode; calls <see cref="TapeServiceBase.InsertVirtualMedia"/> via
    ///  <see cref="ServiceRef"/>. For physical drives, shows the generic insert dialog.
    /// </remarks>
    public bool OnInsertNewMediaConfirm(int nextVolume)
    {
        bool mediaReady = false;
        var svc = ServiceRef;

        _dispatcher.Invoke(() =>
        {
            if (svc?.IsVirtualDrive == true)
            {
                var currentCaps = new VirtualTapeDriveCapabilities
                {
                    MinBlockSize               = svc.MinimumBlockSize,
                    MaxBlockSize               = svc.MaximumBlockSize,
                    DefaultBlockSize           = svc.DefaultBlockSize,
                    SupportsSetmarks           = svc.SupportsSetmarks,
                    SupportsSeqFilemarks       = svc.SupportsSeqFilemarks,
                    SupportsInitiatorPartition = svc.SupportsInitiatorPartition,
                    SupportsCompression        = false,
                };

                var lastVmd = svc.LastVMD;
                VirtualMediaDescriptor? prePopulate = null;
                if (lastVmd != null)
                {
                    prePopulate = lastVmd with
                    {
                        ContentPath = OpenVirtualDriveViewModel.BuildVolumeFilePath(
                            lastVmd.ContentPath, nextVolume)
                    };
                }

                // IoSpeed: read from ViewModel when available, otherwise Unlimited
                IoSpeedOption? ioSpeed = _viewModel?.SelectedIoSpeed;

                var vm = new OpenVirtualDriveViewModel(
                    request =>
                    {
                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                        mediaReady = svc.InsertVirtualMedia(request.Media, System.IO.FileMode.Create);
                    },
                    () =>
                    {
                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                        mediaReady = false;
                    },
                    prePopulate: prePopulate,
                    mediaMode: System.IO.FileMode.Create,
                    currentCapabilities: currentCaps,
                    currentIoSpeed: ioSpeed);

                var window = new OpenVirtualDriveWindow(vm) { Owner = Application.Current.MainWindow };
                window.ShowDialog();
            }
            else
            {
                var dialog = new MediaChangeDialog(
                    "Insert New Media",
                    "The current volume has been ejected.",
                    $"Please insert a formatted media for Volume #{nextVolume}.\n\n" +
                    "Click Continue when the new media is in the drive.",
                    "Continue Backup",
                    showWarning: true)
                {
                    Owner = Application.Current.MainWindow
                };
                if (dialog.ShowDialog() == true)
                    mediaReady = dialog.ContinueBackup;
            }
        });
        return mediaReady;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Shows a <see cref="MessageBox"/> warning and, if confirmed, a
    ///  <see cref="Microsoft.Win32.SaveFileDialog"/> for the export path.
    /// </remarks>
    public string? OnEmergencyTocExportConfirm(string suggestedPath, bool isRetry)
    {
        string? chosenPath = null;
        _dispatcher.Invoke(() =>
        {
            string info = !isRetry
                ? "The TOC could not be saved to media. Without a TOC, the files on media cannot be accessed.\n\n" +
                  "This is your last chance to export the TOC to a file for recovery.\n\n" +
                  "Do you want to choose a save location now?"
                : "Exporting TOC failed. Try a different location?";

            var answer = MessageBox.Show(info, "Emergency TOC Export",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title            = "Emergency TOC Export — Choose Save Location",
                Filter           = $"Tape TOC files (*{TapeLibNET.TapeFileAgent.TOCFileExtension})" +
                                   $"|*{TapeLibNET.TapeFileAgent.TOCFileExtension}|All files (*.*)|*.*",
                FileName         = System.IO.Path.GetFileName(suggestedPath),
                InitialDirectory = System.IO.Path.GetDirectoryName(suggestedPath) ?? "",
                OverwritePrompt  = true,
            };
            if (dlg.ShowDialog() == true)
                chosenPath = dlg.FileName;
        });
        return chosenPath;
    }
}
