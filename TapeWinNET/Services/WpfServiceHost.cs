using System.DirectoryServices;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
// ReSharper disable once RedundantUsingDirective — WpfServiceHost itself lives in TapeWinNET.Services
// but its prompt methods reference dialogs from the TapeWinNET root namespace.
using TapeWinNET.Models;
using TapeWinNET.ViewModels;
using Windows.Win32.System.SystemServices; // Helpers.BytesToString

namespace TapeWinNET.Services;

/// <summary>
/// <see cref="ITapeServiceHost"/> adapter for WPF. Routes log entries to the
///  <see cref="MainViewModel"/> log buffer and marshals all UI interactions to the
///  UI thread via the supplied <see cref="Dispatcher"/>.
/// <para>
/// Threading contract: <see cref="Report"/> is safe to call from any thread.
///  Prompt methods block the caller (always a background worker thread) via
///  <see cref="Dispatcher.Invoke"/>; no deadlock risk as the UI thread never
///  holds the service lock.
/// </para>
/// </summary>
/// <param name="dispatcher">UI dispatcher for marshalling prompt and progress calls.</param>
/// <param name="viewModel">
///  ViewModel whose <see cref="MainViewModel.AddLog"/> sink receives all log entries
///  and whose <see cref="MainViewModel.OnServiceStateChanged"/> receives state hints.
/// </param>
public sealed class WpfServiceHost(Dispatcher dispatcher, MainViewModel viewModel) : ITapeServiceHost
{
    private readonly Dispatcher _dispatcher = dispatcher;
    private readonly MainViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    private readonly Stopwatch _stopwatch = new();

    // ── ITapeServiceHost — Logging ────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Thread-safe — no dispatcher marshalling needed here.</remarks>
    public void Report(ServiceReportLevel level, string message, bool isSubEntry = false)
        => _viewModel.AddLog(new LogEntry(level, message, isSubEntry, DateTime.Now));

    // ── ITapeServiceHost — State notification ─────────────────────────────────

    /// <inheritdoc/>
    public void OnServiceStateChanged(ServiceStateChange change)
    {
        if ((change & ServiceStateChange.OperationStarted) != 0)
        {
            _stopwatch.Restart();
            ResetIOProgressTracking();
            _dispatcher.Invoke(_viewModel.RaiseResetSparkline);
        }

        if ((change & ServiceStateChange.OperationEnded) != 0)
        {
            _stopwatch.Stop();
        }

        _viewModel.OnServiceStateChanged(change);
    }

    // ── Progress update helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Updates the restore progress indicators on the bound <see cref="MainViewModel"/>.
    /// Safe to call from any thread — marshals to the UI dispatcher internally.
    /// </summary>
    public void UpdateRestoreProgress(int processed, int total, long bytes, long totalBytes, string? currentFile)
        => UpdateProgressCore(
            processed, total, bytes, totalBytes, currentFile,
            setCurrentFile: name => _viewModel.CurrentRestoreFile = name,
            setPercent: percent => _viewModel.RestoreProgressPercent = percent,
            setText: text => _viewModel.RestoreProgressText = text,
            filesSuffix: string.Empty);

    /// <summary>
    /// Updates the backup progress indicators on the bound <see cref="MainViewModel"/>.
    /// Safe to call from any thread — marshals to the UI dispatcher internally.
    /// </summary>
    public void UpdateBackupProgress(int processed, int total, long bytes, long totalBytes, string? currentFile)
        => UpdateProgressCore(
            processed, total, bytes, totalBytes, currentFile,
            setCurrentFile: name => _viewModel.CurrentBackupFile = name,
            setPercent: percent => _viewModel.BackupProgressPercent = percent,
            setText: text => _viewModel.BackupProgressText = text,
            filesSuffix: " files");

    /// <summary>
    /// Shared implementation for <see cref="UpdateBackupProgress"/> and
    ///  <see cref="UpdateRestoreProgress"/> — both operations report the same shape
    ///  of progress data, differing only in which view-model properties they update.
    /// </summary>
    private void UpdateProgressCore(
        int processed, int total, long bytes, long totalBytes, string? currentFile,
        Action<string> setCurrentFile, Action<double> setPercent, Action<string> setText, string filesSuffix)
    {
        _dispatcher.Invoke(() =>
        {
            if (currentFile is not null)
                setCurrentFile(System.IO.Path.GetFileName(currentFile));
            double progress = UpdateIOProgress(processed, total, bytes, totalBytes);
            setPercent(Math.Clamp(progress * 100.0, 0.0, 100.0));
            setText($"{processed:N0} file(s) of {total:N0}{filesSuffix} "
                + $"({Helpers.BytesToStringLong(bytes)} of {Helpers.BytesToStringLong(totalBytes)})");
        });
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        string format;

        if (ts.Days >= 1)
            format = @"d\.hh\:mm\:ss";
        else if (ts.Hours >= 1)
            format = @"hh\:mm\:ss";
        else
            format = @"mm\:ss";

        return ts.ToString(format, CultureInfo.CurrentCulture);
    }

    // Tracking fields for rolling EMA window and monotonic progress
    private long _lastBytesProcessed = 0L;
    private int _lastFilesProcessed = 0;
    private double _smoothedAvgFileSize = 0.0;
    private double _lastReportedProgress = 0.0;

    private void ResetIOProgressTracking()
    {
        _lastBytesProcessed = 0;
        _lastFilesProcessed = 0;
        _smoothedAvgFileSize = 0.0;
        _lastReportedProgress = 0.0;
    }

    private static double CalculateDynamicByteWeight(double avgFileBytes, long totalBytes)
    {
        // 1. If totalBytes is unknown, file count is our only reliable anchor
        if (totalBytes <= 0)
            return 0.0;

        // 2. Default to balanced weight if no average size is available yet
        if (avgFileBytes <= 0)
            return 0.50;

        // Thresholds & corresponding weights
        const double minAvgBytes = 10_000.0;      // 10 KB
        const double maxAvgBytes = 50_000_000.0;  // 50 MB
        const double minWeight = 0.50;
        const double maxWeight = 0.95;

        // 3. Clamp boundary extremes early
        if (avgFileBytes <= minAvgBytes) return minWeight;
        if (avgFileBytes >= maxAvgBytes) return maxWeight;

        // 4. Log-linear interpolation between 10 KB and 50 MB
        double logMin = Math.Log(minAvgBytes);
        double logMax = Math.Log(maxAvgBytes);
        double logCurrent = Math.Log(avgFileBytes);

        // 5. Compute ratio [0.0 ... 1.0] in log-space
        double t = (logCurrent - logMin) / (logMax - logMin);

        // 6. Linearly interpolate weight using t
        return minWeight + (t * (maxWeight - minWeight));
    }

    private double UpdateIOProgress(int processed, int total, long bytes, long totalBytes)
    {
        double elapsed = _stopwatch.IsRunning ? _stopwatch.ElapsedSeconds : 0.0;

        // Compute overall throughput rate
        double rate = elapsed > 0.1 ? bytes / elapsed : 0.0;
        _viewModel.IOProgressRate = rate;

        TimeSpan elapsedTime = TimeSpan.FromSeconds(elapsed);
        string progressText = $"Elapsed: {FormatTimeSpan(elapsedTime)}";

        // 1. Compute rolling average file size for the latest batch tick
        long deltaBytes = bytes - _lastBytesProcessed;
        int deltaFiles = processed - _lastFilesProcessed;

        _lastBytesProcessed = bytes;
        _lastFilesProcessed = processed;

        if (deltaFiles > 0 && deltaBytes > 0)
        {
            double batchAvgSize = (double)deltaBytes / deltaFiles;

            // Apply Exponential Moving Average (EMA)
            // alpha = 0.2 gives ~80% weight to history and 20% to the current tick sample
            const double alpha = 0.2;
            _smoothedAvgFileSize = _smoothedAvgFileSize <= 0.0
                ? batchAvgSize
                : (alpha * batchAvgSize) + ((1.0 - alpha) * _smoothedAvgFileSize);
        }

        // 2. Compute individual progress ratios (0.0 to 1.0)
        double fileProgress = total > 0 ? (double)processed / total : 0.0;
        double byteProgress = totalBytes > 0 ? (double)bytes / totalBytes : fileProgress;

        // 3. Compute dynamic byte weight using the rolling smoothed average file size
        double byteWeight = CalculateDynamicByteWeight(_smoothedAvgFileSize, totalBytes);

        // 4. Blended combination
        double rawCombined = (byteProgress * byteWeight) + (fileProgress * (1.0 - byteWeight));

        // Monotonic enforcement: Ensure progress ONLY moves forward
        double combinedProgress = Math.Clamp(Math.Max(_lastReportedProgress, rawCombined), 0.0, 1.0);
        _lastReportedProgress = combinedProgress;

        // 5. Compute overall ETA based on monotonic combined progress
        if (combinedProgress > 0.001 && combinedProgress < 1.0)
        {
            double estimatedTotalSeconds = elapsed / combinedProgress;
            double remainingSeconds = Math.Max(0.0, estimatedTotalSeconds - elapsed);

            TimeSpan remainingTime = TimeSpan.FromSeconds(remainingSeconds);
            progressText += $", est. remaining: {FormatTimeSpan(remainingTime)}";
        }

        if (rate > 0)
        {
            progressText += $", {Helpers.BytesToString((long)rate)}/s";
        }

        _viewModel.IOProgressText = progressText;

        return combinedProgress;
    }

    // ── ITapeServiceHost — Prompts ───────────────────────────────────────────────

    /// <inheritdoc/>
    public bool Confirm(string question, bool defaultAnswer = false)
    {
        bool result = defaultAnswer;
        _dispatcher.Invoke(() =>
        {
            var answer = SimpleBox.Show(
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
    /// Shows a <see cref="MessageBox"/> for two-choice prompts. For three or more
    ///  choices opens a minimal <see cref="SelectDialog"/> with a <c>ListBox</c>.
    /// </remarks>
    public int Select(string topic, string question, IReadOnlyList<string> choices, int defaultIndex = 0)
    {
        if (choices.Count == 0) return defaultIndex;

        int result = defaultIndex;
        _dispatcher.Invoke(() =>
        {
            if (choices.Count == 2)
            {
                var answer = SimpleBox.Show(
                    $"{question}\n\n[Yes] {choices[0]}   [No] {choices[1]}",
                    topic,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    defaultIndex == 0 ? MessageBoxResult.Yes : MessageBoxResult.No);
                result = answer == MessageBoxResult.Yes ? 0 : 1;
            }
            else
            {
                var dialog = new SelectDialog(topic, question, choices, defaultIndex)
                {
                    Owner = Application.Current.MainWindow
                };
                result = dialog.ShowDialog() == true ? dialog.SelectedIndex : defaultIndex;
            }
        });
        return result;
    }

    /// <inheritdoc/>
    public string? Ask(string topic, string question, string? defaultValue = null)
    {
        string? result = defaultValue;
        _dispatcher.Invoke(() =>
        {
            var dialog = new AskDialog(topic, question, defaultValue)
            {
                Owner = Application.Current.MainWindow
            };
            result = dialog.ShowDialog() == true ? dialog.Answer : null;
        });
        return result;
    }

    // ── ITapeServiceHost — Structured operation prompts ───────────────────────

    /// <summary>
    /// Injected by <see cref="TapeService"/> after construction so that prompt methods
    ///  can call <c>InsertVirtualMedia</c> and query drive-capability properties
    ///  without a circular type dependency.
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
    /// For remote virtual drives, opens <see cref="OpenRemoteVirtualDriveWindow"/> forced to
    ///  Open-existing mode; calls <see cref="TapeServiceBase.InsertRemoteVirtualMedia"/> via
    ///  <see cref="ServiceRef"/>.
    /// For local virtual drives, opens <see cref="OpenVirtualDriveWindow"/> so the user
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
            if (svc?.IsRemoteDrive == true)
            {
                // Remote virtual drive: show OpenRemoteVirtualDriveWindow forced to Open mode
                var settings = _viewModel.RemoteHostSettings;
                if (settings is null) return;

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

                // Load available volumes synchronously (we are already off the UI thread inside Invoke)
                var volumes = svc.ListRemoteSessionVolumesAsync().GetAwaiter().GetResult();

                var vm = new OpenRemoteVirtualDriveViewModel(settings);
                foreach (var vol in volumes)
                    vm.AvailableVolumes.Add(vol);

                // Pre-select the volume matching the conventional name for volumeNeeded
                var lastVmd = svc.LastVMD;
                if (lastVmd != null)
                {
                    string expectedName = System.IO.Path.GetFileNameWithoutExtension(
                        OpenVirtualDriveViewModel.BuildVolumeFilePath(lastVmd.ContentPath, volumeNeeded));
                    vm.TryPreSelectVolume(expectedName);
                }

                vm.ForceOpenMode();

                var window = new OpenRemoteVirtualDriveWindow(vm) { Owner = Application.Current.MainWindow };
                if (window.ShowDialog() == true && vm.Result is { } request)
                    mediaReady = svc.InsertRemoteVirtualMedia(request.Media, currentCaps, System.IO.FileMode.Open);
            }
            else if (svc?.IsVirtualDrive == true)
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
                    onOpen: request =>
                    {
                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                        // InsertVirtualMedia runs on the worker thread that holds the
                        //  semaphore; this Invoke is on the UI thread, so no deadlock.
                        mediaReady = svc.InsertVirtualMedia(request.Media, System.IO.FileMode.Open);
                    },
                    onCancel: () =>
                    {
                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                        mediaReady = false;
                    },
                    prePopulate: prePopulate,
                    mediaMode: System.IO.FileMode.Open,
                    currentCapabilities: currentCaps,
                    currentIoRate: _viewModel.SelectedIoSpeed);

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

            var answer = SimpleBox.Show(
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



    // ── ITapeServiceHost — Backup prompts ─────────────────────────────────────

    /// <inheritdoc/>
    public bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup, long totalBytes)
    {
        bool result = false;
        _dispatcher.Invoke(() =>
        {
            var dialog = new MediaChangeDialog(
                "Volume Full",
                $"Volume #{currentVolume} is full.\n" +
                $"Backed up {filesProcessed:N0} of {totalFiles:N0} files\n" +
                $"({Helpers.BytesToStringLong(bytesBackedup)} of {Helpers.BytesToStringLong(totalBytes)}) so far.",
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
    /// For remote virtual drives, opens <see cref="OpenRemoteVirtualDriveWindow"/> forced to
    ///  Create-new mode; calls <see cref="TapeServiceBase.InsertRemoteVirtualMedia"/> via
    ///  <see cref="ServiceRef"/>.
    /// For local virtual drives, opens <see cref="OpenVirtualDriveWindow"/> in file-creation
    ///  mode; calls <see cref="TapeServiceBase.InsertVirtualMedia"/> via
    ///  <see cref="ServiceRef"/>. For physical drives, shows the generic insert dialog.
    /// </remarks>
    public bool OnInsertNewMediaConfirm(int nextVolume)
    {
        bool mediaReady = false;
        var svc = ServiceRef;

        _dispatcher.Invoke(() =>
        {
            if (svc?.IsRemoteDrive == true)
            {
                // Remote virtual drive: show OpenRemoteVirtualDriveWindow forced to Create mode
                var settings = _viewModel.RemoteHostSettings;
                if (settings is null) return;

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

                var vm = new OpenRemoteVirtualDriveViewModel(settings);

                // Pre-populate the content filename from the last VMD (vol01 → vol02 etc.)
                var lastVmd = svc.LastVMD;
                if (lastVmd != null)
                {
                    vm.IsNamed = true;
                    vm.ContentFilePath = System.IO.Path.GetFileNameWithoutExtension(
                        OpenVirtualDriveViewModel.BuildVolumeFilePath(lastVmd.ContentPath, nextVolume));
                }

                vm.ForceCreateMode();
                // Lock capabilities to the current drive's settings: the replacement volume must
                //  be compatible with the volumes already written (same block sizes, features, etc.)
                vm.LockCapabilitiesFrom(currentCaps);

                // Pre-populate capacity from the previous volume (not locked — user may choose a
                //  different size for the continuation volume, mirroring local virtual drive behaviour)
                if (lastVmd != null && lastVmd.ContentCapacity > 0)
                    VirtualDriveConfigViewModelBase.SetCapacityFromBytes(lastVmd.ContentCapacity,
                        v => vm.ContentCapacityValue = v,
                        u => vm.ContentCapacityUnit  = u);

                var window = new OpenRemoteVirtualDriveWindow(vm) { Owner = Application.Current.MainWindow };
                if (window.ShowDialog() == true && vm.Result is { } request)
                    mediaReady = svc.InsertRemoteVirtualMedia(request.Media, currentCaps, System.IO.FileMode.Create);
            }
            else if (svc?.IsVirtualDrive == true)
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



                // IoSpeed: read from ViewModel
                IoRateOption? ioSpeed = _viewModel.SelectedIoSpeed;

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
                    currentIoRate: ioSpeed);

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

            var answer = SimpleBox.Show(info, "Emergency TOC Export",
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

    // ── ITapeServiceHost — Structured rename prompts ──────────────────────────

    /// <inheritdoc/>
    public string? OnAskMediaName(string currentName)
        => Ask("Rename Media", "Enter a new description for the media:", currentName);

    /// <inheritdoc/>
    public string? OnAskBackupSetName(int setIndex, int altIndex, string currentDescription)
        => Ask("Rename Backup Set",
               $"Enter a new description for backup set #{setIndex} | {altIndex}:",
               currentDescription);
}
